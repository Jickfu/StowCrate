using StowCrate.Application.StorageMaintenance;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;

namespace StowCrate.Application.Tests;

public sealed class StorageRelocationConfigurationFingerprintTests
{
    private static readonly PlanId Plan = new(Guid.NewGuid());
    private static readonly SourceId Source = new(Guid.NewGuid());
    private static readonly ArchiveUnitId Unit = new(Guid.NewGuid());

    [Theory]
    [InlineData("name")]
    [InlineData("description")]
    [InlineData("schedule")]
    [InlineData("compression")]
    [InlineData("explicit-format")]
    [InlineData("plan-rules")]
    [InlineData("local-rules")]
    public void FutureBackupAndPresentationChangesDoNotBlockRelocation(string change)
        => Assert.Equal(StorageRelocationConfigurationFingerprint.Compute(Create()), StorageRelocationConfigurationFingerprint.Compute(Create(change)));

    [Theory]
    [InlineData("plan")]
    [InlineData("source")]
    [InlineData("unit")]
    [InlineData("output")]
    [InlineData("path")]
    [InlineData("format")]
    [InlineData("encoding")]
    [InlineData("rule-source")]
    public void IdentityAndLayoutChangesInvalidateRelocation(string change)
        => Assert.NotEqual(StorageRelocationConfigurationFingerprint.Compute(Create()), StorageRelocationConfigurationFingerprint.Compute(Create(change)));

    private static PortableBackupPlan Create(string change = "")
    {
        var source = change == "source" ? new SourceId(Guid.NewGuid()) : Source;
        var unit = change == "unit" ? new ArchiveUnitId(Guid.NewGuid()) : Unit;
        var path = new LogicalPath(change == "path" ? "moved" : "unit");
        return new(change == "plan" ? new(Guid.NewGuid()) : Plan, change == "name" ? "renamed" : "plan",
            change == "description" ? "description" : null, new(1, 1, change == "encoding" ? 2 : 1),
            [new(source, "source", new(change == "output" ? "moved" : "out"))], new([], null),
            change == "plan-rules" ? [new BackupRule(RuleAction.Exclude, "*.tmp")] : [],
            new(change == "format" ? PortableArchiveFormat.Zip : PortableArchiveFormat.SevenZip,
                change == "compression" ? PortableCompressionPreset.Extreme : PortableCompressionPreset.Standard, new NoProtection()),
            [change == "rule-source" ? new FileManagedArchiveUnit(unit, source, path, null, null)
                : new UiManagedArchiveUnit(unit, source, path, new RuleSet(rules: change == "local-rules" ? [new BackupRule(RuleAction.Exclude, "bin/")] : []),
                    change == "explicit-format" ? new(PortableArchiveFormat.SevenZip, null, null) : null, null)],
            [], PortableLinkPolicy.Preserve, PortableChangeDetectionMode.Standard, new HistoryDisabled(),
            change == "schedule" ? new AutomaticSchedule([new DailyScheduleTrigger(new(12, 0))], PortableMissedRunPolicy.Skip) : new ManualOnlySchedule(), []);
    }
}
