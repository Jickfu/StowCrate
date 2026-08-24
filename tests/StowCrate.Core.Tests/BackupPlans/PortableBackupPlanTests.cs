using StowCrate.Core.BackupPlans;

namespace StowCrate.Core.Tests.BackupPlans;

public sealed class PortableBackupPlanTests
{
    [Fact]
    public void TypedPortableIdsRejectNonVersion4Uuid()
    {
        var versionOne = Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

        Assert.Throws<ArgumentException>(() => new PlanId(versionOne));
        Assert.Throws<ArgumentException>(() => new SourceId(versionOne));
        Assert.Throws<ArgumentException>(() => new ArchiveUnitId(versionOne));
        Assert.Throws<ArgumentException>(() => new ExternalSourceId(versionOne));
        Assert.Throws<ArgumentException>(() => new SecretSlotId(versionOne));
    }

    [Fact]
    public void TypedPortableIdsWithSameUuidRemainDifferentTypes()
    {
        var value = Guid.Parse("0c79c2c4-53bc-4a63-b4f0-a67bed58f8d8");
        var planId = new PlanId(value);
        var sourceId = new SourceId(value);

        Assert.Equal(value, planId.Value);
        Assert.Equal(value, sourceId.Value);
        Assert.NotEqual(typeof(PlanId), typeof(SourceId));
    }
}
