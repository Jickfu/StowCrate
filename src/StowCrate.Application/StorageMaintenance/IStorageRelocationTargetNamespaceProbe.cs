namespace StowCrate.Application.StorageMaintenance;

/// <summary>检查冻结清单的目标和事务 temp 当前均未被占用；不创建目录，不提供完整启动授权。</summary>
public interface IStorageRelocationTargetNamespaceProbe
{
    Task VerifyUnoccupiedTargetsAsync(StorageRelocationManifest manifest, CancellationToken cancellationToken);
    Task VerifyUnoccupiedTargetsAsync(StorageRelocationPhysicalInventory observation, Guid transactionId, CancellationToken cancellationToken);
}

/// <summary>绑定拟用事务 ID 的只读观察，仍不是完整的 Begin authority。</summary>
public sealed record StorageRelocationTargetInspection(Guid TransactionId, StorageRelocationPhysicalInventory Observation);
