using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Application.StorageMaintenance;
using StowCrate.Core.ChangeDetection;
using StowCrate.Infrastructure.Filesystem;

namespace StowCrate.Infrastructure.Tests.Filesystem;

public sealed partial class StorageRelocationPhysicalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DestinationParentCapacityBlocksWhenRootWouldHaveEnoughSpace(bool inspect)
    {
        using var fixture = new Fixture();
        var parent = Directory.CreateDirectory(Path.GetDirectoryName(fixture.Target)!).FullName;
        var probe = new DestinationProbe(path => (path == parent ? "nested-volume" : "root-volume", path == parent ? 0 : 10000));
        var physical = RelocationTestPhysicalStore.Create(new Barrier(), probe);
        var error = await Assert.ThrowsAsync<StorageRelocationCapacityException>(() => inspect
            ? physical.ObserveInventoryAsync(Inventory(fixture), default)
            : physical.StageAsync(fixture.Journal, fixture.Version, default));
        Assert.Equal(StorageRelocationCapacityFailure.Insufficient, error.Reason);
        Assert.Equal(new[] { parent }, probe.Paths);
        Assert.Empty(Directory.GetFileSystemEntries(parent));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DestinationNeedsGroupActualParentVolumesInsteadOfRootSlots(bool sameVolume)
    {
        using var fixture = new Fixture();
        var inventory = Inventory(fixture);
        var relative = new RelativeStoragePath("other/unit.7z");
        var source = Path.Combine(inventory.Roots[0].OldRoot.CanonicalPath, "other", "unit.7z");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllBytesAsync(source, fixture.Bytes);
        var second = inventory.Entries[0] with
        {
            RelativePath = relative,
            Artifact = inventory.Entries[0].Artifact with { VersionId = new(Guid.NewGuid()) }
        };
        inventory = inventory with { Entries = inventory.Entries.Add(second) };
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Target)!);
        Directory.CreateDirectory(Path.Combine(fixture.NewRoot, "other"));
        var probe = new DestinationProbe(path => (sameVolume ? "shared" : path, fixture.Bytes.Length * 2 - 1));
        var physical = RelocationTestPhysicalStore.Create(new Barrier(), probe);
        if (sameVolume)
            Assert.Equal(StorageRelocationCapacityFailure.Insufficient,
                (await Assert.ThrowsAsync<StorageRelocationCapacityException>(() => physical.ObserveInventoryAsync(inventory, default))).Reason);
        else
        {
            var result = await physical.ObserveInventoryAsync(inventory, default);
            Assert.Equal(2, result.Capacity.Length);
            Assert.All(result.Capacity, x => Assert.Equal(fixture.Bytes.Length, x.RequiredBytes));
        }
        Assert.DoesNotContain(fixture.NewRoot, probe.Paths);
        Assert.Empty(Directory.GetFiles(fixture.NewRoot, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DestinationLayoutChangesDuringCapacityQueryCannotAuthorizeCopy(bool replace)
    {
        using var fixture = new Fixture();
        var parent = Path.GetDirectoryName(fixture.Target)!;
        if (replace) Directory.CreateDirectory(parent);
        var probe = new DestinationProbe(_ => ("volume", 10000), () =>
        {
            if (replace) Directory.Move(parent, parent + ".held");
            Directory.CreateDirectory(parent);
        });
        await Assert.ThrowsAsync<StorageRelocationCapacityException>(() => RelocationTestPhysicalStore.Create(new Barrier(), probe)
            .StageAsync(fixture.Journal, fixture.Version, default));
        Assert.False(File.Exists(fixture.Temp));
        Assert.False(File.Exists(fixture.Target));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
    }

    // 私有目录上注入不同卷的容量观察，验证映射/合并；不冒充真实挂载卷验收。
    private sealed class DestinationProbe(Func<string, (string Volume, long Available)> observe, Action? mutate = null) : IStorageRelocationCapacityProbe
    {
        public List<string> Paths { get; } = [];
        public Task<StorageCapacityObservation> ObserveAsync(ResolvedPhysicalPath root, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Add(root.CanonicalPath);
            var (volume, available) = observe(root.CanonicalPath);
            var identity = StorageRelocationPhysicalStore.InspectIdentity(root.CanonicalPath, true);
            mutate?.Invoke();
            return Task.FromResult(new StorageCapacityObservation(identity, new("test-volume", 1, volume), available));
        }
    }
}
