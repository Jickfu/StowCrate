using StowCrate.Core.Rules;

namespace StowCrate.Core.Tests.Rules;

public sealed class BackupIgnoreParserTests
{
    [Fact]
    public void ParseDocumentReturnsOptionalArchiveUnitIdentityWithoutChangingLegacyApi()
    {
        const string content = "@id 6c3ad16a-ae76-4d21-9738-c70e6264c209\n@mode include-only\n!keep/**";

        var document = BackupIgnoreParser.ParseDocument(content);
        var legacy = BackupIgnoreParser.Parse(content);

        Assert.Equal(Guid.Parse("6c3ad16a-ae76-4d21-9738-c70e6264c209"), document.ArchiveUnitId?.Value);
        Assert.Equal(RuleMode.IncludeOnly, document.RuleSet.Mode);
        Assert.Equal(legacy.Mode, document.RuleSet.Mode);
        Assert.Equal(legacy.Rules.Select(rule => rule.Pattern), document.RuleSet.Rules.Select(rule => rule.Pattern));
    }

    [Theory]
    [InlineData("6C3AD16A-AE76-4D21-9738-C70E6264C209")]
    [InlineData("6ba7b810-9dad-11d1-80b4-00c04fd430c8")]
    [InlineData("not-a-uuid")]
    public void IdRequiresCanonicalLowercaseUuidV4(string value)
    {
        Assert.Throws<BackupIgnoreParseException>(() => BackupIgnoreParser.ParseDocument($"@id {value}"));
    }

    [Fact]
    public void EmptyFileUsesV1ExcludeAndAutoDefaults()
    {
        var ruleSet = BackupIgnoreParser.Parse(string.Empty);

        Assert.Equal(RuleMode.Exclude, ruleSet.Mode);
        Assert.Equal(CaseSensitivity.Auto, ruleSet.CaseSensitivity);
        Assert.Empty(ruleSet.Rules);
    }

    [Fact]
    public void ParserAcceptsBomCrLfAndKnownDirectives()
    {
        var content = "\uFEFF@version 1\r\n@mode include-only\r\n@case insensitive\r\n!src/\r\n";

        var ruleSet = BackupIgnoreParser.Parse(content);

        Assert.Equal(RuleMode.IncludeOnly, ruleSet.Mode);
        Assert.Equal(CaseSensitivity.Insensitive, ruleSet.CaseSensitivity);
        var rule = Assert.Single(ruleSet.Rules);
        Assert.Equal(RuleAction.Include, rule.Action);
        Assert.Equal("src/", rule.Pattern);
    }

    [Fact]
    public void OrdinaryPatternIsExcludeAndBangPatternIsInclude()
    {
        var ruleSet = BackupIgnoreParser.Parse("*.log\n!important.log");

        Assert.Collection(
            ruleSet.Rules,
            rule => Assert.Equal(RuleAction.Exclude, rule.Action),
            rule => Assert.Equal(RuleAction.Include, rule.Action));
    }

    [Fact]
    public void EscapedDirectiveCommentAndBangAreLiteralPatterns()
    {
        var ruleSet = BackupIgnoreParser.Parse("\\@data\n\\#data\n\\!important");

        Assert.Equal("\\@data|\\#data|\\!important", string.Join('|', ruleSet.Rules.Select(rule => rule.Pattern)));
        Assert.All(ruleSet.Rules, rule => Assert.Equal(RuleAction.Exclude, rule.Action));
    }

    [Theory]
    [InlineData("@version 2")]
    [InlineData("@unknown value")]
    [InlineData("@mode invalid")]
    [InlineData("@case invalid")]
    [InlineData("@mode exclude\n@mode include-only")]
    [InlineData("*.log\n@mode exclude")]
    [InlineData("foo[")]
    [InlineData("../secret")]
    [InlineData("foo//bar")]
    [InlineData("trailing\\")]
    public void InvalidSyntaxIsFatal(string content)
    {
        Assert.Throws<BackupIgnoreParseException>(() => BackupIgnoreParser.Parse(content));
    }

    [Fact]
    public void FullLineCommentsAreIgnoredButInlineHashIsLiteral()
    {
        var ruleSet = BackupIgnoreParser.Parse("  # comment\n*.log # literal");

        var rule = Assert.Single(ruleSet.Rules);
        Assert.Equal("*.log # literal", rule.Pattern);
    }
}
