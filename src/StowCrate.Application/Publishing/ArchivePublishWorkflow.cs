using StowCrate.Application.BackupPlans.Candidates;
using StowCrate.Application.LocalState;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Application.Publishing;

public sealed class ArchivePublishWorkflow(IArchiveUnitDurableStateStore durableState, IArchivePhysicalPublisher physical,
    ICurrentExecutionSemanticSnapshotProvider snapshots, IMaintenanceStateStore? maintenance = null)
{
    public async Task<ArchivePublishResult> PublishAsync(ArchivePublishRequest request, CancellationToken cancellationToken)
    {
        var version = request.Artifact.ArchiveVersion;
        var state = await durableState.LoadAsync(version.PlanId, version.ArchiveUnitId, cancellationToken).ConfigureAwait(false);
        if (state?.PublishIntent is { Stage: not PublishIntentStage.MetadataCommitted })
            return Failed(ArchivePublishFailureCode.UnfinishedPublishIntent, "An incomplete PublishIntent requires recovery.");

        var observedTarget = await physical.ObserveAsync(request.CurrentRoot, request.CurrentRelativePath, cancellationToken).ConfigureAwait(false);
        OldCurrentFacts? old = null;
        if (state?.Current is not null || state?.CurrentArchive is not null)
        {
            if (state.Current is null || state.CurrentArchive is null || state.Current.ArchiveVersionId != state.CurrentArchive.Id
                || state.CurrentArchive.Integrity is null || state.CurrentArchive.Length is null)
                return Failed(ArchivePublishFailureCode.CurrentFilesystemStateConflict, "Durable Current facts are inconsistent.");
            var observedOld = await physical.ObserveAsync(request.CurrentRoot, state.Current.RelativePath, cancellationToken).ConfigureAwait(false);
            if (observedOld is null || observedOld.Sha256 != state.CurrentArchive.Integrity.Value || observedOld.Length != state.CurrentArchive.Length.Value)
                return Failed(ArchivePublishFailureCode.CurrentFilesystemStateConflict, "Physical Current does not match durable Current.");
            old = new(state.CurrentArchive, state.Current);
            if (state.Current.RelativePath != request.CurrentRelativePath && observedTarget is not null)
                return Failed(ArchivePublishFailureCode.UnexpectedCurrentArtifact, "New Current target already exists.");
        }
        else if (observedTarget is not null)
            return Failed(ArchivePublishFailureCode.UnexpectedCurrentArtifact, "Current target is not explained by durable state.");

        CurrentPublishStagingProof staging;
        try { staging = await physical.StageCurrentAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { return Failed(ArchivePublishFailureCode.PhysicalPublishFailed, ex.Message); }

        var intent = PendingPublishIntent.Prepare(version, request.CurrentRelativePath, request.BaselineCandidate, request.OutputLayoutFingerprint, old);
        await durableState.BeginPublishAsync(intent, cancellationToken).ConfigureAwait(false);

        if (old is not null && request.HistoryPolicy is EffectiveHistoryEnabled)
        {
            try
            {
                var historyPath = HistoryPhysicalLayoutV1.Create(version.ArchiveUnitId, old.ArchiveVersion);
                var proof = await physical.CaptureHistoryAsync(old, request.CurrentRoot, request.HistoryRoot!, historyPath, cancellationToken).ConfigureAwait(false);
                var durableProof = new HistoryCaptureProof(old.ArchiveVersion.Id, proof.ObservedSha256,
                    new(version.PlanId, version.ArchiveUnitId, old.ArchiveVersion.Id, proof.RelativeStoragePath));
                intent = intent.MarkHistoryCaptured(durableProof);
                await durableState.SavePublishProgressAsync(intent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) { return Failed(ArchivePublishFailureCode.PhysicalPublishFailed, ex.Message); }
        }

        var currentSnapshot = await snapshots.LoadCurrentAsync(version.PlanId, cancellationToken).ConfigureAwait(false);
        var revalidation = PublishTimeRevalidator.Revalidate(request.CapturedExecutionSnapshot, currentSnapshot, version.ArchiveUnitId);
        if (!revalidation.CanPublish)
        {
            var cleaned = await AbortBeforeCurrentAsync(intent, request, staging, cancellationToken).ConfigureAwait(false);
            return cleaned
                ? Failed(ArchivePublishFailureCode.PlanChangedDuringRun, string.Join(", ", revalidation.Reasons))
                : Failed(ArchivePublishFailureCode.AmbiguousPublishRecovery, "Stale publish side effects could not be safely removed; PublishIntent was preserved.");
        }

        CurrentPublishReceipt receipt;
        try { receipt = await physical.PublishCurrentAsync(request, staging, old, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { return Failed(ArchivePublishFailureCode.PhysicalPublishFailed, ex.Message); }
        intent = intent.MarkCurrentPublished(receipt.PublishedAtUtc);
        await durableState.SavePublishProgressAsync(intent, cancellationToken).ConfigureAwait(false);

        DurableUnitMetadataCommitResult committed;
        try { committed = await durableState.CompleteMetadataCommitAsync(intent.RebuildMetadataCommitPlan(), cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { return Failed(ArchivePublishFailureCode.MetadataCommitFailed, ex.Message); }

        var warnings = new List<string>();
        if (revalidation.HistoryMaintenanceOutOfSync && maintenance is not null)
            await maintenance.SaveAsync(new(version.PlanId, version.ArchiveUnitId, MaintenanceKind.HistoryRetention,
                MaintenanceStatus.OutOfSync, "Retention policy changed during publish; cleanup was skipped.", DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        if (old is not null && old.Placement.RelativePath != request.CurrentRelativePath)
        {
            var removed = await physical.DeleteIfMatchesAsync(request.CurrentRoot, old.Placement.RelativePath,
                old.ArchiveVersion.Integrity!.Value, old.ArchiveVersion.Length!.Value, cancellationToken).ConfigureAwait(false);
            if (!removed)
            {
                warnings.Add("Old Current path cleanup is out of sync.");
                if (maintenance is not null) await maintenance.SaveAsync(new(version.PlanId, version.ArchiveUnitId,
                    MaintenanceKind.OldCurrentPathCleanup, MaintenanceStatus.OutOfSync, warnings[^1], DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            }
        }
        try { await physical.CleanupRuntimeArtifactAsync(request.Artifact.PartialArtifactPath, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { warnings.Add($"Runtime artifact cleanup failed: {ex.Message}"); }
        return new(committed, null, revalidation.SkipRetentionCleanup, [.. warnings]);
    }

    private static ArchivePublishResult Failed(ArchivePublishFailureCode code, string warning) => new(null, code, false, [warning]);

    private async Task<bool> AbortBeforeCurrentAsync(PendingPublishIntent intent, ArchivePublishRequest request,
        CurrentPublishStagingProof staging, CancellationToken cancellationToken)
    {
        if (intent.HistoryCapture is not null && request.HistoryRoot is not null)
        {
            var old = intent.OldCurrent!.ArchiveVersion;
            if (!await physical.DeleteIfMatchesAsync(request.HistoryRoot, intent.HistoryCapture.Placement.RelativePath,
                old.Integrity!.Value, old.Length!.Value, cancellationToken).ConfigureAwait(false)) return false;
        }
        if (!await physical.DeleteIfMatchesAsync(request.CurrentRoot, staging.RelativeStoragePath,
            intent.ExpectedNewIntegrity, intent.NewArchive.Length!.Value, cancellationToken).ConfigureAwait(false)) return false;
        await durableState.AbortIncompletePublishAsync(intent, intent.Stage, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
