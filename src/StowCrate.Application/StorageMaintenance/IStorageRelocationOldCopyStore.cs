using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Application.StorageMaintenance;

/// <summary>仅表示已提交 journal 的 exact old path 已 durable absent；不独立授权 metadata mutation 或释放 reservation。</summary>
public sealed record StorageRelocationOldCopyAbsenceProof(Guid TransactionId, PlanId PlanId, long JournalRevision,
    StorageTransferArtifact Artifact, StorageObjectIdentity OldRootIdentity, StorageObjectIdentity OldIdentity,
    StorageObjectIdentity TargetIdentity);

public interface IStorageRelocationOldCopyStore
{
    /// <summary>调用方须提供从 durable repository 加载且 metadata 已提交的日志。禁止构造内存进度冒充删除权限。</summary>
    Task<StorageRelocationOldCopyAbsenceProof> RemoveOldCopyAsync(StorageRelocationJournal journal,
        ArchiveVersionId versionId, CancellationToken cancellationToken);
}
