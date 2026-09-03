using System.Collections.Immutable;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Application.StorageMaintenance;

// v1 durable payload 使用固定数值，新增阶段不得重排既有编码。
public enum StorageTransferStage { Prepared = 0, TargetsDurable = 1, MetadataCommitted = 2, Completed = 3 }
public enum StorageTransferArtifactStage { Pending = 0, Staged = 1, TargetDurable = 2, OldCopyAbsent = 3 }

/// <summary>仅供 transaction manifest 中真正需要 copy 的条目使用；原位验证项不进入清理集合。</summary>
public sealed record StorageTransferArtifact(ArchiveVersionId VersionId, Sha256Digest Integrity, long Length);

/// <summary>Infrastructure 产生的 native identity 必须带 provider/encoding version，不允许用 hash 代替。</summary>
public sealed record StorageObjectIdentity(string Provider, int EncodingVersion, string Value);

public sealed record StorageTransferProof(Guid TransactionId, PlanId PlanId, ArchiveVersionId VersionId,
    Sha256Digest Integrity, long Length, StorageObjectIdentity ObjectIdentity, bool DataDurable, bool NamespaceDurable);

public sealed record StorageTransferArtifactProgress(StorageTransferArtifact Artifact, StorageTransferArtifactStage Stage,
    StorageObjectIdentity? StagedIdentity);

/// <summary>
/// 迁移的纯进度内核，不执行 I/O，也不独立授权文件操作。持久适配器必须同时验证完整 manifest、root proof 与 CAS。
/// </summary>
public sealed class StorageTransferProgress
{
    private StorageTransferProgress(Guid transactionId, PlanId planId, StorageTransferStage stage,
        ImmutableArray<StorageTransferArtifactProgress> artifacts)
    {
        TransactionId = transactionId;
        PlanId = planId;
        Stage = stage;
        Artifacts = artifacts;
    }

    public Guid TransactionId { get; }
    public PlanId PlanId { get; }
    public StorageTransferStage Stage { get; }
    public ImmutableArray<StorageTransferArtifactProgress> Artifacts { get; }
    public bool IsMetadataCommitted => Stage is StorageTransferStage.MetadataCommitted or StorageTransferStage.Completed;

    public static StorageTransferProgress Prepare(Guid transactionId, PlanId planId, IEnumerable<StorageTransferArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        return Restore(transactionId, planId, StorageTransferStage.Prepared,
            artifacts.Select(x => new StorageTransferArtifactProgress(x, StorageTransferArtifactStage.Pending, null)));
    }

    public static StorageTransferProgress Restore(Guid transactionId, PlanId planId, StorageTransferStage stage,
        IEnumerable<StorageTransferArtifactProgress> artifacts)
    {
        if (transactionId == Guid.Empty || planId.Value == Guid.Empty) throw new ArgumentException("Transfer identity is required.");
        if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        ArgumentNullException.ThrowIfNull(artifacts);
        var entries = artifacts.ToImmutableArray();
        if (entries.Any(x => x is null || x.Artifact is null))
            throw new ArgumentException("Transfer entries cannot be null.", nameof(artifacts));
        if (entries.Select(x => x.Artifact.VersionId).Distinct().Count() != entries.Length)
            throw new ArgumentException("An ArchiveVersion may occur only once in a transfer.", nameof(artifacts));
        var identities = entries.Where(x => x.StagedIdentity is not null).Select(x => x.StagedIdentity).ToArray();
        if (identities.Distinct().Count() != identities.Length)
            throw new ArgumentException("Different artifacts cannot share a staged native object.", nameof(artifacts));
        foreach (var entry in entries)
        {
            var artifact = entry.Artifact;
            if (artifact.VersionId.Value == Guid.Empty || artifact.Integrity == default || artifact.Length < 0)
                throw new ArgumentException("Transfer artifact integrity and identity are required.", nameof(artifacts));
            if (!Enum.IsDefined(entry.Stage)) throw new ArgumentException("Unknown artifact stage.", nameof(artifacts));
            if (entry.Stage is StorageTransferArtifactStage.Pending)
            {
                if (entry.StagedIdentity is not null) throw new ArgumentException("Pending artifact cannot have staged identity.", nameof(artifacts));
            }
            else ValidateIdentity(entry.StagedIdentity);

            var valid = stage switch
            {
                StorageTransferStage.Prepared => entry.Stage is not StorageTransferArtifactStage.OldCopyAbsent,
                StorageTransferStage.TargetsDurable => entry.Stage is StorageTransferArtifactStage.TargetDurable,
                StorageTransferStage.MetadataCommitted => entry.Stage is StorageTransferArtifactStage.TargetDurable or StorageTransferArtifactStage.OldCopyAbsent,
                StorageTransferStage.Completed => entry.Stage is StorageTransferArtifactStage.OldCopyAbsent,
                _ => false,
            };
            if (!valid) throw new ArgumentException("Transfer and artifact stages are inconsistent.", nameof(artifacts));
        }
        return new(transactionId, planId, stage, entries);
    }

    public StorageTransferProgress RecordStaged(StorageTransferProof proof)
    {
        RequireStage(StorageTransferStage.Prepared);
        var index = ValidateProof(proof);
        if (Artifacts[index].Stage is not StorageTransferArtifactStage.Pending)
            throw new InvalidOperationException("Only a pending artifact can be staged.");
        if (Artifacts.Any(x => x.StagedIdentity == proof.ObjectIdentity))
            throw new InvalidOperationException("Staged native object already belongs to another artifact.");
        return Replace(index, Artifacts[index] with { Stage = StorageTransferArtifactStage.Staged, StagedIdentity = proof.ObjectIdentity });
    }

    public StorageTransferProgress RecordTargetDurable(StorageTransferProof proof)
    {
        RequireStage(StorageTransferStage.Prepared);
        var index = ValidateProof(proof);
        var entry = Artifacts[index];
        // 必须先持久记录 temp identity；rename 后沿用同一 native object，不能凭目标 hash 恢复 ownership。
        if (entry.Stage is not StorageTransferArtifactStage.Staged || entry.StagedIdentity != proof.ObjectIdentity)
            throw new InvalidOperationException("Target proof does not match the recorded staged object.");
        return Replace(index, entry with { Stage = StorageTransferArtifactStage.TargetDurable });
    }

    public StorageTransferProgress SealTargets()
    {
        RequireStage(StorageTransferStage.Prepared);
        if (Artifacts.Any(x => x.Stage is not StorageTransferArtifactStage.TargetDurable))
            throw new InvalidOperationException("Every target must be durable before metadata can switch.");
        return new(TransactionId, PlanId, StorageTransferStage.TargetsDurable, Artifacts);
    }

    /// <summary>仅在 repository 原子 metadata switch 成功的同一事务中保存该状态。</summary>
    public StorageTransferProgress MarkMetadataCommitted()
    {
        RequireStage(StorageTransferStage.TargetsDurable);
        return new(TransactionId, PlanId, StorageTransferStage.MetadataCommitted, Artifacts);
    }

    /// <summary>调用方须先按 manifest 中旧 identity 验证旧副本已 durable absent；新目标 identity 不授权旧副本删除。</summary>
    public StorageTransferProgress RecordOldCopyAbsent(ArchiveVersionId versionId)
    {
        RequireStage(StorageTransferStage.MetadataCommitted);
        var index = Find(versionId);
        if (Artifacts[index].Stage is not StorageTransferArtifactStage.TargetDurable)
            throw new InvalidOperationException("Old copy absence has already been recorded.");
        return Replace(index, Artifacts[index] with { Stage = StorageTransferArtifactStage.OldCopyAbsent });
    }

    public StorageTransferProgress Complete()
    {
        RequireStage(StorageTransferStage.MetadataCommitted);
        if (Artifacts.Any(x => x.Stage is not StorageTransferArtifactStage.OldCopyAbsent))
            throw new InvalidOperationException("Old copies still require reconciliation.");
        return new(TransactionId, PlanId, StorageTransferStage.Completed, Artifacts);
    }

    private int ValidateProof(StorageTransferProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (proof.TransactionId != TransactionId || proof.PlanId != PlanId)
            throw new ArgumentException("Proof belongs to another transaction or Plan.", nameof(proof));
        var index = Find(proof.VersionId);
        var artifact = Artifacts[index].Artifact;
        if (proof.Integrity != artifact.Integrity || proof.Length != artifact.Length || !proof.DataDurable || !proof.NamespaceDurable)
            throw new ArgumentException("Proof is incomplete or does not match the artifact.", nameof(proof));
        ValidateIdentity(proof.ObjectIdentity);
        return index;
    }

    private static void ValidateIdentity(StorageObjectIdentity? identity)
    {
        if (identity is null || string.IsNullOrWhiteSpace(identity.Provider) || identity.EncodingVersion < 1 || string.IsNullOrWhiteSpace(identity.Value))
            throw new ArgumentException("Versioned native object identity is required.", nameof(identity));
    }

    private int Find(ArchiveVersionId versionId)
    {
        for (var i = 0; i < Artifacts.Length; i++) if (Artifacts[i].Artifact.VersionId == versionId) return i;
        throw new ArgumentException("ArchiveVersion is not part of this transfer.", nameof(versionId));
    }

    private StorageTransferProgress Replace(int index, StorageTransferArtifactProgress entry)
        => new(TransactionId, PlanId, Stage, Artifacts.SetItem(index, entry));

    private void RequireStage(StorageTransferStage expected)
    {
        if (Stage != expected) throw new InvalidOperationException($"Transfer must be in {expected} stage.");
    }
}
