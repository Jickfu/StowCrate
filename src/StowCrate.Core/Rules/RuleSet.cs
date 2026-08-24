using System.Collections.ObjectModel;

namespace StowCrate.Core.Rules;

public sealed class RuleSet
{
    public RuleSet(
        RuleMode mode = RuleMode.Exclude,
        CaseSensitivity caseSensitivity = CaseSensitivity.Auto,
        IEnumerable<BackupRule>? rules = null)
    {
        Mode = mode;
        CaseSensitivity = caseSensitivity;
        Rules = new ReadOnlyCollection<BackupRule>((rules ?? []).ToArray());
    }

    public RuleMode Mode { get; }

    public CaseSensitivity CaseSensitivity { get; }

    public IReadOnlyList<BackupRule> Rules { get; }
}
