using StowCrate.Application.BackupPlans.Candidates;
using StowCrate.Application.BackupPlans.Documents;
using StowCrate.Application.LocalState;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Application.StorageMaintenance;

/// <summary>当前配置观察，不是 durable journal 或提交授权；完整配置指纹用于发现变化，不替代迁移相关语义比较。</summary>
public sealed record StorageRelocationConfigurationObservation(
    AuthoritativePlanSnapshot Snapshot, PlanSemanticFingerprint ConfigurationFingerprint);

/// <summary>迁移独立读取有效配置，不执行原始输入扫描、备份 readiness 或 Secret material 获取。</summary>
public sealed class StorageRelocationConfigurationReader(AuthoritativePlanWorkflow authority)
{
    /// <summary>重新读取有效配置并检查迁移相关变化。root/binding 与 journal CAS 仍必须在提交事务独立重验。</summary>
    public async Task<StorageRelocationConfigurationObservation> RevalidateAsync(
        StorageRelocationConfigurationObservation captured, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(captured);
        var current = await ReadAsync(captured.Snapshot.Plan.Id, cancellationToken).ConfigureAwait(false);
        if (!captured.Snapshot.IsActive || captured.Snapshot.Authority != current.Snapshot.Authority
            || !string.Equals(captured.Snapshot.FileDocumentPath, current.Snapshot.FileDocumentPath, StringComparison.Ordinal)
            || StorageRelocationConfigurationFingerprint.Compute(captured.Snapshot.Plan)
                != StorageRelocationConfigurationFingerprint.Compute(current.Snapshot.Plan))
            throw new LocalStateConcurrencyException("Relocation configuration identity or output layout changed.");
        // 名称、调度、过滤规则和压缩级别只影响未来备份，不因完整配置指纹/Managed revision 改变而阻断搬迁。
        return current;
    }

    public async Task<StorageRelocationConfigurationObservation> ReadAsync(PlanId planId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 复用严格的 authority reader：File-backed 每次重读，丢失/无效时不使用上次观察结果。
        var snapshot = await authority.LoadAsync(planId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!snapshot.IsActive) throw new LocalStateConcurrencyException("Storage relocation requires an active Plan.");
        return new(snapshot, CandidateFingerprintCalculator.ComputePlanSemantic(snapshot.Plan));
    }
}
