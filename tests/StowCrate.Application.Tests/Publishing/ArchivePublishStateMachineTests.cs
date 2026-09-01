using System.Collections.Immutable;
using StowCrate.Application.Archiving;
using StowCrate.Application.BackupPlans.Candidates;
using StowCrate.Application.LocalState;
using StowCrate.Application.Publishing;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Core.Paths;

namespace StowCrate.Application.Tests.Publishing;

public sealed class ArchivePublishStateMachineTests
{
    private static readonly PlanId Plan = new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly ArchiveUnitId Unit = new(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly OutputRootLocalBinding CurrentRoot = new("C:/current", "current", true);
    private static readonly OutputRootLocalBinding HistoryRoot = new("C:/history", "history", true);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PublishesFirstOrReplacementWithFrozenHistoryRequirement(bool hasOld, bool historyEnabled)
    {
        var fixture = Fixture.Create(hasOld, historyEnabled, "unit.7z", "unit.7z");
        var result = await fixture.Workflow.PublishAsync(fixture.Request, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(historyEnabled && hasOld ? HistoryCaptureRequirement.Required : HistoryCaptureRequirement.NotRequired,
            result.Commit!.CompletedIntent.HistoryRequirement);
        Assert.Equal(historyEnabled && hasOld, result.Commit.HistoryPlacement is not null);
        Assert.Equal(1, fixture.Physical.PublishCalls);
    }

    [Fact]
    public async Task NewPathPublishCleansOldOnlyAfterMetadataCommit()
    {
        var fixture = Fixture.Create(true, false, "old.7z", "new.zip");

        var result = await fixture.Workflow.PublishAsync(fixture.Request, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, fixture.Physical.DeleteCalls);
        Assert.True(fixture.Physical.DeleteObservedMetadataCommitted);
    }

    [Fact]
    public async Task SemanticDriftSafelyAbortsBeforeCurrentPublish()
    {
        var fixture = Fixture.Create(true, true, "unit.7z", "unit.7z", semanticDrift: true);

        var result = await fixture.Workflow.PublishAsync(fixture.Request, CancellationToken.None);

        Assert.Equal(ArchivePublishFailureCode.PlanChangedDuringRun, result.Failure);
        Assert.Equal(0, fixture.Physical.PublishCalls);
        Assert.True(fixture.Store.Aborted);
    }

    [Fact]
    public async Task RetentionOnlyDriftPublishesAndDurablyMarksOutOfSync()
    {
        var fixture = Fixture.Create(true, true, "unit.7z", "unit.7z", retentionDrift: true);

        var result = await fixture.Workflow.PublishAsync(fixture.Request, CancellationToken.None);

        Assert.True(result.Succeeded); Assert.True(result.SkipRetentionCleanup);
        Assert.Contains(fixture.Maintenance.Saved, x => x.Kind == MaintenanceKind.HistoryRetention && x.Status == MaintenanceStatus.OutOfSync);
    }

    [Fact]
    public async Task MetadataCommitFaultRemainsFailureButPostCommitFailuresRemainSuccess()
    {
        var beforeCommit = Fixture.Create(true, false, "old.7z", "new.7z"); beforeCommit.Store.FailCommit = true;
        Assert.Equal(ArchivePublishFailureCode.MetadataCommitFailed,
            (await beforeCommit.Workflow.PublishAsync(beforeCommit.Request, CancellationToken.None)).Failure);

        var afterCommit = Fixture.Create(true, false, "old.7z", "new.7z");
        afterCommit.Physical.ThrowDelete = true; afterCommit.Physical.ThrowCleanup = true; afterCommit.Maintenance.Throw = true;
        var result = await afterCommit.Workflow.PublishAsync(afterCommit.Request, CancellationToken.None);

        Assert.True(result.Succeeded); Assert.NotNull(result.Commit); Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.PendingMaintenance, x => x.Kind == MaintenanceKind.OldCurrentPathCleanup);
    }

    [Theory]
    [InlineData(HistoryCaptureRequirement.Required, true, UnitStartupRecoveryStatus.MetadataCommitCompleted)]
    [InlineData(HistoryCaptureRequirement.NotRequired, false, UnitStartupRecoveryStatus.MetadataCommitCompleted)]
    [InlineData(HistoryCaptureRequirement.UnknownLegacy, false, UnitStartupRecoveryStatus.AmbiguousPublishRecovery)]
    public async Task ExpectedNewPreparedRecoveryUsesOnlyDurableRequirement(HistoryCaptureRequirement requirement,
        bool provideHistory, UnitStartupRecoveryStatus expected)
    {
        var fixture = Fixture.Create(true, false, "unit.7z", "unit.7z");
        var intent = fixture.Intent(requirement); fixture.Store.Intent = intent;
        fixture.Physical.SetCurrent(intent.CurrentRelativePath, intent.NewArchive);
        if (provideHistory)
            fixture.Physical.SetHistory(HistoryPhysicalLayoutV1.Create(Unit, intent.OldCurrent!.ArchiveVersion), intent.OldCurrent.ArchiveVersion);

        var result = await new PublishIntentRecoveryWorkflow(fixture.Store, fixture.Physical)
            .RecoverAsync(intent, Fixture.Bindings, CancellationToken.None);

        Assert.Equal(expected, result.Status);
        if (requirement is HistoryCaptureRequirement.Required && expected is UnitStartupRecoveryStatus.MetadataCommitCompleted)
            Assert.NotNull(fixture.Store.Intent!.HistoryCapture);
    }

    [Fact]
    public async Task RequiredHistoryMissingIsAmbiguousAndOldCurrentCanSafeAbort()
    {
        var fixture = Fixture.Create(true, false, "unit.7z", "unit.7z"); var intent = fixture.Intent(HistoryCaptureRequirement.Required);
        fixture.Physical.SetCurrent(intent.CurrentRelativePath, intent.NewArchive);
        var ambiguous = await new PublishIntentRecoveryWorkflow(fixture.Store, fixture.Physical).RecoverAsync(intent, Fixture.Bindings, CancellationToken.None);
        Assert.Equal(UnitStartupRecoveryStatus.AmbiguousPublishRecovery, ambiguous.Status);

        fixture.Physical.SetCurrent(intent.OldCurrent!.Placement.RelativePath, intent.OldCurrent.ArchiveVersion);
        var aborted = await new PublishIntentRecoveryWorkflow(fixture.Store, fixture.Physical).RecoverAsync(intent, Fixture.Bindings, CancellationToken.None);
        Assert.Equal(UnitStartupRecoveryStatus.ResumeOrAbortRequired, aborted.Status); Assert.True(fixture.Store.Aborted);
    }

    [Fact]
    public async Task CrashWindowsPreserveOrRebuildJournalProgress()
    {
        var fixture = Fixture.Create(true, true, "unit.7z", "unit.7z"); var intent = fixture.Intent(HistoryCaptureRequirement.Required);
        var historyPath = HistoryPhysicalLayoutV1.Create(Unit, intent.OldCurrent!.ArchiveVersion);
        fixture.Physical.SetHistory(historyPath, intent.OldCurrent.ArchiveVersion); fixture.Physical.SetCurrent(intent.CurrentRelativePath, intent.NewArchive);

        var result = await new PublishIntentRecoveryWorkflow(fixture.Store, fixture.Physical).RecoverAsync(intent, Fixture.Bindings, CancellationToken.None);

        Assert.Equal(UnitStartupRecoveryStatus.MetadataCommitCompleted, result.Status);
        Assert.Equal(PublishIntentStage.MetadataCommitted, fixture.Store.Intent!.Stage);
    }

    private sealed class Fixture
    {
        private Fixture(ArchivePublishRequest request, FakeStore store, FakePhysical physical, FakeMaintenance maintenance, ExecutionSemanticSnapshot current)
        { Request = request; Store = store; Physical = physical; physical.Store = store; Maintenance = maintenance; Workflow = new(store, physical, new SnapshotProvider(current), maintenance); }
        public ArchivePublishRequest Request { get; }
        public FakeStore Store { get; }
        public FakePhysical Physical { get; }
        public FakeMaintenance Maintenance { get; }
        public ArchivePublishWorkflow Workflow { get; }
        public static DevicePlanLocalBindings Bindings => new(Plan, new(Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")), [], CurrentRoot, HistoryRoot, []);

        public PendingPublishIntent Intent(HistoryCaptureRequirement requirement) => PendingPublishIntent.Prepare(Request.Artifact.ArchiveVersion,
            Request.CurrentRelativePath, Request.BaselineCandidate, Request.OutputLayoutFingerprint,
            Store.State!.CurrentArchive is null ? null : new(Store.State.CurrentArchive, Store.State.Current!), requirement);

        public static Fixture Create(bool hasOld, bool historyEnabled, string oldPath, string newPath, bool semanticDrift = false, bool retentionDrift = false)
        {
            var spec = new ArchiveSpecFingerprint(Hash("spec")); var layout = new OutputLayoutFingerprint(Hash("layout"));
            var version = ArchiveVersion.Prepare(new(Guid.NewGuid()), Plan, Unit, PortableArchiveFormat.SevenZip, spec).Verify(Hash("new"), 10);
            var manifest = new ArchiveManifestV1(1, 1, Plan, new(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd")), Unit,
                new("unit"), new(PortableArchiveFormat.SevenZip, PortableCompressionPreset.Standard, new NoProtection()), []);
            var fingerprints = Fingerprints(spec, layout); var captured = Snapshot("semantic", "history-a");
            var current = Snapshot(semanticDrift ? "semantic-drift" : "semantic", retentionDrift ? "history-b" : "history-a");
            var request = new ArchivePublishRequest(new("runtime.partial", version, manifest), BaselineCandidate.FromCompleteCandidate(fingerprints), layout,
                new(newPath), new(newPath), historyEnabled ? new EffectiveHistoryEnabled(new KeepAllRetention()) : new EffectiveHistoryDisabled(),
                captured, CurrentRoot, historyEnabled ? HistoryRoot : null);
            ArchiveUnitDurableState? state = null; var physical = new FakePhysical();
            if (hasOld)
            {
                var old = ArchiveVersion.Prepare(new(Guid.NewGuid()), Plan, Unit, PortableArchiveFormat.SevenZip, spec).Verify(Hash("old"), 9).Publish(DateTimeOffset.UnixEpoch);
                var placement = new CurrentVersion(Plan, Unit, old.Id, new(oldPath)); state = new(old, placement, [], null, null, null);
                physical.SetCurrent(placement.RelativePath, old);
            }
            return new(request, new FakeStore(state), physical, new(), current);
        }
    }

    private sealed class FakeStore(ArchiveUnitDurableState? initial) : IArchiveUnitDurableStateStore
    {
        public ArchiveUnitDurableState? State = initial; public PendingPublishIntent? Intent; public bool Aborted; public bool FailCommit; public bool MetadataCommitted;
        public Task<ArchiveUnitDurableState?> LoadAsync(PlanId planId, ArchiveUnitId archiveUnitId, CancellationToken cancellationToken) => Task.FromResult(State is null ? null : State with { PublishIntent = Intent });
        public Task<ImmutableArray<PendingPublishIntent>> ListIncompletePublishIntentsAsync(CancellationToken cancellationToken) => Task.FromResult(ImmutableArray<PendingPublishIntent>.Empty);
        public Task<int> CleanupCompletedPublishIntentsAsync(CancellationToken cancellationToken) => Task.FromResult(0);
        public Task BeginPublishAsync(PendingPublishIntent intent, CancellationToken cancellationToken) { Intent = intent; return Task.CompletedTask; }
        public Task SavePublishProgressAsync(PendingPublishIntent intent, CancellationToken cancellationToken) { Intent = intent; return Task.CompletedTask; }
        public Task AbortIncompletePublishAsync(PendingPublishIntent intent, PublishIntentStage expectedStage, CancellationToken cancellationToken) { Aborted = true; Intent = null; return Task.CompletedTask; }
        public Task<DurableUnitMetadataCommitResult> CompleteMetadataCommitAsync(DurableUnitMetadataCommitPlan commit, CancellationToken cancellationToken)
        {
            if (FailCommit) throw new LocalStateRepositoryException("fault");
            var result = DurableUnitMetadataCommit.ConfirmCommitted(commit); Intent = result.CompletedIntent; MetadataCommitted = true; return Task.FromResult(result);
        }
        public Task CommitOutputReorganizationAsync(OutputReorganizationResult reorganization, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakePhysical : IArchivePhysicalPublisher
    {
        private readonly Dictionary<string, PhysicalArchiveObservation> current = []; private readonly Dictionary<string, PhysicalArchiveObservation> history = [];
        public int PublishCalls; public int DeleteCalls; public bool DeleteObservedMetadataCommitted; public bool ThrowDelete; public bool ThrowCleanup; public FakeStore? Store;
        public void SetCurrent(RelativeStoragePath path, ArchiveVersion version) => current[path.Value] = Observation(path, version);
        public void SetHistory(RelativeStoragePath path, ArchiveVersion version) => history[path.Value] = Observation(path, version);
        public Task<PhysicalArchiveObservation?> ObserveAsync(OutputRootLocalBinding root, RelativeStoragePath path, CancellationToken cancellationToken) =>
            Task.FromResult((root == HistoryRoot ? history : current).GetValueOrDefault(path.Value));
        public Task<CurrentPublishStagingProof> StageCurrentAsync(ArchivePublishRequest request, CancellationToken cancellationToken) => Task.FromResult(new CurrentPublishStagingProof(Plan, Unit,
            request.Artifact.ArchiveVersion.Id, CurrentPublishTempLayoutV1.Create(request.CurrentRelativePath, request.Artifact.ArchiveVersion.Id), request.Artifact.ArchiveVersion.Integrity!.Value, request.Artifact.ArchiveVersion.Integrity.Value, request.Artifact.ArchiveVersion.Length!.Value));
        public Task<HistoryCapturePhysicalProof> CaptureHistoryAsync(OldCurrentFacts oldCurrent, OutputRootLocalBinding currentRoot, OutputRootLocalBinding historyRoot, RelativeStoragePath historyPath, CancellationToken cancellationToken)
        { SetHistory(historyPath, oldCurrent.ArchiveVersion); return Task.FromResult(new HistoryCapturePhysicalProof(Plan, Unit, oldCurrent.ArchiveVersion.Id, historyPath, oldCurrent.ArchiveVersion.Integrity!.Value, oldCurrent.ArchiveVersion.Integrity.Value, oldCurrent.ArchiveVersion.Length!.Value)); }
        public Task<CurrentPublishReceipt> PublishCurrentAsync(ArchivePublishRequest request, CurrentPublishStagingProof staging, OldCurrentFacts? oldCurrent, CancellationToken cancellationToken)
        { PublishCalls++; SetCurrent(request.CurrentRelativePath, request.Artifact.ArchiveVersion); return Task.FromResult(new CurrentPublishReceipt(Plan, Unit, request.Artifact.ArchiveVersion.Id, request.CurrentRelativePath, request.Artifact.ArchiveVersion.Integrity!.Value, request.Artifact.ArchiveVersion.Integrity.Value, request.Artifact.ArchiveVersion.Length!.Value, DateTimeOffset.UtcNow, new(true, "test"))); }
        public Task<bool> DeleteIfMatchesAsync(OutputRootLocalBinding root, RelativeStoragePath path, Sha256Digest expected, long length, CancellationToken cancellationToken)
        { if (ThrowDelete) throw new IOException("delete fault"); DeleteCalls++; DeleteObservedMetadataCommitted |= Store?.MetadataCommitted == true; (root == HistoryRoot ? history : current).Remove(path.Value); return Task.FromResult(true); }
        public Task CleanupRuntimeArtifactAsync(string path, CancellationToken cancellationToken) => ThrowCleanup ? throw new IOException("cleanup fault") : Task.CompletedTask;
        private static PhysicalArchiveObservation Observation(RelativeStoragePath path, ArchiveVersion version) => new(path, version.Integrity!.Value, version.Length!.Value);
    }

    private sealed class FakeMaintenance : IMaintenanceStateStore
    { public List<MaintenanceState> Saved { get; } = []; public bool Throw; public Task<ImmutableArray<MaintenanceState>> ListPendingAsync(PlanId planId, CancellationToken cancellationToken) => Task.FromResult(ImmutableArray<MaintenanceState>.Empty); public Task SaveAsync(MaintenanceState state, CancellationToken cancellationToken) { if (Throw) throw new LocalStateRepositoryException("maintenance fault"); Saved.Add(state); return Task.CompletedTask; } }
    private sealed class SnapshotProvider(ExecutionSemanticSnapshot snapshot) : ICurrentExecutionSemanticSnapshotProvider { public Task<ExecutionSemanticSnapshot> LoadCurrentAsync(PlanId planId, CancellationToken cancellationToken) => Task.FromResult(snapshot); }

    private static CandidateArchiveFingerprints Fingerprints(ArchiveSpecFingerprint spec, OutputLayoutFingerprint layout)
    { var d = new DiagnosticFingerprint(Hash("component")); return new(1, new(1, 1, 1), true, new(Hash("entry")), new(Hash("selection")), spec, layout, new(Hash("semantic")), new(Hash("binding")), new(d, d, d, d, d, d, d, d)); }
    private static ExecutionSemanticSnapshot Snapshot(string semantic, string history) => new(Plan, new(Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")), null,
        new(Hash("plan")), [new(Unit, new(Hash(semantic)), new(Hash("binding")), null, null, new(Hash(history)))]);
    private static Sha256Digest Hash(string value) => CanonicalFingerprintEncodingV1.Encode("test", writer => writer.Utf8(1, value));
}
