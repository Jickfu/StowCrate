using Microsoft.Data.Sqlite;
using StowCrate.Application.LocalState;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Infrastructure.Configuration.BackupPlans.V1;
using StowCrate.Infrastructure.Persistence.ConfigDb;

namespace StowCrate.Infrastructure.Tests.Persistence;

public sealed class SecretAndMaintenanceIntegrationTests
{
    [Fact]
    public void SecretMaterialLeaseZerosOwnedBufferOnDispose()
    {
        var lease = new SecretMaterialLease("sensitive"u8); var observed = lease.Material;
        lease.Dispose();
        Assert.All(observed.ToArray(), value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => lease.Material);
    }

    [Fact]
    public async Task SecretSetReplaceRebindAndUnbindUseCopyOnWriteAndDurableRevision()
    {
        await using var database = await MaintenanceDatabase.Create(); var plan = database.Plan; var slot = plan.SecretSlots[0].Id;
        var materials = new FakeSecretMaterialStore(); var workflow = new SecretBindingWorkflow(database.Repository, materials);
        using var first = new SecretMaterialLease("first"u8); var set = await workflow.SetAsync(plan, slot, "FAKE", first, CancellationToken.None);
        Assert.Equal(1, set.Metadata.Revision.Value); Assert.True(materials.Exists(set.Metadata.OpaqueReference));

        using var second = new SecretMaterialLease("second"u8); var replaced = await workflow.ReplaceAsync(plan, slot, set.Metadata.Revision, second, CancellationToken.None);
        Assert.Equal(2, replaced.Metadata.Revision.Value); Assert.False(materials.Exists(set.Metadata.OpaqueReference)); Assert.True(materials.Exists(replaced.Metadata.OpaqueReference));

        using var third = new SecretMaterialLease("third"u8); var rebound = await workflow.RebindAsync(plan, slot, replaced.Metadata.Revision, "FAKE_2", third, CancellationToken.None);
        Assert.Equal(3, rebound.Metadata.Revision.Value); Assert.Equal("FAKE_2", rebound.Metadata.ProviderToken);
        Assert.Equal(SecretMaterialAvailability.Available, await workflow.ProbeAsync(plan.Id, slot, CancellationToken.None));
        using var opened = await workflow.OpenForHeadlessExecutionAsync(plan.Id, slot, rebound.Metadata.Revision, CancellationToken.None);
        Assert.NotNull(opened);

        materials.FailDelete = true;
        var unbound = await workflow.UnbindAsync(plan.Id, slot, rebound.Metadata.Revision, CancellationToken.None);
        Assert.False(unbound.Metadata.IsActive); Assert.True(unbound.OrphanCleanupRequired);
        Assert.Equal(SecretMaterialAvailability.Unavailable, await workflow.ProbeAsync(plan.Id, slot, CancellationToken.None));
    }

    [Fact]
    public async Task PathBindingSaveCannotOverwriteConcurrentSecretMetadata()
    {
        await using var database = await MaintenanceDatabase.Create(); var plan = database.Plan; var slot = plan.SecretSlots[0].Id;
        var materials = new FakeSecretMaterialStore(); var secrets = new SecretBindingWorkflow(database.Repository, materials);
        using var material = new SecretMaterialLease("bound"u8); var bound = await secrets.SetAsync(plan, slot, "FAKE", material, CancellationToken.None);

        var identity = (await database.Repository.LoadAsync(CancellationToken.None))!;
        await database.Repository.SaveValidatedAggregateAsync(new(plan.Id, identity.DeviceId, [], null, null, []), CancellationToken.None);

        var after = Assert.Single(await ((ISecretBindingMetadataStore)database.Repository).LoadAsync(plan.Id, CancellationToken.None));
        Assert.Equal(bound.Metadata, after);
        var combined = await new DevicePlanLocalFactsLoader(identity, database.Repository, database.Repository).LoadAsync(plan.Id, CancellationToken.None);
        Assert.Equal(bound.Metadata.Revision, Assert.Single(combined.Secrets).Revision);
    }

    [Fact]
    public async Task SecretCasFailureDeletesNewLocatorAndKeepsPreviouslyCommittedBinding()
    {
        await using var database = await MaintenanceDatabase.Create(); var plan = database.Plan; var slot = plan.SecretSlots[0].Id;
        var materials = new FakeSecretMaterialStore(); var workflow = new SecretBindingWorkflow(database.Repository, materials);
        using var first = new SecretMaterialLease("first"u8); var revision1 = await workflow.SetAsync(plan, slot, "FAKE", first, CancellationToken.None);
        using var concurrentValue = new SecretMaterialLease("concurrent"u8); var concurrentLocator = await materials.CreateAsync("FAKE", concurrentValue, CancellationToken.None);
        var concurrent = await database.Repository.ReplaceAsync(plan.Id, slot, revision1.Metadata.Revision, "FAKE", concurrentLocator.OpaqueReference, CancellationToken.None);
        var beforeCount = materials.Count;

        using var stale = new SecretMaterialLease("stale"u8);
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => workflow.ReplaceAsync(plan, slot, revision1.Metadata.Revision, stale, CancellationToken.None));

        var active = Assert.Single(await ((ISecretBindingMetadataStore)database.Repository).LoadAsync(plan.Id, CancellationToken.None));
        Assert.Equal(concurrent.OpaqueReference, active.OpaqueReference); Assert.Equal(2, active.Revision.Value);
        Assert.True(materials.Exists(active.OpaqueReference)); Assert.Equal(beforeCount, materials.Count);
    }

    [Theory]
    [InlineData(SecretFaultPoint.AfterCreate)]
    [InlineData(SecretFaultPoint.AfterCommit)]
    public async Task ProcessInterruptionNeverLeavesActiveMetadataPointingAtUncreatedMaterial(SecretFaultPoint point)
    {
        await using var database = await MaintenanceDatabase.Create(); var plan = database.Plan; var slot = plan.SecretSlots[0].Id;
        var materials = new FakeSecretMaterialStore(); var normal = new SecretBindingWorkflow(database.Repository, materials);
        using var first = new SecretMaterialLease("old"u8); var old = await normal.SetAsync(plan, slot, "FAKE", first, CancellationToken.None);
        using var next = new SecretMaterialLease("new"u8); var interrupted = new SecretBindingWorkflow(database.Repository, materials, new ThrowSecretFault(point));
        await Assert.ThrowsAsync<InjectedSecretFaultException>(() => interrupted.ReplaceAsync(plan, slot, old.Metadata.Revision, next, CancellationToken.None));

        var active = Assert.Single(await ((ISecretBindingMetadataStore)database.Repository).LoadAsync(plan.Id, CancellationToken.None));
        Assert.True(active.IsActive); Assert.True(materials.Exists(active.OpaqueReference));
        Assert.Equal(point is SecretFaultPoint.AfterCreate ? 1 : 2, active.Revision.Value);
    }

    [Fact]
    public async Task SnapshotUsesConsistentBackupAndExplicitRecoveryPreservesCorruptDatabase()
    {
        await using var database = await MaintenanceDatabase.Create(); var service = new ConfigDatabaseMaintenanceService();
        var snapshot = Path.Combine(database.DirectoryPath, "config.snapshot.db");
        var created = await service.CreateSnapshotAsync(database.Path, snapshot, CancellationToken.None);
        Assert.True(created.Diagnostic.IntegrityOk); Assert.True(File.Exists(snapshot));
        var originalIdentity = (await database.Repository.LoadAsync(CancellationToken.None))!;

        await File.WriteAllBytesAsync(database.Path, "not-a-sqlite-database"u8.ToArray());
        await Assert.ThrowsAsync<LocalStateCorruptionException>(() => ConfigDbOpenCoordinator.OpenAsync(database.Path));
        var workflow = new ConfigDatabaseRecoveryWorkflow(service, new ConfigDatabaseSessionOpener());
        var startup = await workflow.OpenOrReportRecoveryAsync(new(database.Path), snapshot, CancellationToken.None);
        Assert.Equal(ConfigDatabaseOpenRecoveryStatus.RecoveryCandidateAvailable, startup.Status); Assert.NotNull(startup.Candidate);
        Assert.True(File.Exists(database.Path)); // 发现候选不会静默覆盖损坏数据库。
        var restored = await workflow.RestoreExplicitAsync(database.Path, snapshot, CancellationToken.None);

        Assert.True(File.Exists(restored.PreservedCorruptDatabasePath));
        Assert.Equal("not-a-sqlite-database", await File.ReadAllTextAsync(restored.PreservedCorruptDatabasePath));
        Assert.Equal(originalIdentity.DatabaseId, restored.Session.Identity.DatabaseId);
        Assert.NotNull(await restored.Session.Plans.LoadAsync(database.Plan.Id, CancellationToken.None));
    }

    [Fact]
    public async Task MaintenanceSnapshotsThenSafelyCleansOnlyCompletedPublishIntent()
    {
        await using var database = await MaintenanceDatabase.Create(); var unit = database.Plan.ArchiveUnits[0];
        var intent = Intent(database.Plan.Id, unit.Id); await database.Repository.BeginPublishAsync(intent, CancellationToken.None);
        var published = intent.MarkCurrentPublished(DateTimeOffset.UnixEpoch); await database.Repository.SavePublishProgressAsync(published, CancellationToken.None);
        await database.Repository.CompleteMetadataCommitAsync(published.RebuildMetadataCommitPlan(), CancellationToken.None);
        var incomplete = Intent(database.Plan.Id, new(Guid.NewGuid())); await database.Repository.BeginPublishAsync(incomplete, CancellationToken.None);
        var service = new ConfigDatabaseMaintenanceService(); var workflow = new ConfigDatabaseMaintenanceWorkflow(service, database.Repository);

        var result = await workflow.RunDurabilityMaintenanceAsync(database.Path, Path.Combine(database.DirectoryPath, "config.snapshot.db"), CancellationToken.None);

        Assert.Equal(1, result.CompletedPublishIntentsRemoved);
        Assert.Equal(incomplete.ArchiveUnitId, Assert.Single(await database.Repository.ListIncompletePublishIntentsAsync(CancellationToken.None)).ArchiveUnitId);
        var state = await database.Repository.LoadAsync(database.Plan.Id, unit.Id, CancellationToken.None);
        Assert.Null(state!.PublishIntent); Assert.NotNull(state.Current); Assert.NotNull(state.Baseline);
        Assert.True((await service.ValidateAsync(result.Snapshot.SnapshotPath, CancellationToken.None)).IntegrityOk);
    }

    [Fact]
    public async Task FutureVersionSnapshotIsNotOfferedAsRecoveryCandidate()
    {
        await using var database = await MaintenanceDatabase.Create(); var service = new ConfigDatabaseMaintenanceService();
        var snapshot = Path.Combine(database.DirectoryPath, "future.snapshot.db"); await service.CreateSnapshotAsync(database.Path, snapshot, CancellationToken.None);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshot }.ConnectionString))
        { await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = "UPDATE DatabaseMetadata SET SchemaVersion=4"; await command.ExecuteNonQueryAsync(); }

        var candidate = await new ConfigDatabaseRecoveryWorkflow(service, new ConfigDatabaseSessionOpener()).DiscoverValidatedCandidateAsync(snapshot, CancellationToken.None);
        Assert.Null(candidate);
    }

    private static PendingPublishIntent Intent(PlanId planId, ArchiveUnitId unitId)
    {
        var hash = Sha256Digest.Hash("maintenance"u8); var d = new DiagnosticFingerprint(hash);
        var fingerprints = new CandidateArchiveFingerprints(1, new(1, 1, 1), true, new(hash), new(hash), new(hash), new(hash), new(hash), new(hash), new(d, d, d, d, d, d, d, d));
        var archive = ArchiveVersion.Prepare(new(Guid.NewGuid()), planId, unitId, PortableArchiveFormat.SevenZip, fingerprints.ArchiveSpec).Verify(hash, 11);
        return PendingPublishIntent.Prepare(archive, new("unit.7z"), BaselineCandidate.FromCompleteCandidate(fingerprints), fingerprints.OutputLayout, null, HistoryCaptureRequirement.NotRequired);
    }

    public enum SecretFaultPoint { AfterCreate, AfterCommit }
    private sealed class ThrowSecretFault(SecretFaultPoint point) : ISecretBindingFaultInjector
    {
        public void AfterMaterialCreated() { if (point is SecretFaultPoint.AfterCreate) throw new InjectedSecretFaultException(); }
        public void AfterMetadataCommitted() { if (point is SecretFaultPoint.AfterCommit) throw new InjectedSecretFaultException(); }
    }
    private sealed class InjectedSecretFaultException : Exception;

    private sealed class FakeSecretMaterialStore : ISecretMaterialStore
    {
        private readonly Dictionary<string, byte[]> items = [];
        public bool FailDelete { get; set; }
        public int Count => items.Count;
        public bool Exists(string reference) => items.ContainsKey(reference);
        public Task<SecretMaterialLocator> CreateAsync(string providerToken, SecretMaterialLease material, CancellationToken cancellationToken)
        {
            var reference = Guid.NewGuid().ToString("N"); items.Add(reference, material.Material.ToArray()); return Task.FromResult(new SecretMaterialLocator(providerToken, reference));
        }
        public Task<SecretMaterialLease?> OpenAsync(SecretMaterialLocator locator, CancellationToken cancellationToken)
            => Task.FromResult(items.TryGetValue(locator.OpaqueReference, out var value) ? new SecretMaterialLease(value) : null);
        public Task<SecretMaterialAvailability> ProbeAsync(SecretMaterialLocator locator, CancellationToken cancellationToken)
            => Task.FromResult(items.ContainsKey(locator.OpaqueReference) ? SecretMaterialAvailability.Available : SecretMaterialAvailability.Unavailable);
        public Task DeleteAsync(SecretMaterialLocator locator, CancellationToken cancellationToken)
        {
            if (FailDelete) throw new InvalidOperationException("Injected delete failure.");
            if (items.Remove(locator.OpaqueReference, out var value)) System.Security.Cryptography.CryptographicOperations.ZeroMemory(value);
            return Task.CompletedTask;
        }
    }

    private sealed class MaintenanceDatabase : IAsyncDisposable
    {
        private MaintenanceDatabase(string directoryPath, string path, ConfigDbRepository repository, PortableBackupPlan plan)
        { DirectoryPath = directoryPath; Path = path; Repository = repository; Plan = plan; }
        public string DirectoryPath { get; }
        public string Path { get; }
        public ConfigDbRepository Repository { get; }
        public PortableBackupPlan Plan { get; }
        public static async Task<MaintenanceDatabase> Create()
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stowcrate-m312-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "config.db"); var repository = await ConfigDbOpenCoordinator.OpenAsync(path, Guid.NewGuid(), new(Guid.NewGuid()));
            var fixture = await File.ReadAllBytesAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "schemas", "fixtures", "backupplan-v1", "valid", "secure-schedule-external.json"));
            var read = new BackupPlanDocumentV1Reader().Read(fixture); var plan = BackupPlanDocumentV1Mapper.Map(read.Document!).Plan!;
            var canonical = new BackupPlanDocumentV1Writer().Write(plan).Bytes!;
            await repository.SaveManagedAsync(new(plan.Id, PlanAuthority.Managed, null, true), canonical, null, CancellationToken.None);
            return new(directory, path, repository, plan);
        }
        public ValueTask DisposeAsync() { try { Directory.Delete(DirectoryPath, true); } catch { } return ValueTask.CompletedTask; }
    }
}
