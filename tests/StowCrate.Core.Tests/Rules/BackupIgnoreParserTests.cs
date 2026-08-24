using StowCrate.Core.Rules;

namespace StowCrate.Core.Tests.Rules;

public sealed class BackupIgnoreParserTests
{
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
