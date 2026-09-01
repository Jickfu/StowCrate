using System.Collections.Immutable;
using StowCrate.Application.BackupPlans.Documents;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Application.LocalState;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Infrastructure.Configuration.BackupPlans;
using StowCrate.Infrastructure.Configuration.BackupPlans.V1;
using StowCrate.Infrastructure.Filesystem;
using StowCrate.Infrastructure.Persistence.ConfigDb;

namespace StowCrate.Infrastructure.Tests.Persistence;

public sealed class ConfigDbWorkflowIntegrationTests
{
    [Fact]
    public async Task StartupReopensDatabaseAndCompletesCurrentPublishedMetadataFromJournalOnly()
    {
        await using var database = await WorkflowDatabase.Create();
        var (plan, unit) = await database.RegisterFixturePlan();
        var currentRoot = Directory.CreateDirectory(Path.Combine(database.DirectoryPath, "current")).FullName;
        var payload = "published-current"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(currentRoot, "unit.7z"), payload);
        var identity = (await database.Repository.LoadAsync(CancellationToken.None))!;
        await database.Repository.SaveValidatedAggregateAsync(new(plan.Id, identity.DeviceId, [], new(currentRoot, Key(currentRoot), true), null, []), CancellationToken.None);
        var prepared = Intent(plan.Id, unit.Id, new(Guid.NewGuid()), Sha256Digest.Hash(payload));
        await database.Repository.BeginPublishAsync(prepared, CancellationToken.None);
        await database.Repository.SavePublishProgressAsync(prepared.MarkCurrentPublished(DateTimeOffset.UnixEpoch), CancellationToken.None);
        var startup = new ConfigDatabaseStartupCoordinator(new ConfigDatabaseSessionOpener(), new CurrentArtifactRecoveryProbe());
        var result = await startup.StartAsync(new(database.Path), CancellationToken.None);

        Assert.Equal(identity.DeviceId, result.Identity.DeviceId);
        Assert.Contains(result.ActivePlans, registration => registration.PlanId == plan.Id);
        Assert.Equal(UnitStartupRecoveryStatus.MetadataCommitCompleted, Assert.Single(result.UnitRecoveries).Status);
        var reopened = await ConfigDbOpenCoordinator.OpenAsync(database.Path);
        var state = await reopened.LoadAsync(plan.Id, unit.Id, CancellationToken.None);
        Assert.Equal(PublishIntentStage.MetadataCommitted, state!.PublishIntent!.Stage);
        Assert.Equal(state.Current!.ArchiveVersionId, state.Baseline!.ArchiveVersionId);
    }

    [Fact]
    public async Task StartupQuarantinesAmbiguousUnitWithoutBlockingOtherPlansOrChangingJournal()
    {
        await using var database = await WorkflowDatabase.Create();
        var (plan, unit) = await database.RegisterFixturePlan();
        var currentRoot = Directory.CreateDirectory(Path.Combine(database.DirectoryPath, "ambiguous-current")).FullName;
        await File.WriteAllTextAsync(Path.Combine(currentRoot, "unit.7z"), "unexpected");
        var identity = (await database.Repository.LoadAsync(CancellationToken.None))!;
        await database.Repository.SaveValidatedAggregateAsync(new(plan.Id, identity.DeviceId, [], new(currentRoot, Key(currentRoot), true), null, []), CancellationToken.None);
        var prepared = Intent(plan.Id, unit.Id, new(Guid.NewGuid()), Sha256Digest.Hash("expected"u8));
        await database.Repository.BeginPublishAsync(prepared, CancellationToken.None);
        await database.Repository.SavePublishProgressAsync(prepared.MarkCurrentPublished(DateTimeOffset.UnixEpoch), CancellationToken.None);
        var healthyPlan = CloneWithNewIdentities(plan);
        await new AuthoritativePlanWorkflow(database.Repository, new BackupPlanDocumentSource()).CreateManagedAsync(healthyPlan, CancellationToken.None);

        var result = await new ConfigDatabaseStartupCoordinator(new ConfigDatabaseSessionOpener(), new CurrentArtifactRecoveryProbe())
            .StartAsync(new(database.Path), CancellationToken.None);

        Assert.Equal(UnitStartupRecoveryStatus.AmbiguousPublishRecovery, Assert.Single(result.UnitRecoveries).Status);
        Assert.Equal(2, result.ActivePlans.Length);
        Assert.Contains(result.ActivePlans, registration => registration.PlanId == healthyPlan.Id);
        var state = await database.Repository.LoadAsync(plan.Id, unit.Id, CancellationToken.None);
        Assert.Equal(PublishIntentStage.CurrentPublished, state!.PublishIntent!.Stage);
        Assert.Null(state.Current);
    }

    [Fact]
    public async Task StartupPreservesHistoryCapturedJournalWhenOldCurrentIsStillObserved()
    {
        await using var database = await WorkflowDatabase.Create(); var (plan, unit) = await database.RegisterFixturePlan();
        var root = Directory.CreateDirectory(Path.Combine(database.DirectoryPath, "old-current")).FullName; var oldBytes = "old-current"u8.ToArray(); await File.WriteAllBytesAsync(Path.Combine(root, "unit.7z"), oldBytes);
        var identity = (await database.Repository.LoadAsync(CancellationToken.None))!; await database.Repository.SaveValidatedAggregateAsync(new(plan.Id, identity.DeviceId, [], new(root, Key(root), true), null, []), CancellationToken.None);
        var oldIntent = Intent(plan.Id, unit.Id, new(Guid.NewGuid()), Sha256Digest.Hash(oldBytes)); await database.Repository.BeginPublishAsync(oldIntent, CancellationToken.None); var oldPublished = oldIntent.MarkCurrentPublished(DateTimeOffset.UnixEpoch); await database.Repository.SavePublishProgressAsync(oldPublished, CancellationToken.None); await database.Repository.CompleteMetadataCommitAsync(oldPublished.RebuildMetadataCommitPlan(), CancellationToken.None);
        var oldState = (await database.Repository.LoadAsync(plan.Id, unit.Id, CancellationToken.None))!;
        var fingerprints = Fingerprints(); var nextArchive = ArchiveVersion.Prepare(new(Guid.NewGuid()), plan.Id, unit.Id, PortableArchiveFormat.SevenZip, fingerprints.ArchiveSpec).Verify(Sha256Digest.Hash("new"u8), 3);
        var next = PendingPublishIntent.Prepare(nextArchive, new("unit.7z"), BaselineCandidate.FromCompleteCandidate(fingerprints), fingerprints.OutputLayout,
            new(oldState.CurrentArchive!, oldState.Current!), HistoryCaptureRequirement.Required);
        await database.Repository.BeginPublishAsync(next, CancellationToken.None);
        var history = new HistoryVersionPlacement(plan.Id, unit.Id, oldState.CurrentArchive!.Id, new("history/unit.7z"));
        await database.Repository.SavePublishProgressAsync(next.MarkHistoryCaptured(new(oldState.CurrentArchive.Id, oldState.CurrentArchive.Integrity!.Value, history)), CancellationToken.None);

        var result = await new ConfigDatabaseStartupCoordinator(new ConfigDatabaseSessionOpener(), new CurrentArtifactRecoveryProbe()).StartAsync(new(database.Path), CancellationToken.None);

        Assert.Equal(UnitStartupRecoveryStatus.ResumeOrAbortRequired, Assert.Single(result.UnitRecoveries).Status);
        var preserved = await database.Repository.LoadAsync(plan.Id, unit.Id, CancellationToken.None);
        Assert.Equal(PublishIntentStage.HistoryCaptured, preserved!.PublishIntent!.Stage);
        Assert.Equal(oldState.Current!.ArchiveVersionId, preserved.Current!.ArchiveVersionId);
    }

    [Fact]
    public async Task AuthoritativeWorkflowRequiresExplicitConversionsAndNeverFallsBackForMissingFile()
    {
        await using var database = await WorkflowDatabase.Create(registerDefaultPlan: false);
        var source = new BackupPlanDocumentSource();
        var fixture = await ReadFixturePlan();
        var workflow = new AuthoritativePlanWorkflow(database.Repository, source);

        var managed = await workflow.CreateManagedAsync(fixture, CancellationToken.None);
        Assert.Equal(1, managed.ManagedRevision);
        var runtimeIntent = Intent(fixture.Id, fixture.ArchiveUnits[0].Id, new(Guid.NewGuid()), Sha256Digest.Hash("runtime"u8));
        await database.Repository.BeginPublishAsync(runtimeIntent, CancellationToken.None);
        var runtimePublished = runtimeIntent.MarkCurrentPublished(DateTimeOffset.UnixEpoch);
        await database.Repository.SavePublishProgressAsync(runtimePublished, CancellationToken.None);
        await database.Repository.CompleteMetadataCommitAsync(runtimePublished.RebuildMetadataCommitPlan(), CancellationToken.None);
        await workflow.SetActiveAsync(fixture.Id, false, CancellationToken.None);
        var inactive = await workflow.LoadAsync(fixture.Id, CancellationToken.None);
        Assert.False(inactive.IsActive);
        Assert.Equal(1, inactive.ManagedRevision);
        Assert.NotNull((await database.Repository.LoadAsync(fixture.Id, fixture.ArchiveUnits[0].Id, CancellationToken.None))!.Baseline);

        var filePath = Path.Combine(database.DirectoryPath, "plan.backupplan");
        await File.WriteAllBytesAsync(filePath, source.Write(fixture).CanonicalUtf8Payload.ToArray());
        var fileBacked = await workflow.ConvertToFileBackedAsync(fixture.Id, filePath, CancellationToken.None);
        Assert.Equal(PlanAuthority.FileBacked, fileBacked.Authority);
        Assert.NotNull((await database.Repository.LoadAsync(fixture.Id, fixture.ArchiveUnits[0].Id, CancellationToken.None))!.Current);
        await Assert.ThrowsAsync<AuthoritativePlanConflictException>(() => workflow.UpdateManagedAsync(fixture, 1, CancellationToken.None));
        File.Delete(filePath);
        await Assert.ThrowsAsync<BackupPlanDocumentSourceException>(() => workflow.LoadAsync(fixture.Id, CancellationToken.None));
    }

    [Fact]
    public async Task RegisterRejectsSameIdentityWithDifferentSemantics()
    {
        await using var database = await WorkflowDatabase.Create(registerDefaultPlan: false);
        var source = new BackupPlanDocumentSource(); var plan = await ReadFixturePlan(); var workflow = new AuthoritativePlanWorkflow(database.Repository, source);
        var firstPath = Path.Combine(database.DirectoryPath, "first.backupplan"); var secondPath = Path.Combine(database.DirectoryPath, "second.backupplan");
        await File.WriteAllBytesAsync(firstPath, source.Write(plan).CanonicalUtf8Payload.ToArray());
        var changed = CopyWithName(plan, plan.Name + " changed");
        await File.WriteAllBytesAsync(secondPath, source.Write(changed).CanonicalUtf8Payload.ToArray());
        await workflow.RegisterFileBackedAsync(firstPath, CancellationToken.None);
        var error = await Assert.ThrowsAsync<AuthoritativePlanConflictException>(() => workflow.RegisterFileBackedAsync(secondPath, CancellationToken.None));
        Assert.Equal(AuthoritativePlanConflictCode.IdentityConflict, error.Code);
    }

    [Fact]
    public async Task BindingWorkflowPersistsIncompleteSafeStateAndRejectsUnsafeOrSpoofedDevice()
    {
        await using var database = await WorkflowDatabase.Create(); var (plan, _) = await database.RegisterFixturePlan();
        var identity = (await database.Repository.LoadAsync(CancellationToken.None))!;
        var sourcePath = Directory.CreateDirectory(Path.Combine(database.DirectoryPath, "source")).FullName;
        var workflow = new LocalBindingWorkflow(identity, database.Repository, new LocalPhysicalPathResolver());
        var incomplete = await workflow.SaveAsync(plan, new(plan.Id, [new(plan.Sources[0].Id, sourcePath)], null, null, []), CancellationToken.None);
        Assert.Null(incomplete.CurrentRoot);
        Assert.Null((await database.Repository.LoadAsync(plan.Id, CancellationToken.None))!.CurrentRoot);

        var unsafeRequest = new LocalBindingSaveRequest(plan.Id, [new(plan.Sources[0].Id, sourcePath)], Path.Combine(sourcePath, "output"), null, []);
        await Assert.ThrowsAsync<LocalBindingValidationException>(() => workflow.SaveAsync(plan, unsafeRequest, CancellationToken.None));
        var persisted = await database.Repository.LoadAsync(plan.Id, CancellationToken.None);
        Assert.Null(persisted!.CurrentRoot);

        var spoofed = incomplete with { DeviceId = new(Guid.NewGuid()) };
        await Assert.ThrowsAsync<LocalStateCorruptionException>(() => database.Repository.SaveValidatedAggregateAsync(spoofed, CancellationToken.None));
    }

    [Fact]
    public async Task BindingWorkflowRejectsWritableCollisionWithAnotherActivePlan()
    {
        await using var database = await WorkflowDatabase.Create(registerDefaultPlan: false); var identity = (await database.Repository.LoadAsync(CancellationToken.None))!;
        var planA = await ReadFixturePlan(); var planB = CloneWithNewIdentities(planA);
        var source = new BackupPlanDocumentSource(); var authority = new AuthoritativePlanWorkflow(database.Repository, source);
        await authority.CreateManagedAsync(planA, CancellationToken.None); await authority.CreateManagedAsync(planB, CancellationToken.None);
        var shared = Directory.CreateDirectory(Path.Combine(database.DirectoryPath, "shared-output")).FullName;
        var sourceA = Directory.CreateDirectory(Path.Combine(database.DirectoryPath, "source-a")).FullName;
        var sourceB = Directory.CreateDirectory(Path.Combine(database.DirectoryPath, "source-b")).FullName;
        var workflow = new LocalBindingWorkflow(identity, database.Repository, new LocalPhysicalPathResolver());
        await workflow.SaveAsync(planA, new(planA.Id, [new(planA.Sources[0].Id, sourceA)], shared, null, []), CancellationToken.None);
        var error = await Assert.ThrowsAsync<LocalBindingValidationException>(() => workflow.SaveAsync(planB, new(planB.Id, [new(planB.Sources[0].Id, sourceB)], Path.Combine(shared, "child"), null, []), CancellationToken.None));
        Assert.Contains(error.Issues, issue => issue.Code == PlanResolutionIssueCode.ActivePlanRootConflict);
    }

    private static PendingPublishIntent Intent(PlanId planId, ArchiveUnitId unitId, ArchiveVersionId versionId, Sha256Digest integrity)
    {
        var f = Fingerprints(); var archive = ArchiveVersion.Prepare(versionId, planId, unitId, PortableArchiveFormat.SevenZip, f.ArchiveSpec).Verify(integrity, 10);
        return PendingPublishIntent.Prepare(archive, new("unit.7z"), BaselineCandidate.FromCompleteCandidate(f), f.OutputLayout, null, HistoryCaptureRequirement.NotRequired);
    }
    private static CandidateArchiveFingerprints Fingerprints() { var hash = Sha256Digest.Hash("x"u8); var d = new DiagnosticFingerprint(hash); return new(1, new(1, 1, 1), true, new(hash), new(hash), new(hash), new(hash), new(hash), new(hash), new(d, d, d, d, d, d, d, d)); }
    private static string Key(string path) { var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Replace('\\', '/'); return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? key.ToUpperInvariant() : key; }
    private static async Task<PortableBackupPlan> ReadFixturePlan() { var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "schemas", "fixtures", "backupplan-v1", "valid", "minimal-ui-managed.json")); var read = new BackupPlanDocumentV1Reader().Read(bytes); return BackupPlanDocumentV1Mapper.Map(read.Document!).Plan!; }
    private static PortableBackupPlan CopyWithName(PortableBackupPlan x, string name) => new(x.Id, name, x.Description, x.Semantics, x.Sources, x.GlobalRules, x.PlanRules, x.ArchiveSpecDefault, x.ArchiveUnits, x.SecretSlots, x.LinkPolicy, x.ChangeDetection, x.HistoryDefault, x.Schedule, x.ExternalSources);
    private static PortableBackupPlan CloneWithNewIdentities(PortableBackupPlan x)
    {
        var sourceId = new SourceId(Guid.NewGuid()); var unitId = new ArchiveUnitId(Guid.NewGuid()); var unit = x.ArchiveUnits[0];
        AuthoredArchiveUnit cloned = unit is UiManagedArchiveUnit ui ? new UiManagedArchiveUnit(unitId, sourceId, ui.Path, ui.LocalRules, ui.ArchiveSpecOverride, ui.HistoryOverride) : new FileManagedArchiveUnit(unitId, sourceId, unit.Path, unit.ArchiveSpecOverride, unit.HistoryOverride);
        return new(new(Guid.NewGuid()), x.Name + " clone", x.Description, x.Semantics, [new(sourceId, x.Sources[0].Name, x.Sources[0].SourceOutputPath)], x.GlobalRules, x.PlanRules, x.ArchiveSpecDefault, [cloned], [], x.LinkPolicy, x.ChangeDetection, x.HistoryDefault, x.Schedule, []);
    }

    private sealed class WorkflowDatabase : IAsyncDisposable
    {
        private WorkflowDatabase(string directoryPath, string path, ConfigDbRepository repository) { DirectoryPath = directoryPath; Path = path; Repository = repository; }
        public string DirectoryPath { get; } public string Path { get; } public ConfigDbRepository Repository { get; }
        public static async Task<WorkflowDatabase> Create(bool registerDefaultPlan = true)
        {
            var directory = Directory.CreateTempSubdirectory("stowcrate-workflow-"); var path = System.IO.Path.Combine(directory.FullName, "config.db"); var repository = await ConfigDbOpenCoordinator.OpenAsync(path, Guid.NewGuid(), new DeviceId(Guid.NewGuid())); var value = new WorkflowDatabase(directory.FullName, path, repository); if (registerDefaultPlan) await value.RegisterFixturePlan(); return value;
        }
        public async Task<(PortableBackupPlan Plan, AuthoredArchiveUnit Unit)> RegisterFixturePlan() { var plan = await ReadFixturePlan(); var source = new BackupPlanDocumentSource(); var existing = await ((IPlanRegistrationStore)Repository).LoadAsync(plan.Id, CancellationToken.None); if (existing is null) await new AuthoritativePlanWorkflow(Repository, source).CreateManagedAsync(plan, CancellationToken.None); return (plan, plan.ArchiveUnits[0]); }
        public ValueTask DisposeAsync() { Directory.Delete(DirectoryPath, recursive: true); return ValueTask.CompletedTask; }
    }
}
