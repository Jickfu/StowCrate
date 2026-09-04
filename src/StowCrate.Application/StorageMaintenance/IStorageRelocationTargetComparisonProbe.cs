namespace StowCrate.Application.StorageMaintenance;

/// <summary>只读验证全部 final/temp 的真实文件系统比较语义及冲突，不得通过写探测文件识别能力。</summary>
public interface IStorageRelocationTargetComparisonProbe
{
    // 必须覆盖现存父目录及待创建子目录的继承规则，不能按操作系统猜测或复用路径规范化 key。
    // 无法可靠识别时抛出 StorageRelocationComparisonUnavailableException；冲突同样拒绝成功。
    Task VerifyTargetsAsync(StorageRelocationPhysicalInventory observation, Guid transactionId, CancellationToken cancellationToken);
}

public sealed class StorageRelocationComparisonUnavailableException()
    : IOException("无法可靠识别目标文件系统的大小写或 Unicode 比较规则，已阻止迁移。")
{
    public string DiagnosticCode { get; } = "RELOCATION_TARGET_COMPARISON_UNAVAILABLE";
}
