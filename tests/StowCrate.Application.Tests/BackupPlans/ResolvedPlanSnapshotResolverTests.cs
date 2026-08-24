using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;

namespace StowCrate.Application.Tests.BackupPlans;

public sealed class ResolvedPlanSnapshotResolverTests
{
    private static readonly DeviceId Device = new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private readonly ResolvedPlanSnapshotResolver resolver = new();

    [Fact]
    public void MissingPreObservationBindingsReturnIssuesAndNoSnapshot()
    {
        var plan = CreatePlan();
        var bindings = new DevicePlanBindingFacts(plan.Id, Device, [], null, null, [], []);

        var result = resolver.Resolve(plan, bindings, []);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Snapshot);
        Assert.Contains(result.Issues, issue => issue.Code == PlanResolutionIssueCode.MissingSourceBinding);
        Assert.Contains(result.Issues, issue => issue.Code == PlanResolutionIssueCode.MissingCurrentRootBinding);
        Assert.Contains(result.Issues, issue => issue.Code == PlanResolutionIssueCode.MissingExternalSourceBinding);
    }

    [Fact]
    public void MissingHistoryAndSecretDoNotBlockPreObservationResolution()
    {
        var plan = CreatePlan();

        var result = resolver.Resolve(plan, RequiredBindings(plan, historyRoot: null, secrets: []), []);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues));
        Assert.Null(result.Snapshot!.HistoryRoot);
        Assert.Empty(result.Snapshot.SecretBindings);
        Assert.IsType<EffectiveHistoryEnabled>(result.Snapshot.DefaultUnitPolicy.History);
        Assert.IsType<SecureProtection>(result.Snapshot.DefaultUnitPolicy.ArchiveSpec.Protection);
    }

    [Fact]
    public void PreparedDeclarationsResolveDefaultsOverridesAndRuleSourceShape()
    {
        var plan = CreatePlan();

        var snapshot = resolver.Resolve(plan, RequiredBindings(plan), []).Snapshot!;

        Assert.Equal(PortableArchiveFormat.SevenZip, snapshot.DefaultUnitPolicy.ArchiveSpec.Format);
        Assert.IsType<EffectiveHistoryEnabled>(snapshot.DefaultUnitPolicy.History);

        var ui = Assert.Single(snapshot.DeclaredArchiveUnits.OfType<PreparedUiManagedArchiveUnit>());
        Assert.Equal(PortableArchiveFormat.SevenZip, ui.ArchiveSpec.Format);
        Assert.IsType<SecureProtection>(ui.ArchiveSpec.Protection);
        Assert.IsType<EffectiveHistoryEnabled>(ui.History);
        Assert.Single(ui.LocalRules.Rules);

        var file = Assert.Single(snapshot.DeclaredArchiveUnits.OfType<PreparedFileManagedArchiveUnit>());
        Assert.Equal(PortableArchiveFormat.Zip, file.ArchiveSpec.Format);
        Assert.Equal(PortableCompressionPreset.Standard, file.ArchiveSpec.CompressionPreset);
        Assert.IsType<PrivacyProtection>(file.ArchiveSpec.Protection);
        Assert.IsType<EffectiveHistoryDisabled>(file.History);
        Assert.DoesNotContain(file.GetType().GetProperties(), property => property.Name == "LocalRules");
    }

    [Fact]
    public void SnapshotCarriesOnlyExecutionRelevantPortableAndResolvedFacts()
    {
        var plan = CreatePlan();
        var secret = new SecretBindingFact(plan.SecretSlots[0].Id, new SecretRevision(7));

        var snapshot = resolver.Resolve(plan, RequiredBindings(plan, secrets: [secret]), []).Snapshot!;

        Assert.Equal(plan.Id, snapshot.PlanId);
        Assert.Equal(Device, snapshot.DeviceId);
        Assert.Single(snapshot.Sources);
        Assert.Single(snapshot.ExternalSources);
        Assert.Equal(7, Assert.Single(snapshot.SecretBindings).Revision.Value);
        Assert.DoesNotContain(snapshot.GetType().GetProperties(), property =>
            property.Name.Contains("Authority", StringComparison.Ordinal)
            || property.Name.Contains("Registration", StringComparison.Ordinal)
            || property.Name.Contains("Schedule", StringComparison.Ordinal)
            || property.Name.Contains("Description", StringComparison.Ordinal)
            || property.Name.Contains("Provenance", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/data/source/output", "/data/source/output")]
    [InlineData("/data/source/output", "/data/source")]
    [InlineData("/DATA/SOURCE/output", "/data/source")]
    public void SinglePlanRootOverlapBlocksResolution(string currentCanonical, string currentComparisonKey)
    {
        var plan = CreatePlan();
        var bindings = RequiredBindings(
            plan,
            currentRoot: new ResolvedPhysicalPath(currentCanonical, currentComparisonKey));

        var result = resolver.Resolve(plan, bindings, []);

        Assert.Null(result.Snapshot);
        Assert.Contains(result.Issues, issue => issue.Code == PlanResolutionIssueCode.RootOverlap);
    }

    [Fact]
    public void BoundHistoryRootParticipatesInRootSafetyWithoutBeingRequired()
    {
        var plan = CreatePlan();
        var bindings = RequiredBindings(
            plan,
            historyRoot: new ResolvedPhysicalPath("/backup/current/history", "/backup/current/history"));

        var result = resolver.Resolve(plan, bindings, []);

        Assert.Contains(result.Issues, issue => issue.Code == PlanResolutionIssueCode.RootOverlap);
    }

    [Fact]
    public void CrossPlanWritableOverlapBlocksButReadOnlySourceOverlapIsAllowed()
    {
        var plan = CreatePlan();
        var otherId = new PlanId(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
        var conflicting = new ActivePlanRootFacts(
            otherId,
            Device,
            [new ResolvedPhysicalPath("/backup", "/backup")],
            new ResolvedPhysicalPath("/other/current", "/other/current"),
            null);
        var sharedReadOnly = new ActivePlanRootFacts(
            otherId,
            Device,
            [new ResolvedPhysicalPath("/data/source", "/data/source")],
            new ResolvedPhysicalPath("/other/current", "/other/current"),
            null);

        var conflict = resolver.Resolve(plan, RequiredBindings(plan), [conflicting]);
        var allowed = resolver.Resolve(plan, RequiredBindings(plan), [sharedReadOnly]);

        Assert.Contains(conflict.Issues, issue => issue.Code == PlanResolutionIssueCode.ActivePlanRootConflict);
        Assert.True(allowed.IsSuccess, string.Join(Environment.NewLine, allowed.Issues));
    }

    [Fact]
    public void BindingFactsDefensivelyCopyInputCollections()
    {
        var plan = CreatePlan();
        var sources = new List<SourceBindingFact>
        {
            new(plan.Sources[0].Id, new ResolvedPhysicalPath("/data/source", "/data/source"))
        };
        var bindings = new DevicePlanBindingFacts(
            plan.Id,
            Device,
            sources,
            new ResolvedPhysicalPath("/backup/current", "/backup/current"),
            null,
            [new ExternalSourceBindingFact(plan.ExternalSources[0].Id, new ResolvedPhysicalPath("/external/input", "/external/input"))],
            []);
        sources.Clear();

        var result = resolver.Resolve(plan, bindings, []);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues));
    }

    private static PortableBackupPlan CreatePlan()
    {
        var planId = new PlanId(Guid.Parse("10000000-0000-4000-8000-000000000000"));
        var sourceId = new SourceId(Guid.Parse("20000000-0000-4000-8000-000000000000"));
        var uiUnitId = new ArchiveUnitId(Guid.Parse("30000000-0000-4000-8000-000000000000"));
        var fileUnitId = new ArchiveUnitId(Guid.Parse("40000000-0000-4000-8000-000000000000"));
        var externalId = new ExternalSourceId(Guid.Parse("50000000-0000-4000-8000-000000000000"));
        var slotId = new SecretSlotId(Guid.Parse("60000000-0000-4000-8000-000000000000"));
        var defaultSpec = new AuthoredArchiveSpec(
            PortableArchiveFormat.SevenZip,
            PortableCompressionPreset.Standard,
            new SecureProtection(slotId));
        var ui = new UiManagedArchiveUnit(
            uiUnitId,
            sourceId,
            new LogicalPath("ui"),
            new RuleSet(rules: [new BackupRule(RuleAction.Exclude, "*.tmp")]),
            null,
            new HistoryInherit());
        var file = new FileManagedArchiveUnit(
            fileUnitId,
            sourceId,
            new LogicalPath("file"),
            new AuthoredArchiveSpecOverride(PortableArchiveFormat.Zip, null, new PrivacyProtection()),
            new HistoryOverrideDisabled());

        return new PortableBackupPlan(
            planId,
            "Plan",
            "Must not enter execution snapshot",
            new PortableSemanticsPins(1, 1, 1),
            [new PortableBackupSource(sourceId, "Source", new LogicalPath("source-output"))],
            new GlobalRulesSnapshot(
                [new BackupRule(RuleAction.Exclude, "global")],
                new GlobalRuleProvenance("library", "Library", "1")),
            [new BackupRule(RuleAction.Include, "plan")],
            defaultSpec,
            [ui, file],
            [new PortableSecretSlot(slotId, "Secret")],
            PortableLinkPolicy.Preserve,
            PortableChangeDetectionMode.Strict,
            new HistoryEnabled(new KeepAllRetention()),
            new AutomaticSchedule([new DailyScheduleTrigger(new TimeOnly(2, 0))], PortableMissedRunPolicy.Skip),
            [new PortableExternalSource(externalId, "External", PortableExternalSourceKind.File, uiUnitId, new LogicalPath("external/file"))]);
    }

    private static DevicePlanBindingFacts RequiredBindings(
        PortableBackupPlan plan,
        ResolvedPhysicalPath? currentRoot = null,
        ResolvedPhysicalPath? historyRoot = null,
        IEnumerable<SecretBindingFact>? secrets = null) =>
        new(
            plan.Id,
            Device,
            [new SourceBindingFact(plan.Sources[0].Id, new ResolvedPhysicalPath("/data/source", "/data/source"))],
            currentRoot ?? new ResolvedPhysicalPath("/backup/current", "/backup/current"),
            historyRoot,
            [new ExternalSourceBindingFact(plan.ExternalSources[0].Id, new ResolvedPhysicalPath("/external/input", "/external/input"))],
            secrets ?? []);
}
