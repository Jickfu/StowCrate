using System.Collections.Immutable;

namespace StowCrate.Application.BackupPlans.Resolution;

public enum PlanResolutionIssueCode
{
    InvalidPortablePlan,
    BindingPlanMismatch,
    DuplicateSourceBinding,
    DuplicateExternalSourceBinding,
    DuplicateSecretBinding,
    MissingSourceBinding,
    MissingCurrentRootBinding,
    MissingExternalSourceBinding,
    RootOverlap,
    ActivePlanRootConflict
}

public sealed record PlanResolutionIssue(
    PlanResolutionIssueCode Code,
    string Message,
    string? Subject = null);

public sealed class PlanResolutionResult
{
    public PlanResolutionResult(ResolvedPlanSnapshot? snapshot, IEnumerable<PlanResolutionIssue> issues)
    {
        Snapshot = snapshot;
        Issues = [.. issues];
    }

    public ResolvedPlanSnapshot? Snapshot { get; }
    public ImmutableArray<PlanResolutionIssue> Issues { get; }
    public bool IsSuccess => Snapshot is not null && Issues.IsEmpty;
}
