using StowCrate.Core.ChangeDetection;

namespace StowCrate.Application.StorageMaintenance;

/// <summary>调用方须从 durable repository 加载 journal；返回 proof 后必须先持久化才能执行下一步。</summary>
public interface IStorageRelocationPhysicalStore
{
    Task<StorageTransferProof> StageAsync(StorageRelocationJournal journal, ArchiveVersionId versionId, CancellationToken cancellationToken);
    Task<StorageTransferProof> PublishTargetAsync(StorageRelocationJournal journal, ArchiveVersionId versionId, CancellationToken cancellationToken);

    /// <summary>提交前重验整个冻结集合；不改变 journal，不签发可持久复用的提交授权。调用后仍须事务内 CAS 与语义重验。</summary>
    Task VerifyForCommitAsync(StorageRelocationJournal journal, CancellationToken cancellationToken);
}
