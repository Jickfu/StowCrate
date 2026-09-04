using StowCrate.Application.StorageMaintenance;
using StowCrate.Infrastructure.Filesystem;

namespace StowCrate.Infrastructure.Tests.Filesystem;

public sealed partial class StorageRelocationPhysicalTests
{
    [Fact]
    public async Task StageChecksOtherEntriesComparisonBeforeCopyingSelectedEntry()
    {
        using var fixture = new Fixture();
        var manifest = fixture.Journal.Manifest;
        var first = manifest.Entries[0];
        var version = new StowCrate.Core.ChangeDetection.ArchiveVersionId(Guid.NewGuid());
        var relative = new StowCrate.Core.ChangeDetection.RelativeStoragePath("other/archive.7z");
        var second = first with { Artifact = first.Artifact with { VersionId = version }, RelativePath = relative,
            TempRelativePath = StorageRelocationTempLayout.Create(manifest.TransactionId, version, relative) };
        var whole = new StorageRelocationManifest(manifest.TransactionId, manifest.PlanId, manifest.DeviceId,
            manifest.ExecutionSemanticDigest, manifest.Roots, [first, second]);
        var journal = new StorageRelocationJournal(whole, StorageTransferProgress.Prepare(whole.TransactionId, whole.PlanId, whole.Entries.Select(x => x.Artifact)), 1);
        var other = Directory.CreateDirectory(Path.Combine(fixture.NewRoot, "other")).FullName;
        var comparison = new StorageRelocationTargetComparisonProbe(path => path == other
            ? throw new StorageRelocationComparisonUnavailableException() : StorageRelocationPhysicalStore.InspectIdentity(path, true));
        var physical = new StorageRelocationPhysicalStore(new Barrier(), comparisonProbe: comparison);
        await Assert.ThrowsAsync<StorageRelocationComparisonUnavailableException>(() => physical.StageAsync(journal, fixture.Version, default));
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.Temp)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(other));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StageRejectsUnknownComparisonBeforeCopyIncludingNewParents(int failureCall)
    {
        using var fixture = new Fixture();
        var comparison = new TransferComparisonProbe(failureCall);
        var physical = new StorageRelocationPhysicalStore(new Barrier(), comparisonProbe: comparison);
        await Assert.ThrowsAsync<StorageRelocationComparisonUnavailableException>(() => physical.StageAsync(fixture.Journal, fixture.Version, default));
        Assert.False(File.Exists(fixture.Temp));
        Assert.False(File.Exists(fixture.Target));
        Assert.Equal(failureCall == 2, Directory.Exists(Path.GetDirectoryName(fixture.Temp)));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PublishComparisonFailurePreservesStagedOwnershipAndCanResume(int failureCall)
    {
        using var fixture = new Fixture();
        var ordinary = RelocationTestPhysicalStore.Create(new Barrier());
        var staged = await ordinary.StageAsync(fixture.Journal, fixture.Version, default);
        var journal = fixture.Staged(staged);
        var blocked = new StorageRelocationPhysicalStore(new Barrier(), comparisonProbe: new TransferComparisonProbe(failureCall));
        await Assert.ThrowsAsync<StorageRelocationComparisonUnavailableException>(() => blocked.PublishTargetAsync(journal, fixture.Version, default));
        Assert.Equal(failureCall != 3, File.Exists(fixture.Temp));
        Assert.Equal(failureCall == 3, File.Exists(fixture.Target));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
        var resumed = await ordinary.PublishTargetAsync(journal, fixture.Version, default);
        Assert.Equal(staged.ObjectIdentity, resumed.ObjectIdentity);
        Assert.False(File.Exists(fixture.Temp));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CommitRechecksComparisonWithoutChangingAnyCopies(int failureCall)
    {
        using var fixture = new Fixture();
        var journal = await PublishAllAsync(fixture.Journal);
        var blocked = new StorageRelocationPhysicalStore(new Barrier(), comparisonProbe: new TransferComparisonProbe(failureCall));
        await Assert.ThrowsAsync<StorageRelocationComparisonUnavailableException>(() => blocked.VerifyForCommitAsync(journal, default));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Target));
        Assert.False(File.Exists(fixture.Temp));
    }

    [Fact]
    public async Task AfterRenameComparisonCannotTurnCallerCancellationIntoLostProof()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var ordinary = RelocationTestPhysicalStore.Create(new Barrier());
        var staged = await ordinary.StageAsync(fixture.Journal, fixture.Version, default);
        var comparison = new TransferComparisonProbe(0, (call, token) =>
        {
            if (call == 3) { Assert.False(token.CanBeCanceled); cancellation.Cancel(); }
        });
        var physical = new StorageRelocationPhysicalStore(new Barrier(), comparisonProbe: comparison);
        var published = await physical.PublishTargetAsync(fixture.Staged(staged), fixture.Version, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(staged.ObjectIdentity, published.ObjectIdentity);
        Assert.True(File.Exists(fixture.Target));
    }

    [Fact]
    public async Task DefaultTransferRequiresNativeComparisonAndDoesNotCreateFilesWhenUnavailable()
    {
        using var fixture = new Fixture();
        // 只注入 barrier，比较端口必须使用产品默认原生适配器。
        var physical = new StorageRelocationPhysicalStore(new Barrier());
        StorageTransferProof? staged = null;
        var error = await Record.ExceptionAsync(async () => { staged = await physical.StageAsync(fixture.Journal, fixture.Version, default); });
        if (Environment.GetEnvironmentVariable("STOWCRATE_REQUIRE_EXT_COMPARISON") == "1") Assert.Null(error);
        if (error is not null)
        {
            Assert.IsType<StorageRelocationComparisonUnavailableException>(error);
            Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.NewRoot));
        }
        else
        {
            Assert.NotNull(staged);
            Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Temp));
            var journal = fixture.Staged(staged);
            var published = await physical.PublishTargetAsync(journal, fixture.Version, default);
            journal = journal with { Progress = journal.Progress.RecordTargetDurable(published).SealTargets(), Revision = 4 };
            await physical.VerifyForCommitAsync(journal, default);
            Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Target));
            Assert.False(File.Exists(fixture.Temp));
        }
        if (!OperatingSystem.IsLinux()) Assert.IsType<StorageRelocationComparisonUnavailableException>(error);
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
    }

    private sealed class TransferComparisonProbe(int failureCall, Action<int, CancellationToken>? action = null) : IStorageRelocationTargetComparisonProbe
    {
        private int calls;
        public Task VerifyTargetsAsync(StorageRelocationPhysicalInventory observation, Guid transactionId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Transfer must verify layout without imposing empty targets.");
        public Task VerifyLayoutAsync(StorageRelocationManifest manifest, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotEmpty(manifest.Roots);
            calls++;
            action?.Invoke(calls, cancellationToken);
            if (calls == failureCall) throw new StorageRelocationComparisonUnavailableException();
            return Task.CompletedTask;
        }
    }
}
