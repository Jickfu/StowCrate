using StowCrate.Application.StorageMaintenance;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Application.Tests;

public sealed class StorageTransferProgressTests
{
    private static readonly PlanId Plan = new(Guid.NewGuid());
    private static readonly Guid Transaction = Guid.NewGuid();
    private static readonly StorageObjectIdentity Identity = new("fixture-native", 1, "volume:object");
    private static Sha256Digest Hash(string value) => CanonicalFingerprintEncodingV1.Encode("transfer-test", writer => writer.Utf8(1, value));
    private static StorageTransferArtifact Artifact() => new(new(Guid.NewGuid()), Hash("bytes"), 42);
    private static StorageTransferProof Proof(StorageTransferArtifact artifact) => new(Transaction, Plan, artifact.VersionId, artifact.Integrity, artifact.Length,
        Identity with { Value = artifact.VersionId.Value.ToString("D") }, true, true);

    [Fact]
    public void MultipleTargetsMustAllBeDurableBeforeMetadataSwitchAndCleanup()
    {
        var first = Artifact(); var second = Artifact();
        var original = StorageTransferProgress.Prepare(Transaction, Plan, [first, second]);
        var state = original.RecordStaged(Proof(first)).RecordTargetDurable(Proof(first));
        Assert.Throws<InvalidOperationException>(() => state.SealTargets());
        Assert.Throws<InvalidOperationException>(() => state.MarkMetadataCommitted());
        Assert.Throws<InvalidOperationException>(() => state.RecordOldCopyAbsent(first.VersionId));
        state = state.RecordStaged(Proof(second)).RecordTargetDurable(Proof(second)).SealTargets();
        Assert.False(state.IsMetadataCommitted);
        state = state.MarkMetadataCommitted().RecordOldCopyAbsent(first.VersionId);
        Assert.True(state.IsMetadataCommitted);
        Assert.Throws<InvalidOperationException>(() => state.Complete());
        state = state.RecordOldCopyAbsent(second.VersionId).Complete();
        Assert.Equal(StorageTransferStage.Completed, state.Stage);
        Assert.True(state.IsMetadataCommitted);
        Assert.All(original.Artifacts, x => Assert.Equal(StorageTransferArtifactStage.Pending, x.Stage));
        Assert.Equal(first, state.Artifacts[0].Artifact);
        Assert.Equal(second, state.Artifacts[1].Artifact);
        Assert.Throws<InvalidOperationException>(() => state.RecordStaged(Proof(first)));
    }

    [Theory]
    [InlineData("transaction")]
    [InlineData("plan")]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("data")]
    [InlineData("namespace")]
    [InlineData("identity")]
    [InlineData("provider")]
    [InlineData("encoding")]
    public void IncompleteOrForeignProofCannotAdvanceProgress(string change)
    {
        var artifact = Artifact(); var proof = Proof(artifact);
        proof = change switch
        {
            "transaction" => proof with { TransactionId = Guid.NewGuid() },
            "plan" => proof with { PlanId = new(Guid.NewGuid()) },
            "version" => proof with { VersionId = new(Guid.NewGuid()) },
            "hash" => proof with { Integrity = Hash("other") },
            "length" => proof with { Length = 43 },
            "data" => proof with { DataDurable = false },
            "namespace" => proof with { NamespaceDurable = false },
            "identity" => proof with { ObjectIdentity = Identity with { Value = "" } },
            "provider" => proof with { ObjectIdentity = Identity with { Provider = "" } },
            _ => proof with { ObjectIdentity = Identity with { EncodingVersion = 0 } },
        };
        var state = StorageTransferProgress.Prepare(Transaction, Plan, [artifact]);
        Assert.Throws<ArgumentException>(() => state.RecordStaged(proof));
        state = state.RecordStaged(Proof(artifact));
        Assert.Throws<ArgumentException>(() => state.RecordTargetDurable(proof));
    }

    [Fact]
    public void TargetMustHavePreviouslyRecordedIdenticalNativeIdentity()
    {
        var artifact = Artifact(); var proof = Proof(artifact);
        var state = StorageTransferProgress.Prepare(Transaction, Plan, [artifact]);
        Assert.Throws<InvalidOperationException>(() => state.RecordTargetDurable(proof));
        state = state.RecordStaged(proof);
        Assert.Throws<InvalidOperationException>(() => state.RecordStaged(proof));
        Assert.Throws<InvalidOperationException>(() => state.RecordTargetDurable(proof with { ObjectIdentity = Identity with { Value = "replaced" } }));
        state = state.RecordTargetDurable(proof);
        Assert.Throws<InvalidOperationException>(() => state.RecordTargetDurable(proof));
    }

    [Fact]
    public void RestoreRejectsEveryInconsistentStageCombination()
    {
        var artifact = Artifact();
        foreach (var stage in Enum.GetValues<StorageTransferStage>())
        foreach (var entryStage in Enum.GetValues<StorageTransferArtifactStage>())
        {
            var entry = new StorageTransferArtifactProgress(artifact, entryStage,
                entryStage == StorageTransferArtifactStage.Pending ? null : Identity);
            var valid = stage switch
            {
                StorageTransferStage.Prepared => entryStage != StorageTransferArtifactStage.OldCopyAbsent,
                StorageTransferStage.TargetsDurable => entryStage == StorageTransferArtifactStage.TargetDurable,
                StorageTransferStage.MetadataCommitted => entryStage is StorageTransferArtifactStage.TargetDurable or StorageTransferArtifactStage.OldCopyAbsent,
                StorageTransferStage.Completed => entryStage == StorageTransferArtifactStage.OldCopyAbsent,
                _ => false,
            };
            if (valid) Assert.Equal(stage, StorageTransferProgress.Restore(Transaction, Plan, stage, [entry]).Stage);
            else Assert.Throws<ArgumentException>(() => StorageTransferProgress.Restore(Transaction, Plan, stage, [entry]));
        }
    }

    [Fact]
    public void SnapshotRestorationPreservesRecordedIdentityAndCannotSkipCleanup()
    {
        var artifact = Artifact();
        var state = StorageTransferProgress.Prepare(Transaction, Plan, [artifact]).RecordStaged(Proof(artifact));
        var restored = StorageTransferProgress.Restore(state.TransactionId, state.PlanId, state.Stage, state.Artifacts);
        restored = restored.RecordTargetDurable(Proof(artifact)).SealTargets().MarkMetadataCommitted();
        Assert.Throws<InvalidOperationException>(() => restored.Complete());
        restored = restored.RecordOldCopyAbsent(artifact.VersionId);
        Assert.Throws<InvalidOperationException>(() => restored.RecordOldCopyAbsent(artifact.VersionId));
        Assert.Equal(StorageTransferStage.Completed, restored.Complete().Stage);
    }

    [Fact]
    public void InvalidManifestAndUnknownStagesFailClosed()
    {
        var artifact = Artifact();
        Assert.Throws<ArgumentException>(() => StorageTransferProgress.Prepare(Transaction, Plan, [artifact, artifact]));
        Assert.Throws<ArgumentException>(() => StorageTransferProgress.Prepare(Guid.Empty, Plan, [artifact]));
        Assert.Throws<ArgumentException>(() => StorageTransferProgress.Prepare(Transaction, default, [artifact]));
        Assert.Throws<ArgumentException>(() => StorageTransferProgress.Prepare(Transaction, Plan, [artifact with { VersionId = default }]));
        Assert.Throws<ArgumentException>(() => StorageTransferProgress.Prepare(Transaction, Plan, [artifact with { Integrity = default }]));
        Assert.Throws<ArgumentException>(() => StorageTransferProgress.Prepare(Transaction, Plan, [artifact with { Length = -1 }]));
        Assert.Throws<ArgumentOutOfRangeException>(() => StorageTransferProgress.Restore(Transaction, Plan, (StorageTransferStage)99, []));
        Assert.Throws<ArgumentException>(() => StorageTransferProgress.Restore(Transaction, Plan, StorageTransferStage.Prepared, [new(artifact, (StorageTransferArtifactStage)99, Identity)]));
        Assert.Throws<ArgumentException>(() => StorageTransferProgress.Restore(Transaction, Plan, StorageTransferStage.Prepared, [new(artifact, StorageTransferArtifactStage.Staged, null)]));
        Assert.Throws<ArgumentException>(() => StorageTransferProgress.Restore(Transaction, Plan, StorageTransferStage.Prepared, [new(artifact, StorageTransferArtifactStage.Pending, Identity)]));
        Assert.Throws<ArgumentException>(() => StorageTransferProgress.Restore(Transaction, Plan, StorageTransferStage.Prepared, [null!]));
        Assert.Throws<ArgumentException>(() => StorageTransferProgress.Prepare(Transaction, Plan, [null!]));
    }

    [Fact]
    public void TwoArtifactsCannotClaimTheSamePhysicalObjectEvenWithMatchingBytes()
    {
        var first = Artifact(); var second = Artifact();
        var proof = Proof(first);
        var state = StorageTransferProgress.Prepare(Transaction, Plan, [first, second]).RecordStaged(proof);
        Assert.Throws<InvalidOperationException>(() => state.RecordStaged(Proof(second) with { ObjectIdentity = proof.ObjectIdentity }));
        Assert.Throws<ArgumentException>(() => StorageTransferProgress.Restore(Transaction, Plan, StorageTransferStage.Prepared,
            [new(first, StorageTransferArtifactStage.Staged, proof.ObjectIdentity), new(second, StorageTransferArtifactStage.Staged, proof.ObjectIdentity)]));
    }

    [Fact]
    public void EmptyCopySetStillRequiresExplicitMetadataCommit()
    {
        // 空 root 的迁移没有 copy 项，root/metadata 验证仍由完整 journal workflow 负责。
        var state = StorageTransferProgress.Prepare(Transaction, Plan, []);
        Assert.Throws<InvalidOperationException>(() => state.Complete());
        Assert.Equal(StorageTransferStage.Completed, state.SealTargets().MarkMetadataCommitted().Complete().Stage);
    }
}
