using StowCrate.Core.BackupPlans;
using StowCrate.Infrastructure.Configuration.BackupPlans.V1;

namespace StowCrate.Infrastructure.Tests.Configuration;

public sealed class BackupPlanDocumentV1SemanticTests
{
    private static readonly string FixtureRoot = Path.Combine(
        AppContext.BaseDirectory,
        "schemas",
        "fixtures",
        "backupplan-v1");

    private readonly BackupPlanDocumentV1Reader reader = new();

    public static TheoryData<string, BackupPlanSemanticErrorCode[]> SemanticInvalidFixtures => new()
    {
        {
            "unsupported-semantics.json",
            [BackupPlanSemanticErrorCode.UnsupportedDocumentSemantics]
        },
        {
            "identity-and-references.json",
            [
                BackupPlanSemanticErrorCode.DuplicateSourceId,
                BackupPlanSemanticErrorCode.DuplicateArchiveUnitId,
                BackupPlanSemanticErrorCode.DuplicateExternalSourceId,
                BackupPlanSemanticErrorCode.DuplicateSecretSlotId,
                BackupPlanSemanticErrorCode.UnknownSourceReference,
                BackupPlanSemanticErrorCode.UnknownArchiveUnitReference,
                BackupPlanSemanticErrorCode.UnknownSecretSlotReference,
                BackupPlanSemanticErrorCode.DuplicateArchiveUnitDeclaration
            ]
        },
        {
            "invalid-rule-patterns.json",
            [BackupPlanSemanticErrorCode.InvalidRulePattern]
        },
        {
            "duplicate-schedule-trigger.json",
            [BackupPlanSemanticErrorCode.DuplicateScheduleTrigger]
        },
        {
            "external-ownership-collision.json",
            [BackupPlanSemanticErrorCode.ExternalOwnershipCollision]
        },
        {
            "external-declared-child-boundary.json",
            [BackupPlanSemanticErrorCode.ExternalCrossesDeclaredChildBoundary]
        }
    };

    [Theory]
    [MemberData(nameof(SemanticInvalidFixtures))]
    public void SchemaValidFixtureIsRejectedForExpectedSemanticReason(
        string fixtureName,
        BackupPlanSemanticErrorCode[] expectedCodes)
    {
        var documentResult = reader.Read(File.ReadAllBytes(SemanticFixturePath(fixtureName)));
        Assert.True(documentResult.IsSuccess, $"Fixture must pass Draft 2020-12 Schema first: {documentResult.Error}");

        var semanticResult = BackupPlanDocumentV1Mapper.Map(documentResult.Document!);

        Assert.False(semanticResult.IsSuccess);
        Assert.Null(semanticResult.Plan);
        foreach (var expectedCode in expectedCodes)
        {
            Assert.Contains(semanticResult.Errors, error => error.Code == expectedCode);
        }
    }

    [Theory]
    [InlineData("minimal-ui-managed.json")]
    [InlineData("secure-schedule-external.json")]
    public void ValidDocumentMapsToIndependentFrozenPortableAggregate(string fixtureName)
    {
        var documentResult = reader.Read(File.ReadAllBytes(Path.Combine(FixtureRoot, "valid", fixtureName)));

        var document = documentResult.Document! with
        {
            Semantics = new PortableSemanticsPinsV1(1, 1, 1)
        };

        var result = BackupPlanDocumentV1Mapper.Map(document);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        Assert.NotNull(result.Plan);
        Assert.Equal(1, result.Plan.Semantics.Archive);
    }

    [Fact]
    public void MapperPreservesAuthoredDefaultsOverridesAndInheritIntent()
    {
        var documentResult = reader.Read(File.ReadAllBytes(Path.Combine(FixtureRoot, "valid", "file-managed-overrides.json")));

        var document = documentResult.Document! with
        {
            Semantics = new PortableSemanticsPinsV1(1, 1, 1)
        };

        var result = BackupPlanDocumentV1Mapper.Map(document);

        var plan = Assert.IsType<PortableBackupPlan>(result.Plan);
        Assert.NotNull(plan.ArchiveSpecDefault);
        Assert.Contains(plan.ArchiveUnits, unit => unit.ArchiveSpecOverride is not null);
        Assert.Contains(plan.ArchiveUnits, unit => unit.HistoryOverride is not null);
    }

    [Fact]
    public void UnsupportedSemanticsStopsBeforeRuleGrammarInterpretation()
    {
        var documentResult = reader.Read(File.ReadAllBytes(SemanticFixturePath("unsupported-semantics.json")));
        var document = documentResult.Document! with
        {
            PlanRules = [new RuleV1(RuleActionV1.Exclude, "[")]
        };

        var result = BackupPlanDocumentV1Mapper.Map(document);

        Assert.All(result.Errors, error => Assert.Equal(BackupPlanSemanticErrorCode.UnsupportedDocumentSemantics, error.Code));
    }

    private static string SemanticFixturePath(string fixtureName) =>
        Path.Combine(FixtureRoot, "semantic-invalid", fixtureName);
}
