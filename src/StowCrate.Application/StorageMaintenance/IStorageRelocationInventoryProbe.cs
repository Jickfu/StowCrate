using System.Collections.Immutable;

namespace StowCrate.Application.StorageMaintenance;

public sealed record StorageRelocationPlacementObservation(StorageRelocationPlacement Placement, StorageObjectIdentity Identity);

/// <summary>瞬时只读观察；不包含 durable proof、manifest 或启动许可，不能作为文件操作授权。</summary>
public sealed record StorageRelocationPhysicalInventory(StorageRelocationInventory Inventory,
    ImmutableArray<StorageRelocationRoot> Roots,
    ImmutableArray<StorageRelocationPlacementObservation> Entries,
    ImmutableArray<StorageCapacitySummary> Capacity);

public interface IStorageRelocationInventoryProbe
{
    Task<StorageRelocationPhysicalInventory> ObserveInventoryAsync(StorageRelocationInventory inventory, CancellationToken cancellationToken);
}
