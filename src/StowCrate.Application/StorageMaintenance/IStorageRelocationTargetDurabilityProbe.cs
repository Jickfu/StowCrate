namespace StowCrate.Application.StorageMaintenance;

/// <summary>检查现存目标目录的持久化屏障，不创建目录、探测文件或 journal，也不保证后续写入成功。</summary>
public interface IStorageRelocationTargetDurabilityProbe
{
    Task VerifyTargetDurabilityAsync(StorageRelocationManifest manifest, CancellationToken cancellationToken);
}

public sealed class StorageRelocationDurabilityUnavailableException()
    : IOException("目标目录无法完成持久化屏障检查，已阻止迁移。")
{
    public string DiagnosticCode { get; } = "RELOCATION_TARGET_DURABILITY_UNAVAILABLE";
}
