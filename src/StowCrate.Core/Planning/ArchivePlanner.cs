using StowCrate.Core.Paths;
using StowCrate.Core.Rules;

namespace StowCrate.Core.Planning;

public static class ArchivePlanner
{
    private const string BackupIgnoreFileName = ".backupignore";
    private const string ReservedArchiveNamespace = "__stowcrate__";

    public static PlanningResult CreatePlan(BackupPlan backupPlan, SourceSnapshot sourceSnapshot)
    {
        ArgumentNullException.ThrowIfNull(backupPlan);
        ArgumentNullException.ThrowIfNull(sourceSnapshot);

        var issues = new List<PlanningIssue>();
        if (!backupPlan.Source.Id.Equals(sourceSnapshot.Source.Id, StringComparison.Ordinal))
        {
            issues.Add(new PlanningIssue(
                "SOURCE_MISMATCH",
                $"BackupPlan source '{backupPlan.Source.Id}' 与 snapshot source '{sourceSnapshot.Source.Id}' 不一致。"));
            return PlanningResult.Failure(issues);
        }

        var candidates = DiscoverArchiveUnits(backupPlan, sourceSnapshot, issues);
        if (candidates.Count == 0 && issues.Count == 0)
        {
            issues.Add(new PlanningIssue("NO_ARCHIVE_UNIT", "备份源中没有可规划的 Archive Unit。"));
        }

        if (issues.Count > 0)
        {
            return PlanningResult.Failure(SortIssues(issues));
        }

        var tree = BuildArchiveUnitTree(backupPlan, sourceSnapshot, candidates);
        ValidateReservedNamespaces(tree, sourceSnapshot, issues);
        if (issues.Count > 0)
        {
            return PlanningResult.Failure(SortIssues(issues));
        }

        var archives = tree.Units
            .Select(unit => BuildPlannedArchive(backupPlan, sourceSnapshot, tree, unit))
            .ToArray();
        var planFingerprint = Fingerprint.Compute(
            new[]
            {
                $"plan:{backupPlan.Id}",
                $"source:{backupPlan.Source.Id}",
                $"source-name:{backupPlan.Source.Name}",
                $"filesystem-case:{sourceSnapshot.FileSystemCaseSensitivity}",
            }.Concat(archives.Select(archive => $"archive:{archive.OutputPath.Value}:{archive.Fingerprint}")));
        var archivePlan = new ArchivePlan(
            backupPlan.Id,
            backupPlan.Source,
            tree,
            archives,
            planFingerprint);

        return PlanningResult.Success(archivePlan);
    }

    private static Dictionary<LogicalPath, ArchiveUnitCandidate> DiscoverArchiveUnits(
        BackupPlan backupPlan,
        SourceSnapshot sourceSnapshot,
        List<PlanningIssue> issues)
    {
        var candidates = new Dictionary<LogicalPath, ArchiveUnitCandidate>();
        var controlFiles = sourceSnapshot.Entries
            .Where(entry => entry.Kind is SourceEntryKind.File && IsBackupIgnore(entry.Path))
            .ToArray();

        foreach (var controlFile in controlFiles)
        {
            try
            {
                var localRules = BackupIgnoreParser.Parse(controlFile.TextContent ?? string.Empty);
                candidates.Add(
                    controlFile.Path.Parent,
                    new ArchiveUnitCandidate(controlFile.Path.Parent, RuleSource.FileManaged, localRules));
            }
            catch (BackupIgnoreParseException exception)
            {
                issues.Add(new PlanningIssue(
                    "BACKUPIGNORE_PARSE_ERROR",
                    exception.Message,
                    controlFile.Path));
            }
        }

        foreach (var definition in backupPlan.ArchiveUnits)
        {
            if (candidates.TryGetValue(definition.Root, out var existing)
                && existing.RuleSource is RuleSource.FileManaged)
            {
                issues.Add(new PlanningIssue(
                    "RULE_SOURCE_CONFLICT",
                    "同一 Archive Unit 同时存在 UI_MANAGED 配置和 .backupignore。",
                    definition.Root));
                continue;
            }

            if (candidates.ContainsKey(definition.Root))
            {
                issues.Add(new PlanningIssue(
                    "DUPLICATE_ARCHIVE_UNIT",
                    "BackupPlan 重复定义了同一 Archive Unit。",
                    definition.Root));
                continue;
            }

            if (!definition.Root.IsRoot && !sourceSnapshot.Entries.Any(
                    entry => entry.Path == definition.Root && entry.Kind is SourceEntryKind.Directory))
            {
                issues.Add(new PlanningIssue(
                    "ARCHIVE_UNIT_NOT_FOUND",
                    "UI_MANAGED Archive Unit 根目录不在 SourceSnapshot 中。",
                    definition.Root));
                continue;
            }

            candidates.Add(
                definition.Root,
                new ArchiveUnitCandidate(definition.Root, RuleSource.UiManaged, definition.LocalRules));
        }

        return candidates;
    }

    private static ArchiveUnitTree BuildArchiveUnitTree(
        BackupPlan backupPlan,
        SourceSnapshot sourceSnapshot,
        IReadOnlyDictionary<LogicalPath, ArchiveUnitCandidate> candidates)
    {
        var units = candidates.Values
            .Select(candidate => new ArchiveUnit(
                candidate.Root,
                candidate.RuleSource,
                candidate.LocalRules,
                new EffectiveRuleSet(
                    backupPlan.GlobalRules,
                    backupPlan.PlanRules,
                    candidate.LocalRules,
                    sourceSnapshot.FileSystemCaseSensitivity)))
            .OrderBy(unit => unit.Root.Value, StringComparer.Ordinal)
            .ToArray();
        var boundaries = new List<ArchiveBoundary>();

        foreach (var child in units)
        {
            var parent = units
                .Where(candidate => child.Root.IsDescendantOf(candidate.Root))
                .OrderByDescending(candidate => candidate.Root.Value.Length)
                .FirstOrDefault();
            if (parent is not null)
            {
                boundaries.Add(new ArchiveBoundary(parent.Root, child.Root));
            }
        }

        return new ArchiveUnitTree(units, boundaries);
    }

    private static void ValidateReservedNamespaces(
        ArchiveUnitTree tree,
        SourceSnapshot sourceSnapshot,
        List<PlanningIssue> issues)
    {
        foreach (var unit in tree.Units)
        {
            var conflict = sourceSnapshot.Entries.FirstOrDefault(entry =>
            {
                if (!entry.Path.IsDescendantOf(unit.Root))
                {
                    return false;
                }

                var relativePath = entry.Path.RelativeTo(unit.Root);
                var firstSegment = relativePath.Value.Split('/', 2)[0];
                return firstSegment.Equals(ReservedArchiveNamespace, StringComparison.Ordinal);
            });

            if (conflict is not null)
            {
                issues.Add(new PlanningIssue(
                    "RESERVED_NAMESPACE_CONFLICT",
                    $"Archive Unit 根目录下的 '{ReservedArchiveNamespace}' 由 StowCrate 保留。",
                    conflict.Path));
            }
        }
    }

    private static PlannedArchive BuildPlannedArchive(
        BackupPlan backupPlan,
        SourceSnapshot sourceSnapshot,
        ArchiveUnitTree tree,
        ArchiveUnit unit)
    {
        var directChildren = tree.GetDirectChildren(unit);
        var entries = new List<ArchiveEntry>();

        foreach (var sourceEntry in sourceSnapshot.Entries)
        {
            if (!sourceEntry.Path.IsSameOrDescendantOf(unit.Root) || sourceEntry.Path == unit.Root)
            {
                continue;
            }

            if (directChildren.Any(child => sourceEntry.Path.IsSameOrDescendantOf(child.Root)))
            {
                continue;
            }

            var archivePath = sourceEntry.Path.RelativeTo(unit.Root);
            var isOwnControlFile = unit.RuleSource is RuleSource.FileManaged
                && archivePath.Value.Equals(BackupIgnoreFileName, StringComparison.Ordinal);
            var action = isOwnControlFile
                ? RuleAction.Include
                : unit.EffectiveRules.Decide(archivePath, sourceEntry.Kind);
            if (action is RuleAction.Exclude)
            {
                continue;
            }

            entries.Add(new ArchiveEntry(
                sourceEntry.Path,
                archivePath,
                sourceEntry.Kind,
                sourceEntry.Length,
                sourceEntry.ContentFingerprint));
        }

        var outputPath = unit.Root.IsRoot
            ? new RelativePath($"{backupPlan.Source.Name}.7z")
            : new RelativePath($"{unit.Root.Value}.7z");
        var descendantBoundaries = tree.Units
            .Where(candidate => candidate.Root.IsDescendantOf(unit.Root))
            .Select(candidate => candidate.Root.Value)
            .Order(StringComparer.Ordinal);
        var fingerprint = Fingerprint.Compute(
            new[]
            {
                $"unit:{unit.Root.Value}",
                $"rule-source:{unit.RuleSource}",
                $"rules:{unit.EffectiveRules.Fingerprint}",
            }
            .Concat(descendantBoundaries.Select(boundary => $"boundary:{boundary}"))
            .Concat(entries
                .OrderBy(entry => entry.ArchivePath.Value, StringComparer.Ordinal)
                .Select(entry =>
                    $"entry:{entry.ArchivePath.Value}:{entry.Kind}:{entry.Length}:{entry.SourceFingerprint}")));

        return new PlannedArchive(unit, outputPath, entries, fingerprint);
    }

    private static bool IsBackupIgnore(LogicalPath path)
    {
        return path.Name.Equals(BackupIgnoreFileName, StringComparison.Ordinal);
    }

    private static IEnumerable<PlanningIssue> SortIssues(IEnumerable<PlanningIssue> issues)
    {
        return issues
            .OrderBy(issue => issue.Path?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal);
    }

    private sealed record ArchiveUnitCandidate(LogicalPath Root, RuleSource RuleSource, RuleSet LocalRules);
}
