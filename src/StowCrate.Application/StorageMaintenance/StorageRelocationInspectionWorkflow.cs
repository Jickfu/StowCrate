using StowCrate.Application.LocalState;

namespace StowCrate.Application.StorageMaintenance;

/// <summary>只读迁移检查；返回观察结果，不启动 journal，也不授予复制或删除权限。</summary>
public sealed class StorageRelocationInspectionWorkflow(StorageRelocationConfigurationReader configuration,
    IStorageRelocationInventoryStore inventory, IStorageRelocationInventoryProbe physical,
    IStorageRelocationTargetNamespaceProbe? targets = null)
{
    public Task<StorageRelocationPhysicalInventory> InspectAsync(StorageRelocationInventoryRequest request, CancellationToken cancellationToken)
        => InspectCoreAsync(request, null, cancellationToken);

    public async Task<StorageRelocationTargetInspection> InspectTargetsAsync(StorageRelocationInventoryRequest request,
        Guid transactionId, CancellationToken cancellationToken)
    {
        if (transactionId == Guid.Empty) throw new ArgumentException("Transaction identity is required.", nameof(transactionId));
        if (targets is null) throw new InvalidOperationException("Relocation target namespace probe is required.");
        return new(transactionId, await InspectCoreAsync(request, transactionId, cancellationToken).ConfigureAwait(false));
    }

    private async Task<StorageRelocationPhysicalInventory> InspectCoreAsync(StorageRelocationInventoryRequest request,
        Guid? transactionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var captured = await configuration.ReadAsync(request.PlanId, cancellationToken).ConfigureAwait(false);
        var before = await inventory.ReadRelocationInventoryAsync(request, captured, cancellationToken).ConfigureAwait(false);
        var observed = await physical.ObserveInventoryAsync(before, cancellationToken).ConfigureAwait(false);
        if (transactionId is { } id)
            await targets!.VerifyUnoccupiedTargetsAsync(observed, id, cancellationToken).ConfigureAwait(false);
        var current = await configuration.RevalidateAsync(captured, cancellationToken).ConfigureAwait(false);
        var after = await inventory.ReadRelocationInventoryAsync(request, current, cancellationToken).ConfigureAwait(false);
        // 哈希期间可能发生新的发布或 binding 变更，不能将两个时间点的集合拼成成功结果。
        if (before.PlanId != after.PlanId || before.DeviceId != after.DeviceId
            || !before.Roots.SequenceEqual(after.Roots) || !before.Entries.SequenceEqual(after.Entries))
            throw new LocalStateConcurrencyException("Relocation inventory changed during physical inspection.");
        cancellationToken.ThrowIfCancellationRequested();
        return observed;
    }
}
