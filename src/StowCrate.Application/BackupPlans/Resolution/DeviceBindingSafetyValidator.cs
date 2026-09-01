using StowCrate.Core.BackupPlans;

namespace StowCrate.Application.BackupPlans.Resolution;

public static class DeviceBindingSafetyValidator
{
    public static IReadOnlyList<PlanResolutionIssue> Validate(
        PlanId planId,
        DeviceId deviceId,
        IEnumerable<ResolvedPhysicalPath> sourceRoots,
        ResolvedPhysicalPath? currentRoot,
        ResolvedPhysicalPath? historyRoot,
        IReadOnlyCollection<ActivePlanRootFacts> otherActivePlans)
    {
        ArgumentNullException.ThrowIfNull(sourceRoots);
        ArgumentNullException.ThrowIfNull(otherActivePlans);
        var sources = sourceRoots.ToArray();
        var issues = new List<PlanResolutionIssue>();

        foreach (var source in sources)
        {
            Add(source, currentRoot, "SourceRoot", "CurrentRoot", PlanResolutionIssueCode.RootOverlap, issues);
            Add(source, historyRoot, "SourceRoot", "HistoryRoot", PlanResolutionIssueCode.RootOverlap, issues);
        }
        Add(currentRoot, historyRoot, "CurrentRoot", "HistoryRoot", PlanResolutionIssueCode.RootOverlap, issues);

        var all = sources.Concat(new[] { currentRoot, historyRoot }.OfType<ResolvedPhysicalPath>()).ToArray();
        var writable = new[] { currentRoot, historyRoot }.OfType<ResolvedPhysicalPath>().ToArray();
        foreach (var other in otherActivePlans.Where(other => other.DeviceId == deviceId && other.PlanId != planId))
        {
            var otherAll = other.SourceRoots.Concat(new[] { other.CurrentRoot, other.HistoryRoot }.OfType<ResolvedPhysicalPath>()).ToArray();
            var otherWritable = new[] { other.CurrentRoot, other.HistoryRoot }.OfType<ResolvedPhysicalPath>();
            foreach (var root in writable) foreach (var candidate in otherAll) Add(root, candidate, "WritableRoot", "OtherPlanRoot", PlanResolutionIssueCode.ActivePlanRootConflict, issues);
            foreach (var root in otherWritable) foreach (var candidate in all) Add(root, candidate, "OtherPlanWritableRoot", "PlanRoot", PlanResolutionIssueCode.ActivePlanRootConflict, issues);
        }
        return issues;
    }

    private static void Add(ResolvedPhysicalPath? left, ResolvedPhysicalPath? right, string leftRole, string rightRole, PlanResolutionIssueCode code, List<PlanResolutionIssue> issues)
    {
        if (left is not null && right is not null && left.Overlaps(right))
            issues.Add(new(code, $"{leftRole} '{left.CanonicalPath}' overlaps {rightRole} '{right.CanonicalPath}'.", left.CanonicalPath));
    }
}
