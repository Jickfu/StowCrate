using StowCrate.Application.LocalState;

namespace StowCrate.Application.StorageMaintenance;

/// <summary>显式启动仅创建 PREPARED 日志；不复制、自动恢复或重放结果不明的 Begin。</summary>
public sealed class StorageRelocationBeginWorkflow(StorageRelocationInspectionWorkflow inspection, IStorageRelocationJournalStore journals)
{
    public async Task<StorageRelocationJournal> BeginAsync(StorageRelocationInventoryRequest request, Guid transactionId,
        CancellationToken cancellationToken)
    {
        var prepared = await inspection.InspectForBeginAsync(request, transactionId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // 成功返回之后不再检查 caller cancellation，避免把已提交日志误报为未启动。
            return await journals.BeginRelocationAsync(prepared.Manifest, prepared.Configuration, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException
            || exception is LocalStateRepositoryException and not LocalStateCorruptionException)
        {
            // 仓储调用已发出，异常不证明事务未提交；保留原 transaction ID 供显式查询，绝不自动重试。
            throw new StorageRelocationBeginOutcomeUnknownException(transactionId, exception);
        }
    }
}

public sealed class StorageRelocationBeginOutcomeUnknownException(Guid transactionId, Exception innerException)
    : IOException("迁移启动结果未确认，请查询原事务日志后再决定恢复；不要重复启动。", innerException)
{
    public Guid TransactionId { get; } = transactionId;
    public string DiagnosticCode { get; } = "RELOCATION_BEGIN_OUTCOME_UNKNOWN";
}
