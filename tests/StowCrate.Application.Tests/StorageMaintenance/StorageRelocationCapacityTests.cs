using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Application.StorageMaintenance;

namespace StowCrate.Application.Tests.StorageMaintenance;

public sealed class StorageRelocationCapacityTests
{
    private static readonly StorageObjectIdentity Identity = new("test", 1, "root");
    private static StorageCapacityNeed Need(long bytes) => new(new("/target", "/target"), bytes, Identity);
    private static StorageCapacityObservation Observation(long? bytes, string volume = "volume") => new(Identity, new("test", 1, volume), bytes);

    [Theory]
    [InlineData(null)]
    [InlineData(-1L)]
    [InlineData(9L)]
    public async Task UnknownOrInsufficientBlocks(long? available)
    {
        var guard = new StorageRelocationCapacityGuard(new Probe(_ => Observation(available)));
        var error = await Assert.ThrowsAsync<StorageRelocationCapacityException>(() => guard.CheckAsync([Need(10)], default));
        Assert.Equal(available is null or < 0 ? StorageRelocationCapacityFailure.Unavailable : StorageRelocationCapacityFailure.Insufficient, error.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GroupsOnlySameVolume(bool sameVolume)
    {
        var guard = new StorageRelocationCapacityGuard(new Probe(call => Observation(10, sameVolume ? "same" : call == 1 ? "first" : "second")));
        if (sameVolume) await Assert.ThrowsAsync<StorageRelocationCapacityException>(() => guard.CheckAsync([Need(6), Need(6)], default));
        else Assert.Equal(2, (await guard.CheckAsync([Need(6), Need(6)], default)).Length);
    }

    [Fact]
    public async Task UsesMinimumObservationAndAllowsExactCapacity()
    {
        var guard = new StorageRelocationCapacityGuard(new Probe(call => Observation(call == 1 ? 100 : 12)));
        var result = Assert.Single(await guard.CheckAsync([Need(6), Need(6)], default));
        Assert.Equal(12, result.RequiredBytes);
        Assert.Equal(12, result.AvailableBytes);
    }

    [Fact]
    public async Task OverflowCannotBecomeNegativeDemand()
    {
        var guard = new StorageRelocationCapacityGuard(new Probe(_ => Observation(long.MaxValue)));
        await Assert.ThrowsAsync<OverflowException>(() => guard.CheckAsync([Need(long.MaxValue), Need(1)], default));
    }

    [Fact]
    public async Task ChangedRootBlocks()
    {
        var guard = new StorageRelocationCapacityGuard(new Probe(_ => Observation(100) with { RootIdentity = new("test", 1, "changed") }));
        Assert.Equal(StorageRelocationCapacityFailure.Unavailable,
            (await Assert.ThrowsAsync<StorageRelocationCapacityException>(() => guard.CheckAsync([Need(1)], default))).Reason);
    }

    [Fact]
    public async Task QueryFailureDoesNotLeakPhysicalPath()
    {
        var guard = new StorageRelocationCapacityGuard(new Probe(_ => throw new IOException("private-path")));
        var error = await Assert.ThrowsAsync<StorageRelocationCapacityException>(() => guard.CheckAsync([Need(1)], default));
        Assert.DoesNotContain("private-path", error.ToString());
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var guard = new StorageRelocationCapacityGuard(new Probe(_ => throw new InvalidOperationException()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guard.CheckAsync([Need(1)], cancellation.Token));
    }

    private sealed class Probe(Func<int, StorageCapacityObservation> observe) : IStorageRelocationCapacityProbe
    {
        private int calls;
        public Task<StorageCapacityObservation> ObserveAsync(ResolvedPhysicalPath root, CancellationToken cancellationToken)
            => Task.FromResult(observe(++calls));
    }
}
