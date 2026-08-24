using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;

namespace StowCrate.Core.Rules;

public sealed class BackupRule
{
    private readonly GlobPattern _globPattern;

    public BackupRule(RuleAction action, string pattern)
    {
        Action = action;
        _globPattern = new GlobPattern(pattern);
        Pattern = _globPattern.Pattern;
    }

    public RuleAction Action { get; }

    public string Pattern { get; }

    public bool Matches(
        RelativePath path,
        FileSystemEntryKind entryKind,
        CaseSensitivity caseSensitivity,
        bool linkTargetsDirectory = false)
    {
        return _globPattern.IsMatch(path, entryKind, caseSensitivity, linkTargetsDirectory);
    }

    public override string ToString()
    {
        return Action is RuleAction.Include ? $"!{Pattern}" : Pattern;
    }
}
