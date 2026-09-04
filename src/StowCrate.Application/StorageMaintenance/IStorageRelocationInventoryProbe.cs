using System.Collections.Immutable;

namespace StowCrate.Application.StorageMaintenance;

/// <summary>目标根尚未创建；与无权限、链接或其他不可用状态区分，不携带设备路径。</summary>
public sealed class StorageRelocationTargetRootMissingException(StorageRootKind rootKind)
    : IOException("迁移目标根目录不存在，请先创建目录后重试。")
{
    public StorageRootKind RootKind { get; } = rootKind;
    public string DiagnosticCode { get; } = "RELOCATION_TARGET_ROOT_MISSING";
}

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
