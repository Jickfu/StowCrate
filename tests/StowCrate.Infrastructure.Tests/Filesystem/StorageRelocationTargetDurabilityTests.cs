using StowCrate.Application.Publishing;
using StowCrate.Application.StorageMaintenance;

namespace StowCrate.Infrastructure.Tests.Filesystem;

public sealed partial class StorageRelocationPhysicalTests
{
    [Theory]
    [InlineData("missing-parent")]
    [InlineData("existing-parent")]
    [InlineData("empty")]
    [InlineData("unavailable")]
    [InlineData("parent-appears")]
    [InlineData("parent-replaced")]
    [InlineData("target-appears")]
    [InlineData("cancel")]
    public async Task TargetDurabilityInspectionChecksExistingDirectoriesWithoutCreatingPaths(string scenario)
    {
        using var fixture = new Fixture();
        var parent = Path.GetDirectoryName(fixture.Target)!;
        if (scenario is "existing-parent" or "parent-replaced") Directory.CreateDirectory(parent);
        var manifest = fixture.Journal.Manifest;
        if (scenario == "empty") manifest = new(manifest.TransactionId, manifest.PlanId, manifest.DeviceId, manifest.Roots, []);
        using var cancellation = new CancellationTokenSource();
        var barrier = new InspectionBarrier(path =>
        {
            if (path != fixture.NewRoot) return;
            if (scenario == "parent-appears") Directory.CreateDirectory(parent);
            if (scenario == "parent-replaced")
            {
                Directory.Move(parent, parent + ".held");
                Directory.CreateDirectory(parent);
            }
            if (scenario == "target-appears")
            {
                Directory.CreateDirectory(parent);
                File.WriteAllBytes(fixture.Target, fixture.Bytes);
            }
            if (scenario == "cancel") cancellation.Cancel();
        }, scenario != "unavailable");
        var physical = RelocationTestPhysicalStore.Create(barrier);
        if (scenario == "unavailable")
        {
            var error = await Assert.ThrowsAsync<StorageRelocationDurabilityUnavailableException>(
                () => physical.VerifyTargetDurabilityAsync(manifest, cancellation.Token));
            Assert.Equal("RELOCATION_TARGET_DURABILITY_UNAVAILABLE", error.DiagnosticCode);
        }
        else if (scenario == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => physical.VerifyTargetDurabilityAsync(manifest, cancellation.Token));
        else if (scenario is "parent-appears" or "parent-replaced" or "target-appears")
            await Assert.ThrowsAsync<IOException>(() => physical.VerifyTargetDurabilityAsync(manifest, cancellation.Token));
        else
            await physical.VerifyTargetDurabilityAsync(manifest, cancellation.Token);

        Assert.Equal(scenario == "existing-parent" ? new[] { fixture.NewRoot, parent } : new[] { fixture.NewRoot }, barrier.Paths);
        Assert.False(File.Exists(fixture.Temp));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
        if (scenario is "missing-parent" or "empty" or "unavailable" or "cancel")
            Assert.Empty(Directory.GetFileSystemEntries(fixture.NewRoot));
    }

    private sealed class InspectionBarrier(Action<string> onFlush, bool completed) : IArchivePublishMetadataDurabilityBarrier
    {
        public List<string> Paths { get; } = [];
        public Task<PublishMetadataDurabilityProof> FlushDirectoryMetadataAsync(string destinationDirectory, CancellationToken cancellationToken)
        {
            Paths.Add(destinationDirectory);
            onFlush(destinationDirectory);
            return Task.FromResult(new PublishMetadataDurabilityProof(completed, "test-inspection"));
        }
    }
}
