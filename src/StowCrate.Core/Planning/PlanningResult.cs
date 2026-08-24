using System.Collections.ObjectModel;
using StowCrate.Core.Paths;

namespace StowCrate.Core.Planning;

public sealed record PlanningIssue(string Code, string Message, LogicalPath? Path = null);

public sealed class PlanningResult
{
    private PlanningResult(ArchivePlan? plan, IEnumerable<PlanningIssue> issues)
    {
        Plan = plan;
        Issues = new ReadOnlyCollection<PlanningIssue>(issues.ToArray());
    }

    public bool IsSuccess => Plan is not null && Issues.Count == 0;

    public ArchivePlan? Plan { get; }

    public IReadOnlyList<PlanningIssue> Issues { get; }

    public static PlanningResult Success(ArchivePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new PlanningResult(plan, []);
    }

    public static PlanningResult Failure(IEnumerable<PlanningIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var frozenIssues = issues.ToArray();
        if (frozenIssues.Length == 0)
        {
            throw new ArgumentException("失败结果至少需要一个 issue。", nameof(issues));
        }

        return new PlanningResult(null, frozenIssues);
    }
}
