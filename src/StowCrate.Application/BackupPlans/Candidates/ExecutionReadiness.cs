using System.Collections.Immutable;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.Paths;

namespace StowCrate.Application.BackupPlans.Candidates;

public sealed record ResolvedArchiveCapability(string CapabilitySemantics);

public sealed record ArchiveCapabilityResolution(
    ResolvedArchiveCapability? Capability,
    string? UnsupportedReason)
{
    public bool IsSupported => Capability is not null;
}

public interface IArchiveCapabilityResolver
{
    ArchiveCapabilityResolution Resolve(EffectiveArchiveSpec archiveSpec, int archiveSemanticsVersion);
}

public sealed record CommittedArchiveUnitRegistrationFact(
    SourceId SourceId,
    LogicalPath Path,
    ArchiveUnitId ArchiveUnitId);

public enum ExecutionReadinessBlockerCode
{
    CandidateCompositionInvalid,
    IncompleteObservation,
    MissingHistoryRootBinding,
    MissingSecretBinding,
    UnsupportedArchiveCapability,
    PendingArchiveUnitRegistration
}

public sealed record ExecutionReadinessBlocker(
    ExecutionReadinessBlockerCode Code,
    string Message,
    ArchiveUnitId? ArchiveUnitId = null,
    SecretSlotId? SecretSlotId = null);

public sealed record SecureRevisionRequirement(SecretSlotId SecretSlotId, SecretRevision SecretRevision);

public sealed record ExecutionReadyArchive(
    CandidateArchive Candidate,
    ResolvedArchiveCapability Capability,
    EffectiveHistoryPolicy History,
    SecureRevisionRequirement? SecureRequirement);

public sealed class ExecutionReadyArchiveSet
{
    public ExecutionReadyArchiveSet(PortableSemanticsPins semantics, IEnumerable<ExecutionReadyArchive> archives)
    {
        Semantics = semantics;
        Archives = [.. archives];
    }

    public PortableSemanticsPins Semantics { get; }
    public ImmutableArray<ExecutionReadyArchive> Archives { get; }
}

public sealed class ExecutionReadinessResult
{
    public ExecutionReadinessResult(ExecutionReadyArchiveSet? readySet, IEnumerable<ExecutionReadinessBlocker> blockers)
    {
        ReadySet = readySet;
        Blockers = [.. blockers];
    }

    public ExecutionReadyArchiveSet? ReadySet { get; }
    public ImmutableArray<ExecutionReadinessBlocker> Blockers { get; }
    public bool CanExecute => ReadySet is not null && Blockers.IsEmpty;
}

public interface IExecutionReadinessEvaluator
{
    ExecutionReadinessResult Evaluate(
        ResolvedPlanSnapshot plan,
        CandidateArchiveSet candidates,
        IReadOnlyCollection<CommittedArchiveUnitRegistrationFact> committedRegistrations,
        IArchiveCapabilityResolver capabilityResolver);
}

public sealed class ExecutionReadinessEvaluator : IExecutionReadinessEvaluator
{
    public ExecutionReadinessResult Evaluate(
        ResolvedPlanSnapshot plan,
        CandidateArchiveSet candidates,
        IReadOnlyCollection<CommittedArchiveUnitRegistrationFact> committedRegistrations,
        IArchiveCapabilityResolver capabilityResolver)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(committedRegistrations);
        ArgumentNullException.ThrowIfNull(capabilityResolver);

        var blockers = new List<ExecutionReadinessBlocker>();
        if (candidates.Semantics != plan.Semantics)
        {
            blockers.Add(new ExecutionReadinessBlocker(
                ExecutionReadinessBlockerCode.CandidateCompositionInvalid,
                "Candidate semantics pins do not match the resolved Plan snapshot."));
        }
        foreach (var issue in candidates.Issues)
        {
            blockers.Add(new ExecutionReadinessBlocker(
                issue.Code is CandidateCompositionIssueCode.IncompleteObservation
                    ? ExecutionReadinessBlockerCode.IncompleteObservation
                    : ExecutionReadinessBlockerCode.CandidateCompositionInvalid,
                issue.Message,
                issue.ArchiveUnitId));
        }

        // 条件 binding 只能依据本轮实际 resolved units 判断，不能由 portable default 提前阻止。
        if (candidates.Archives.Any(archive => archive.Unit.History is EffectiveHistoryEnabled) && plan.HistoryRoot is null)
        {
            blockers.Add(new ExecutionReadinessBlocker(ExecutionReadinessBlockerCode.MissingHistoryRootBinding, "At least one resolved unit enables History, so HistoryRoot is required."));
        }

        var secrets = plan.SecretBindings.ToDictionary(binding => binding.SecretSlotId);
        var ready = new List<ExecutionReadyArchive>();
        foreach (var archive in candidates.Archives)
        {
            SecureRevisionRequirement? secure = null;
            if (archive.Unit.ArchiveSpec.Protection is SecureProtection protection)
            {
                if (!secrets.TryGetValue(protection.SecretSlotId, out var binding))
                {
                    blockers.Add(new ExecutionReadinessBlocker(
                        ExecutionReadinessBlockerCode.MissingSecretBinding,
                        "Secure archive requires a bound SecretRevision.",
                        archive.Unit.ArchiveUnitId,
                        protection.SecretSlotId));
                }
                else secure = new SecureRevisionRequirement(binding.SecretSlotId, binding.Revision);
            }

            var capability = capabilityResolver.Resolve(archive.Unit.ArchiveSpec, candidates.Semantics.Archive);
            if (!capability.IsSupported)
            {
                blockers.Add(new ExecutionReadinessBlocker(
                    ExecutionReadinessBlockerCode.UnsupportedArchiveCapability,
                    capability.UnsupportedReason ?? "Archive capability is unsupported.",
                    archive.Unit.ArchiveUnitId));
                continue;
            }
            ready.Add(new ExecutionReadyArchive(archive, capability.Capability!, archive.Unit.History, secure));
        }

        foreach (var pending in candidates.PendingRegistrations)
        {
            if (!committedRegistrations.Contains(new CommittedArchiveUnitRegistrationFact(pending.SourceId, pending.Path, pending.ArchiveUnitId)))
            {
                blockers.Add(new ExecutionReadinessBlocker(
                    ExecutionReadinessBlockerCode.PendingArchiveUnitRegistration,
                    "Generated Archive Unit identity must be durably registered before execution.",
                    pending.ArchiveUnitId));
            }
        }

        return blockers.Count == 0
            ? new ExecutionReadinessResult(new ExecutionReadyArchiveSet(candidates.Semantics, ready), blockers)
            : new ExecutionReadinessResult(null, blockers);
    }
}
