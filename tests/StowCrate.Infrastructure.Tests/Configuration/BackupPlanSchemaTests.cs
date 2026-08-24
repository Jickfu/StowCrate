using System.Text.Json.Nodes;
using StowCrate.Infrastructure.Configuration.BackupPlans.V1;

namespace StowCrate.Infrastructure.Tests.Configuration;

public sealed class BackupPlanSchemaTests
{
    private static readonly string SchemaRoot = Path.Combine(AppContext.BaseDirectory, "schemas");
    private static readonly BackupPlanDocumentV1Reader Reader = new();

    public static TheoryData<string> ValidFixtures => DiscoverFixtures("valid");

    public static TheoryData<string> InvalidFixtures => DiscoverFixtures("invalid");

    [Fact]
    public void SchemaDeclaresDraft202012AndOmitsUnconfiguredCanonicalId()
    {
        var schemaDocument = JsonNode.Parse(
            File.ReadAllText(Path.Combine(SchemaRoot, "backupplan-v1.schema.json")))!.AsObject();

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schemaDocument["$schema"]!.GetValue<string>());
        Assert.False(schemaDocument.ContainsKey("$id"));
    }

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public void ValidFixturePassesSchemaValidation(string fixturePath)
    {
        var result = Reader.Read(File.ReadAllBytes(fixturePath));

        Assert.True(result.IsSuccess, $"Expected valid fixture '{Path.GetFileName(fixturePath)}' to pass.\n{result.Error}");
    }

    [Theory]
    [MemberData(nameof(InvalidFixtures))]
    public void InvalidFixtureFailsForItsNamedStructuralReason(string fixturePath)
    {
        var result = Reader.Read(File.ReadAllBytes(fixturePath));

        Assert.False(result.IsSuccess, $"Expected structural/schema fixture '{Path.GetFileName(fixturePath)}' to fail.");
    }

    private static TheoryData<string> DiscoverFixtures(string kind)
    {
        var fixtureRoot = Path.Combine(SchemaRoot, "fixtures", "backupplan-v1", kind);
        var fixtures = Directory.GetFiles(fixtureRoot, "*.json", SearchOption.TopDirectoryOnly);
        Array.Sort(fixtures, StringComparer.Ordinal);

        var data = new TheoryData<string>();
        foreach (var fixture in fixtures)
        {
            data.Add(fixture);
        }

        return data;
    }
}
