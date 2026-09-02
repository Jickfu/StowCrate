using System.Collections.Immutable;
using StowCrate.Application.LocalState;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Application.Publishing;

public enum HistoryDeletionPhysicalStatus { DeletedDurably, AlreadyAbsentDurably, Mismatch, UnsupportedObject, Failed }
public sealed record HistoryDeletionPhysicalResult(HistoryDeletionPhysicalStatus Status, string? Detail = null)
{
    public bool CanCommitMetadata => Status is HistoryDeletionPhysicalStatus.DeletedDurably or HistoryDeletionPhysicalStatus.AlreadyAbsentDurably;
}

public interface IHistoryArtifactDeletionStore
{
    Task<HistoryDeletionPhysicalResult> DeleteDurablyIfMatchesAsync(OutputRootLocalBinding historyRoot,
        RetentionDeletionIntent intent, CancellationToken cancellationToken);
    Task<bool> ConfirmAbsentDurablyAsync(OutputRootLocalBinding historyRoot, RetentionDeletionIntent intent, CancellationToken cancellationToken);
}

public sealed record HistoryRetentionRunResult(int Selected, int Completed, ImmutableArray<RetentionDeletionIntent> Pending,
    ImmutableArray<string> Diagnostics);

public interface IHistoryRetentionRecoveryCoordinator
{
    Task<HistoryRetentionRunResult> ReconcileAsync(PlanId planId, ArchiveUnitId unitId, OutputRootLocalBinding? historyRoot, CancellationToken cancellationToken);
}

public sealed class HistoryRetentionWorkflow(IHistoryRetentionDurableStore durable, IHistoryArtifactDeletionStore physical,
    IMaintenanceStateStore maintenance) : IHistoryRetentionRecoveryCoordinator
{
    public const int RetentionSemanticsVersion = 1;

    public async Task<HistoryRetentionRunResult> RunAsync(PlanId planId, ArchiveUnitId unitId, EffectiveHistoryPolicy policy,
        OutputRootLocalBinding? historyRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var selected = 0;
        if (policy is EffectiveHistoryEnabled { Retention: KeepLastVersionsRetention keep })
        {
            if (historyRoot is null) return await OutOfSync(planId, unitId, "HistoryRoot is unavailable.", 0, cancellationToken).ConfigureAwait(false);
            var snapshot = await durable.LoadRetentionSnapshotAsync(planId, unitId, cancellationToken).ConfigureAwait(false);
            var ordered = snapshot.Entries.OrderBy(x => x.Archive.PublishedAtUtc!.Value)
                .ThenBy(x => x.Archive.Id.Value, RfcGuidComparer.Instance).ToArray();
            var victims = ordered.Take(Math.Max(0, ordered.Length - keep.Count)).ToArray(); selected = victims.Length;
            if (victims.Length > 0)
                await durable.BeginDeletionIntentsAsync(new(Guid.NewGuid()), planId, unitId, keep.Count, victims, cancellationToken).ConfigureAwait(false);
        }

        if (historyRoot is null)
        {
            var existing = await durable.ListDeletionIntentsAsync(false, cancellationToken).ConfigureAwait(false);
            if (existing.Any(x => x.PlanId == planId && x.ArchiveUnitId == unitId))
                return await OutOfSync(planId, unitId, "HistoryRoot is unavailable for pending retention.", selected, cancellationToken).ConfigureAwait(false);
            return new(selected, 0, [], []);
        }

        var pending = (await durable.ListDeletionIntentsAsync(false, cancellationToken).ConfigureAwait(false))
            .Where(x => x.PlanId == planId && x.ArchiveUnitId == unitId).ToArray();
        var completed = 0; var diagnostics = ImmutableArray.CreateBuilder<string>();
        foreach (var intent in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // destructive action 开始后不再使用 caller cancellation；必须把单个 victim 推进到可恢复稳定点。
            var deletion = await physical.DeleteDurablyIfMatchesAsync(historyRoot, intent, CancellationToken.None).ConfigureAwait(false);
            if (!deletion.CanCommitMetadata) { diagnostics.Add(deletion.Detail ?? deletion.Status.ToString()); continue; }
            await durable.CompleteDeletionAsync(intent, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false); completed++;
        }
        var remaining = (await durable.ListDeletionIntentsAsync(false, CancellationToken.None).ConfigureAwait(false))
            .Where(x => x.PlanId == planId && x.ArchiveUnitId == unitId).ToImmutableArray();
        await maintenance.SaveAsync(new(planId, unitId, MaintenanceKind.HistoryRetention,
            remaining.IsEmpty && diagnostics.Count == 0 ? MaintenanceStatus.Completed : MaintenanceStatus.OutOfSync,
            diagnostics.Count == 0 ? null : string.Join("; ", diagnostics), DateTimeOffset.UtcNow), CancellationToken.None).ConfigureAwait(false);
        return new(selected, completed, remaining, diagnostics.ToImmutable());
    }

    private async Task<HistoryRetentionRunResult> OutOfSync(PlanId planId, ArchiveUnitId unitId, string detail, int selected, CancellationToken token)
    {
        await maintenance.SaveAsync(new(planId, unitId, MaintenanceKind.HistoryRetention, MaintenanceStatus.OutOfSync, detail, DateTimeOffset.UtcNow), token).ConfigureAwait(false);
        return new(selected, 0, [], [detail]);
    }

    public async Task<HistoryRetentionRunResult> ReconcileAsync(PlanId planId, ArchiveUnitId unitId, OutputRootLocalBinding? historyRoot,
        CancellationToken cancellationToken)
    {
        var all = (await durable.ListDeletionIntentsAsync(true, cancellationToken).ConfigureAwait(false)).Where(x => x.PlanId == planId && x.ArchiveUnitId == unitId).ToArray();
        if (all.Length == 0) return new(0, 0, [], []);
        if (historyRoot is null) return await OutOfSync(planId, unitId, "HistoryRoot is unavailable for retention reconciliation.", 0, cancellationToken).ConfigureAwait(false);
        var completed = 0; var diagnostics = ImmutableArray.CreateBuilder<string>(); var compactable = new List<ArchiveVersionId>();
        foreach (var intent in all)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (intent.Stage is RetentionDeletionStage.Prepared)
            {
                var result = await physical.DeleteDurablyIfMatchesAsync(historyRoot, intent, CancellationToken.None).ConfigureAwait(false);
                if (!result.CanCommitMetadata) { diagnostics.Add(result.Detail ?? result.Status.ToString()); continue; }
                await durable.CompleteDeletionAsync(intent, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false); completed++; continue;
            }
            if (!await physical.ConfirmAbsentDurablyAsync(historyRoot, intent, CancellationToken.None).ConfigureAwait(false))
            {
                var result = await physical.DeleteDurablyIfMatchesAsync(historyRoot, intent, CancellationToken.None).ConfigureAwait(false);
                if (!result.CanCommitMetadata) { diagnostics.Add(result.Detail ?? result.Status.ToString()); continue; }
            }
            if (await physical.ConfirmAbsentDurablyAsync(historyRoot, intent, CancellationToken.None).ConfigureAwait(false)) compactable.Add(intent.ArchiveVersionId);
        }
        if (compactable.Count > 0) await durable.CompactCompletedDeletionIntentsAsync(compactable, CancellationToken.None).ConfigureAwait(false);
        var pending = (await durable.ListDeletionIntentsAsync(false, CancellationToken.None).ConfigureAwait(false)).Where(x => x.PlanId == planId && x.ArchiveUnitId == unitId).ToImmutableArray();
        await maintenance.SaveAsync(new(planId, unitId, MaintenanceKind.HistoryRetention, pending.IsEmpty && diagnostics.Count == 0 ? MaintenanceStatus.Completed : MaintenanceStatus.OutOfSync,
            diagnostics.Count == 0 ? null : string.Join("; ", diagnostics), DateTimeOffset.UtcNow), CancellationToken.None).ConfigureAwait(false);
        return new(0, completed, pending, diagnostics.ToImmutable());
    }

    private sealed class RfcGuidComparer : IComparer<Guid>
    {
        public static RfcGuidComparer Instance { get; } = new();
        public int Compare(Guid x, Guid y) { Span<byte> xb = stackalloc byte[16]; Span<byte> yb = stackalloc byte[16]; x.TryWriteBytes(xb, true, out _); y.TryWriteBytes(yb, true, out _); return xb.SequenceCompareTo(yb); }
    }
}
