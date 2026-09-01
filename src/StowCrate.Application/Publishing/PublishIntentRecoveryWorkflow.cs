using StowCrate.Application.LocalState;
using StowCrate.Core.ChangeDetection;
using StowCrate.Core.BackupPlans;

namespace StowCrate.Application.Publishing;

public interface IPublishIntentRecoveryCoordinator
{
    Task<UnitStartupRecoveryResult> RecoverAsync(PendingPublishIntent intent, DevicePlanLocalBindings bindings, CancellationToken cancellationToken);
}

public interface IRecoveryHistoryPolicyProvider
{
    Task<EffectiveHistoryPolicy> LoadEffectiveAsync(PlanId planId, ArchiveUnitId archiveUnitId, CancellationToken cancellationToken);
}

public sealed class PublishIntentRecoveryWorkflow(IArchiveUnitDurableStateStore durableState, IArchivePhysicalPublisher physical,
    IRecoveryHistoryPolicyProvider historyPolicies)
    : IPublishIntentRecoveryCoordinator
{
    public async Task<UnitStartupRecoveryResult> RecoverAsync(PendingPublishIntent intent, DevicePlanLocalBindings bindings, CancellationToken cancellationToken)
    {
        if (bindings.CurrentRoot is null) return Ambiguous(intent, "CurrentRoot binding is unavailable.");
        var current = await physical.ObserveAsync(bindings.CurrentRoot, intent.CurrentRelativePath, cancellationToken).ConfigureAwait(false);
        if (Matches(current, intent.NewArchive))
        {
            var recovered = intent;
            if (recovered.Stage is PublishIntentStage.Prepared && recovered.OldCurrent is not null)
            {
                var historyPolicy = await historyPolicies.LoadEffectiveAsync(intent.PlanId, intent.ArchiveUnitId, cancellationToken).ConfigureAwait(false);
                if (historyPolicy is EffectiveHistoryEnabled)
                {
                    if (bindings.HistoryRoot is null) return Ambiguous(intent, "Expected History proof cannot be resolved.");
                    var historyPath = HistoryPhysicalLayoutV1.Create(intent.ArchiveUnitId, recovered.OldCurrent.ArchiveVersion);
                    var history = await physical.ObserveAsync(bindings.HistoryRoot, historyPath, cancellationToken).ConfigureAwait(false);
                    if (!Matches(history, recovered.OldCurrent.ArchiveVersion)) return Ambiguous(intent, "Expected History proof is missing or invalid.");
                    recovered = recovered.MarkHistoryCaptured(new(recovered.OldCurrent.ArchiveVersion.Id, history!.Sha256,
                        new(intent.PlanId, intent.ArchiveUnitId, recovered.OldCurrent.ArchiveVersion.Id, historyPath)));
                    await durableState.SavePublishProgressAsync(recovered, cancellationToken).ConfigureAwait(false);
                }
            }
            if (recovered.Stage is PublishIntentStage.Prepared or PublishIntentStage.HistoryCaptured)
            {
                recovered = recovered.MarkCurrentPublished(DateTimeOffset.UtcNow);
                await durableState.SavePublishProgressAsync(recovered, cancellationToken).ConfigureAwait(false);
            }
            await durableState.CompleteMetadataCommitAsync(recovered.RebuildMetadataCommitPlan(), cancellationToken).ConfigureAwait(false);
            return new(intent.PlanId, intent.ArchiveUnitId, UnitStartupRecoveryStatus.MetadataCommitCompleted, "RecoveredPublishedAtUtc was recorded at recovery time.");
        }

        var oldAtAuthorityPath = intent.OldCurrent is null
            ? current is null
            : Matches(await physical.ObserveAsync(bindings.CurrentRoot, intent.OldCurrent.Placement.RelativePath, cancellationToken).ConfigureAwait(false), intent.OldCurrent.ArchiveVersion);
        if (!oldAtAuthorityPath) return Ambiguous(intent, "Observed Current proves neither old nor expected-new authority.");
        if (intent.Stage is PublishIntentStage.CurrentPublished) return Ambiguous(intent, "Journal says Current was published but filesystem still contains old Current.");

        var abortHistoryPath = intent.HistoryCapture?.Placement.RelativePath;
        if (abortHistoryPath is null && intent.OldCurrent is not null
            && await historyPolicies.LoadEffectiveAsync(intent.PlanId, intent.ArchiveUnitId, cancellationToken).ConfigureAwait(false) is EffectiveHistoryEnabled)
            abortHistoryPath = HistoryPhysicalLayoutV1.Create(intent.ArchiveUnitId, intent.OldCurrent.ArchiveVersion);
        if (abortHistoryPath is not null)
        {
            if (bindings.HistoryRoot is null) return Ambiguous(intent, "HistoryRoot binding is unavailable for safe abort.");
            var old = intent.OldCurrent!.ArchiveVersion;
            if (!await physical.DeleteIfMatchesAsync(bindings.HistoryRoot, abortHistoryPath.Value,
                old.Integrity!.Value, old.Length!.Value, cancellationToken).ConfigureAwait(false)) return Ambiguous(intent, "Uncommitted History could not be safely removed.");
        }
        var temp = CurrentPublishTempLayoutV1.Create(intent.CurrentRelativePath, intent.NewVersionId);
        if (!await physical.DeleteIfMatchesAsync(bindings.CurrentRoot, temp, intent.ExpectedNewIntegrity,
            intent.NewArchive.Length!.Value, cancellationToken).ConfigureAwait(false)) return Ambiguous(intent, "Current publish temp could not be safely removed.");
        await durableState.AbortIncompletePublishAsync(intent, intent.Stage, cancellationToken).ConfigureAwait(false);
        return new(intent.PlanId, intent.ArchiveUnitId, UnitStartupRecoveryStatus.ResumeOrAbortRequired, "Old Current was proven and the incomplete publish was safely aborted.");
    }

    private static bool Matches(PhysicalArchiveObservation? observed, ArchiveVersion version) => observed is not null
        && version.Integrity is not null && version.Length is not null && observed.Sha256 == version.Integrity.Value && observed.Length == version.Length.Value;
    private static UnitStartupRecoveryResult Ambiguous(PendingPublishIntent intent, string detail) =>
        new(intent.PlanId, intent.ArchiveUnitId, UnitStartupRecoveryStatus.AmbiguousPublishRecovery, detail);
}
