using StowCrate.Application.LocalState;

namespace StowCrate.Application.StorageMaintenance;

/// <summary>只读迁移检查；返回观察结果，不启动 journal，也不授予复制或删除权限。</summary>
public sealed class StorageRelocationInspectionWorkflow(StorageRelocationConfigurationReader configuration,
    IStorageRelocationInventoryStore inventory, IStorageRelocationInventoryProbe physical)
{
    public async Task<StorageRelocationPhysicalInventory> InspectAsync(StorageRelocationInventoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var captured = await configuration.ReadAsync(request.PlanId, cancellationToken).ConfigureAwait(false);
        var before = await inventory.ReadRelocationInventoryAsync(request, captured, cancellationToken).ConfigureAwait(false);
        var observed = await physical.ObserveInventoryAsync(before, cancellationToken).ConfigureAwait(false);
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
