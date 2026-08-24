using System.Text;
using System.Text.Json.Nodes;
using StowCrate.Infrastructure.Configuration.BackupPlans;
using StowCrate.Infrastructure.Configuration.BackupPlans.V1;

namespace StowCrate.Infrastructure.Tests.Configuration;

public sealed class BackupPlanDocumentV1ReaderTests
{
    private static readonly string FixtureRoot = Path.Combine(
        AppContext.BaseDirectory,
        "schemas",
        "fixtures",
        "backupplan-v1");

    private readonly BackupPlanDocumentV1Reader reader = new();

    public static TheoryData<string> ValidFixtures => DiscoverFixtures("valid");
    public static TheoryData<string> InvalidFixtures => DiscoverFixtures("invalid");

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public void ReadsEveryValidSchemaFixture(string fixturePath)
    {
        var result = reader.Read(File.ReadAllBytes(fixturePath));

        Assert.True(result.IsSuccess, $"{Path.GetFileName(fixturePath)}: {result.Error}");
        Assert.NotNull(result.Document);
        Assert.Equal(1, result.Document.SchemaVersion);
    }

    [Theory]
    [MemberData(nameof(InvalidFixtures))]
    public void RejectsEveryInvalidSchemaFixture(string fixturePath)
    {
        var result = reader.Read(File.ReadAllBytes(fixturePath));

        Assert.False(result.IsSuccess, Path.GetFileName(fixturePath));
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void MaterializesClosedDiscriminatedUnions()
    {
        var path = Path.Combine(FixtureRoot, "valid", "secure-schedule-external.json");

        var result = reader.Read(File.ReadAllBytes(path));

        var document = Assert.IsType<BackupPlanDocumentV1>(result.Document);
        Assert.IsType<SecureProtectionV1>(document.ArchiveSpecDefault.Protection);
        var schedule = Assert.IsType<AutomaticScheduleV1>(document.Schedule);
        Assert.Contains(schedule.Triggers, trigger => trigger is WeeklyTriggerV1);
        Assert.NotEmpty(document.ExternalSources);
    }

    [Fact]
    public void AcceptsUtf8BomFromStream()
    {
        var bytes = ValidFixtureBytes();
        using var stream = new MemoryStream([.. Encoding.UTF8.GetPreamble(), .. bytes]);

        var result = reader.Read(stream);

        Assert.True(result.IsSuccess, result.Error?.ToString());
    }

    [Fact]
    public async Task ReadsAsynchronouslyFromStream()
    {
        await using var stream = new MemoryStream(ValidFixtureBytes());

        var result = await reader.ReadAsync(stream, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.ToString());
    }

    [Fact]
    public void RejectsInvalidUtf8BeforeJsonParsing()
    {
        var bytes = ValidFixtureBytes().ToList();
        bytes.Insert(1, 0xff);

        var result = reader.Read(bytes.ToArray());

        AssertError(BackupPlanDocumentErrorCode.InvalidUtf8, result);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,/*comment*/\"x\":1}")]
    [InlineData("{\"schemaVersion\":1,}")]
    [InlineData("{\"schemaVersion\":1")]
    public void RejectsNonStrictOrMalformedJson(string json)
    {
        var result = reader.Read(Encoding.UTF8.GetBytes(json));

        AssertError(BackupPlanDocumentErrorCode.MalformedJson, result);
    }

    [Fact]
    public void RejectsDuplicateRootProperty()
    {
        var json = ValidFixtureText().Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1, \"schemaVersion\": 1,",
            StringComparison.Ordinal);

        var result = reader.Read(Encoding.UTF8.GetBytes(json));

        AssertError(BackupPlanDocumentErrorCode.DuplicateProperty, result);
    }

    [Fact]
    public void RejectsDuplicateNestedProperty()
    {
        var json = ValidFixtureText().Replace(
            "\"mode\": \"exclude\",",
            "\"mode\": \"exclude\", \"mode\": \"exclude\",",
            StringComparison.Ordinal);

        var result = reader.Read(Encoding.UTF8.GetBytes(json));

        AssertError(BackupPlanDocumentErrorCode.DuplicateProperty, result);
    }

    [Fact]
    public void PropertyMatchingIsCaseSensitive()
    {
        var json = ValidFixtureText().Replace("\"planId\"", "\"PlanId\"", StringComparison.Ordinal);

        var result = reader.Read(Encoding.UTF8.GetBytes(json));

        AssertError(BackupPlanDocumentErrorCode.SchemaValidationFailed, result);
    }

    [Fact]
    public void MissingSchemaVersionIsReportedBeforeV1Validation()
    {
        var root = ValidFixtureNode();
        root.Remove("schemaVersion");

        var result = Read(root);

        AssertError(BackupPlanDocumentErrorCode.MissingSchemaVersion, result);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("string")]
    [InlineData("fraction")]
    public void InvalidSchemaVersionIsNotReportedAsUnsupported(string kind)
    {
        var root = ValidFixtureNode();
        root["schemaVersion"] = kind switch
        {
            "zero" => JsonValue.Create(0),
            "string" => JsonValue.Create("1"),
            _ => JsonValue.Create(1.5)
        };

        var result = Read(root);

        AssertError(BackupPlanDocumentErrorCode.InvalidSchemaVersion, result);
    }

    [Fact]
    public void FutureSchemaVersionIsReportedAsUnsupportedBeforeV1Validation()
    {
        var root = ValidFixtureNode();
        root["schemaVersion"] = 2;
        root["futureProperty"] = true;

        var result = Read(root);

        AssertError(BackupPlanDocumentErrorCode.UnsupportedSchemaVersion, result);
    }

    [Fact]
    public void UnsupportedSemanticsPinRemainsStructurallyValidForV1()
    {
        var root = ValidFixtureNode();
        root["semantics"]!["archive"] = 2;

        var result = Read(root);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(2, result.Document!.Semantics.Archive);
    }

    private BackupPlanDocumentReadResult<BackupPlanDocumentV1> Read(JsonObject root) =>
        reader.Read(Encoding.UTF8.GetBytes(root.ToJsonString()));

    private static void AssertError(
        BackupPlanDocumentErrorCode expected,
        BackupPlanDocumentReadResult<BackupPlanDocumentV1> result)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Error?.Code);
    }

    private static byte[] ValidFixtureBytes() => File.ReadAllBytes(ValidFixturePath());
    private static string ValidFixtureText() => File.ReadAllText(ValidFixturePath());
    private static JsonObject ValidFixtureNode() => JsonNode.Parse(ValidFixtureText())!.AsObject();
    private static string ValidFixturePath() => Path.Combine(FixtureRoot, "valid", "minimal-ui-managed.json");

    private static TheoryData<string> DiscoverFixtures(string kind)
    {
        var paths = Directory.GetFiles(Path.Combine(FixtureRoot, kind), "*.json");
        Array.Sort(paths, StringComparer.Ordinal);
        var data = new TheoryData<string>();
        foreach (var path in paths)
        {
            data.Add(path);
        }

        return data;
    }
}
