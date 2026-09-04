using StowCrate.Application.LocalState;
using StowCrate.Core.BackupPlans;

namespace StowCrate.Application.StorageMaintenance;

public enum StorageRelocationRecoveryStatus { NotFound, ResumeRequired, CleanupPending, CompletedReservationsRetained, OutcomeUnknown }

public sealed record StorageRelocationRecoveryResult(PlanId PlanId, Guid? TransactionId,
    StorageRelocationRecoveryStatus Status, string? Diagnostic);

/// <summary>启动时只恢复已提交清理，不自动启动 pre-commit 复制，不释放 reservation。</summary>
public sealed class StorageRelocationRecoveryWorkflow(IStorageRelocationJournalStore store, IStorageRelocationOldCopyStore? physical)
{
    public async Task<StorageRelocationRecoveryResult> ResumeAsync(PlanId planId, Guid transactionId,
        IStorageRelocationPhysicalStore transfer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        if (transactionId == Guid.Empty) throw new ArgumentException("Transaction identity is required.", nameof(transactionId));
        var journal = await store.LoadRelocationAsync(planId, cancellationToken).ConfigureAwait(false);
        if (journal is null) return new(planId, transactionId, StorageRelocationRecoveryStatus.NotFound, null);
        if (journal.Manifest.TransactionId != transactionId)
            throw new LocalStateConcurrencyException("Selected relocation transaction changed.");
        try
        {
            if (journal.Progress.Stage == StorageTransferStage.Prepared)
            {
                foreach (var entry in journal.Manifest.Entries)
                {
                    // 只按当前 durable stage 推进，不重复复制已拥有 staged identity 的条目。
                    while (journal.Progress.Artifacts.Single(x => x.Artifact.VersionId == entry.Artifact.VersionId).Stage
                        is StorageTransferArtifactStage.Pending or StorageTransferArtifactStage.Staged)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        journal = await store.ResumeRelocationEntryAsync(transactionId, journal.Revision,
                            entry.Artifact.VersionId, transfer, cancellationToken).ConfigureAwait(false);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                journal = await store.SealRelocationTargetsAsync(transactionId, journal.Revision, cancellationToken).ConfigureAwait(false);
            }
            if (journal.Progress.Stage == StorageTransferStage.TargetsDurable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                journal = await store.CommitRelocationAsync(transactionId, journal.Revision, transfer, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (IsRecoverable(exception)
            || exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            // 可能在提交后丢失响应；只读重查，不重放失败的动作，也不猜测永久成功点。
            try
            {
                var current = await store.LoadRelocationAsync(planId, CancellationToken.None).ConfigureAwait(false);
                if (current is not null && current.Manifest.TransactionId == transactionId)
                    return new(planId, transactionId, current.Progress.Stage == StorageTransferStage.Completed
                        ? StorageRelocationRecoveryStatus.CompletedReservationsRetained
                        : current.Progress.IsMetadataCommitted ? StorageRelocationRecoveryStatus.CleanupPending : StorageRelocationRecoveryStatus.ResumeRequired,
                        exception is StorageRelocationComparisonUnavailableException ? "RELOCATION_TARGET_COMPARISON_UNAVAILABLE"
                            : exception is StorageRelocationCapacityException capacityFailure
                            ? capacityFailure.Reason == StorageRelocationCapacityFailure.Unavailable ? "RELOCATION_CAPACITY_UNAVAILABLE" : "RELOCATION_CAPACITY_INSUFFICIENT"
                            : "RELOCATION_RESUME_STOPPED");
            }
            catch (Exception reloadException) when (IsRecoverable(reloadException)) { }
            return new(planId, transactionId, StorageRelocationRecoveryStatus.OutcomeUnknown, "RELOCATION_OUTCOME_REQUIRES_RECONCILIATION");
        }
        // 已有提交响应，不再用可取消 Load 把永久成功误报为取消。
        return await RecoverJournalAsync(journal, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StorageRelocationRecoveryResult> RecoverAsync(PlanId planId, CancellationToken cancellationToken)
    {
        // 枚举快照不是权限；当前数据库完整日志验证失败必须向上传播。
        var journal = await store.LoadRelocationAsync(planId, cancellationToken).ConfigureAwait(false);
        if (journal is null) return new(planId, null, StorageRelocationRecoveryStatus.NotFound, null);
        return await RecoverJournalAsync(journal, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StorageRelocationRecoveryResult> RecoverJournalAsync(StorageRelocationJournal journal, CancellationToken cancellationToken)
    {
        StorageRelocationRecoveryResult Result(StorageRelocationRecoveryStatus status, string? diagnostic = null)
            => new(journal.Manifest.PlanId, journal.Manifest.TransactionId, status, diagnostic);
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
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // 不输出原始异常，避免把设备路径或其他本地信息带入诊断。
            return Result(StorageRelocationRecoveryStatus.CleanupPending, "RELOCATION_CLEANUP_REQUIRES_RECONCILIATION");
        }
    }

    private static bool IsRecoverable(Exception exception) => exception is IOException or UnauthorizedAccessException
        || exception is LocalStateRepositoryException and not LocalStateCorruptionException;
}
