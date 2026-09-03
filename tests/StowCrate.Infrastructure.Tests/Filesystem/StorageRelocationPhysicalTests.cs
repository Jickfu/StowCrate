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
