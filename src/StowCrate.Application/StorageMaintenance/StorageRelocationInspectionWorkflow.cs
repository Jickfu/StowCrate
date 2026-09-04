using StowCrate.Application.LocalState;

namespace StowCrate.Application.StorageMaintenance;

/// <summary>只读迁移检查；返回观察结果，不启动 journal，也不授予复制或删除权限。</summary>
public sealed class StorageRelocationInspectionWorkflow(StorageRelocationConfigurationReader configuration,
    IStorageRelocationInventoryStore inventory, IStorageRelocationInventoryProbe physical,
    IStorageRelocationTargetNamespaceProbe? targets = null,
    IStorageRelocationTargetComparisonProbe? comparison = null,
    IStorageRelocationTargetDurabilityProbe? durability = null)
{
    public async Task<StorageRelocationPhysicalInventory> InspectAsync(StorageRelocationInventoryRequest request, CancellationToken cancellationToken)
        => (await InspectCoreAsync(request, null, false, cancellationToken).ConfigureAwait(false)).Observation;

    public async Task<StorageRelocationTargetInspection> InspectTargetsAsync(StorageRelocationInventoryRequest request,
        Guid transactionId, CancellationToken cancellationToken)
    {
        if (transactionId == Guid.Empty) throw new ArgumentException("Transaction identity is required.", nameof(transactionId));
        cancellationToken.ThrowIfCancellationRequested();
        if (comparison is null) throw new StorageRelocationComparisonUnavailableException();
        if (targets is null) throw new InvalidOperationException("Relocation target namespace probe is required.");
        return new(transactionId, (await InspectCoreAsync(request, transactionId, false, cancellationToken).ConfigureAwait(false)).Observation);
    }

    internal async Task<(StorageRelocationManifest Manifest, StorageRelocationConfigurationObservation Configuration)> InspectForBeginAsync(
        StorageRelocationInventoryRequest request, Guid transactionId, CancellationToken cancellationToken)
    {
        if (transactionId == Guid.Empty) throw new ArgumentException("Transaction identity is required.", nameof(transactionId));
        cancellationToken.ThrowIfCancellationRequested();
        if (comparison is null) throw new StorageRelocationComparisonUnavailableException();
        if (targets is null) throw new InvalidOperationException("Relocation target namespace probe is required.");
        if (durability is null) throw new StorageRelocationDurabilityUnavailableException();
        var result = await InspectCoreAsync(request, transactionId, true, cancellationToken).ConfigureAwait(false);
        return (Manifest(result.Observation, transactionId), result.Configuration);
    }

    private async Task<(StorageRelocationPhysicalInventory Observation, StorageRelocationConfigurationObservation Configuration)> InspectCoreAsync(StorageRelocationInventoryRequest request,
        Guid? transactionId, bool forBegin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var captured = await configuration.ReadAsync(request.PlanId, cancellationToken).ConfigureAwait(false);
        var before = await inventory.ReadRelocationInventoryAsync(request, captured, cancellationToken).ConfigureAwait(false);
        var observed = await physical.ObserveInventoryAsync(before, cancellationToken).ConfigureAwait(false);
        if (observed.Inventory.PlanId != before.PlanId || observed.Inventory.DeviceId != before.DeviceId
            || !observed.Inventory.Roots.SequenceEqual(before.Roots) || !observed.Inventory.Entries.SequenceEqual(before.Entries))
            throw new InvalidOperationException("Physical observation changed the requested inventory.");
        if (transactionId is { } id)
        {
            await comparison!.VerifyTargetsAsync(observed, id, cancellationToken).ConfigureAwait(false);
            await targets!.VerifyUnoccupiedTargetsAsync(observed, id, cancellationToken).ConfigureAwait(false);
            if (forBegin)
            {
                await durability!.VerifyTargetDurabilityAsync(Manifest(observed, id), cancellationToken).ConfigureAwait(false);
                // 屏障 I/O 之后再核对比较规则与占位，配置/metadata 的最终重验必须位于所有预检之后。
                await comparison.VerifyTargetsAsync(observed, id, cancellationToken).ConfigureAwait(false);
                await targets.VerifyUnoccupiedTargetsAsync(observed, id, cancellationToken).ConfigureAwait(false);
            }
        }
        var current = await configuration.RevalidateAsync(captured, cancellationToken).ConfigureAwait(false);
        var after = await inventory.ReadRelocationInventoryAsync(request, current, cancellationToken).ConfigureAwait(false);
        // 哈希期间可能发生新的发布或 binding 变更，不能将两个时间点的集合拼成成功结果。
        if (before.PlanId != after.PlanId || before.DeviceId != after.DeviceId
            || !before.Roots.SequenceEqual(after.Roots) || !before.Entries.SequenceEqual(after.Entries))
            throw new LocalStateConcurrencyException("Relocation inventory changed during physical inspection.");
        cancellationToken.ThrowIfCancellationRequested();
        return (observed, current);
    }

    private static StorageRelocationManifest Manifest(StorageRelocationPhysicalInventory observed, Guid transactionId)
    {
        if (!observed.Roots.Select(x => new StorageRelocationRootPaths(x.Kind, x.OldRoot, x.NewRoot)).SequenceEqual(observed.Inventory.Roots)
            || !observed.Entries.Select(x => x.Placement).SequenceEqual(observed.Inventory.Entries))
            throw new InvalidOperationException("Physical observation does not cover the inventory.");
        return new(transactionId, observed.Inventory.PlanId, observed.Inventory.DeviceId, observed.Roots,
            observed.Entries.Select(x => new StorageRelocationEntry(x.Placement.UnitId, x.Placement.RootKind,
                x.Placement.Artifact, x.Placement.RelativePath,
                StorageRelocationTempLayout.Create(transactionId, x.Placement.Artifact.VersionId, x.Placement.RelativePath), x.Identity)));
    }
}
