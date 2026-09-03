using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Application.StorageMaintenance;

/// <summary>仅迁移所需的 portable identity/layout 语义；与完整 Plan 或备份执行指纹不可互换。</summary>
public readonly record struct StorageRelocationConfigurationFingerprint(Sha256Digest Digest)
{
    public const int EncodingVersion = 1;

    public static StorageRelocationConfigurationFingerprint Compute(PortableBackupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new(CanonicalFingerprintEncodingV1.Encode("storage-relocation-configuration-v1", writer =>
        {
            writer.Utf8(1, plan.Id.Value.ToString("D"));
            writer.SignedNumber(2, plan.Semantics.OutputPathEncoding);
            // 未声明的 FILE_MANAGED 单元使用默认格式；离线时不能靠重新 discovery 排除其布局影响。
            writer.SignedNumber(3, (int)plan.ArchiveSpecDefault.Format);
            foreach (var source in plan.Sources.OrderBy(x => x.Id.Value.ToString("D"), StringComparer.Ordinal))
                writer.Digest(10, CanonicalFingerprintEncodingV1.Encode("relocation-source-v1", nested =>
                {
                    nested.Utf8(1, source.Id.Value.ToString("D"));
                    nested.Utf8(2, source.SourceOutputPath.Value);
                }));
            foreach (var unit in plan.ArchiveUnits.OrderBy(x => x.Id.Value.ToString("D"), StringComparer.Ordinal))
                writer.Digest(20, CanonicalFingerprintEncodingV1.Encode("relocation-unit-v1", nested =>
                {
                    nested.Utf8(1, unit.Id.Value.ToString("D"));
                    nested.Utf8(2, unit.SourceId.Value.ToString("D"));
                    nested.Utf8(3, unit.Path.Value);
                    nested.SignedNumber(4, unit switch
                    {
                        UiManagedArchiveUnit => 0,
                        FileManagedArchiveUnit => 1,
                        _ => throw new ArgumentException("Unknown Archive Unit declaration.", nameof(plan))
                    });
                    nested.SignedNumber(5, (int)(unit.ArchiveSpecOverride?.Format ?? plan.ArchiveSpecDefault.Format));
                }));
        }));
    }
}
