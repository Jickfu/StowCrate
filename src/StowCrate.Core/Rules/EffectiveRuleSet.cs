using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using StowCrate.Core.Paths;

namespace StowCrate.Core.Rules;

public sealed class EffectiveRuleSet
{
    public EffectiveRuleSet(
        IEnumerable<BackupRule> globalRules,
        IEnumerable<BackupRule> planRules,
        RuleSet localRules,
        CaseSensitivity fileSystemCaseSensitivity)
    {
        ArgumentNullException.ThrowIfNull(globalRules);
        ArgumentNullException.ThrowIfNull(planRules);
        ArgumentNullException.ThrowIfNull(localRules);

        if (fileSystemCaseSensitivity is CaseSensitivity.Auto)
        {
            throw new ArgumentException("文件系统 case sensitivity 必须已解析。", nameof(fileSystemCaseSensitivity));
        }

        var frozenGlobalRules = globalRules.ToArray();
        var frozenPlanRules = planRules.ToArray();
        var frozenLocalRules = localRules.Rules.ToArray();

        Mode = localRules.Mode;
        DeclaredCaseSensitivity = localRules.CaseSensitivity;
        ResolvedCaseSensitivity = localRules.CaseSensitivity is CaseSensitivity.Auto
            ? fileSystemCaseSensitivity
            : localRules.CaseSensitivity;
        OrderedRules = new ReadOnlyCollection<BackupRule>(
            frozenGlobalRules.Concat(frozenPlanRules).Concat(frozenLocalRules).ToArray());
        Fingerprint = ComputeFingerprint(frozenGlobalRules, frozenPlanRules, frozenLocalRules);
    }

    public RuleMode Mode { get; }

    public CaseSensitivity DeclaredCaseSensitivity { get; }

    public CaseSensitivity ResolvedCaseSensitivity { get; }

    public IReadOnlyList<BackupRule> OrderedRules { get; }

    public string Fingerprint { get; }

    public RuleAction Decide(RelativePath path, SourceEntryKind entryKind)
    {
        var decision = Mode is RuleMode.Exclude ? RuleAction.Include : RuleAction.Exclude;

        foreach (var rule in OrderedRules)
        {
            if (rule.Matches(path, entryKind, ResolvedCaseSensitivity))
            {
                decision = rule.Action;
            }
        }

        return decision;
    }

    private string ComputeFingerprint(
        IReadOnlyList<BackupRule> globalRules,
        IReadOnlyList<BackupRule> planRules,
        IReadOnlyList<BackupRule> localRules)
    {
        var canonical = new StringBuilder()
            .Append("mode:").Append(Mode).Append('\n')
            .Append("declared-case:").Append(DeclaredCaseSensitivity).Append('\n')
            .Append("resolved-case:").Append(ResolvedCaseSensitivity).Append('\n');

        AppendRules(canonical, "global", globalRules);
        AppendRules(canonical, "plan", planRules);
        AppendRules(canonical, "local", localRules);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void AppendRules(
        StringBuilder canonical,
        string scope,
        IEnumerable<BackupRule> rules)
    {
        canonical.Append('[').Append(scope).Append("]\n");
        foreach (var rule in rules)
        {
            canonical.Append(rule.Action).Append(':').Append(rule.Pattern).Append('\n');
        }
    }
}
