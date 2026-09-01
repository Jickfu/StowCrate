using System.Collections.Immutable;
using StowCrate.Application.BackupPlans.ArchiveUnits;
using StowCrate.Application.BackupPlans.Candidates;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Application.Tests.BackupPlans;

public sealed class CandidateArchiveCompositionTests
{
    private static readonly PlanId PlanId = new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly SourceId SourceId = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly ArchiveUnitId UnitId = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));
    private static readonly ArchiveUnitId ChildId = new(Guid.Parse("33333333-3333-4333-8333-333333333333"));
    private static readonly ExternalSourceId ExternalId = new(Guid.Parse("44444444-4444-4444-8444-444444444444"));
    private static readonly SecretSlotId SecretId = new(Guid.Parse("55555555-5555-4555-8555-555555555555"));

    [Fact]
    public void CandidatePreservesSemanticsSelectsControlEntryAndStopsAtChildBoundary()
    {
        var semantics = new PortableSemanticsPins(7, 8, 9);
        var plan = Plan(semantics: semantics, includeExternal: false);
        var parent = Unit(UnitId, "project", RuleSource.FileManaged);
        var child = Unit(ChildId, "project/child", RuleSource.FileManaged, parent: UnitId);
        parent = parent with { ChildArchiveUnitIds = [ChildId] };
        var source = Source(
            Entry("project/.backupignore", content: "*.tmp"),
            Entry("project/keep.txt", lastWriteTimeUtc: DateTimeOffset.UnixEpoch),
            Entry("project/drop.tmp"),
            Entry("project/child/owned.txt"));
        var set = Units([parent, child], source, External());

        var result = new CandidateArchiveComposer().Compose(plan, set, []);

        Assert.Same(semantics, result.Semantics);
        var archive = Assert.Single(result.Archives.Where(item => item.Unit.ArchiveUnitId == UnitId));
        Assert.Contains(archive.Entries, item => item.ArchivePath.Value == ".backupignore");
        Assert.Contains(archive.Entries, item => item.ArchivePath.Value == "keep.txt");
        Assert.Equal(DateTimeOffset.UnixEpoch, archive.Entries.Single(item => item.ArchivePath.Value == "keep.txt").LastWriteTimeUtc);
        Assert.DoesNotContain(archive.Entries, item => item.ArchivePath.Value == "drop.tmp");
        Assert.DoesNotContain(archive.Entries, item => item.ArchivePath.Value.StartsWith("child/", StringComparison.Ordinal));
        Assert.Equal("out/project.7z", archive.OutputRelativePath.Value);
        Assert.Equal(8, archive.GeneratedMetadata.ArchiveSemanticsVersion);
    }

    [Fact]
    public void ExternalBypassesRulesButUnifiedOwnershipCollisionIsFatal()
    {
        var plan = Plan(externalDestination: "keep.txt");
        var set = Units(
            [Unit(UnitId, "project", RuleSource.UiManaged)],
            Source(Entry("project/keep.txt")),
            External(Entry("payload.txt")));

        var result = new CandidateArchiveComposer().Compose(plan, set, []);

        Assert.Contains(result.Issues, issue => issue.Code == CandidateCompositionIssueCode.EntryOwnershipCollision);
        var archive = Assert.Single(result.Archives);
        Assert.Contains(archive.Entries, item => item.OwnerKind == CandidateEntryOwnerKind.External && item.ArchivePath.Value == "keep.txt/payload.txt");
    }

    [Fact]
    public void IncompleteObservationStillProducesDiagnosticCandidateButCannotExecute()
    {
        var plan = Plan(includeExternal: false);
        var set = Units([Unit(UnitId, "project", RuleSource.UiManaged)], Source(ObservationCompleteness.Incomplete, Entry("project/a.txt")), External());
        var candidates = new CandidateArchiveComposer().Compose(plan, set, []);

        var readiness = new ExecutionReadinessEvaluator().Evaluate(plan, candidates, [], new SupportedCapabilities());

        Assert.Single(candidates.Archives);
        Assert.Contains(candidates.Issues, issue => issue.Code == CandidateCompositionIssueCode.IncompleteObservation);
        Assert.False(readiness.CanExecute);
        Assert.Contains(readiness.Blockers, blocker => blocker.Code == ExecutionReadinessBlockerCode.IncompleteObservation);
    }

    [Fact]
    public void ReadinessChecksOnlyResolvedConditionalRequirementsAndPendingRegistration()
    {
        var secureHistory = Unit(
            UnitId,
            "project",
            RuleSource.UiManaged,
            archive: new EffectiveArchiveSpec(PortableArchiveFormat.Zip, PortableCompressionPreset.Extreme, new SecureProtection(SecretId)),
            history: new EffectiveHistoryEnabled(new KeepAllRetention()));
        var plan = Plan(includeExternal: false);
        var pending = new PendingArchiveUnitRegistration(SourceId, new LogicalPath("project"), UnitId);
        var candidates = new CandidateArchiveComposer().Compose(plan, Units([secureHistory], Source(Entry("project/a")), External()), [pending]);

        var result = new ExecutionReadinessEvaluator().Evaluate(plan, candidates, [], new UnsupportedCapabilities());

        Assert.False(result.CanExecute);
        Assert.Contains(result.Blockers, item => item.Code == ExecutionReadinessBlockerCode.MissingHistoryRootBinding);
        Assert.Contains(result.Blockers, item => item.Code == ExecutionReadinessBlockerCode.MissingSecretBinding && item.SecretSlotId == SecretId);
        Assert.Contains(result.Blockers, item => item.Code == ExecutionReadinessBlockerCode.UnsupportedArchiveCapability);
        Assert.Contains(result.Blockers, item => item.Code == ExecutionReadinessBlockerCode.PendingArchiveUnitRegistration);
    }

    [Fact]
    public void ReadySetFreezesCapabilityHistoryAndSecretRevisionWithoutSecretLocator()
    {
        var archive = Unit(UnitId, "project", RuleSource.UiManaged,
            archive: new EffectiveArchiveSpec(PortableArchiveFormat.SevenZip, PortableCompressionPreset.Standard, new SecureProtection(SecretId)));
        var plan = Plan(includeExternal: false, historyRoot: new ResolvedPhysicalPath("/history", "/history"), secrets: [new SecretBindingFact(SecretId, new SecretRevision(4))]);
        var pending = new PendingArchiveUnitRegistration(SourceId, new LogicalPath("project"), UnitId);
        var candidates = new CandidateArchiveComposer().Compose(plan, Units([archive], Source(Entry("project/a")), External()), [pending]);
        var committed = new CommittedArchiveUnitRegistrationFact(SourceId, pending.Path, UnitId);

        var result = new ExecutionReadinessEvaluator().Evaluate(plan, candidates, [committed], new SupportedCapabilities());

        Assert.True(result.CanExecute, string.Join(Environment.NewLine, result.Blockers));
        var ready = Assert.Single(result.ReadySet!.Archives);
        Assert.Equal(4, ready.SecureRequirement!.SecretRevision.Value);
        Assert.Equal("test-capability-v1", ready.Capability.CapabilitySemantics);
    }

    [Fact]
    public void StrictFingerprintRequiresFullHashWhileStandardUsesVersionedMetadataIdentity()
    {
        var standardPlan = Plan(includeExternal: false);
        var unit = Unit(UnitId, "project", RuleSource.UiManaged);
        var standardCandidates = new CandidateArchiveComposer().Compose(standardPlan, Units([unit], Source(Entry("project/a")), External()), []);
        var standardReady = new ExecutionReadinessEvaluator().Evaluate(standardPlan, standardCandidates, [], new SupportedCapabilities()).ReadySet!.Archives[0];
        var standard = CandidateFingerprintCalculator.Compute(standardPlan, standardReady, new StorageBindingFingerprintFacts(1, "fs-capability"));

        var strictPlan = Plan(includeExternal: false, changeDetection: PortableChangeDetectionMode.Strict);
        var strictCandidates = new CandidateArchiveComposer().Compose(strictPlan, Units([unit], Source(Entry("project/a")), External()), []);
        var strictReady = new ExecutionReadinessEvaluator().Evaluate(strictPlan, strictCandidates, [], new SupportedCapabilities()).ReadySet!.Archives[0];
        var missingHash = CandidateFingerprintCalculator.Compute(strictPlan, strictReady, new StorageBindingFingerprintFacts(1, "fs-capability"));

        Assert.NotNull(standard.Fingerprints);
        Assert.Contains(missingHash.Errors, error => error.Code == CandidateFingerprintErrorCode.MissingStrictContentHash);
    }

    [Fact]
    public void LocalSecretRevisionAndCapabilityAffectArchiveSpecButNotExecutionSemantic()
    {
        var plan = Plan(includeExternal: false);
        var unit = Unit(UnitId, "project", RuleSource.UiManaged,
            archive: new EffectiveArchiveSpec(PortableArchiveFormat.SevenZip, PortableCompressionPreset.Standard, new SecureProtection(SecretId)));
        var candidate = new CandidateArchiveComposer().Compose(plan, Units([unit], Source(Entry("project/a")), External()), []).Archives[0];
        var first = new ExecutionReadyArchive(candidate, Capability(unit.ArchiveSpec, "cap-a"), unit.History, new SecureRevisionRequirement(SecretId, new SecretRevision(1)));
        var second = new ExecutionReadyArchive(candidate, Capability(unit.ArchiveSpec, "cap-b"), unit.History, new SecureRevisionRequirement(SecretId, new SecretRevision(2)));

        var firstFingerprints = CandidateFingerprintCalculator.Compute(plan, first, new StorageBindingFingerprintFacts(1, "fs")).Fingerprints!;
        var secondFingerprints = CandidateFingerprintCalculator.Compute(plan, second, new StorageBindingFingerprintFacts(1, "fs")).Fingerprints!;

        Assert.NotEqual(firstFingerprints.ArchiveSpec, secondFingerprints.ArchiveSpec);
        Assert.Equal(firstFingerprints.ExecutionSemantic, secondFingerprints.ExecutionSemantic);
    }

    private static ResolvedPlanSnapshot Plan(
        PortableSemanticsPins? semantics = null,
        bool includeExternal = true,
        string externalDestination = "external",
        ResolvedPhysicalPath? historyRoot = null,
        IEnumerable<SecretBindingFact>? secrets = null,
        PortableChangeDetectionMode changeDetection = PortableChangeDetectionMode.Standard)
    {
        var external = includeExternal
            ? new[] { new ResolvedExternalSource(ExternalId, PortableExternalSourceKind.Directory, UnitId, new LogicalPath(externalDestination), new ResolvedPhysicalPath("/external", "/external")) }
            : [];
        var spec = new EffectiveArchiveSpec(PortableArchiveFormat.SevenZip, PortableCompressionPreset.Standard, new NoProtection());
        return new ResolvedPlanSnapshot(
            PlanId,
            new DeviceId(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")),
            semantics ?? new PortableSemanticsPins(1, 1, 1),
            [new ResolvedBackupSource(SourceId, new LogicalPath("out"), new ResolvedPhysicalPath("/source", "/source"))],
            new ResolvedPhysicalPath("/current", "/current"),
            historyRoot,
            [], [], [],
            new DefaultUnitPolicy(spec, new EffectiveHistoryDisabled()),
            PortableLinkPolicy.Preserve,
            changeDetection,
            external,
            secrets ?? []);
    }

    private static ResolvedArchiveUnit Unit(
        ArchiveUnitId id,
        string root,
        RuleSource ruleSource,
        ArchiveUnitId? parent = null,
        EffectiveArchiveSpec? archive = null,
        EffectiveHistoryPolicy? history = null)
    {
        var rules = ruleSource is RuleSource.FileManaged
            ? new RuleSet(rules: [new BackupRule(RuleAction.Exclude, "*.tmp")])
            : new RuleSet();
        return new ResolvedArchiveUnit(
            id, SourceId, new LogicalPath(root), ruleSource, rules,
            new EffectiveRuleSet([], [], rules, CaseSensitivity.Sensitive),
            archive ?? new EffectiveArchiveSpec(PortableArchiveFormat.SevenZip, PortableCompressionPreset.Standard, new NoProtection()),
            history ?? new EffectiveHistoryDisabled(),
            ruleSource is RuleSource.FileManaged ? Sha256Digest.Hash("rules-fp"u8) : null,
            parent,
            ImmutableArray<ArchiveUnitId>.Empty);
    }

    private static ResolvedArchiveUnitSet Units(IEnumerable<ResolvedArchiveUnit> units, SourceObservationSnapshot source, ExternalSourceSnapshot external) =>
        new(units, [source], [external]);

    private static SourceObservationSnapshot Source(params ObservedFileSystemEntry[] entries) => Source(ObservationCompleteness.Complete, entries);
    private static SourceObservationSnapshot Source(ObservationCompleteness completeness, params ObservedFileSystemEntry[] entries) => new(SourceId, CaseSensitivity.Sensitive, entries, [], completeness);
    private static ExternalSourceSnapshot External(params ObservedFileSystemEntry[] entries) => new(ExternalId, ExternalObservedRootKind.Directory, entries, [], ObservationCompleteness.Complete);
    private static ObservedFileSystemEntry Entry(string path, string? content = null, DateTimeOffset? lastWriteTimeUtc = null) => new(new LogicalPath(path), FileSystemEntryKind.File, 1, content, ObservedContentIdentity.MetadataV1, null, lastWriteTimeUtc, null, SourceMetadata.None);

    private sealed class SupportedCapabilities : IArchiveCapabilityResolver
    {
        public ArchiveCapabilityResolution Resolve(EffectiveArchiveSpec archiveSpec, int archiveSemanticsVersion) => new(Capability(archiveSpec, "test-capability-v1"), null);
    }

    private sealed class UnsupportedCapabilities : IArchiveCapabilityResolver
    {
        public ArchiveCapabilityResolution Resolve(EffectiveArchiveSpec archiveSpec, int archiveSemanticsVersion) => new(null, "unsupported test combination");
    }

    private static ResolvedArchiveCapability Capability(EffectiveArchiveSpec spec, string semantics) =>
        new(spec.Format, spec.CompressionPreset, spec.Protection, ArchiveLinkSemantics.PreserveSymbolicLinks, ArchiveMetadataSemantics.PortableBasic, true, semantics);
}
