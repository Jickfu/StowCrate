using StowCrate.Application.BackupPlans.Candidates;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;

namespace StowCrate.Application.Tests.BackupPlans;

public sealed class ExecutionSemanticSnapshotTests
{
    private static readonly PlanId PlanId = new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly ArchiveUnitId UnitId = new(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly SourceId SourceId = new(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"));
    private static readonly SecretSlotId SecretId = new(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"));
    private static readonly DeviceId DeviceId = new(Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"));

    [Fact]
    public void PlanSemanticFingerprintIncludesAuthoredIntentScheduleRetentionAndDisplay()
    {
        var omitted = Plan("Plan", null, new ManualOnlySchedule());
        var explicitInherit = Plan("Plan", new HistoryInherit(), new ManualOnlySchedule());
        var renamed = Plan("Renamed", null, new ManualOnlySchedule());
        var scheduled = Plan("Plan", null, new AutomaticSchedule([new DailyScheduleTrigger(new TimeOnly(2, 0))], PortableMissedRunPolicy.Skip));

        Assert.NotEqual(CandidateFingerprintCalculator.ComputePlanSemantic(omitted), CandidateFingerprintCalculator.ComputePlanSemantic(explicitInherit));
        Assert.NotEqual(CandidateFingerprintCalculator.ComputePlanSemantic(omitted), CandidateFingerprintCalculator.ComputePlanSemantic(renamed));
        Assert.NotEqual(CandidateFingerprintCalculator.ComputePlanSemantic(omitted), CandidateFingerprintCalculator.ComputePlanSemantic(scheduled));
    }

    [Fact]
    public void PlanRevisionAndPlanFingerprintChangesDoNotBlockWhenUnitEffectiveStateIsSame()
    {
        var unit = UnitState(history: "retention-a");
        var captured = Snapshot(new PlanRevision(1), "plan-a", unit);
        var current = Snapshot(new PlanRevision(2), "plan-b", unit);

        var result = PublishTimeRevalidator.Revalidate(captured, current, UnitId);

        Assert.True(result.CanPublish);
        Assert.False(result.SkipRetentionCleanup);
    }

    [Fact]
    public void RetentionOnlyDriftAllowsPublishAndMarksMaintenanceOutOfSync()
    {
        var captured = Snapshot(null, "plan-a", UnitState(history: "retention-a"));
        var current = Snapshot(null, "plan-b", UnitState(history: "retention-b"));

        var result = PublishTimeRevalidator.Revalidate(captured, current, UnitId);

        Assert.True(result.CanPublish);
        Assert.True(result.SkipRetentionCleanup);
        Assert.True(result.HistoryMaintenanceOutOfSync);
    }

    [Theory]
    [InlineData("semantic")]
    [InlineData("binding")]
    [InlineData("rules")]
    [InlineData("secret")]
    public void CurrentUnitExecutionCriticalDriftBlocksPublish(string drift)
    {
        var before = UnitState();
        var after = drift switch
        {
            "semantic" => before with { ExecutionSemantic = new ExecutionSemanticFingerprint(Hash("semantic-2")) },
            "binding" => before with { ExecutionBinding = new ExecutionBindingFingerprint(Hash("binding-2")) },
            "rules" => before with { FileManagedRuleSource = Hash("rules-2") },
            "secret" => before with { SecureRequirement = new SecureRevisionRequirement(SecretId, new SecretRevision(2)) },
            _ => throw new InvalidOperationException()
        };

        var result = PublishTimeRevalidator.Revalidate(Snapshot(null, "p", before), Snapshot(null, "p", after), UnitId);

        Assert.False(result.CanPublish);
        Assert.NotEmpty(result.Reasons);
    }

    private static ExecutionSemanticSnapshot Snapshot(PlanRevision? revision, string plan, UnitExecutionSemanticState unit) =>
        new(PlanId, DeviceId, revision, new PlanSemanticFingerprint(Hash(plan)), [unit]);

    private static UnitExecutionSemanticState UnitState(string history = "retention-a") => new(
        UnitId,
        new ExecutionSemanticFingerprint(Hash("semantic")),
        new ExecutionBindingFingerprint(Hash("binding")),
        Hash("rules"),
        new SecureRevisionRequirement(SecretId, new SecretRevision(1)),
        new HistoryMaintenanceFingerprint(Hash(history)));

    private static PortableBackupPlan Plan(string name, AuthoredHistoryOverride? historyOverride, PortableScheduleIntent schedule)
    {
        var archive = new AuthoredArchiveSpec(PortableArchiveFormat.SevenZip, PortableCompressionPreset.Standard, new NoProtection());
        return new PortableBackupPlan(
            PlanId, name, null, new PortableSemanticsPins(1, 1, 1),
            [new PortableBackupSource(SourceId, "Source", new LogicalPath("out"))],
            new GlobalRulesSnapshot([], null), [], archive,
            [new UiManagedArchiveUnit(UnitId, SourceId, new LogicalPath("unit"), new RuleSet(), null, historyOverride)],
            [], PortableLinkPolicy.Preserve, PortableChangeDetectionMode.Standard,
            new HistoryDisabled(), schedule, []);
    }

    private static Sha256Digest Hash(string value) => CanonicalFingerprintEncodingV1.Encode("test", writer => writer.Utf8(1, value));
}
