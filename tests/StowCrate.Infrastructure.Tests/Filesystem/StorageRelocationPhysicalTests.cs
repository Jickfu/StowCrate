using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Application.Publishing;
using StowCrate.Application.StorageMaintenance;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Infrastructure.Filesystem;

namespace StowCrate.Infrastructure.Tests.Filesystem;

public sealed partial class StorageRelocationPhysicalTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("occupied-temp")]
    [InlineData("planned-collision")]
    [InlineData("missing-entry")]
    public async Task ObservedTargetCheckUsesExactTransactionTemporaryPath(string scenario)
    {
        using var fixture = new Fixture();
        var physical = new StorageRelocationPhysicalStore();
        var observed = await physical.ObserveInventoryAsync(Inventory(fixture), default);
        var transaction = fixture.Journal.Manifest.TransactionId;
        if (scenario == "occupied-temp")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Temp)!);
            await File.WriteAllBytesAsync(fixture.Temp, fixture.Bytes);
        }
        if (scenario == "planned-collision")
        {
            var placement = observed.Inventory.Entries[0] with
            {
                Artifact = observed.Inventory.Entries[0].Artifact with { VersionId = new(Guid.NewGuid()) },
                RelativePath = fixture.Journal.Manifest.Entries[0].TempRelativePath
            };
            observed = observed with
            {
                Inventory = observed.Inventory with { Entries = observed.Inventory.Entries.Add(placement) },
                Entries = observed.Entries.Add(new(placement, observed.Entries[0].Identity))
            };
        }
        if (scenario == "missing-entry") observed = observed with { Entries = [] };
        var before = Directory.GetFileSystemEntries(fixture.NewRoot, "*", SearchOption.AllDirectories);
        if (scenario == "none") await physical.VerifyUnoccupiedTargetsAsync(observed, transaction, default);
        else await Assert.ThrowsAnyAsync<Exception>(() => physical.VerifyUnoccupiedTargetsAsync(observed, transaction, default));
        Assert.Equal(before, Directory.GetFileSystemEntries(fixture.NewRoot, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterPendingTargetOrTempBlocksFirstCopyBeforeAnyWrites(bool temporary)
    {
        using var fixture = new Fixture();
        var original = fixture.Journal.Manifest;
        var version = new ArchiveVersionId(Guid.NewGuid());
        var relative = new RelativeStoragePath("second.7z");
        var temp = StorageRelocationTempLayout.Create(original.TransactionId, version, relative);
        var source = Path.Combine(original.Roots[0].OldRoot.CanonicalPath, relative.Value);
        await File.WriteAllBytesAsync(source, fixture.Bytes);
        var second = new StorageRelocationEntry(new(Guid.NewGuid()), StorageRootKind.Current,
            new(version, Sha256Digest.Hash(fixture.Bytes), fixture.Bytes.Length), relative, temp,
            StorageRelocationPhysicalStore.InspectIdentity(source, false));
        var manifest = new StorageRelocationManifest(original.TransactionId, original.PlanId, original.DeviceId,
            original.ExecutionSemanticDigest, original.Roots, [original.Entries[0], second]);
        var journal = new StorageRelocationJournal(manifest,
            StorageTransferProgress.Prepare(manifest.TransactionId, manifest.PlanId, manifest.Entries.Select(x => x.Artifact)), 1);
        var occupied = Path.Combine(fixture.NewRoot, temporary ? temp.Value : relative.Value);
        await File.WriteAllBytesAsync(occupied, fixture.Bytes);
        await Assert.ThrowsAsync<IOException>(() => new StorageRelocationPhysicalStore(new Barrier()).StageAsync(journal, fixture.Version, default));
        Assert.Equal(new[] { occupied }, Directory.GetFileSystemEntries(fixture.NewRoot));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(occupied));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
        Assert.False(File.Exists(fixture.Temp));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("temp-file")]
    [InlineData("temp-directory")]
    [InlineData("target")]
    [InlineData("root-drift")]
    [InlineData("cancel")]
    public async Task TargetNamespaceProbeIsReadOnlyAndNeverAdoptsExistingEntries(string scenario)
    {
        using var fixture = new Fixture();
        if (scenario is "temp-file" or "temp-directory" or "target")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Target)!);
            if (scenario == "temp-directory") Directory.CreateDirectory(fixture.Temp);
            else await File.WriteAllBytesAsync(scenario == "target" ? fixture.Target : fixture.Temp, fixture.Bytes);
        }
        if (scenario == "root-drift")
        {
            Directory.Move(fixture.NewRoot, fixture.NewRoot + ".held");
            Directory.CreateDirectory(fixture.NewRoot);
        }
        var before = Directory.GetFileSystemEntries(fixture.NewRoot, "*", SearchOption.AllDirectories);
        using var cancellation = new CancellationTokenSource();
        if (scenario == "cancel") cancellation.Cancel();
        var probe = new StorageRelocationPhysicalStore(new Barrier(false));
        if (scenario == "none") await probe.VerifyUnoccupiedTargetsAsync(fixture.Journal.Manifest, default);
        else await Assert.ThrowsAnyAsync<Exception>(() => probe.VerifyUnoccupiedTargetsAsync(fixture.Journal.Manifest, cancellation.Token));
        Assert.Equal(before, Directory.GetFileSystemEntries(fixture.NewRoot, "*", SearchOption.AllDirectories));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
    }

    [Fact]
    public async Task PendingNamespaceCheckDoesNotRejectAlreadyStagedOwnedTemporaryFile()
    {
        using var fixture = new Fixture();
        var physical = new StorageRelocationPhysicalStore(new Barrier());
        var staged = await physical.StageAsync(fixture.Journal, fixture.Version, default);
        var original = fixture.Journal.Manifest;
        var version = new ArchiveVersionId(Guid.NewGuid());
        var relative = new RelativeStoragePath("second.7z");
        var source = Path.Combine(original.Roots[0].OldRoot.CanonicalPath, relative.Value);
        await File.WriteAllBytesAsync(source, fixture.Bytes);
        var second = new StorageRelocationEntry(new(Guid.NewGuid()), StorageRootKind.Current,
            new(version, Sha256Digest.Hash(fixture.Bytes), fixture.Bytes.Length), relative,
            StorageRelocationTempLayout.Create(original.TransactionId, version, relative), StorageRelocationPhysicalStore.InspectIdentity(source, false));
        var manifest = new StorageRelocationManifest(original.TransactionId, original.PlanId, original.DeviceId,
            original.ExecutionSemanticDigest, original.Roots, [original.Entries[0], second]);
        var progress = StorageTransferProgress.Prepare(manifest.TransactionId, manifest.PlanId, manifest.Entries.Select(x => x.Artifact)).RecordStaged(staged);
        var next = await physical.StageAsync(new(manifest, progress, 2), version, default);
        Assert.Equal(version, next.VersionId);
        Assert.Equal(staged.ObjectIdentity, StorageRelocationPhysicalStore.InspectIdentity(fixture.Temp, false));
        // 已有 staged ownership 继续交给 PublishTarget；全量初次 probe 则仍拒绝已有 temp。
        await Assert.ThrowsAsync<IOException>(() => physical.VerifyUnoccupiedTargetsAsync(fixture.Journal.Manifest, default));
        await physical.PublishTargetAsync(fixture.Staged(staged), fixture.Version, default);
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Target));
    }

    [Theory]
    [InlineData(StorageRootKind.Current, false)]
    [InlineData(StorageRootKind.History, false)]
    [InlineData(StorageRootKind.Current, true)]
    public async Task MissingTargetRootRequestsUserCreationWithoutWriting(StorageRootKind kind, bool missingParent)
    {
        using var fixture = new Fixture();
        Directory.Move(fixture.NewRoot, fixture.NewRoot + ".held");
        var inventory = Inventory(fixture);
        var target = missingParent ? Path.Combine(fixture.NewRoot, "child") : fixture.NewRoot;
        inventory = inventory with
        {
            Roots = [inventory.Roots[0] with { Kind = kind, NewRoot = new(target, target.Replace('\\', '/')) }],
            Entries = [inventory.Entries[0] with { RootKind = kind }]
        };
        var error = await Assert.ThrowsAsync<StorageRelocationTargetRootMissingException>(() =>
            new StorageRelocationPhysicalStore().ObserveInventoryAsync(inventory, default));
        Assert.Equal(kind, error.RootKind);
        Assert.Equal("RELOCATION_TARGET_ROOT_MISSING", error.DiagnosticCode);
        Assert.Contains("请先创建目录", error.Message);
        Assert.DoesNotContain(target, error.Message);
        Assert.False(Directory.Exists(fixture.NewRoot));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileOccupyingTargetRootOrParentIsNotReportedAsMissing(bool parentFile)
    {
        using var fixture = new Fixture();
        Directory.Move(fixture.NewRoot, fixture.NewRoot + ".held");
        await File.WriteAllTextAsync(fixture.NewRoot, "keep");
        var inventory = Inventory(fixture);
        if (parentFile)
        {
            var path = Path.Combine(fixture.NewRoot, "child");
            inventory = inventory with { Roots = [inventory.Roots[0] with { NewRoot = new(path, path.Replace('\\', '/')) }] };
        }
        var error = await Assert.ThrowsAnyAsync<IOException>(() => new StorageRelocationPhysicalStore().ObserveInventoryAsync(inventory, default));
        Assert.IsNotType<StorageRelocationTargetRootMissingException>(error);
        Assert.Equal("keep", await File.ReadAllTextAsync(fixture.NewRoot));
    }

    private static StorageRelocationInventory Inventory(Fixture fixture)
    {
        var manifest = fixture.Journal.Manifest;
        return new(manifest.PlanId, manifest.DeviceId,
            [.. manifest.Roots.Select(x => new StorageRelocationRootPaths(x.Kind, x.OldRoot, x.NewRoot))],
            [.. manifest.Entries.Select(x => new StorageRelocationPlacement(x.UnitId, x.RootKind, x.Artifact, x.RelativePath))]);
    }

    [Fact]
    public async Task PhysicalInventoryChecksOpaqueBytesAndCapacityWithoutCreatingParents()
    {
        using var fixture = new Fixture();
        var unknown = Path.Combine(fixture.NewRoot, "unknown.txt");
        await File.WriteAllTextAsync(unknown, "keep");
        var observed = await new StorageRelocationPhysicalStore(new Barrier(false), new CapacityProbe(1000))
            .ObserveInventoryAsync(Inventory(fixture), default);
        Assert.Equal(fixture.Journal.Manifest.Entries[0].OldIdentity, Assert.Single(observed.Entries).Identity);
        Assert.Equal(fixture.Bytes.Length, Assert.Single(observed.Capacity).RequiredBytes);
        Assert.Equal(new[] { unknown }, Directory.GetFileSystemEntries(fixture.NewRoot));
        Assert.Equal("keep", await File.ReadAllTextAsync(unknown));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("missing")]
    [InlineData("target")]
    [InlineData("parent-file")]
    [InlineData("capacity-unknown")]
    [InlineData("capacity-insufficient")]
    [InlineData("cancel")]
    public async Task PhysicalInventoryRejectsUnsafeStateWithoutWrites(string failure)
    {
        using var fixture = new Fixture();
        if (failure == "corrupt") await File.WriteAllTextAsync(fixture.Source, "corrupt");
        if (failure == "missing") File.Move(fixture.Source, fixture.Source + ".held");
        if (failure == "target")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Target)!);
            await File.WriteAllBytesAsync(fixture.Target, fixture.Bytes);
        }
        if (failure == "parent-file") await File.WriteAllTextAsync(Path.GetDirectoryName(fixture.Target)!, "keep");
        var before = Directory.GetFileSystemEntries(fixture.NewRoot, "*", SearchOption.AllDirectories);
        using var cancellation = new CancellationTokenSource();
        if (failure == "cancel") cancellation.Cancel();
        var physical = new StorageRelocationPhysicalStore(new Barrier(), new CapacityProbe(failure switch
        {
            "capacity-unknown" => null,
            "capacity-insufficient" => 0,
            _ => 1000
        }));
        await Assert.ThrowsAnyAsync<Exception>(() => physical.ObserveInventoryAsync(Inventory(fixture), cancellation.Token));
        Assert.Equal(before, Directory.GetFileSystemEntries(fixture.NewRoot, "*", SearchOption.AllDirectories));
        Assert.False(File.Exists(fixture.Temp));
        if (failure == "target") Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Target));
    }

    [Theory]
    [InlineData("old-root")]
    [InlineData("new-root")]
    [InlineData("source")]
    [InlineData("target")]
    public async Task PhysicalInventoryRechecksWholeSetAfterCapacityIo(string drift)
    {
        using var fixture = new Fixture();
        var inventory = Inventory(fixture);
        var physical = new StorageRelocationPhysicalStore(new Barrier(), new MutatingCapacityProbe(() =>
        {
            if (drift == "source")
            {
                File.Move(fixture.Source, fixture.Source + ".held");
                File.WriteAllBytes(fixture.Source, fixture.Bytes);
            }
            else if (drift == "target")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fixture.Target)!);
                File.WriteAllBytes(fixture.Target, fixture.Bytes);
            }
            else
            {
                var path = drift == "old-root" ? inventory.Roots[0].OldRoot.CanonicalPath : fixture.NewRoot;
                Directory.Move(path, path + ".held");
                Directory.CreateDirectory(path);
            }
        }));
        if (drift == "target")
            await Assert.ThrowsAsync<StorageRelocationCapacityException>(() => physical.ObserveInventoryAsync(inventory, default));
        else await Assert.ThrowsAsync<IOException>(() => physical.ObserveInventoryAsync(inventory, default));
        Assert.False(File.Exists(fixture.Temp));
    }

    [Fact]
    public async Task EmptyPhysicalInventoryStillChecksOldRootAndCapacity()
    {
        using var fixture = new Fixture();
        var inventory = Inventory(fixture) with { Entries = [] };
        var physical = new StorageRelocationPhysicalStore(new Barrier(), new CapacityProbe(1000));
        Assert.Empty((await physical.ObserveInventoryAsync(inventory, default)).Entries);
        Directory.Move(inventory.Roots[0].OldRoot.CanonicalPath, inventory.Roots[0].OldRoot.CanonicalPath + ".held");
        await Assert.ThrowsAnyAsync<IOException>(() => physical.ObserveInventoryAsync(inventory, default));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.NewRoot));
    }

    private sealed class MutatingCapacityProbe(Action mutate) : IStorageRelocationCapacityProbe
    {
        public Task<StorageCapacityObservation> ObserveAsync(ResolvedPhysicalPath root, CancellationToken cancellationToken)
        {
            var observed = new StorageCapacityObservation(StorageRelocationPhysicalStore.InspectIdentity(root.CanonicalPath, true), new("test", 1, "volume"), 1000);
            mutate();
            return Task.FromResult(observed);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhysicalInventoryDoesNotFollowExistingOrDanglingTargetAncestor(bool dangling)
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(Path.GetDirectoryName(fixture.NewRoot)!, "outside");
        if (!dangling) Directory.CreateDirectory(outside);
        try { Directory.CreateSymbolicLink(Path.GetDirectoryName(fixture.Target)!, outside); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { return; }
        catch (IOException exception) when (OperatingSystem.IsWindows() && (exception.HResult & 0xffff) == 1314) { return; }
        var physical = new StorageRelocationPhysicalStore(new Barrier(), new CapacityProbe(1000));
        await Assert.ThrowsAsync<IOException>(() => physical.ObserveInventoryAsync(Inventory(fixture), default));
        Assert.False(File.Exists(fixture.Target));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
        if (!dangling) Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public async Task CapacityFailureCreatesNoDirectoriesOrTemporaryFiles(long? available)
    {
        using var fixture = new Fixture();
        var physical = new StorageRelocationPhysicalStore(new Barrier(), new CapacityProbe(available));
        await Assert.ThrowsAsync<StorageRelocationCapacityException>(() => physical.StageAsync(fixture.Journal, fixture.Version, default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.NewRoot));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
    }

    [Fact]
    public async Task AlreadyStagedRenameDoesNotRequireMoreCapacity()
    {
        using var fixture = new Fixture();
        var proof = await new StorageRelocationPhysicalStore(new Barrier()).StageAsync(fixture.Journal, fixture.Version, default);
        Assert.Empty(await new StorageRelocationCapacityGuard(new CapacityProbe(null)).CheckPendingAsync(fixture.Staged(proof), default));
        await new StorageRelocationPhysicalStore(new Barrier(), new CapacityProbe(null))
            .PublishTargetAsync(fixture.Staged(proof), fixture.Version, default);
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Target));
    }

    [Fact]
    public async Task NativeCapacityProbeReturnsRootIdentityWithoutWrites()
    {
        using var fixture = new Fixture();
        var root = fixture.Journal.Manifest.Roots[0];
        var observation = await new StorageRelocationCapacityProbe().ObserveAsync(root.NewRoot, default);
        Assert.Equal(root.NewIdentity, observation.RootIdentity);
        Assert.True(observation.AvailableBytes >= 0);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.NewRoot));
    }

    private sealed class CapacityProbe(long? available) : IStorageRelocationCapacityProbe
    {
        public Task<StorageCapacityObservation> ObserveAsync(ResolvedPhysicalPath root, CancellationToken cancellationToken)
            => Task.FromResult(new StorageCapacityObservation(StorageRelocationPhysicalStore.InspectIdentity(root.CanonicalPath, true), new("test", 1, "volume"), available));
    }

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
        await Assert.ThrowsAsync<IOException>(() => new StorageRelocationPhysicalStore(new FailNthBarrier(3)).StageAsync(fixture.Journal, fixture.Version, default));
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

    [Fact]
    public async Task CleanupDeletesOnlyCommittedExactOldCopyAndKeepsUnknownFiles()
    {
        using var fixture = new Fixture();
        var journal = await CommittedPhysicalFixtureAsync(fixture);
        var unknown = Path.Combine(Path.GetDirectoryName(fixture.Source)!, "unknown.txt");
        await File.WriteAllTextAsync(unknown, "unowned");
        var proof = await new StorageRelocationPhysicalStore(new Barrier()).RemoveOldCopyAsync(journal, fixture.Version, default);
        Assert.False(File.Exists(fixture.Source));
        Assert.True(Directory.Exists(Path.GetDirectoryName(fixture.Source)));
        Assert.Equal("unowned", await File.ReadAllTextAsync(unknown));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Target));
        Assert.Equal(journal.Manifest.TransactionId, proof.TransactionId);
        Assert.Equal(journal.Revision, proof.JournalRevision);
        Assert.Equal(journal.Manifest.Entries[0].OldIdentity, proof.OldIdentity);
        Assert.Equal(StorageTransferStage.MetadataCommitted, journal.Progress.Stage);
    }

    [Fact]
    public async Task CleanupCannotUsePreCommitProgressAsDeletionAuthority()
    {
        using var fixture = new Fixture();
        var journal = await PublishAllAsync(fixture.Journal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new StorageRelocationPhysicalStore(new Barrier()).RemoveOldCopyAsync(journal, fixture.Version, default));
        Assert.True(File.Exists(fixture.Source));
    }

    [Theory]
    [InlineData("old-bytes")]
    [InlineData("old-identity")]
    [InlineData("target-bytes")]
    [InlineData("target-identity")]
    [InlineData("old-root")]
    [InlineData("target-root")]
    public async Task CleanupRefusesChangedOldOrNewObjects(string drift)
    {
        using var fixture = new Fixture();
        var journal = await CommittedPhysicalFixtureAsync(fixture);
        var root = journal.Manifest.Roots[0];
        var survivingOld = fixture.Source;
        switch (drift)
        {
            case "old-bytes": await File.WriteAllBytesAsync(fixture.Source, new byte[fixture.Bytes.Length]); break;
            case "target-bytes": await File.WriteAllBytesAsync(fixture.Target, new byte[fixture.Bytes.Length]); break;
            case "old-identity":
                File.Move(fixture.Source, fixture.Source + ".held");
                await File.WriteAllBytesAsync(fixture.Source, fixture.Bytes); break;
            case "target-identity":
                File.Move(fixture.Target, fixture.Target + ".held");
                await File.WriteAllBytesAsync(fixture.Target, fixture.Bytes); break;
            case "old-root":
                Directory.Move(root.OldRoot.CanonicalPath, root.OldRoot.CanonicalPath + ".held");
                Directory.CreateDirectory(root.OldRoot.CanonicalPath);
                survivingOld = Path.Combine(root.OldRoot.CanonicalPath + ".held", "资料", "单元.7z"); break;
            case "target-root":
                Directory.Move(fixture.NewRoot, fixture.NewRoot + ".held");
                Directory.CreateDirectory(fixture.NewRoot); break;
        }
        await Assert.ThrowsAnyAsync<IOException>(() => new StorageRelocationPhysicalStore(new Barrier()).RemoveOldCopyAsync(journal, fixture.Version, default));
        Assert.True(File.Exists(survivingOld));
    }

    [Fact]
    public async Task CleanupAfterDeleteBarrierFailureReconcilesAbsenceWithoutRollback()
    {
        using var fixture = new Fixture();
        var journal = await CommittedPhysicalFixtureAsync(fixture);
        await Assert.ThrowsAsync<IOException>(() => new StorageRelocationPhysicalStore(new FailNthBarrier(2)).RemoveOldCopyAsync(journal, fixture.Version, default));
        Assert.False(File.Exists(fixture.Source));
        var proof = await new StorageRelocationPhysicalStore(new Barrier()).RemoveOldCopyAsync(journal, fixture.Version, default);
        Assert.Equal(fixture.Version, proof.Artifact.VersionId);
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Target));
    }

    [Fact]
    public async Task CleanupUnavailableBarrierAndPreCancellationPreserveOldCopy()
    {
        using var fixture = new Fixture();
        var journal = await CommittedPhysicalFixtureAsync(fixture);
        await Assert.ThrowsAsync<IOException>(() => new StorageRelocationPhysicalStore(new Barrier(false)).RemoveOldCopyAsync(journal, fixture.Version, default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StorageRelocationPhysicalStore(new Barrier()).RemoveOldCopyAsync(journal, fixture.Version, cancellation.Token));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
    }

    [Fact]
    public async Task CleanupIgnoresCancellationAfterDeleteAndRejectsReappearedEntry()
    {
        using var fixture = new Fixture();
        var journal = await CommittedPhysicalFixtureAsync(fixture);
        using var cancellation = new CancellationTokenSource();
        var store = new StorageRelocationPhysicalStore(new CancelAfterDeleteBarrier(fixture.Source, cancellation));
        await store.RemoveOldCopyAsync(journal, fixture.Version, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        await File.WriteAllBytesAsync(fixture.Source, fixture.Bytes);
        var recorded = journal with { Progress = journal.Progress.RecordOldCopyAbsent(fixture.Version) };
        await Assert.ThrowsAsync<IOException>(() => store.RemoveOldCopyAsync(recorded, fixture.Version, default));
        Assert.True(File.Exists(fixture.Source));
    }

    // 物理适配器测试显式模拟已提交日志；真实 metadata commit 另有 SQLite 组合测试，不能由内存进度授权用户文件删除。
    private static async Task<StorageRelocationJournal> CommittedPhysicalFixtureAsync(Fixture fixture)
    {
        var journal = await PublishAllAsync(fixture.Journal);
        return journal with { Progress = journal.Progress.MarkMetadataCommitted(), Revision = journal.Revision + 1 };
    }

    private sealed class CancelAfterDeleteBarrier(string oldPath, CancellationTokenSource cancellation) : IArchivePublishMetadataDurabilityBarrier
    {
        public Task<PublishMetadataDurabilityProof> FlushDirectoryMetadataAsync(string path, CancellationToken token)
        {
            if (!File.Exists(oldPath))
            {
                cancellation.Cancel();
                Assert.False(token.CanBeCanceled);
            }
            return Task.FromResult(new PublishMetadataDurabilityProof(true, "test-only"));
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("old")]
    [InlineData("temp")]
    [InlineData("target")]
    [InlineData("root")]
    [InlineData("barrier")]
    [InlineData("cancel")]
    public async Task CompletedProbeNeverDeletesReappearedOrUnknownFiles(string drift)
    {
        using var fixture = new Fixture();
        var physical = new StorageRelocationPhysicalStore(new Barrier());
        var journal = await CommittedPhysicalFixtureAsync(fixture);
        await physical.RemoveOldCopyAsync(journal, fixture.Version, default);
        journal = journal with { Progress = journal.Progress.RecordOldCopyAbsent(fixture.Version).Complete() };
        var unknown = Path.Combine(fixture.NewRoot, "unknown.txt");
        await File.WriteAllTextAsync(unknown, "keep");
        if (drift == "old") await File.WriteAllBytesAsync(fixture.Source, fixture.Bytes);
        if (drift == "temp") await File.WriteAllBytesAsync(fixture.Temp, fixture.Bytes);
        if (drift == "target") await File.WriteAllTextAsync(fixture.Target, "changed");
        if (drift == "root")
        {
            Directory.Move(fixture.NewRoot, fixture.NewRoot + "-moved");
            Directory.CreateDirectory(fixture.NewRoot);
            unknown = Path.Combine(fixture.NewRoot + "-moved", "unknown.txt");
        }
        using var cancellation = new CancellationTokenSource();
        if (drift == "cancel") cancellation.Cancel();
        if (drift == "barrier") physical = new(new Barrier(false));
        if (drift == "none") await physical.VerifyCompletedAsync(journal, default);
        else await Assert.ThrowsAnyAsync<Exception>(() => physical.VerifyCompletedAsync(journal, cancellation.Token));
        Assert.Equal("keep", await File.ReadAllTextAsync(unknown));
        if (drift == "old") Assert.True(File.Exists(fixture.Source));
        if (drift == "temp") Assert.True(File.Exists(fixture.Temp));
    }

    private sealed class FailNthBarrier(int failureCall) : IArchivePublishMetadataDurabilityBarrier
    {
        private int calls;
        public Task<PublishMetadataDurabilityProof> FlushDirectoryMetadataAsync(string destinationDirectory, CancellationToken cancellationToken)
            => Task.FromResult(new PublishMetadataDurabilityProof(++calls != failureCall, "injected-test-barrier"));
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
