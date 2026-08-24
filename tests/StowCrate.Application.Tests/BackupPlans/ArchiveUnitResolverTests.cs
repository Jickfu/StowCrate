using StowCrate.Application.BackupPlans.ArchiveUnits;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;
using StowCrate.Core.ChangeDetection;
using System.Text;

namespace StowCrate.Application.Tests.BackupPlans;

public sealed class ArchiveUnitResolverTests
{
    private static readonly SourceId SourceId = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly ArchiveUnitId UiId = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));
    private static readonly ArchiveUnitId FileId = new(Guid.Parse("33333333-3333-4333-8333-333333333333"));
    private static readonly ExternalSourceId ExternalId = new(Guid.Parse("44444444-4444-4444-8444-444444444444"));
    private static readonly ArchiveUnitId GeneratedId = new(Guid.Parse("55555555-5555-4555-8555-555555555555"));

    [Fact]
    public void ResolvesUiDeclaredFileDeclaredAndUndeclaredUnitsWithPendingRegistration()
    {
        var plan = CreatePlan();
        var source = SourceObservation(
            Marker("declared", $"@id {FileId.Value:D}\n@mode include-only\n!keep/**", "declared-fingerprint"),
            Marker("new", "*.tmp", "new-fingerprint"));
        var external = ExternalObservation(Entry(".backupignore", "external payload only"));
        var resolver = new ArchiveUnitResolver(new FixedIdGenerator(GeneratedId));

        var result = resolver.Resolve(plan, [source], [external], []);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues));
        Assert.Equal(3, result.ResolvedSet!.Units.Length);
        var ui = Assert.Single(result.ResolvedSet.Units.Where(unit => unit.ArchiveUnitId == UiId));
        Assert.Equal(RuleSource.UiManaged, ui.RuleSource);
        Assert.Null(ui.RuleSourceObservationFingerprint);
        var declared = Assert.Single(result.ResolvedSet.Units.Where(unit => unit.ArchiveUnitId == FileId));
        Assert.Equal(RuleSource.FileManaged, declared.RuleSource);
        Assert.Equal(RawSha($"@id {FileId.Value:D}\n@mode include-only\n!keep/**"), declared.RuleSourceObservationFingerprint);
        Assert.Equal(RuleMode.IncludeOnly, declared.LocalRuleSet.Mode);
        Assert.Equal(PortableArchiveFormat.Zip, declared.ArchiveSpec.Format);
        var generated = Assert.Single(result.ResolvedSet.Units.Where(unit => unit.ArchiveUnitId == GeneratedId));
        Assert.Equal(PortableArchiveFormat.SevenZip, generated.ArchiveSpec.Format);
        Assert.Equal(RawSha("*.tmp"), generated.RuleSourceObservationFingerprint);
        var pending = Assert.Single(result.PendingRegistrations);
        Assert.Equal(new LogicalPath("new"), pending.Path);
        Assert.True(result.RequiresDurableRegistrationCommit);
        Assert.Equal(3, result.ResolvedSet.Units.Length); // external .backupignore never discovers a unit
    }

    [Fact]
    public void ExistingRegistrationProvidesIdentityWithoutPendingWrite()
    {
        var plan = CreatePlan(includeFileDeclaration: false);
        var registration = new LocalArchiveUnitIdentityRegistration(SourceId, new LogicalPath("new"), GeneratedId);

        var result = new ArchiveUnitResolver(new FixedIdGenerator(FileId)).Resolve(
            plan,
            [SourceObservation(Marker("new", "", "fp"))],
            [ExternalObservation()],
            [registration]);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues));
        Assert.Contains(result.ResolvedSet!.Units, unit => unit.ArchiveUnitId == GeneratedId);
        Assert.Empty(result.PendingRegistrations);
    }

    [Theory]
    [InlineData("id-mismatch", ArchiveUnitResolutionIssueCode.BackupIgnoreDeclarationIdMismatch)]
    [InlineData("duplicate-id", ArchiveUnitResolutionIssueCode.DuplicateObservedArchiveUnitId)]
    [InlineData("relocated", ArchiveUnitResolutionIssueCode.ArchiveUnitRelocated)]
    [InlineData("ui-conflict", ArchiveUnitResolutionIssueCode.RuleSourceConflict)]
    [InlineData("missing-file", ArchiveUnitResolutionIssueCode.MissingFileManagedRuleSource)]
    [InlineData("registration-conflict", ArchiveUnitResolutionIssueCode.IdentityConflict)]
    public void IdentityAndRuleSourceContradictionsFailExplicitly(
        string scenario,
        ArchiveUnitResolutionIssueCode expected)
    {
        var plan = CreatePlan();
        var otherId = GeneratedId;
        var entries = scenario switch
        {
            "id-mismatch" => new[] { Marker("declared", $"@id {otherId.Value:D}", "fp") },
            "duplicate-id" => new[] { Marker("declared", $"@id {FileId.Value:D}", "a"), Marker("other", $"@id {FileId.Value:D}", "b") },
            "relocated" => new[] { Marker("moved", $"@id {FileId.Value:D}", "fp") },
            "ui-conflict" => new[] { Marker("ui", "", "fp"), Marker("declared", $"@id {FileId.Value:D}", "declared") },
            "missing-file" => Array.Empty<ObservedFileSystemEntry>(),
            "registration-conflict" => new[] { Marker("declared", $"@id {FileId.Value:D}", "fp") },
            _ => throw new InvalidOperationException()
        };
        var registrations = scenario == "registration-conflict"
            ? new[] { new LocalArchiveUnitIdentityRegistration(SourceId, new LogicalPath("declared"), otherId) }
            : [];

        var result = new ArchiveUnitResolver(new FixedIdGenerator(GeneratedId)).Resolve(
            plan,
            [SourceObservation(entries)],
            [ExternalObservation()],
            registrations);

        Assert.Null(result.ResolvedSet);
        Assert.Contains(result.Issues, issue => issue.Code == expected);
    }

    [Fact]
    public void IncompleteObservationProducesPreviewCapableButUnsuccessfulResolvedSet()
    {
        var plan = CreatePlan(includeFileDeclaration: false);
        var incomplete = SourceObservation([], ObservationCompleteness.Incomplete);

        var result = new ArchiveUnitResolver(new FixedIdGenerator(GeneratedId)).Resolve(
            plan,
            [incomplete],
            [ExternalObservation()],
            []);

        Assert.NotNull(result.ResolvedSet);
        Assert.True(result.CanPreview);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Code == ArchiveUnitResolutionIssueCode.IncompleteObservation);
    }

    [Fact]
    public void BuildsTypedBoundariesAndRejectsExternalDestinationAcrossDiscoveredChild()
    {
        var plan = CreatePlan(externalTarget: FileId, externalDestination: "child/injected");
        var childId = GeneratedId;
        var source = SourceObservation(
            Marker("declared", $"@id {FileId.Value:D}", "parent"),
            Marker("declared/child", $"@id {childId.Value:D}", "child"));

        var result = new ArchiveUnitResolver(new FixedIdGenerator(UiId)).Resolve(
            plan,
            [source],
            [ExternalObservation()],
            []);

        Assert.Null(result.ResolvedSet);
        Assert.Contains(result.Issues, issue => issue.Code == ArchiveUnitResolutionIssueCode.ExternalCrossesDiscoveredChildBoundary);
    }

    private static ResolvedPlanSnapshot CreatePlan(
        bool includeFileDeclaration = true,
        ArchiveUnitId? externalTarget = null,
        string externalDestination = "external/file")
    {
        var defaultArchive = new EffectiveArchiveSpec(
            PortableArchiveFormat.SevenZip,
            PortableCompressionPreset.Standard,
            new NoProtection());
        var defaultHistory = new EffectiveHistoryDisabled();
        var ui = new PreparedUiManagedArchiveUnit(
            UiId,
            SourceId,
            new LogicalPath("ui"),
            defaultArchive,
            defaultHistory,
            new RuleSet(rules: [new BackupRule(RuleAction.Exclude, "ui-rule")]));
        var units = new List<PreparedDeclaredArchiveUnit> { ui };
        if (includeFileDeclaration)
        {
            units.Add(new PreparedFileManagedArchiveUnit(
                FileId,
                SourceId,
                new LogicalPath("declared"),
                new EffectiveArchiveSpec(PortableArchiveFormat.Zip, PortableCompressionPreset.Standard, new PrivacyProtection()),
                new EffectiveHistoryEnabled(new KeepAllRetention())));
        }

        return new ResolvedPlanSnapshot(
            new PlanId(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")),
            new DeviceId(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")),
            new PortableSemanticsPins(1, 1, 1),
            [new ResolvedBackupSource(SourceId, new LogicalPath("out"), new ResolvedPhysicalPath("/source", "/source"))],
            new ResolvedPhysicalPath("/current", "/current"),
            null,
            [new BackupRule(RuleAction.Exclude, "global")],
            [new BackupRule(RuleAction.Exclude, "plan")],
            units,
            new DefaultUnitPolicy(defaultArchive, defaultHistory),
            PortableLinkPolicy.Preserve,
            PortableChangeDetectionMode.Standard,
            [new ResolvedExternalSource(
                ExternalId,
                PortableExternalSourceKind.Directory,
                externalTarget ?? UiId,
                new LogicalPath(externalDestination),
                new ResolvedPhysicalPath("/external", "/external"))],
            []);
    }

    private static SourceObservationSnapshot SourceObservation(
        params ObservedFileSystemEntry[] entries) => SourceObservation(entries, ObservationCompleteness.Complete);

    private static SourceObservationSnapshot SourceObservation(
        IEnumerable<ObservedFileSystemEntry> entries,
        ObservationCompleteness completeness) =>
        new(SourceId, CaseSensitivity.Sensitive, entries, [], completeness);

    private static ExternalSourceSnapshot ExternalObservation(params ObservedFileSystemEntry[] entries) =>
        new(ExternalId, ExternalObservedRootKind.Directory, entries, [], ObservationCompleteness.Complete);

    private static ObservedFileSystemEntry Marker(string root, string content, string fingerprint) =>
        Entry($"{root}/.backupignore", content, fingerprint);

    private static ObservedFileSystemEntry Entry(string path, string? content = null, string fingerprint = "fp") =>
        new(new LogicalPath(path), FileSystemEntryKind.File, 0, content, ObservedContentIdentity.MetadataV1,
            content is null ? null : RawSha(content), null, null, SourceMetadata.None);

    private static Sha256Digest RawSha(string content) => Sha256Digest.Hash(Encoding.UTF8.GetBytes(content));

    private sealed class FixedIdGenerator(ArchiveUnitId id) : IArchiveUnitIdGenerator
    {
        public ArchiveUnitId Generate() => id;
    }
}
