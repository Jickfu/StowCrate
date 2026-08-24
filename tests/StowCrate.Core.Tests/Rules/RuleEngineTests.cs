using System.Text;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;

namespace StowCrate.Core.Tests.Rules;

public sealed class RuleEngineTests
{
    [Fact]
    public void ExcludeModeIncludesByDefaultAndLastMatchWins()
    {
        var rules = BackupIgnoreParser.Parse("*.log\n!important.log\nimportant*.log\n!important-keep.log");
        var effective = Effective(rules);

        Assert.Equal(RuleAction.Include, Decide(effective, "readme.md"));
        Assert.Equal(RuleAction.Exclude, Decide(effective, "important.log"));
        Assert.Equal(RuleAction.Include, Decide(effective, "important-keep.log"));
    }

    [Fact]
    public void IncludeOnlyModeExcludesByDefault()
    {
        var effective = Effective(BackupIgnoreParser.Parse("@mode include-only\n!src/\n/src/generated/"));

        Assert.Equal(RuleAction.Exclude, Decide(effective, "README.md"));
        Assert.Equal(RuleAction.Include, Decide(effective, "src/App.cs"));
        Assert.Equal(RuleAction.Exclude, Decide(effective, "src/generated/App.g.cs"));
    }

    [Fact]
    public void RulesAreFlattenedGlobalThenPlanThenLocal()
    {
        var global = new BackupRule(RuleAction.Exclude, "node_modules/");
        var plan = new BackupRule(RuleAction.Include, "vendor/node_modules/");
        var local = new RuleSet(rules: [new BackupRule(RuleAction.Exclude, "vendor/node_modules/cache/")]);
        var effective = new EffectiveRuleSet(
            [global],
            [plan],
            local,
            CaseSensitivity.Sensitive);

        Assert.Equal(RuleAction.Exclude, Decide(effective, "app/node_modules/index.js"));
        Assert.Equal(RuleAction.Include, Decide(effective, "vendor/node_modules/index.js"));
        Assert.Equal(RuleAction.Exclude, Decide(effective, "vendor/node_modules/cache/data.bin"));
    }

    [Fact]
    public void FingerprintPreservesRuleScopeBoundaries()
    {
        var rule = new BackupRule(RuleAction.Exclude, "*.log");
        var localRules = new RuleSet();
        var global = new EffectiveRuleSet([rule], [], localRules, CaseSensitivity.Sensitive);
        var plan = new EffectiveRuleSet([], [rule], localRules, CaseSensitivity.Sensitive);

        Assert.NotEqual(global.Fingerprint, plan.Fingerprint);
    }

    [Theory]
    [InlineData("/build/", "build/output.bin", true)]
    [InlineData("/build/", "src/build/output.bin", false)]
    [InlineData("build/", "src/build/output.bin", true)]
    [InlineData("src/**/obj/", "src/obj/cache.bin", true)]
    [InlineData("src/**/obj/", "src/A/B/obj/cache.bin", true)]
    [InlineData("*.log", "logs/a.log", true)]
    [InlineData("file?.txt", "deep/file1.txt", true)]
    [InlineData("file?.txt", "file10.txt", false)]
    [InlineData("file[0-9].txt", "file7.txt", true)]
    [InlineData("file[!0-9].txt", "fileA.txt", true)]
    public void GlobSyntaxMatchesDefinedV1Behavior(string pattern, string path, bool expectedMatch)
    {
        var rule = new BackupRule(RuleAction.Exclude, pattern);

        var matches = rule.Matches(new RelativePath(path), FileSystemEntryKind.File, CaseSensitivity.Sensitive);

        Assert.Equal(expectedMatch, matches);
    }

    [Fact]
    public void ExcludedDirectoryDoesNotPreventLaterDescendantInclude()
    {
        var effective = Effective(BackupIgnoreParser.Parse("build/\n!build/release/app.exe"));

        Assert.Equal(RuleAction.Include, Decide(effective, "build/release/app.exe"));
        Assert.Equal(RuleAction.Exclude, Decide(effective, "build/debug/app.exe"));
    }

    [Fact]
    public void CaseMatchingUsesExplicitOrdinalPolicy()
    {
        var sensitive = Effective(BackupIgnoreParser.Parse("@case sensitive\nfoo"));
        var insensitive = Effective(BackupIgnoreParser.Parse("@case insensitive\nfoo"));

        Assert.Equal(RuleAction.Include, Decide(sensitive, "FOO"));
        Assert.Equal(RuleAction.Exclude, Decide(insensitive, "FOO"));
    }

    [Fact]
    public void AutoCasePolicyIsResolvedFromSourceSnapshotSemantics()
    {
        var effective = new EffectiveRuleSet(
            [],
            [],
            BackupIgnoreParser.Parse("foo"),
            CaseSensitivity.Insensitive);

        Assert.Equal(CaseSensitivity.Auto, effective.DeclaredCaseSensitivity);
        Assert.Equal(CaseSensitivity.Insensitive, effective.ResolvedCaseSensitivity);
        Assert.Equal(RuleAction.Exclude, Decide(effective, "FOO"));
    }

    [Fact]
    public void DirectoryOnlyPatternDoesNotMatchFileWithSameName()
    {
        var rule = new BackupRule(RuleAction.Exclude, "node_modules/");

        Assert.False(rule.Matches(
            new RelativePath("node_modules"),
            FileSystemEntryKind.File,
            CaseSensitivity.Sensitive));
        Assert.True(rule.Matches(
            new RelativePath("node_modules"),
            FileSystemEntryKind.Directory,
            CaseSensitivity.Sensitive));
    }

    [Fact]
    public void MatchingNormalizesUnicodeToNfc()
    {
        var decomposed = "é".Normalize(NormalizationForm.FormD);
        var effective = Effective(BackupIgnoreParser.Parse($"caf{decomposed}.txt"));

        Assert.Equal(RuleAction.Exclude, Decide(effective, "café.txt"));
    }

    [Theory]
    [InlineData("\\!important.txt", "!important.txt")]
    [InlineData("\\#data.txt", "#data.txt")]
    [InlineData("\\@data.txt", "@data.txt")]
    [InlineData("literal\\*.txt", "literal*.txt")]
    [InlineData("name\\ ", "name ")]
    public void EscapesMatchLiteralCharacters(string pattern, string path)
    {
        var rule = new BackupRule(RuleAction.Exclude, pattern);

        Assert.True(rule.Matches(new RelativePath(path), FileSystemEntryKind.File, CaseSensitivity.Sensitive));
    }

    private static EffectiveRuleSet Effective(RuleSet localRules)
    {
        return new EffectiveRuleSet([], [], localRules, CaseSensitivity.Sensitive);
    }

    private static RuleAction Decide(EffectiveRuleSet ruleSet, string path)
    {
        return ruleSet.Decide(new RelativePath(path), FileSystemEntryKind.File);
    }

    [Fact]
    public void DirectoryPatternMatchesOnlyDirectoryTargetLinks()
    {
        var rule = new BackupRule(RuleAction.Exclude, "shared/");
        var path = new RelativePath("shared");

        Assert.True(rule.Matches(path, FileSystemEntryKind.Link, CaseSensitivity.Sensitive, linkTargetsDirectory: true));
        Assert.False(rule.Matches(path, FileSystemEntryKind.Link, CaseSensitivity.Sensitive, linkTargetsDirectory: false));
    }
}
