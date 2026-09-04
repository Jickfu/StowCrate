using StowCrate.Application.LocalState;
using StowCrate.Core.BackupPlans;

namespace StowCrate.Application.StorageMaintenance;

public enum StorageRelocationCompactionStatus { NotFound, NotReady, AdapterUnavailable, Compacted, Retained, OutcomeUnknown }

public sealed record StorageRelocationCompactionResult(PlanId PlanId, Guid TransactionId, long ExpectedRevision,
    StorageRelocationCompactionStatus Status, string? Diagnostic);

/// <summary>显式释放已完成迁移的日志和 reservation；不自动恢复、不删除归档、不重试不明确的写入。</summary>
public sealed class StorageRelocationCompactionWorkflow(IStorageRelocationJournalStore store, IStorageRelocationCompletionProbe? physical)
{
    public async Task<StorageRelocationCompactionResult> CompactAsync(PlanId planId, Guid transactionId,
        long expectedRevision, CancellationToken cancellationToken)
    {
        if (planId.Value == Guid.Empty) throw new ArgumentException("Plan identity is required.", nameof(planId));
        if (transactionId == Guid.Empty) throw new ArgumentException("Transaction identity is required.", nameof(transactionId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRevision);
        cancellationToken.ThrowIfCancellationRequested();
        var journal = await store.LoadRelocationAsync(planId, cancellationToken).ConfigureAwait(false);
        if (journal is null) return Result(StorageRelocationCompactionStatus.NotFound, "RELOCATION_COMPACTION_NOT_FOUND");
        if (journal.Manifest.PlanId != planId || journal.Manifest.TransactionId != transactionId || journal.Revision != expectedRevision)
            throw new LocalStateConcurrencyException("Selected relocation transaction or revision changed.");
        if (journal.Progress.Stage != StorageTransferStage.Completed)
            return Result(StorageRelocationCompactionStatus.NotReady, "RELOCATION_COMPACTION_NOT_READY");
        if (physical is null)
            return Result(StorageRelocationCompactionStatus.AdapterUnavailable, "RELOCATION_COMPACTION_ADAPTER_UNAVAILABLE");
        try
        {
            // 仓储事务重新加载完整日志、核验物理状态并原子释放；此处的读取快照不能独立授权释放。
            await store.CompactRelocationAsync(transactionId, expectedRevision, physical, cancellationToken).ConfigureAwait(false);
            // 已取得提交成功响应，后续 caller cancellation 不能把本次完成改报为取消。
            return Result(StorageRelocationCompactionStatus.Compacted);
        }
        catch (Exception exception) when (IsRecoverable(exception)
            || exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            try
            {
                var current = await store.LoadRelocationAsync(planId, CancellationToken.None).ConfigureAwait(false);
                if (current is not null && current.Manifest.PlanId == planId && current.Manifest.TransactionId == transactionId
                    && current.Revision == expectedRevision && current.Progress.Stage == StorageTransferStage.Completed)
                    return Result(StorageRelocationCompactionStatus.Retained, exception is OperationCanceledException
                        ? "RELOCATION_COMPACTION_CANCELLED" : "RELOCATION_COMPACTION_REQUIRES_RECONCILIATION");
            }
            catch (Exception reloadException) when (IsRecoverable(reloadException)) { }
            // 日志缺失可能是提交响应丢失或并发操作，不猜测本次动作成功，不补发清理动作。
            return Result(StorageRelocationCompactionStatus.OutcomeUnknown, "RELOCATION_COMPACTION_OUTCOME_UNKNOWN");
        }

        StorageRelocationCompactionResult Result(StorageRelocationCompactionStatus status, string? diagnostic = null)
            => new(planId, transactionId, expectedRevision, status, diagnostic);
    }

    private static bool IsRecoverable(Exception exception) => exception is IOException or UnauthorizedAccessException
        || exception is LocalStateRepositoryException and not LocalStateCorruptionException;
}
