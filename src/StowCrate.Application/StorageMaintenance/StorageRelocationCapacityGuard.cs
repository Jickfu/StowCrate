using System.Collections.Immutable;
using StowCrate.Application.BackupPlans.Resolution;

namespace StowCrate.Application.StorageMaintenance;

public enum StorageRelocationCapacityFailure { Unavailable, Insufficient }
public sealed class StorageRelocationCapacityException(StorageRelocationCapacityFailure reason)
    : IOException(reason == StorageRelocationCapacityFailure.Unavailable ? "Relocation capacity is unavailable." : "Relocation capacity is insufficient.")
{
    public StorageRelocationCapacityFailure Reason { get; } = reason;
}

public sealed record StorageVolumeIdentity(string Provider, int EncodingVersion, string Value);
public sealed record StorageCapacityObservation(StorageObjectIdentity RootIdentity, StorageVolumeIdentity VolumeIdentity, long? AvailableBytes);
public sealed record StorageCapacityNeed(ResolvedPhysicalPath Root, long RequiredBytes, StorageObjectIdentity? ExpectedIdentity = null);
public sealed record StorageCapacitySummary(StorageVolumeIdentity VolumeIdentity, long RequiredBytes, long AvailableBytes);

public interface IStorageRelocationCapacityProbe
{
    Task<StorageCapacityObservation> ObserveAsync(ResolvedPhysicalPath root, CancellationToken cancellationToken);
}

/// <summary>容量只是一时观察和 bytes 下界，不是空间预留；未知一律失败，没有强制继续参数。</summary>
public sealed class StorageRelocationCapacityGuard(IStorageRelocationCapacityProbe probe)
{
    /// <summary>仅按 metadata 根分组的估算；物理检查须解析实际目标父目录并调用 CheckAsync，不能据此授权复制。</summary>
    public Task<ImmutableArray<StorageCapacitySummary>> CheckInventoryAsync(StorageRelocationInventory inventory, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        if (inventory.Roots.IsDefaultOrEmpty || inventory.Roots.Select(x => x.Kind).Distinct().Count() != inventory.Roots.Length
            || inventory.Entries.Any(x => x.Artifact.Length < 0 || !inventory.Roots.Any(r => r.Kind == x.RootKind)))
            throw new ArgumentException("Invalid relocation inventory.", nameof(inventory));
        return CheckAsync(inventory.Roots.Select(root => new StorageCapacityNeed(root.NewRoot,
            inventory.Entries.Where(x => x.RootKind == root.Kind).Aggregate(0L, (sum, x) => checked(sum + x.Artifact.Length)))), token);
    }

    /// <summary>仅按 journal 根分组的估算；不处理根下独立挂载卷，实际 Stage 使用物理目录解析后的需求。</summary>
    public Task<ImmutableArray<StorageCapacitySummary>> CheckPendingAsync(StorageRelocationJournal journal, CancellationToken token)
    {
        var pending = journal.Progress.Artifacts.Where(x => x.Stage == StorageTransferArtifactStage.Pending).Select(x => x.Artifact.VersionId).ToHashSet();
        var entries = journal.Manifest.Entries.Where(x => pending.Contains(x.Artifact.VersionId)).ToArray();
        return CheckAsync(journal.Manifest.Roots.Where(root => entries.Any(x => x.RootKind == root.Kind))
            .Select(root => new StorageCapacityNeed(root.NewRoot,
                entries.Where(x => x.RootKind == root.Kind).Aggregate(0L, (sum, x) => checked(sum + x.Artifact.Length)), root.NewIdentity)), token);
    }

    public async Task<ImmutableArray<StorageCapacitySummary>> CheckAsync(IEnumerable<StorageCapacityNeed> needs, CancellationToken token)
    {
        var groups = new Dictionary<StorageVolumeIdentity, (long Required, long Available)>();
        foreach (var need in needs)
        {
            token.ThrowIfCancellationRequested();
            if (need.RequiredBytes < 0) throw new ArgumentException("Capacity demand cannot be negative.", nameof(needs));
            StorageCapacityObservation observed;
            try { observed = await probe.ObserveAsync(need.Root, token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            { throw new StorageRelocationCapacityException(StorageRelocationCapacityFailure.Unavailable); }
            if (observed is null || observed.AvailableBytes is null or < 0
                || observed.RootIdentity is null || string.IsNullOrWhiteSpace(observed.RootIdentity.Provider) || observed.RootIdentity.EncodingVersion < 1 || string.IsNullOrWhiteSpace(observed.RootIdentity.Value)
                || observed.VolumeIdentity is null || string.IsNullOrWhiteSpace(observed.VolumeIdentity.Provider) || observed.VolumeIdentity.EncodingVersion < 1 || string.IsNullOrWhiteSpace(observed.VolumeIdentity.Value)
                || need.ExpectedIdentity is not null && need.ExpectedIdentity != observed.RootIdentity)
                throw new StorageRelocationCapacityException(StorageRelocationCapacityFailure.Unavailable);
            var previous = groups.GetValueOrDefault(observed.VolumeIdentity, (Required: 0L, Available: observed.AvailableBytes.Value));
            groups[observed.VolumeIdentity] = (checked(previous.Required + need.RequiredBytes), Math.Min(previous.Available, observed.AvailableBytes.Value));
        }
        token.ThrowIfCancellationRequested();
        if (groups.Values.Any(x => x.Available < x.Required))
            throw new StorageRelocationCapacityException(StorageRelocationCapacityFailure.Insufficient);
        return groups.Select(x => new StorageCapacitySummary(x.Key, x.Value.Required, x.Value.Available)).ToImmutableArray();
    }
}
