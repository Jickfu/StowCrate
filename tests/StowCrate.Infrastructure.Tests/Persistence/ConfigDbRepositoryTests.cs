using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Application.LocalState;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Infrastructure.Persistence.ConfigDb;
using StowCrate.Infrastructure.Configuration.BackupPlans.V1;

namespace StowCrate.Infrastructure.Tests.Persistence;

public sealed class ConfigDbRepositoryTests
{
    private static readonly PlanId PlanId = new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly ArchiveUnitId UnitId = new(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly ArchiveVersionId VersionId = new(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"));
    private static readonly DeviceId DeviceId = new(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"));

    [Fact]
    public async Task InitialMigrationUsesDurablePragmasAndPartialMaintenanceIndexes()
    {
        await using var database = await TestDatabase.Create();
        Assert.Equal(3, (await database.Repository.LoadAsync(TestContext.Current.CancellationToken))!.SchemaVersion);
        Assert.Equal(3, (await (await ConfigDbOpenCoordinator.OpenAsync(database.Path)).LoadAsync(TestContext.Current.CancellationToken))!.SchemaVersion);
        await using var connection = database.Connection(); await connection.OpenAsync();
        Assert.Equal("wal", await Scalar(connection, "PRAGMA journal_mode"));
        Assert.Equal(1L, await Scalar(connection, "PRAGMA foreign_keys"));
        Assert.Equal(2L, await Scalar(connection, "PRAGMA synchronous"));
        var sql = (string)(await Scalar(connection, "SELECT group_concat(sql,' ') FROM sqlite_master WHERE type='index' AND name LIKE 'UX_MaintenanceState_%'"))!;
        Assert.Contains("ArchiveUnitId IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("ArchiveUnitId IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CASCADE", (string)(await Scalar(connection, "SELECT group_concat(sql,' ') FROM sqlite_master WHERE type='table'"))!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplicitV2DatabaseMigratesDirectlyToV3()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stowcrate-v2-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new ConfigDbContextFactory(path);
            await using (var context = factory.Create())
            {
                await context.GetService<IMigrator>().MigrateAsync("20260831085053_InitialConfigDbV1");
                await context.Database.ExecuteSqlRawAsync("INSERT INTO DatabaseMetadata(SingletonKey,SchemaVersion,DatabaseId,DeviceId,CreatedAtUtcMs) VALUES(1,1,randomblob(16),randomblob(16),0)");
                await context.GetService<IMigrator>().MigrateAsync("20260901180347_AddPublishIntentHistoryRequirementV2");
            }
            var repository = await ConfigDbOpenCoordinator.OpenAsync(path);
            Assert.Equal(3, (await repository.LoadAsync(CancellationToken.None))!.SchemaVersion);
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ExistingFutureOrInvalidDatabaseFailsBeforeMigration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stowcrate-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False")) { await connection.OpenAsync(); await Execute(connection, "CREATE TABLE DatabaseMetadata(SingletonKey INTEGER,SchemaVersion INTEGER); INSERT INTO DatabaseMetadata VALUES(1,99)"); }
            await Assert.ThrowsAsync<UnsupportedConfigDatabaseVersionException>(() => ConfigDbOpenCoordinator.OpenAsync(path));
            await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False")) { await connection.OpenAsync(); Assert.Equal(0L, await Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE name='__EFMigrationsHistory'")); }
            File.Delete(path);
            await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False")) { await connection.OpenAsync(); await Execute(connection, "CREATE TABLE Wrong(Value INTEGER)"); }
            await Assert.ThrowsAsync<LocalStateCorruptionException>(() => ConfigDbOpenCoordinator.OpenAsync(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ExplicitV1DatabaseMigratesThroughV2ToV3WithoutEditingOldMigrations()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stowcrate-v1-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new ConfigDbContextFactory(path);
            await using (var context = factory.Create())
            {
                await context.GetService<IMigrator>().MigrateAsync("20260831085053_InitialConfigDbV1");
                await context.Database.ExecuteSqlRawAsync("INSERT INTO DatabaseMetadata(SingletonKey,SchemaVersion,DatabaseId,DeviceId,CreatedAtUtcMs) VALUES(1,1,randomblob(16),randomblob(16),0)");
            }

            _ = await ConfigDbOpenCoordinator.OpenAsync(path);

            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False"); await connection.OpenAsync();
            Assert.Equal(3L, await Scalar(connection, "SELECT SchemaVersion FROM DatabaseMetadata WHERE SingletonKey=1"));
            Assert.Equal(1L, await Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='RetentionDeletionIntent'"));
            Assert.Equal(1L, await Scalar(connection, "SELECT count(*) FROM pragma_table_info('PublishIntent') WHERE name='HistoryCaptureRequirement'"));
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task CurrentPublishedJournalSurvivesRestartAndCompletesAtomically()
    {
        await using var database = await TestDatabase.Create();
        var repository = database.Repository;
        var intent = Intent().MarkCurrentPublished(DateTimeOffset.UnixEpoch);
        await repository.BeginPublishAsync(Intent(), TestContext.Current.CancellationToken);
        await repository.SavePublishProgressAsync(intent, TestContext.Current.CancellationToken);

        repository = await ConfigDbOpenCoordinator.OpenAsync(database.Path);
        var recovered = await repository.LoadAsync(PlanId, UnitId, TestContext.Current.CancellationToken);
        Assert.Equal(PublishIntentStage.CurrentPublished, recovered!.PublishIntent!.Stage);
        var committed = await repository.CompleteMetadataCommitAsync(recovered.PublishIntent.RebuildMetadataCommitPlan(), TestContext.Current.CancellationToken);

        repository = await ConfigDbOpenCoordinator.OpenAsync(database.Path);
        var durable = await repository.LoadAsync(PlanId, UnitId, TestContext.Current.CancellationToken);
        Assert.Equal(VersionId, durable!.Current!.ArchiveVersionId);
        Assert.Equal(VersionId, durable.Baseline!.ArchiveVersionId);
        Assert.Equal(PublishIntentStage.MetadataCommitted, durable.PublishIntent!.Stage);
        Assert.Equal(VersionId, committed.PublishedArchive.Id);
    }

    [Fact]
    public async Task PublishProgressUsesExpectedPreviousStageCas()
    {
        await using var database = await TestDatabase.Create(); var current = Intent().MarkCurrentPublished(DateTimeOffset.UnixEpoch);
        await database.Repository.BeginPublishAsync(Intent(), TestContext.Current.CancellationToken);
        await database.Repository.SavePublishProgressAsync(current, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => database.Repository.SavePublishProgressAsync(current, TestContext.Current.CancellationToken));
        var recovered = await database.Repository.LoadAsync(PlanId, UnitId, TestContext.Current.CancellationToken);
        Assert.Equal(PublishIntentStage.CurrentPublished, recovered!.PublishIntent!.Stage);
    }

    [Fact]
    public async Task SafeAbortDeletesOnlyExpectedIncompleteStage()
    {
        await using var database = await TestDatabase.Create(); var intent = Intent();
        await database.Repository.BeginPublishAsync(intent, CancellationToken.None);

        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() =>
            database.Repository.AbortIncompletePublishAsync(intent, PublishIntentStage.HistoryCaptured, CancellationToken.None));
        Assert.NotNull((await database.Repository.LoadAsync(PlanId, UnitId, CancellationToken.None))!.PublishIntent);

        await database.Repository.AbortIncompletePublishAsync(intent, PublishIntentStage.Prepared, CancellationToken.None);
        Assert.Null(await database.Repository.LoadAsync(PlanId, UnitId, CancellationToken.None));
    }

    [Theory]
    [InlineData(MetadataCommitFaultPoint.AfterNewArchive)]
    [InlineData(MetadataCommitFaultPoint.AfterHistory)]
    [InlineData(MetadataCommitFaultPoint.AfterCurrent)]
    [InlineData(MetadataCommitFaultPoint.AfterBaseline)]
    [InlineData(MetadataCommitFaultPoint.AfterLayout)]
    [InlineData(MetadataCommitFaultPoint.AfterIntentCompletion)]
    internal async Task MetadataCommitFaultRollsBackEveryStep(MetadataCommitFaultPoint point)
    {
        await using var database = await TestDatabase.Create(); var intent = Intent().MarkCurrentPublished(DateTimeOffset.UnixEpoch);
        await database.Repository.BeginPublishAsync(Intent(), TestContext.Current.CancellationToken); await database.Repository.SavePublishProgressAsync(intent, TestContext.Current.CancellationToken);
        var failing = new ConfigDbRepository(new(database.Path), new ThrowAt(point));
        await Assert.ThrowsAsync<InjectedFaultException>(() => failing.CompleteMetadataCommitAsync(intent.RebuildMetadataCommitPlan(), TestContext.Current.CancellationToken));

        var reopened = await ConfigDbOpenCoordinator.OpenAsync(database.Path); var state = await reopened.LoadAsync(PlanId, UnitId, TestContext.Current.CancellationToken);
        Assert.Null(state!.Current); Assert.Null(state.Baseline); Assert.Null(state.OutputLayout); Assert.Empty(state.History); Assert.Equal(PublishIntentStage.CurrentPublished, state.PublishIntent!.Stage);
    }

    [Fact]
    public async Task OutputReorganizationOnlyChangesCurrentPathAndLayout()
    {
        await using var database = await TestDatabase.Create(); var prepared = Intent(); var published = prepared.MarkCurrentPublished(DateTimeOffset.UnixEpoch);
        await database.Repository.BeginPublishAsync(prepared, TestContext.Current.CancellationToken); await database.Repository.SavePublishProgressAsync(published, TestContext.Current.CancellationToken); await database.Repository.CompleteMetadataCommitAsync(published.RebuildMetadataCommitPlan(), TestContext.Current.CancellationToken);
        var before = (await database.Repository.LoadAsync(PlanId, UnitId, TestContext.Current.CancellationToken))!;
        var beforeCurrent = before.Current!;
        var beforeBaseline = before.Baseline!;
        var moved = OutputReorganization.Commit(beforeCurrent, before.OutputLayout!, new("moved/unit.7z"), new(Hash("new-layout")));
        await database.Repository.CommitOutputReorganizationAsync(moved, TestContext.Current.CancellationToken);
        var after = (await database.Repository.LoadAsync(PlanId, UnitId, TestContext.Current.CancellationToken))!;
        var afterCurrent = after.Current!;
        var afterBaseline = after.Baseline!;
        Assert.Equal("moved/unit.7z", afterCurrent.RelativePath.Value); Assert.Equal(beforeCurrent.ArchiveVersionId, afterCurrent.ArchiveVersionId); Assert.Equal(beforeBaseline.ArchiveVersionId, afterBaseline.ArchiveVersionId); Assert.Equal(beforeBaseline.EntrySet, afterBaseline.EntrySet); Assert.Equal(before.History, after.History);
    }

    [Fact]
    public async Task UnknownTokenAndBaselineMismatchFailClosed()
    {
        await using var database = await TestDatabase.Create(); var prepared = Intent(); var published = prepared.MarkCurrentPublished(DateTimeOffset.UnixEpoch);
        await database.Repository.BeginPublishAsync(prepared, TestContext.Current.CancellationToken); await database.Repository.SavePublishProgressAsync(published, TestContext.Current.CancellationToken); await database.Repository.CompleteMetadataCommitAsync(published.RebuildMetadataCommitPlan(), TestContext.Current.CancellationToken);
        await using var connection = database.Connection(); await connection.OpenAsync(); await Execute(connection, "PRAGMA ignore_check_constraints=ON; UPDATE ArchiveVersion SET ArchiveFormat='UNKNOWN'");
        await Assert.ThrowsAsync<LocalStateCorruptionException>(() => database.Repository.LoadAsync(PlanId, UnitId, TestContext.Current.CancellationToken));
        await Execute(connection, "PRAGMA foreign_keys=OFF; UPDATE ArchiveVersion SET ArchiveFormat='SEVEN_ZIP'; UPDATE CommittedArchiveUnitBaseline SET ArchiveVersionId=randomblob(16)");
        await Assert.ThrowsAsync<LocalStateCorruptionException>(() => database.Repository.LoadAsync(PlanId, UnitId, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("UPDATE ArchiveVersion SET ArchiveVersionId=x'00'")]
    [InlineData("UPDATE ArchiveVersion SET ArchiveSpecFingerprint=x'00'")]
    [InlineData("UPDATE ArchiveVersion SET Lifecycle='VERIFIED'")]
    [InlineData("UPDATE PublishIntent SET Stage='CURRENT_PUBLISHED',CurrentPublishedAtUtcMs=NULL")]
    public async Task InvalidLengthLifecycleAndJournalCombinationsFailClosed(string mutation)
    {
        await using var database = await TestDatabase.Create(); var prepared = Intent(); var published = prepared.MarkCurrentPublished(DateTimeOffset.UnixEpoch);
        await database.Repository.BeginPublishAsync(prepared, TestContext.Current.CancellationToken); await database.Repository.SavePublishProgressAsync(published, TestContext.Current.CancellationToken); await database.Repository.CompleteMetadataCommitAsync(published.RebuildMetadataCommitPlan(), TestContext.Current.CancellationToken);
        await using var connection = database.Connection(); await connection.OpenAsync(); await Execute(connection, $"PRAGMA foreign_keys=OFF; PRAGMA ignore_check_constraints=ON; {mutation}");
        await Assert.ThrowsAsync<LocalStateCorruptionException>(() => database.Repository.LoadAsync(PlanId, UnitId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ManagedCasAuthorityConversionAndUnregisterPreserveRuntimeState()
    {
        await using var database = await TestDatabase.Create();
        var fixture = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "schemas", "fixtures", "backupplan-v1", "valid", "minimal-ui-managed.json"));
        var read = new BackupPlanDocumentV1Reader().Read(fixture);
        var plan = BackupPlanDocumentV1Mapper.Map(read.Document!).Plan!;
        var canonical = new BackupPlanDocumentV1Writer().Write(plan).Bytes!;
        var registration = new PlanRegistration(plan.Id, PlanAuthority.Managed, null, true);
        var first = await database.Repository.SaveManagedAsync(registration, canonical, null, TestContext.Current.CancellationToken);
        Assert.Equal(1, first.Revision);
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => database.Repository.SaveManagedAsync(registration, canonical, 0, TestContext.Current.CancellationToken));

        var unit = plan.ArchiveUnits[0].Id;
        var intent = Intent(plan.Id, unit, new(Guid.NewGuid()));
        await database.Repository.BeginPublishAsync(intent, TestContext.Current.CancellationToken);
        var published = intent.MarkCurrentPublished(DateTimeOffset.UnixEpoch);
        await database.Repository.SavePublishProgressAsync(published, TestContext.Current.CancellationToken);
        await database.Repository.CompleteMetadataCommitAsync(published.RebuildMetadataCommitPlan(), TestContext.Current.CancellationToken);

        await database.Repository.SaveFileBackedAsync(new(plan.Id, PlanAuthority.FileBacked, database.Path + ".backupplan", false), TestContext.Current.CancellationToken);
        Assert.Null((await ((IPlanRegistrationStore)database.Repository).LoadAsync(plan.Id, TestContext.Current.CancellationToken))!.ManagedDocument);
        Assert.NotNull(await database.Repository.LoadAsync(plan.Id, unit, TestContext.Current.CancellationToken));
        await database.Repository.SaveManagedAsync(registration, canonical, null, TestContext.Current.CancellationToken);
        Assert.NotNull(await database.Repository.LoadAsync(plan.Id, unit, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("current", "CURRENT")]
    [InlineData("history", "HISTORY")]
    [InlineData("prepared", "CURRENT")]
    [InlineData("prepared", "HISTORY")]
    [InlineData("publish-completed", "HISTORY")]
    [InlineData("retention-prepared", "HISTORY")]
    [InlineData("retention-completed", "HISTORY")]
    [InlineData("cleanup", "CURRENT")]
    [InlineData("relocation", "HISTORY")]
    [InlineData("reorganization", "CURRENT")]
    public async Task OrdinaryBindingSaveCannotRedirectExistingStorageAuthority(string authority, string rootKind)
    {
        await using var database = await TestDatabase.Create();
        var before = new DevicePlanLocalBindings(PlanId, DeviceId, [], new("/current", "/current", true), new("/history", "/history", true), []);
        await database.Repository.SaveValidatedAggregateAsync(before, CancellationToken.None);
        await SeedStorageAuthority(database, authority);
        // inactive Plan 的 retained recovery state 也需要保护，不能用注销绕过迁移。
        await database.Repository.SetActiveAsync(PlanId, false, CancellationToken.None);
        var source = new SourceLocalBinding(new(Guid.NewGuid()), "/source", "/source", true);
        var replacement = new OutputRootLocalBinding("/new-root", "/new-root", true);
        var proposed = before with { Sources = [source], CurrentRoot = rootKind == "CURRENT" ? replacement : before.CurrentRoot,
            HistoryRoot = rootKind == "HISTORY" ? replacement : before.HistoryRoot };

        var error = await Assert.ThrowsAsync<StorageRelocationRequiredException>(() => database.Repository.SaveValidatedAggregateAsync(proposed, CancellationToken.None));
        Assert.Equal(PlanId, error.PlanId);
        Assert.Equal(rootKind, error.RootKind);
        var reopened = await ConfigDbOpenCoordinator.OpenAsync(database.Path);
        var unchanged = await reopened.LoadAsync(PlanId, CancellationToken.None);
        Assert.Equal(before.CurrentRoot, unchanged!.CurrentRoot);
        Assert.Equal(before.HistoryRoot, unchanged.HistoryRoot);
        Assert.Empty(unchanged.Sources);

        // 同一个 Plan 的其他 binding 修改不应被禁止。
        await reopened.SaveValidatedAggregateAsync(before with { Sources = [source] }, CancellationToken.None);
        Assert.Single((await reopened.LoadAsync(PlanId, CancellationToken.None))!.Sources);
    }

    [Theory]
    [InlineData("omit")]
    [InlineData("deactivate")]
    [InlineData("key")]
    [InlineData("path")]
    public async Task ExistingCurrentRootCannotBeDisabledOrReinterpreted(string change)
    {
        await using var database = await TestDatabase.Create();
        var before = new DevicePlanLocalBindings(PlanId, DeviceId, [], new("/current", "/current", true), null, []);
        await database.Repository.SaveValidatedAggregateAsync(before, CancellationToken.None);
        await SeedStorageAuthority(database, "current");
        var root = before.CurrentRoot!;
        var proposed = before with { CurrentRoot = change switch
        {
            "omit" => null,
            "deactivate" => root with { IsActive = false },
            "key" => root with { ComparisonKey = "/other" },
            _ => root with { CanonicalPath = "/other" },
        } };
        await Assert.ThrowsAsync<StorageRelocationRequiredException>(() => database.Repository.SaveValidatedAggregateAsync(proposed, CancellationToken.None));
        Assert.Equal(root, (await database.Repository.LoadAsync(PlanId, CancellationToken.None))!.CurrentRoot);
    }

    [Fact]
    public async Task EmptyStorageRootsRemainEditableAndUnrelatedPlansDoNotBlock()
    {
        await using var database = await TestDatabase.Create();
        await SeedStorageAuthority(database, "prepared");
        var other = new PlanId(Guid.NewGuid());
        await database.Repository.SaveFileBackedAsync(new(other, PlanAuthority.FileBacked, database.Path + ".other", true), CancellationToken.None);
        var bindings = new DevicePlanLocalBindings(other, DeviceId, [], new("/first", "/first", true), new("/history", "/history", true), []);
        await database.Repository.SaveValidatedAggregateAsync(bindings, CancellationToken.None);
        var changed = bindings with { CurrentRoot = new("/second", "/second", true), HistoryRoot = null };
        await database.Repository.SaveValidatedAggregateAsync(changed, CancellationToken.None);
        var saved = await database.Repository.LoadAsync(other, CancellationToken.None);
        Assert.Equal(changed.CurrentRoot, saved!.CurrentRoot);
        Assert.False(saved.HistoryRoot!.IsActive);
    }

    private static async Task SeedStorageAuthority(TestDatabase database, string authority)
    {
        if (authority is "cleanup" or "relocation" or "reorganization")
        {
            var kind = authority == "cleanup" ? MaintenanceKind.OldCurrentPathCleanup
                : authority == "relocation" ? MaintenanceKind.StorageRelocation : MaintenanceKind.OutputReorganization;
            await database.Repository.SaveAsync(new MaintenanceState(PlanId, null, kind, MaintenanceStatus.OutOfSync, null, DateTimeOffset.UnixEpoch), CancellationToken.None);
            return;
        }
        var prepared = Intent();
        await database.Repository.BeginPublishAsync(prepared, CancellationToken.None);
        if (authority == "prepared") return;
        var published = prepared.MarkCurrentPublished(DateTimeOffset.UnixEpoch);
        await database.Repository.SavePublishProgressAsync(published, CancellationToken.None);
        await database.Repository.CompleteMetadataCommitAsync(published.RebuildMetadataCommitPlan(), CancellationToken.None);
        if (authority == "publish-completed") return;
        await database.Repository.CleanupCompletedPublishIntentsAsync(CancellationToken.None);
        if (authority == "current") return;
        await using var connection = database.Connection();
        await connection.OpenAsync();
        await Execute(connection, "DELETE FROM CommittedArchiveUnitBaseline; DELETE FROM CurrentVersion; UPDATE ArchiveVersion SET Lifecycle='SUPERSEDED'; INSERT INTO HistoryVersionPlacement SELECT ArchiveVersionId,PlanId,ArchiveUnitId,'history-v1/unit.7z' FROM ArchiveVersion");
        if (authority == "history") return;
        var snapshot = await database.Repository.LoadRetentionSnapshotAsync(PlanId, UnitId, CancellationToken.None);
        await database.Repository.BeginDeletionIntentsAsync(new(Guid.NewGuid()), PlanId, UnitId, 1, snapshot.Entries, CancellationToken.None);
        if (authority == "retention-completed")
        {
            var intent = Assert.Single(await database.Repository.ListDeletionIntentsAsync(false, CancellationToken.None));
            await database.Repository.CompleteDeletionAsync(intent, DateTimeOffset.UnixEpoch, CancellationToken.None);
        }
    }

    private static PendingPublishIntent Intent()
        => Intent(PlanId, UnitId, VersionId);

    private static PendingPublishIntent Intent(PlanId planId, ArchiveUnitId unitId, ArchiveVersionId versionId)
    {
        var fingerprints = Fingerprints(); var archive = ArchiveVersion.Prepare(versionId, planId, unitId, PortableArchiveFormat.SevenZip, fingerprints.ArchiveSpec).Verify(Hash("artifact"), 42);
        return PendingPublishIntent.Prepare(archive, new("unit.7z"), BaselineCandidate.FromCompleteCandidate(fingerprints), fingerprints.OutputLayout, null, HistoryCaptureRequirement.NotRequired);
    }
    private static CandidateArchiveFingerprints Fingerprints() { var d = new DiagnosticFingerprint(Hash("component")); return new(1, new(1, 1, 1), true, new(Hash("entry")), new(Hash("selection")), new(Hash("spec")), new(Hash("layout")), new(Hash("semantic")), new(Hash("binding")), new(d, d, d, d, d, d, d, d)); }
    private static Sha256Digest Hash(string value) => CanonicalFingerprintEncodingV1.Encode("test", writer => writer.Utf8(1, value));
    private static async Task<object?> Scalar(SqliteConnection connection, string sql) { await using var command = connection.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync(); }
    private static async Task Execute(SqliteConnection connection, string sql) { await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }

    private sealed class ThrowAt(MetadataCommitFaultPoint point) : IMetadataCommitFaultInjector { public void ThrowIfRequested(MetadataCommitFaultPoint current) { if (current == point) throw new InjectedFaultException(); } }
    private sealed class InjectedFaultException : Exception;
    private static class TestContext { public static TestCancellationContext Current { get; } = new(); }
    private sealed class TestCancellationContext { public CancellationToken CancellationToken { get; } = CancellationToken.None; }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private TestDatabase(string path, ConfigDbRepository repository) { Path = path; Repository = repository; }
        public string Path { get; }
        public ConfigDbRepository Repository { get; }
        public static async Task<TestDatabase> Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"stowcrate-{Guid.NewGuid():N}.db"); var repository = await ConfigDbOpenCoordinator.OpenAsync(path, Guid.NewGuid(), DeviceId);
            await repository.SaveFileBackedAsync(new(PlanId, PlanAuthority.FileBacked, path + ".backupplan", true), TestContext.Current.CancellationToken); return new(path, repository);
        }
        public SqliteConnection Connection() => new($"Data Source={Path};Pooling=False");
        public ValueTask DisposeAsync() { foreach (var suffix in new[] { "", "-wal", "-shm" }) { var file = Path + suffix; if (File.Exists(file)) File.Delete(file); } return ValueTask.CompletedTask; }
    }
}
