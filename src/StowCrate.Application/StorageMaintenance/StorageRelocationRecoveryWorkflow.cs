using StowCrate.Application.LocalState;
using StowCrate.Core.BackupPlans;

namespace StowCrate.Application.StorageMaintenance;

public enum StorageRelocationRecoveryStatus { NotFound, ResumeRequired, CleanupPending, CompletedReservationsRetained }

public sealed record StorageRelocationRecoveryResult(PlanId PlanId, Guid? TransactionId,
    StorageRelocationRecoveryStatus Status, string? Diagnostic);

/// <summary>启动时只恢复已提交清理，不自动启动 pre-commit 复制，不释放 reservation。</summary>
public sealed class StorageRelocationRecoveryWorkflow(IStorageRelocationJournalStore store, IStorageRelocationOldCopyStore? physical)
{
    public async Task<StorageRelocationRecoveryResult> RecoverAsync(PlanId planId, CancellationToken cancellationToken)
    {
        // 枚举快照不是权限；当前数据库完整日志验证失败必须向上传播。
        var journal = await store.LoadRelocationAsync(planId, cancellationToken).ConfigureAwait(false);
        if (journal is null) return new(planId, null, StorageRelocationRecoveryStatus.NotFound, null);
        StorageRelocationRecoveryResult Result(StorageRelocationRecoveryStatus status, string? diagnostic = null)
            => new(planId, journal.Manifest.TransactionId, status, diagnostic);
        if (!journal.Progress.IsMetadataCommitted)
            return Result(StorageRelocationRecoveryStatus.ResumeRequired, "RELOCATION_PRECOMMIT_RESUME_REQUIRED");
        if (journal.Progress.Stage == StorageTransferStage.Completed)
            return Result(StorageRelocationRecoveryStatus.CompletedReservationsRetained);
        if (physical is null)
            return Result(StorageRelocationRecoveryStatus.CleanupPending, "RELOCATION_CLEANUP_ADAPTER_UNAVAILABLE");
        try
        {
            foreach (var artifact in journal.Progress.Artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (artifact.Stage == StorageTransferArtifactStage.OldCopyAbsent) continue;
                journal = await store.CleanupRelocationEntryAsync(journal.Manifest.TransactionId, journal.Revision,
                    artifact.Artifact.VersionId, physical, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            journal = await store.CompleteRelocationAsync(journal.Manifest.TransactionId, journal.Revision, physical, cancellationToken).ConfigureAwait(false);
            return Result(StorageRelocationRecoveryStatus.CompletedReservationsRetained);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // metadata 已提交：取消只暂停清理，不能把迁移永久成功点反转成失败。
            return Result(StorageRelocationRecoveryStatus.CleanupPending, "RELOCATION_CLEANUP_CANCELLED");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            || exception is LocalStateRepositoryException and not LocalStateCorruptionException)
        {
            // 不输出原始异常，避免把设备路径或其他本地信息带入诊断。
            return Result(StorageRelocationRecoveryStatus.CleanupPending, "RELOCATION_CLEANUP_REQUIRES_RECONCILIATION");
        }
    }
}
