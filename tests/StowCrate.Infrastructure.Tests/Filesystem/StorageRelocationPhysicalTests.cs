using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Application.Publishing;
using StowCrate.Application.StorageMaintenance;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Infrastructure.Filesystem;

namespace StowCrate.Infrastructure.Tests.Filesystem;

public sealed class StorageRelocationPhysicalTests
{
    [Fact]
    public async Task TransfersOpaqueArchiveBytesWithoutOriginalInputsOrDecryption()
    {
        using var fixture = new Fixture();
        var original = Path.Combine(Path.GetDirectoryName(fixture.NewRoot)!, "original-input");
        Directory.CreateDirectory(original);
        await File.WriteAllTextAsync(Path.Combine(original, "document.txt"), "original source content");
        // 模拟原始输入盘不可访问；旧归档仍在线。仅操作 fixture 私有目录，不动真实用户输入。
        Directory.Move(original, original + ".disconnected");
        var journal = await PublishAllAsync(fixture.Journal);
        await new StorageRelocationPhysicalStore(new Barrier()).VerifyForCommitAsync(journal, default);
        Assert.False(Directory.Exists(original));
        // fixture 为不透明字节而非可解码归档：迁移不能偷偷要求格式解析或密码。
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Target));
        Assert.Equal(StorageTransferStage.TargetsDurable, journal.Progress.Stage);
    }

    [Fact]
    public async Task CopiesAndPublishesNestedUnicodeTargetWithoutMovingOldArchive()
    {
        using var fixture = new Fixture();
        var store = new StorageRelocationPhysicalStore(new Barrier());
        var staged = await store.StageAsync(fixture.Journal, fixture.Version, default);
        Assert.True(File.Exists(fixture.Temp));
        Assert.False(File.Exists(fixture.Target));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
        var journal = fixture.Staged(staged);
        var target = await store.PublishTargetAsync(journal, fixture.Version, default);
        Assert.Equal(staged.ObjectIdentity, target.ObjectIdentity);
        Assert.True(target.NamespaceDurable);
        Assert.False(File.Exists(fixture.Temp));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Target));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
    }

    [Fact]
    public async Task SameHashUnexpectedTargetIsNeverOverwrittenOrAdopted()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Target)!);
        await File.WriteAllBytesAsync(fixture.Target, fixture.Bytes);
        await Assert.ThrowsAsync<IOException>(() => new StorageRelocationPhysicalStore(new Barrier()).StageAsync(fixture.Journal, fixture.Version, default));
        Assert.False(File.Exists(fixture.Temp));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
    }

    [Fact]
    public async Task RenameThenBarrierFailureCanRecoverOnlyWithRecordedObjectIdentity()
    {
        using var fixture = new Fixture();
        var store = new StorageRelocationPhysicalStore(new Barrier());
        var staged = await store.StageAsync(fixture.Journal, fixture.Version, default);
        var journal = fixture.Staged(staged);
        await Assert.ThrowsAsync<IOException>(() => new StorageRelocationPhysicalStore(new Barrier(false)).PublishTargetAsync(journal, fixture.Version, default));
        Assert.True(File.Exists(fixture.Target));
        Assert.False(File.Exists(fixture.Temp));
        var restored = journal with { Progress = StorageTransferProgress.Restore(journal.Progress.TransactionId, journal.Progress.PlanId, journal.Progress.Stage, journal.Progress.Artifacts) };
        var proof = await new StorageRelocationPhysicalStore(new Barrier()).PublishTargetAsync(restored, fixture.Version, default);
        Assert.Equal(staged.ObjectIdentity, proof.ObjectIdentity);
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
    }

    [Fact]
    public async Task MatchingBytesWithDifferentNativeIdentityFailRecovery()
    {
        using var fixture = new Fixture();
        var store = new StorageRelocationPhysicalStore(new Barrier());
        var staged = await store.StageAsync(fixture.Journal, fixture.Version, default);
        // 保留原对象，避免文件系统立即复用 inode/file ID 影响 replacement fixture。
        File.Move(fixture.Temp, fixture.Temp + ".held");
        await File.WriteAllBytesAsync(fixture.Target, fixture.Bytes);
        await Assert.ThrowsAsync<IOException>(() => store.PublishTargetAsync(fixture.Staged(staged), fixture.Version, default));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
    }

    [Fact]
    public async Task RootReplacementFailsClosedBeforePublish()
    {
        using var fixture = new Fixture();
        var store = new StorageRelocationPhysicalStore(new Barrier());
        var staged = await store.StageAsync(fixture.Journal, fixture.Version, default);
        Directory.Move(fixture.NewRoot, fixture.NewRoot + ".held");
        Directory.CreateDirectory(fixture.NewRoot);
        await Assert.ThrowsAsync<IOException>(() => store.PublishTargetAsync(fixture.Staged(staged), fixture.Version, default));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
    }

    [Fact]
    public async Task MissingBarrierNeverReturnsStagedProofAndUnownedTempRemains()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Target)!);
        await Assert.ThrowsAsync<IOException>(() => new StorageRelocationPhysicalStore(new FailSecondBarrier()).StageAsync(fixture.Journal, fixture.Version, default));
        Assert.True(File.Exists(fixture.Temp));
        await Assert.ThrowsAsync<IOException>(() => new StorageRelocationPhysicalStore(new Barrier()).StageAsync(fixture.Journal, fixture.Version, default));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
    }

    [Fact]
    public async Task CorruptSourceAndPreCancelledOperationNeverPublish()
    {
        using var fixture = new Fixture();
        var store = new StorageRelocationPhysicalStore(new Barrier());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.StageAsync(fixture.Journal, fixture.Version, cancellation.Token));
        Assert.False(File.Exists(fixture.Temp));
        await File.WriteAllBytesAsync(fixture.Source, "wrong"u8.ToArray());
        await Assert.ThrowsAsync<IOException>(() => store.StageAsync(fixture.Journal, fixture.Version, default));
        Assert.False(File.Exists(fixture.Target));
    }

    [Fact]
    public async Task NativePlatformBarrierEitherProvidesProofOrFailsClosed()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Target)!);
        var capability = await new PlatformArchivePublishMetadataDurabilityBarrier().FlushDirectoryMetadataAsync(fixture.NewRoot, default);
        var store = new StorageRelocationPhysicalStore();
        if (!capability.BarrierCompleted)
        {
            await Assert.ThrowsAsync<IOException>(() => store.StageAsync(fixture.Journal, fixture.Version, default));
            Assert.False(File.Exists(fixture.Target));
        }
        else
        {
            var staged = await store.StageAsync(fixture.Journal, fixture.Version, default);
            Assert.True((await store.PublishTargetAsync(fixture.Staged(staged), fixture.Version, default)).NamespaceDurable);
        }
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
    }

    [Fact]
    public async Task RetryMustFlushExistingParentLeftByFailedAttempt()
    {
        using var fixture = new Fixture();
        var store = new StorageRelocationPhysicalStore(new Barrier(false));
        await Assert.ThrowsAsync<IOException>(() => store.StageAsync(fixture.Journal, fixture.Version, default));
        Assert.True(Directory.Exists(Path.GetDirectoryName(fixture.Target)));
        await Assert.ThrowsAsync<IOException>(() => store.StageAsync(fixture.Journal, fixture.Version, default));
        Assert.False(File.Exists(fixture.Temp));
        Assert.True((await new StorageRelocationPhysicalStore(new Barrier()).StageAsync(fixture.Journal, fixture.Version, default)).NamespaceDurable);
    }

    [Fact]
    public async Task CommitVerificationRequiresSealedCompleteSetAndPreservesProgress()
    {
        using var fixture = new Fixture();
        var store = new StorageRelocationPhysicalStore(new Barrier());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.VerifyForCommitAsync(fixture.Journal, default));
        var sealedJournal = await PublishAllAsync(fixture.Journal);
        await store.VerifyForCommitAsync(sealedJournal, default);
        Assert.Equal(StorageTransferStage.TargetsDurable, sealedJournal.Progress.Stage);
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
        var incomplete = sealedJournal with { Progress = StorageTransferProgress.Prepare(sealedJournal.Manifest.TransactionId, sealedJournal.Manifest.PlanId, []).SealTargets() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.VerifyForCommitAsync(incomplete, default));
    }

    [Theory]
    [InlineData("source-bytes")]
    [InlineData("target-bytes")]
    [InlineData("target-identity")]
    [InlineData("temp")]
    [InlineData("missing-parent")]
    public async Task CommitVerificationRejectsDriftAfterDurableRecord(string drift)
    {
        using var fixture = new Fixture();
        var journal = await PublishAllAsync(fixture.Journal);
        switch (drift)
        {
            case "source-bytes": await File.WriteAllBytesAsync(fixture.Source, new byte[fixture.Bytes.Length]); break;
            case "target-bytes": await File.WriteAllBytesAsync(fixture.Target, new byte[fixture.Bytes.Length]); break;
            case "target-identity":
                File.Move(fixture.Target, fixture.Target + ".held");
                await File.WriteAllBytesAsync(fixture.Target, fixture.Bytes);
                break;
            case "temp": await File.WriteAllBytesAsync(fixture.Temp, fixture.Bytes); break;
            case "missing-parent": Directory.Move(Path.GetDirectoryName(fixture.Target)!, Path.GetDirectoryName(fixture.Target)! + ".held"); break;
        }
        await Assert.ThrowsAnyAsync<IOException>(() => new StorageRelocationPhysicalStore(new Barrier()).VerifyForCommitAsync(journal, default));
        Assert.True(File.Exists(fixture.Source));
        if (drift == "missing-parent") Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.Target)));
    }

    [Fact]
    public async Task CommitVerificationRequiresBarrierAndHonorsCancellation()
    {
        using var fixture = new Fixture();
        var journal = await PublishAllAsync(fixture.Journal);
        await Assert.ThrowsAsync<IOException>(() => new StorageRelocationPhysicalStore(new Barrier(false)).VerifyForCommitAsync(journal, default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StorageRelocationPhysicalStore(new Barrier()).VerifyForCommitAsync(journal, cancellation.Token));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Target));
    }

    [Fact]
    public async Task CommitVerificationChecksEmptyRelocatedRootIdentity()
    {
        using var fixture = new Fixture();
        var original = fixture.Journal.Manifest;
        var manifest = new StorageRelocationManifest(original.TransactionId, original.PlanId, original.DeviceId,
            original.ExecutionSemanticDigest, original.Roots, []);
        var journal = new StorageRelocationJournal(manifest, StorageTransferProgress.Prepare(manifest.TransactionId, manifest.PlanId, []).SealTargets(), 2);
        var store = new StorageRelocationPhysicalStore(new Barrier());
        await store.VerifyForCommitAsync(journal, default);
        await Assert.ThrowsAsync<IOException>(() => new StorageRelocationPhysicalStore(new Barrier(false)).VerifyForCommitAsync(journal, default));
        Directory.Move(fixture.NewRoot, fixture.NewRoot + ".held");
        Directory.CreateDirectory(fixture.NewRoot);
        await Assert.ThrowsAsync<IOException>(() => store.VerifyForCommitAsync(journal, default));
    }

    [Fact]
    public async Task CommitVerificationChecksEveryArtifactInSet()
    {
        using var fixture = new Fixture();
        var original = fixture.Journal.Manifest;
        var version = new ArchiveVersionId(Guid.NewGuid());
        var relative = new RelativeStoragePath("second.7z");
        var source = Path.Combine(original.Roots[0].OldRoot.CanonicalPath, relative.Value);
        await File.WriteAllBytesAsync(source, fixture.Bytes);
        var second = new StorageRelocationEntry(new(Guid.NewGuid()), StorageRootKind.Current,
            new(version, Sha256Digest.Hash(fixture.Bytes), fixture.Bytes.Length), relative,
            StorageRelocationTempLayout.Create(original.TransactionId, version, relative), StorageRelocationPhysicalStore.InspectIdentity(source, false));
        var manifest = new StorageRelocationManifest(original.TransactionId, original.PlanId, original.DeviceId,
            original.ExecutionSemanticDigest, original.Roots, original.Entries.Add(second));
        var journal = await PublishAllAsync(new(manifest, StorageTransferProgress.Prepare(manifest.TransactionId, manifest.PlanId, manifest.Entries.Select(x => x.Artifact)), 1));
        var store = new StorageRelocationPhysicalStore(new Barrier());
        await store.VerifyForCommitAsync(journal, default);
        await File.WriteAllBytesAsync(Path.Combine(fixture.NewRoot, relative.Value), new byte[fixture.Bytes.Length]);
        await Assert.ThrowsAsync<IOException>(() => store.VerifyForCommitAsync(journal, default));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(source));
    }

    // 该 fixture 仅模拟 durable store 返回的连续状态，不宣称已接入真实 SQLite/物理迁移编排。
    private static async Task<StorageRelocationJournal> PublishAllAsync(StorageRelocationJournal journal)
    {
        var store = new StorageRelocationPhysicalStore(new Barrier());
        foreach (var entry in journal.Manifest.Entries)
        {
            var staged = await store.StageAsync(journal, entry.Artifact.VersionId, default);
            journal = journal with { Progress = journal.Progress.RecordStaged(staged), Revision = journal.Revision + 1 };
            var published = await store.PublishTargetAsync(journal, entry.Artifact.VersionId, default);
            journal = journal with { Progress = journal.Progress.RecordTargetDurable(published), Revision = journal.Revision + 1 };
        }
        return journal with { Progress = journal.Progress.SealTargets(), Revision = journal.Revision + 1 };
    }

    private sealed class FailSecondBarrier : IArchivePublishMetadataDurabilityBarrier
    {
        private int calls;
        public Task<PublishMetadataDurabilityProof> FlushDirectoryMetadataAsync(string destinationDirectory, CancellationToken cancellationToken)
            => Task.FromResult(new PublishMetadataDurabilityProof(++calls != 2, "injected-test-barrier"));
    }

    private sealed class Barrier(bool completed = true) : IArchivePublishMetadataDurabilityBarrier
    {
        public Task<PublishMetadataDurabilityProof> FlushDirectoryMetadataAsync(string destinationDirectory, CancellationToken cancellationToken)
            => Task.FromResult(new PublishMetadataDurabilityProof(completed, "injected-test-barrier"));
    }
    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo workspace = Directory.CreateTempSubdirectory("stowcrate-relocation-physical-");
        public byte[] Bytes { get; } = "verified archive bytes"u8.ToArray();
        public ArchiveVersionId Version { get; } = new(Guid.NewGuid());
        public StorageRelocationJournal Journal { get; }
        public string NewRoot { get; }
        public string Source { get; }
        public string Target { get; }
        public string Temp { get; }
        public Fixture()
        {
            var oldRoot = Directory.CreateDirectory(Path.Combine(workspace.FullName, "old")).FullName;
            NewRoot = Directory.CreateDirectory(Path.Combine(workspace.FullName, "new")).FullName;
            var relative = new RelativeStoragePath("资料/单元.7z");
            Source = Path.Combine(oldRoot, "资料", "单元.7z"); Target = Path.Combine(NewRoot, "资料", "单元.7z");
            Directory.CreateDirectory(Path.GetDirectoryName(Source)!); File.WriteAllBytes(Source, Bytes);
            var transaction = Guid.NewGuid(); var plan = new PlanId(Guid.NewGuid()); var hash = Sha256Digest.Hash(Bytes);
            var temp = StorageRelocationTempLayout.Create(transaction, Version, relative);
            Temp = Path.Combine(NewRoot, temp.Value.Replace('/', Path.DirectorySeparatorChar));
            var manifest = new StorageRelocationManifest(transaction, plan, new(Guid.NewGuid()), hash,
                [new(StorageRootKind.Current, Physical(oldRoot), Physical(NewRoot), StorageRelocationPhysicalStore.InspectIdentity(oldRoot, true), StorageRelocationPhysicalStore.InspectIdentity(NewRoot, true))],
                [new(new(Guid.NewGuid()), StorageRootKind.Current, new(Version, hash, Bytes.Length), relative, temp, StorageRelocationPhysicalStore.InspectIdentity(Source, false))]);
            Journal = new(manifest, StorageTransferProgress.Prepare(transaction, plan, manifest.Entries.Select(x => x.Artifact)), 1);
        }
        public StorageRelocationJournal Staged(StorageTransferProof proof) => Journal with { Progress = Journal.Progress.RecordStaged(proof), Revision = 2 };
        private static ResolvedPhysicalPath Physical(string path) => new(path, (OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path).Replace('\\', '/'));
        public void Dispose() => workspace.Delete(recursive: true);
    }
}
