using StowCrate.Application.Publishing;
using StowCrate.Application.StorageMaintenance;
using StowCrate.Core.ChangeDetection;
using StowCrate.Infrastructure.Filesystem;

namespace StowCrate.Infrastructure.Tests.Filesystem;

public sealed partial class StorageRelocationPhysicalTests
{
    [Theory]
    [InlineData("unsupported")]
    [InlineData("temp")]
    [InlineData("target")]
    [InlineData("source")]
    [InlineData("root")]
    [InlineData("cancel")]
    public async Task RootLevelCopyRechecksDurabilityBeforeCreatingAnyTemporaryFile(string failure)
    {
        using var fixture = new Fixture();
        var original = fixture.Journal.Manifest;
        var relative = new RelativeStoragePath("unit.7z");
        var source = Path.Combine(original.Roots[0].OldRoot.CanonicalPath, relative.Value);
        await File.WriteAllBytesAsync(source, fixture.Bytes);
        var entry = original.Entries[0] with
        {
            RelativePath = relative,
            TempRelativePath = StorageRelocationTempLayout.Create(original.TransactionId, fixture.Version, relative),
            OldIdentity = StorageRelocationPhysicalStore.InspectIdentity(source, false)
        };
        var manifest = new StorageRelocationManifest(original.TransactionId, original.PlanId, original.DeviceId,
            original.LegacyExecutionSemanticDigest!.Value, original.Roots, [entry]);
        var journal = new StorageRelocationJournal(manifest, StorageTransferProgress.Prepare(manifest.TransactionId, manifest.PlanId, [entry.Artifact]), 1);
        var temp = Path.Combine(fixture.NewRoot, entry.TempRelativePath.Value);
        var target = Path.Combine(fixture.NewRoot, relative.Value);
        using var cancellation = new CancellationTokenSource();
        var barrier = new BeforeCopyBarrier(() =>
        {
            if (failure == "temp") File.WriteAllText(temp, "unowned");
            if (failure == "target") File.WriteAllText(target, "unowned");
            if (failure == "source")
            {
                File.Move(source, source + ".held");
                File.WriteAllBytes(source, fixture.Bytes);
            }
            if (failure == "root")
            {
                Directory.Move(fixture.NewRoot, fixture.NewRoot + ".held");
                Directory.CreateDirectory(fixture.NewRoot);
            }
            if (failure == "cancel") cancellation.Cancel();
            return failure != "unsupported";
        });
        var physical = RelocationTestPhysicalStore.Create(barrier);
        await Assert.ThrowsAnyAsync<Exception>(() => physical.StageAsync(journal, fixture.Version, cancellation.Token));
        Assert.Equal(1, barrier.Calls);
        if (failure == "temp") Assert.Equal("unowned", await File.ReadAllTextAsync(temp));
        else Assert.False(File.Exists(temp));
        if (failure == "target") Assert.Equal("unowned", await File.ReadAllTextAsync(target));
        else Assert.False(File.Exists(target));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(source));
    }

    private sealed class BeforeCopyBarrier(Func<bool> act) : IArchivePublishMetadataDurabilityBarrier
    {
        public int Calls { get; private set; }
        public Task<PublishMetadataDurabilityProof> FlushDirectoryMetadataAsync(string destinationDirectory, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new PublishMetadataDurabilityProof(act(), "test-only-before-copy"));
        }
    }
}
