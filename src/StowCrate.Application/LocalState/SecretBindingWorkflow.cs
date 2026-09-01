using System.Collections.Immutable;
using System.Security.Cryptography;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Core.BackupPlans;

namespace StowCrate.Application.LocalState;

/// <summary>Secret material 的短生命周期缓冲区；Dispose 后立即清零。</summary>
public sealed class SecretMaterialLease : IDisposable
{
    private byte[]? bytes;
    public SecretMaterialLease(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) throw new ArgumentException("Secret material cannot be empty.", nameof(value));
        bytes = value.ToArray();
    }
    public ReadOnlyMemory<byte> Material => bytes ?? throw new ObjectDisposedException(nameof(SecretMaterialLease));
    ~SecretMaterialLease() => Clear();
    public void Dispose() { Clear(); GC.SuppressFinalize(this); }
    private void Clear() { var owned = Interlocked.Exchange(ref bytes, null); if (owned is not null) CryptographicOperations.ZeroMemory(owned); }
}

public sealed record SecretMaterialLocator(string ProviderToken, string OpaqueReference);
public enum SecretMaterialAvailability { Available, Unavailable, ProviderUnavailable }

/// <summary>平台适配器必须在 CreateAsync 返回前消费 lease，不得记录其内容，也不得原地覆盖既有 locator。</summary>
public interface ISecretMaterialStore
{
    Task<SecretMaterialLocator> CreateAsync(string providerToken, SecretMaterialLease material, CancellationToken cancellationToken);
    Task<SecretMaterialLease?> OpenAsync(SecretMaterialLocator locator, CancellationToken cancellationToken);
    Task<SecretMaterialAvailability> ProbeAsync(SecretMaterialLocator locator, CancellationToken cancellationToken);
    Task DeleteAsync(SecretMaterialLocator locator, CancellationToken cancellationToken);
}

public enum SecretBindingOperation { Set, Replace, Rebind, Unbind }
public sealed record SecretBindingWorkflowResult(SecretBindingMetadata Metadata, SecretBindingOperation Operation, bool OrphanCleanupRequired);

public interface ISecretBindingFaultInjector
{
    void AfterMaterialCreated();
    void AfterMetadataCommitted();
}

public sealed class NoSecretBindingFaultInjector : ISecretBindingFaultInjector
{
    public static NoSecretBindingFaultInjector Instance { get; } = new();
    public void AfterMaterialCreated() { }
    public void AfterMetadataCommitted() { }
}

public sealed class SecretBindingWorkflow(ISecretBindingMetadataStore metadata, ISecretMaterialStore materials,
    ISecretBindingFaultInjector? faults = null)
{
    private readonly ISecretBindingFaultInjector faults = faults ?? NoSecretBindingFaultInjector.Instance;

    public Task<ImmutableArray<SecretBindingMetadata>> LoadAsync(PlanId planId, CancellationToken cancellationToken)
        => metadata.LoadAsync(planId, cancellationToken);

    public Task<SecretBindingWorkflowResult> SetAsync(PortableBackupPlan plan, SecretSlotId slotId, string providerToken,
        SecretMaterialLease material, CancellationToken cancellationToken)
        => SwitchAsync(plan, slotId, null, providerToken, material, SecretBindingOperation.Set, cancellationToken);

    public Task<SecretBindingWorkflowResult> ReplaceAsync(PortableBackupPlan plan, SecretSlotId slotId, SecretRevision expectedRevision,
        SecretMaterialLease material, CancellationToken cancellationToken)
        => SwitchAsync(plan, slotId, expectedRevision, null, material, SecretBindingOperation.Replace, cancellationToken);

    public Task<SecretBindingWorkflowResult> RebindAsync(PortableBackupPlan plan, SecretSlotId slotId, SecretRevision expectedRevision,
        string providerToken, SecretMaterialLease material, CancellationToken cancellationToken)
        => SwitchAsync(plan, slotId, expectedRevision, providerToken, material, SecretBindingOperation.Rebind, cancellationToken);

    public async Task<SecretBindingWorkflowResult> UnbindAsync(PlanId planId, SecretSlotId slotId, SecretRevision expectedRevision,
        CancellationToken cancellationToken)
    {
        var current = await FindAsync(planId, slotId, cancellationToken).ConfigureAwait(false);
        if (!current.IsActive || current.Revision != expectedRevision) throw new LocalStateConcurrencyException("Secret binding revision/state changed.");
        var deactivated = await metadata.DeactivateAsync(planId, slotId, expectedRevision, cancellationToken).ConfigureAwait(false);
        var cleanupFailed = !await TryDeleteAsync(new(current.ProviderToken, current.OpaqueReference), cancellationToken).ConfigureAwait(false);
        return new(deactivated, SecretBindingOperation.Unbind, cleanupFailed);
    }

    public async Task<SecretMaterialAvailability> ProbeAsync(PlanId planId, SecretSlotId slotId, CancellationToken cancellationToken)
    {
        var current = await FindAsync(planId, slotId, cancellationToken).ConfigureAwait(false);
        return current.IsActive
            ? await materials.ProbeAsync(new(current.ProviderToken, current.OpaqueReference), cancellationToken).ConfigureAwait(false)
            : SecretMaterialAvailability.Unavailable;
    }

    public async Task<SecretMaterialLease?> OpenForHeadlessExecutionAsync(PlanId planId, SecretSlotId slotId,
        SecretRevision expectedRevision, CancellationToken cancellationToken)
    {
        var current = await FindAsync(planId, slotId, cancellationToken).ConfigureAwait(false);
        if (!current.IsActive || current.Revision != expectedRevision) return null;
        return await materials.OpenAsync(new(current.ProviderToken, current.OpaqueReference), cancellationToken).ConfigureAwait(false);
    }

    private async Task<SecretBindingWorkflowResult> SwitchAsync(PortableBackupPlan plan, SecretSlotId slotId,
        SecretRevision? expectedRevision, string? providerToken, SecretMaterialLease material, SecretBindingOperation operation,
        CancellationToken cancellationToken)
    {
        if (!plan.SecretSlots.Any(slot => slot.Id == slotId)) throw new ArgumentException("SecretSlotId is not declared by the Plan.", nameof(slotId));
        SecretBindingMetadata? old = expectedRevision is null ? null : await FindAsync(plan.Id, slotId, cancellationToken).ConfigureAwait(false);
        if (old is not null && old.Revision != expectedRevision) throw new LocalStateConcurrencyException("Secret binding revision changed.");
        var selectedProvider = providerToken ?? old?.ProviderToken ?? throw new ArgumentException("Secret provider is required.", nameof(providerToken));
        var created = await materials.CreateAsync(selectedProvider, material, cancellationToken).ConfigureAwait(false);
        if (old is not null && string.Equals(created.ProviderToken, old.ProviderToken, StringComparison.Ordinal)
            && string.Equals(created.OpaqueReference, old.OpaqueReference, StringComparison.Ordinal))
            throw new InvalidOperationException("Secret Store did not create a new copy-on-write locator.");
        if (!string.Equals(created.ProviderToken, selectedProvider, StringComparison.Ordinal))
        {
            await TryDeleteAsync(created, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("Secret Store returned a locator for a different provider.");
        }

        var metadataCommitted = false;
        try
        {
            faults.AfterMaterialCreated();
            var saved = operation switch
            {
                SecretBindingOperation.Set => await metadata.BindAsync(plan.Id, slotId, created.ProviderToken, created.OpaqueReference, cancellationToken).ConfigureAwait(false),
                SecretBindingOperation.Replace => await metadata.ReplaceAsync(plan.Id, slotId, expectedRevision!.Value, created.ProviderToken, created.OpaqueReference, cancellationToken).ConfigureAwait(false),
                SecretBindingOperation.Rebind => await metadata.RebindAsync(plan.Id, slotId, expectedRevision!.Value, created.ProviderToken, created.OpaqueReference, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("Unsupported Secret binding switch.")
            };
            metadataCommitted = true;
            faults.AfterMetadataCommitted();
            var cleanupFailed = old is not null && !await TryDeleteAsync(new(old.ProviderToken, old.OpaqueReference), cancellationToken).ConfigureAwait(false);
            return new(saved, operation, cleanupFailed);
        }
        catch
        {
            // DB CAS 失败时新 locator 只是 orphan；commit 后必须保留新 locator，不能破坏 active metadata。
            if (!metadataCommitted) await TryDeleteAsync(created, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<SecretBindingMetadata> FindAsync(PlanId planId, SecretSlotId slotId, CancellationToken cancellationToken)
        => (await metadata.LoadAsync(planId, cancellationToken).ConfigureAwait(false)).SingleOrDefault(item => item.SecretSlotId == slotId)
            ?? throw new LocalStateConcurrencyException("Secret binding does not exist.");

    private async Task<bool> TryDeleteAsync(SecretMaterialLocator locator, CancellationToken cancellationToken)
    {
        try { await materials.DeleteAsync(locator, cancellationToken).ConfigureAwait(false); return true; }
        catch { return false; }
    }
}

/// <summary>只在解析执行快照前组合 path facts 与 active SecretRevision，不合并两个持久化 aggregate。</summary>
public sealed class DevicePlanLocalFactsLoader(ConfigDatabaseIdentity identity, IDevicePlanBindingStore bindings, ISecretBindingMetadataStore secrets)
{
    public async Task<DevicePlanBindingFacts> LoadAsync(PlanId planId, CancellationToken cancellationToken)
    {
        var paths = await bindings.LoadAsync(planId, cancellationToken).ConfigureAwait(false);
        var secretMetadata = await secrets.LoadAsync(planId, cancellationToken).ConfigureAwait(false);
        return new(planId, identity.DeviceId,
            paths?.Sources.Where(x => x.IsActive).Select(x => new SourceBindingFact(x.SourceId, new(x.CanonicalPath, x.ComparisonKey))) ?? [],
            paths?.CurrentRoot is { IsActive: true } current ? new(current.CanonicalPath, current.ComparisonKey) : null,
            paths?.HistoryRoot is { IsActive: true } history ? new(history.CanonicalPath, history.ComparisonKey) : null,
            paths?.ExternalSources.Where(x => x.IsActive).Select(x => new ExternalSourceBindingFact(x.ExternalSourceId, new(x.CanonicalPath, x.ComparisonKey))) ?? [],
            secretMetadata.Where(x => x.IsActive).Select(x => new SecretBindingFact(x.SecretSlotId, x.Revision)));
    }
}
