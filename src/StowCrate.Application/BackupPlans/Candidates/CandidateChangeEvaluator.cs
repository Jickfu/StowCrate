using StowCrate.Core.ChangeDetection;

namespace StowCrate.Application.BackupPlans.Candidates;

public sealed record CandidateChangeEvaluation(
    CandidateArchiveFingerprints? Fingerprints,
    ChangeDecision Decision,
    IReadOnlyList<CandidateFingerprintError> FingerprintErrors);

public static class CandidateChangeEvaluator
{
    public static CandidateChangeEvaluation Evaluate(
        Resolution.ResolvedPlanSnapshot plan,
        CandidateArchiveSet candidateSet,
        ExecutionReadyArchive? readyArchive,
        CommittedArchiveUnitBaseline? baseline,
        StorageBindingFingerprintFacts storageFacts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(candidateSet);
        ArgumentNullException.ThrowIfNull(storageFacts);
        if (candidateSet.Issues.Any(issue => issue.Code is CandidateCompositionIssueCode.IncompleteObservation))
        {
            var blocked = new ChangeDecision(
                ArchiveChangeStatus.BlockedByIncompleteSource,
                OutputLayoutChangeStatus.BlockedByIncompleteSource,
                [ChangeReason.IncompleteObservation]);
            return new CandidateChangeEvaluation(null, blocked, []);
        }

        ArgumentNullException.ThrowIfNull(readyArchive);
        var computed = CandidateFingerprintCalculator.Compute(plan, readyArchive, storageFacts);
        if (computed.Fingerprints is null)
        {
            var blocked = new ChangeDecision(
                ArchiveChangeStatus.BlockedByIncompleteSource,
                OutputLayoutChangeStatus.BlockedByIncompleteSource,
                [ChangeReason.IncompleteObservation]);
            return new CandidateChangeEvaluation(null, blocked, computed.Errors);
        }

        return new CandidateChangeEvaluation(
            computed.Fingerprints,
            ChangeDetector.Detect(computed.Fingerprints, baseline),
            computed.Errors);
    }
}
