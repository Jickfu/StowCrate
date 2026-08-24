using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace StowCrate.Infrastructure.Tests.Configuration;

public sealed class BackupPlanSchemaTests
{
    private static readonly string SchemaRoot = Path.Combine(AppContext.BaseDirectory, "schemas");
    private static readonly JsonSchema Schema = LoadSchema();

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
        var result = Evaluate(fixturePath);

        Assert.True(result.IsValid, $"Expected valid fixture '{Path.GetFileName(fixturePath)}' to pass.\n{result}");
    }

    [Theory]
    [MemberData(nameof(InvalidFixtures))]
    public void InvalidFixtureFailsForItsNamedStructuralReason(string fixturePath)
    {
        var result = Evaluate(fixturePath);

        Assert.False(result.IsValid, $"Expected structural/schema fixture '{Path.GetFileName(fixturePath)}' to fail.");
    }

    private static EvaluationResults Evaluate(string fixturePath)
    {
        using var instance = JsonDocument.Parse(File.ReadAllText(fixturePath));

        return Schema.Evaluate(instance.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });
    }

    private static JsonSchema LoadSchema()
    {
        var schemaPath = Path.Combine(SchemaRoot, "backupplan-v1.schema.json");
        return JsonSchema.FromText(File.ReadAllText(schemaPath));
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
