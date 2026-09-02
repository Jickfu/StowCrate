using System.Collections.Immutable;
using StowCrate.Application.LocalState;
using StowCrate.Application.Publishing;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Application.Tests.Publishing;

public sealed class HistoryRetentionWorkflowTests
{
    private static readonly PlanId Plan = new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly ArchiveUnitId Unit = new(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly OutputRootLocalBinding Root = new("C:/history", "history", true);

    [Fact]
    public async Task KeepLastSelectsDeterministicPrefixAndCompletesEachVictim()
    {
        var store = new Store([Entry("00000000-0000-4000-8000-000000000002", 1), Entry("00000000-0000-4000-8000-000000000001", 1), Entry("00000000-0000-4000-8000-000000000003", 2)]);
        var workflow = new HistoryRetentionWorkflow(store, new Physical(), store);

        var result = await workflow.RunAsync(Plan, Unit, new EffectiveHistoryEnabled(new KeepLastVersionsRetention(1)), Root, CancellationToken.None);

        Assert.Equal(2, result.Selected); Assert.Equal(2, result.Completed); Assert.Empty(result.Pending);
        Assert.Equal([Guid.Parse("00000000-0000-4000-8000-000000000001"), Guid.Parse("00000000-0000-4000-8000-000000000002")], store.Begun.Select(x => x.ArchiveVersionId.Value));
        Assert.Equal(MaintenanceStatus.Completed, store.Maintenance.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisabledAndKeepAllNeverCreateDeletionAuthorization(bool disabled)
    {
        var store = new Store([Entry(Guid.NewGuid().ToString(), 1)]); var policy = disabled ? (EffectiveHistoryPolicy)new EffectiveHistoryDisabled() : new EffectiveHistoryEnabled(new KeepAllRetention());
        var result = await new HistoryRetentionWorkflow(store, new Physical(), store).RunAsync(Plan, Unit, policy, Root, CancellationToken.None);
        Assert.Equal(0, result.Selected); Assert.Empty(store.Begun);
    }

    private static HistoryRetentionEntry Entry(string id, long milliseconds)
    {
        var hash = Sha256Digest.Hash(idu8(id)); var archive = ArchiveVersion.Prepare(new(Guid.Parse(id)), Plan, Unit, PortableArchiveFormat.SevenZip, new(hash))
            .Verify(hash, id.Length).Publish(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)).Supersede();
        return new(archive, new(Plan, Unit, archive.Id, new($"history-v1/{Unit.Value:D}/{id}.7z")));
    }

    private static byte[] idu8(string value) => System.Text.Encoding.UTF8.GetBytes(value);

    private sealed class Store(ImmutableArray<HistoryRetentionEntry> entries) : IHistoryRetentionDurableStore, IMaintenanceStateStore
    {
        private readonly List<RetentionDeletionIntent> intents = [];
        public ImmutableArray<RetentionDeletionIntent> Begun { get; private set; } = [];
        public MaintenanceState Maintenance { get; private set; } = new(Plan, Unit, MaintenanceKind.HistoryRetention, MaintenanceStatus.Pending, null, DateTimeOffset.UnixEpoch);
        public Task<HistoryRetentionSnapshot> LoadRetentionSnapshotAsync(PlanId planId, ArchiveUnitId archiveUnitId, CancellationToken cancellationToken) => Task.FromResult(new HistoryRetentionSnapshot(planId, archiveUnitId, entries));
        public Task BeginDeletionIntentsAsync(RetentionSelectionId selectionId, PlanId planId, ArchiveUnitId archiveUnitId, int keepLastVersionsCount, IReadOnlyCollection<HistoryRetentionEntry> victims, CancellationToken cancellationToken)
        { intents.AddRange(victims.Select(x => new RetentionDeletionIntent(selectionId, planId, archiveUnitId, x.Archive.Id, RetentionDeletionStage.Prepared, x.Placement.RelativePath, x.Archive.Integrity!.Value, x.Archive.Length!.Value, 1, keepLastVersionsCount, DateTimeOffset.UtcNow))); Begun = [.. intents]; return Task.CompletedTask; }
        public Task<ImmutableArray<RetentionDeletionIntent>> ListDeletionIntentsAsync(bool includeCompleted, CancellationToken cancellationToken) => Task.FromResult(intents.Where(x => includeCompleted || x.Stage == RetentionDeletionStage.Prepared).ToImmutableArray());
        public Task CompleteDeletionAsync(RetentionDeletionIntent intent, DateTimeOffset completedAtUtc, CancellationToken cancellationToken) { intents.Remove(intent); intents.Add(intent with { Stage = RetentionDeletionStage.Completed, CompletedAtUtc = completedAtUtc }); return Task.CompletedTask; }
        public Task<int> CompactCompletedDeletionIntentsAsync(IReadOnlyCollection<ArchiveVersionId> confirmedAbsentVersions, CancellationToken cancellationToken) { var count = intents.RemoveAll(x => confirmedAbsentVersions.Contains(x.ArchiveVersionId) && x.Stage == RetentionDeletionStage.Completed); return Task.FromResult(count); }
        public Task<ImmutableArray<MaintenanceState>> ListPendingAsync(PlanId planId, CancellationToken cancellationToken) => Task.FromResult(ImmutableArray<MaintenanceState>.Empty);
        public Task SaveAsync(MaintenanceState state, CancellationToken cancellationToken) { Maintenance = state; return Task.CompletedTask; }
    }

    private sealed class Physical : IHistoryArtifactDeletionStore
    {
        public Task<HistoryDeletionPhysicalResult> DeleteDurablyIfMatchesAsync(OutputRootLocalBinding historyRoot, RetentionDeletionIntent intent, CancellationToken cancellationToken) => Task.FromResult(new HistoryDeletionPhysicalResult(HistoryDeletionPhysicalStatus.DeletedDurably));
        public Task<bool> ConfirmAbsentDurablyAsync(OutputRootLocalBinding historyRoot, RetentionDeletionIntent intent, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
