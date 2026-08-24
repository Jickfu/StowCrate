namespace StowCrate.Core.Rules;

using StowCrate.Core.BackupPlans;

public sealed record BackupIgnoreParseResult(ArchiveUnitId? ArchiveUnitId, RuleSet RuleSet);

public static class BackupIgnoreParser
{
    public static RuleSet Parse(string content)
    {
        return ParseDocument(content).RuleSet;
    }

    public static BackupIgnoreParseResult ParseDocument(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var mode = RuleMode.Exclude;
        var caseSensitivity = CaseSensitivity.Auto;
        var seenDirectives = new HashSet<string>(StringComparer.Ordinal);
        var rules = new List<BackupRule>();
        var patternSeen = false;
        ArchiveUnitId? archiveUnitId = null;
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var line = index == 0 ? lines[index].TrimStart('\uFEFF') : lines[index];
            var trimmedStart = line.TrimStart(' ', '\t');

            if (trimmedStart.Length == 0 || trimmedStart.StartsWith('#'))
            {
                continue;
            }

            if (trimmedStart.StartsWith('@'))
            {
                if (patternSeen)
                {
                    throw new BackupIgnoreParseException(lineNumber, "Directive 必须位于第一条 pattern 之前。");
                }

                ParseDirective(
                    trimmedStart,
                    lineNumber,
                    seenDirectives,
                    ref mode,
                    ref caseSensitivity,
                    ref archiveUnitId);
                continue;
            }

            patternSeen = true;
            var pattern = GlobPattern.TrimPatternWhitespace(line);
            var action = RuleAction.Exclude;
            if (pattern.StartsWith('!'))
            {
                action = RuleAction.Include;
                pattern = pattern[1..];
            }

            try
            {
                rules.Add(new BackupRule(action, pattern));
            }
            catch (ArgumentException exception)
            {
                throw new BackupIgnoreParseException(lineNumber, exception.Message);
            }
        }

        return new BackupIgnoreParseResult(archiveUnitId, new RuleSet(mode, caseSensitivity, rules));
    }

    private static void ParseDirective(
        string line,
        int lineNumber,
        HashSet<string> seenDirectives,
        ref RuleMode mode,
        ref CaseSensitivity caseSensitivity,
        ref ArchiveUnitId? archiveUnitId)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            throw new BackupIgnoreParseException(lineNumber, "Directive 必须包含名称和一个值。");
        }

        var name = parts[0];
        if (!seenDirectives.Add(name))
        {
            throw new BackupIgnoreParseException(lineNumber, $"Directive '{name}' 不能重复。");
        }

        switch (name)
        {
            case "@version" when parts[1] == "1":
                break;
            case "@version":
                throw new BackupIgnoreParseException(lineNumber, $"不支持 .backupignore 版本 '{parts[1]}'。");
            case "@id":
                archiveUnitId = ParseArchiveUnitId(parts[1], lineNumber);
                break;
            case "@mode" when parts[1] == "exclude":
                mode = RuleMode.Exclude;
                break;
            case "@mode" when parts[1] == "include-only":
                mode = RuleMode.IncludeOnly;
                break;
            case "@mode":
                throw new BackupIgnoreParseException(lineNumber, $"未知 mode '{parts[1]}'。");
            case "@case" when parts[1] == "auto":
                caseSensitivity = CaseSensitivity.Auto;
                break;
            case "@case" when parts[1] == "sensitive":
                caseSensitivity = CaseSensitivity.Sensitive;
                break;
            case "@case" when parts[1] == "insensitive":
                caseSensitivity = CaseSensitivity.Insensitive;
                break;
            case "@case":
                throw new BackupIgnoreParseException(lineNumber, $"未知 case policy '{parts[1]}'。");
            default:
                throw new BackupIgnoreParseException(lineNumber, $"未知 Directive '{name}'。");
        }
    }

    private static ArchiveUnitId ParseArchiveUnitId(string value, int lineNumber)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed)
            || !value.Equals(parsed.ToString("D"), StringComparison.Ordinal))
        {
            throw new BackupIgnoreParseException(lineNumber, "@id 必须是 canonical lowercase UUID v4。");
        }

        try
        {
            return new ArchiveUnitId(parsed);
        }
        catch (ArgumentException)
        {
            throw new BackupIgnoreParseException(lineNumber, "@id 必须是 canonical lowercase UUID v4。");
        }
    }
}
