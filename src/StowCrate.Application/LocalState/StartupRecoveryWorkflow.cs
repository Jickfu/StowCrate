using System.Collections.Immutable;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Application.Publishing;

namespace StowCrate.Application.LocalState;

public interface ICurrentArtifactRecoveryProbe
{
    Task<Sha256Digest?> ObserveIntegrityAsync(OutputRootLocalBinding currentRoot, RelativeStoragePath relativePath, CancellationToken cancellationToken);
}

public enum UnitStartupRecoveryStatus { MetadataCommitCompleted, ResumeOrAbortRequired, AmbiguousPublishRecovery }
public sealed record UnitStartupRecoveryResult(PlanId PlanId, ArchiveUnitId ArchiveUnitId, UnitStartupRecoveryStatus Status, string? Detail);
public sealed record HistoryRetentionStartupResult(PlanId PlanId, ArchiveUnitId ArchiveUnitId, int Completed, int Pending, ImmutableArray<string> Diagnostics);
public sealed record ConfigDatabaseStartupResult(ConfigDatabaseIdentity Identity, ImmutableArray<PlanRegistration> ActivePlans,
    ImmutableArray<UnitStartupRecoveryResult> UnitRecoveries, ImmutableArray<HistoryRetentionStartupResult> RetentionRecoveries = default);

public sealed class ConfigDatabaseStartupCoordinator(IConfigDatabaseSessionOpener opener, ICurrentArtifactRecoveryProbe probe,
    IPublishIntentRecoveryCoordinator? publishRecovery = null, IHistoryRetentionRecoveryCoordinator? retentionRecovery = null)
{
    public async Task<ConfigDatabaseStartupResult> StartAsync(ConfigDatabaseOpenRequest request, CancellationToken cancellationToken)
    {
        var session = await opener.OpenAsync(request, cancellationToken).ConfigureAwait(false);
        var registrations = await session.Plans.ListRegisteredAsync(activeOnly: true, cancellationToken).ConfigureAwait(false);
        var intents = await session.ArchiveUnits.ListIncompletePublishIntentsAsync(cancellationToken).ConfigureAwait(false);
        var recoveries = ImmutableArray.CreateBuilder<UnitStartupRecoveryResult>();

        foreach (var intent in intents)
        {
            var bindings = await session.Bindings.LoadAsync(intent.PlanId, cancellationToken).ConfigureAwait(false);
            if (bindings is not null && publishRecovery is not null)
            {
                recoveries.Add(await publishRecovery.RecoverAsync(intent, bindings, cancellationToken).ConfigureAwait(false));
                continue;
            }
            if (bindings?.CurrentRoot is null)
            {
                recoveries.Add(new(intent.PlanId, intent.ArchiveUnitId, UnitStartupRecoveryStatus.AmbiguousPublishRecovery, "CurrentRoot binding is unavailable."));
                continue;
            }

            var observed = await probe.ObserveIntegrityAsync(bindings.CurrentRoot, intent.CurrentRelativePath, cancellationToken).ConfigureAwait(false);
            var action = PublishRecoveryDecider.Decide(intent, observed);
            if (action is PublishRecoveryAction.CompleteMetadataCommit && intent.Stage is PublishIntentStage.CurrentPublished)
            {
                await session.ArchiveUnits.CompleteMetadataCommitAsync(intent.RebuildMetadataCommitPlan(), cancellationToken).ConfigureAwait(false);
                recoveries.Add(new(intent.PlanId, intent.ArchiveUnitId, UnitStartupRecoveryStatus.MetadataCommitCompleted, null));
            }
            else if (action is PublishRecoveryAction.AbortOrResumeOldCurrent)
            {
                recoveries.Add(new(intent.PlanId, intent.ArchiveUnitId, UnitStartupRecoveryStatus.ResumeOrAbortRequired, "Durable PublishIntent was preserved."));
            }
            else
            {
                recoveries.Add(new(intent.PlanId, intent.ArchiveUnitId, UnitStartupRecoveryStatus.AmbiguousPublishRecovery, "Observed Current does not prove old or expected-new artifact."));
            }
        }

        var retentionRecoveries = ImmutableArray.CreateBuilder<HistoryRetentionStartupResult>();
        if (retentionRecovery is not null)
        {
            var deletionIntents = await session.HistoryRetention.ListDeletionIntentsAsync(true, cancellationToken).ConfigureAwait(false);
            foreach (var group in deletionIntents.GroupBy(x => (x.PlanId, x.ArchiveUnitId)))
            {
                var bindings = await session.Bindings.LoadAsync(group.Key.PlanId, cancellationToken).ConfigureAwait(false);
                var result = await retentionRecovery.ReconcileAsync(group.Key.PlanId, group.Key.ArchiveUnitId, bindings?.HistoryRoot, cancellationToken).ConfigureAwait(false);
                retentionRecoveries.Add(new(group.Key.PlanId, group.Key.ArchiveUnitId, result.Completed, result.Pending.Length, result.Diagnostics));
            }
        }
        return new(session.Identity, registrations, recoveries.ToImmutable(), retentionRecoveries.ToImmutable());
    }
}
