using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Application.LocalState;
using StowCrate.Application.StorageMaintenance;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Infrastructure.Persistence.ConfigDb;

namespace StowCrate.Infrastructure.Tests.Persistence;

public sealed class StorageRelocationJournalTests
{
    private static readonly PlanId Plan = new(Guid.NewGuid());
    private static readonly ArchiveUnitId Unit = new(Guid.NewGuid());
    private static readonly DeviceId Device = new(Guid.NewGuid());
    private static Sha256Digest Hash(string value) => CanonicalFingerprintEncodingV1.Encode("relocation-test", x => x.Utf8(1, value));
    private static StorageObjectIdentity Identity(string value) => new("fixture", 1, value);

    [Fact]
    public async Task ReopenAndCasPreserveManifestAndNeverSwitchRootsOrBaseline()
    {
        await using var fixture = await Fixture.Create();
        var before = (await fixture.Repository.LoadAsync(Plan, Unit, default))!;
        var journal = await fixture.Repository.BeginRelocationAsync(fixture.Manifest(), default);
        var artifact = Assert.Single(journal.Manifest.Entries).Artifact;
        var proof = new StorageTransferProof(journal.Manifest.TransactionId, Plan, artifact.VersionId, artifact.Integrity, artifact.Length, Identity("staged"), true, true);
        var staged = await fixture.Repository.RecordRelocationStagedAsync(journal.Manifest.TransactionId, 1, proof, default);
        Assert.Equal(2, staged.Revision);
        var reopened = await ConfigDbOpenCoordinator.OpenAsync(fixture.Path);
        Assert.Equal(StorageTransferArtifactStage.Staged, Assert.Single((await reopened.LoadRelocationAsync(Plan, default))!.Progress.Artifacts).Stage);
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => reopened.RecordRelocationTargetAsync(journal.Manifest.TransactionId, 1, proof, default));
        var target = await reopened.RecordRelocationTargetAsync(journal.Manifest.TransactionId, 2, proof, default);
        var sealedTargets = await reopened.SealRelocationTargetsAsync(journal.Manifest.TransactionId, target.Revision, default);
        Assert.Equal(StorageTransferStage.TargetsDurable, sealedTargets.Progress.Stage);
        Assert.False(sealedTargets.Progress.IsMetadataCommitted);
        Assert.Equal(journal.Manifest.TransactionId, sealedTargets.Manifest.TransactionId);
        Assert.Equal(journal.Manifest.Entries.ToArray(), sealedTargets.Manifest.Entries.ToArray());
        Assert.Equal("/old", (await reopened.LoadAsync(Plan, default))!.CurrentRoot!.CanonicalPath);
        var after = (await reopened.LoadAsync(Plan, Unit, default))!;
        Assert.Equal(before.Current, after.Current);
        Assert.Equal(before.Baseline!.EntrySet, after.Baseline!.EntrySet);
        Assert.Equal(before.Baseline.ArchiveVersionId, after.Baseline.ArchiveVersionId);
    }

    [Theory]
    [InlineData("device")]
    [InlineData("missing-entry")]
    [InlineData("wrong-path")]
    [InlineData("wrong-integrity")]
    [InlineData("wrong-old-root")]
    [InlineData("publish")]
    [InlineData("maintenance")]
    public async Task InvalidOrStaleBeginRollsBackWholeJournal(string scenario)
    {
        await using var fixture = await Fixture.Create();
        var initial = fixture.Manifest();
        var entry = initial.Entries[0];
        var roots = initial.Roots;
        if (scenario == "wrong-path") entry = entry with { RelativePath = new("other.7z"), TempRelativePath = StorageRelocationTempLayout.Create(initial.TransactionId, entry.Artifact.VersionId, new("other.7z")) };
        if (scenario == "wrong-integrity") entry = entry with { Artifact = entry.Artifact with { Integrity = Hash("wrong") } };
        if (scenario == "wrong-old-root") roots = [roots[0] with { OldRoot = new("/wrong", "/wrong") }];
        var manifest = new StorageRelocationManifest(initial.TransactionId, Plan, scenario == "device" ? new(Guid.NewGuid()) : Device,
            initial.ExecutionSemanticDigest, roots, scenario == "missing-entry" ? [] : [entry]);
        if (scenario == "publish") await fixture.Repository.BeginPublishAsync(Intent(new(Guid.NewGuid())), default);
        if (scenario == "maintenance") await fixture.Repository.SaveAsync(new MaintenanceState(Plan, null, MaintenanceKind.OldCurrentPathCleanup, MaintenanceStatus.Pending, null, DateTimeOffset.UnixEpoch), default);
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => fixture.Repository.BeginRelocationAsync(manifest, default));
        Assert.Null(await fixture.Repository.LoadRelocationAsync(Plan, default));
        await using var db = new ConfigDbContextFactory(fixture.Path).Create();
        Assert.Empty(await db.StorageRelocationRootReservations.ToListAsync());
    }

    [Fact]
    public async Task PendingJournalBlocksConflictingPlanMutations()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Repository.BeginRelocationAsync(fixture.Manifest(), default);
        var repository = fixture.Repository;
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => repository.BeginRelocationAsync(fixture.Manifest(), default));
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => repository.SetActiveAsync(Plan, false, default));
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => repository.SaveFileBackedAsync(new(Plan, PlanAuthority.FileBacked, "/other.backupplan", true), default));
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => repository.SaveValidatedAggregateAsync(new(Plan, Device, [], new("/old", "/old", true), null, []), default));
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => repository.BeginPublishAsync(Intent(new(Guid.NewGuid())), default));
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => repository.ReplaceActiveRegistrationsAsync(Plan, [], default));
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => repository.BindAsync(Plan, new(Guid.NewGuid()), "fixture", "opaque", default));
        var state = (await repository.LoadAsync(Plan, Unit, default))!;
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => repository.CommitOutputReorganizationAsync(
            OutputReorganization.Commit(state.Current!, state.OutputLayout!, new("moved.7z"), new(Hash("layout2"))), default));
    }

    [Fact]
    public async Task ReservedRootsBlockCrossPlanBindingsActivationAndCompetingRelocation()
    {
        await using var fixture = await Fixture.Create();
        var other = new PlanId(Guid.NewGuid());
        await fixture.Repository.SaveFileBackedAsync(new(other, PlanAuthority.FileBacked, "/other.backupplan", false), default);
        var dormant = new DevicePlanLocalBindings(other, Device, [], new("/new/child", "/new/child", true), null, []);
        await fixture.Repository.SaveValidatedAggregateAsync(dormant, default);
        await fixture.Repository.BeginRelocationAsync(fixture.Manifest(), default);
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => fixture.Repository.SetActiveAsync(other, true, default));
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => fixture.Repository.SaveFileBackedAsync(new(other, PlanAuthority.FileBacked, "/other.backupplan", true), default));
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => fixture.Repository.SaveValidatedAggregateAsync(dormant, default));
        await fixture.Repository.SaveValidatedAggregateAsync(dormant with { CurrentRoot = new("/unrelated", "/unrelated", true) }, default);
        await fixture.Repository.SetActiveAsync(other, true, default);
        var competing = new StorageRelocationManifest(Guid.NewGuid(), other, Device, Hash("execution"),
            [new(StorageRootKind.Current, new("/unrelated", "/unrelated"), new("/new/child", "/new/child"), Identity("other-old"), Identity("other-new"))], []);
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => fixture.Repository.BeginRelocationAsync(competing, default));
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("reservation")]
    [InlineData("stage")]
    [InlineData("duplicate-field")]
    [InlineData("unknown-field")]
    [InlineData("missing-field")]
    [InlineData("future-protocol")]
    public async Task CorruptJournalIsNeverTreatedAsMissingOrAdopted(string corruption)
    {
        await using var fixture = await Fixture.Create();
        var journal = await fixture.Repository.BeginRelocationAsync(fixture.Manifest(), default);
        await using (var db = new ConfigDbContextFactory(fixture.Path).Create())
        {
            var row = await db.StorageRelocationIntents.SingleAsync();
            if (corruption == "reservation") db.StorageRelocationRootReservations.Remove(await db.StorageRelocationRootReservations.FirstAsync());
            else if (corruption == "stage") row.Stage = "TARGETS_DURABLE";
            else if (corruption == "digest") row.ManifestSha256 = new byte[32];
            else
            {
                var replacement = corruption switch
                {
                    "unknown-field" => "\"Version\":1,\"Future\":true",
                    "missing-field" => "",
                    "future-protocol" => "\"Version\":99",
                    _ => "\"Version\":1,\"Version\":1",
                };
                var original = System.Text.Encoding.UTF8.GetString(row.ManifestPayload);
                var text = corruption == "missing-field" ? original.Replace("\"Version\":1,", "", StringComparison.Ordinal)
                    : original.Replace("\"Version\":1", replacement, StringComparison.Ordinal);
                row.ManifestPayload = System.Text.Encoding.UTF8.GetBytes(text);
                row.ManifestSha256 = System.Security.Cryptography.SHA256.HashData(row.ManifestPayload);
            }
            await db.SaveChangesAsync();
        }
        var reopened = await ConfigDbOpenCoordinator.OpenAsync(fixture.Path);
        await Assert.ThrowsAsync<LocalStateCorruptionException>(() => reopened.LoadRelocationAsync(Plan, default));
        await Assert.ThrowsAsync<LocalStateCorruptionException>(() => reopened.SealRelocationTargetsAsync(journal.Manifest.TransactionId, 1, default));
    }

    [Fact]
    public async Task VersionThreeDatabaseMigratesToFourWithoutRewritingEarlierMigrations()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"stowcrate-relocation-v3-{Guid.NewGuid():N}.db");
        try
        {
            await using (var db = new ConfigDbContextFactory(path).Create())
            {
                await db.GetService<IMigrator>().MigrateAsync("20260902102622_AddRetentionDeletionIntentV3");
                await db.Database.ExecuteSqlRawAsync("INSERT INTO DatabaseMetadata VALUES(1,3,randomblob(16),randomblob(16),0)");
            }
            var repository = await ConfigDbOpenCoordinator.OpenAsync(path);
            Assert.Equal(4, (await repository.LoadAsync(default))!.SchemaVersion);
            await using var current = new ConfigDbContextFactory(path).Create();
            Assert.Empty(await current.StorageRelocationIntents.ToListAsync());
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
    }

    [Fact]
    public async Task FaultsRollbackIntentReservationsAndProgressTogether()
    {
        await using var fixture = await Fixture.Create();
        var manifest = fixture.Manifest();
        var failing = new ConfigDbRepository(new(fixture.Path), new FailAt(MetadataCommitFaultPoint.AfterRelocationIntent));
        await Assert.ThrowsAsync<InjectedFailure>(() => failing.BeginRelocationAsync(manifest, default));
        Assert.Null(await fixture.Repository.LoadRelocationAsync(Plan, default));
        await using (var db = new ConfigDbContextFactory(fixture.Path).Create()) Assert.Empty(await db.StorageRelocationRootReservations.ToListAsync());
        var journal = await fixture.Repository.BeginRelocationAsync(manifest, default);
        var artifact = journal.Manifest.Entries[0].Artifact;
        var proof = new StorageTransferProof(manifest.TransactionId, Plan, artifact.VersionId, artifact.Integrity, artifact.Length, Identity("staged"), true, true);
        failing = new(new(fixture.Path), new FailAt(MetadataCommitFaultPoint.AfterRelocationProgress));
        await Assert.ThrowsAsync<InjectedFailure>(() => failing.RecordRelocationStagedAsync(manifest.TransactionId, 1, proof, default));
        var reopened = (await fixture.Repository.LoadRelocationAsync(Plan, default))!;
        Assert.Equal(1, reopened.Revision);
        Assert.Equal(StorageTransferArtifactStage.Pending, reopened.Progress.Artifacts[0].Stage);
    }

    [Fact]
    public async Task RetentionIntentsBlockRelocationEvenAfterPlacementRemoval()
    {
        await using var fixture = await Fixture.Create();
        var version = new ArchiveVersionId(Guid.NewGuid());
        await using (var db = new ConfigDbContextFactory(fixture.Path).Create())
        {
            var current = await db.ArchiveVersions.SingleAsync();
            db.ArchiveVersions.Add(new() { ArchiveVersionId = DurableCodecs.Uuid(version.Value), PlanId = current.PlanId, ArchiveUnitId = current.ArchiveUnitId,
                ArchiveFormat = current.ArchiveFormat, ArchiveSpecFingerprint = current.ArchiveSpecFingerprint, Lifecycle = "SUPERSEDED", IntegritySha256 = current.IntegritySha256, Length = current.Length, PublishedAtUtcMs = current.PublishedAtUtcMs });
            db.HistoryVersionPlacements.Add(new() { ArchiveVersionId = DurableCodecs.Uuid(version.Value), PlanId = current.PlanId, ArchiveUnitId = current.ArchiveUnitId, HistoryRelativePath = "history-v1/old.7z" });
            await db.SaveChangesAsync();
        }
        var snapshot = await fixture.Repository.LoadRetentionSnapshotAsync(Plan, Unit, default);
        await fixture.Repository.BeginDeletionIntentsAsync(new(Guid.NewGuid()), Plan, Unit, 1, snapshot.Entries, default);
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => fixture.Repository.BeginRelocationAsync(fixture.Manifest(), default));
        var intent = Assert.Single(await fixture.Repository.ListDeletionIntentsAsync(false, default));
        await fixture.Repository.CompleteDeletionAsync(intent, DateTimeOffset.UtcNow, default);
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => fixture.Repository.BeginRelocationAsync(fixture.Manifest(), default));
    }

    [Fact]
    public async Task BothRootRelocationRequiresEveryTrackedHistoryAndCurrentEntry()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Repository.SaveValidatedAggregateAsync(new(Plan, Device, [], new("/old", "/old", true), new("/history", "/history", true), []), default);
        var historyVersion = new ArchiveVersionId(Guid.NewGuid());
        await using (var db = new ConfigDbContextFactory(fixture.Path).Create())
        {
            var current = await db.ArchiveVersions.SingleAsync();
            db.ArchiveVersions.Add(new() { ArchiveVersionId = DurableCodecs.Uuid(historyVersion.Value), PlanId = current.PlanId, ArchiveUnitId = current.ArchiveUnitId,
                ArchiveFormat = current.ArchiveFormat, ArchiveSpecFingerprint = current.ArchiveSpecFingerprint, Lifecycle = "SUPERSEDED", IntegritySha256 = current.IntegritySha256, Length = current.Length, PublishedAtUtcMs = current.PublishedAtUtcMs });
            db.HistoryVersionPlacements.Add(new() { ArchiveVersionId = DurableCodecs.Uuid(historyVersion.Value), PlanId = current.PlanId, ArchiveUnitId = current.ArchiveUnitId, HistoryRelativePath = "history-v1/old.7z" });
            await db.SaveChangesAsync();
        }
        var initial = fixture.Manifest();
        var roots = initial.Roots.Add(new(StorageRootKind.History, new("/history", "/history"), new("/new-history", "/new-history"), Identity("old-history"), Identity("new-history")));
        var missing = new StorageRelocationManifest(initial.TransactionId, Plan, Device, initial.ExecutionSemanticDigest, roots, initial.Entries);
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => fixture.Repository.BeginRelocationAsync(missing, default));
        var path = new RelativeStoragePath("history-v1/old.7z");
        var entries = initial.Entries.Add(new(Unit, StorageRootKind.History, new(historyVersion, Hash("archive"), 42), path,
            StorageRelocationTempLayout.Create(initial.TransactionId, historyVersion, path), Identity("old-history-file")));
        var manifest = new StorageRelocationManifest(initial.TransactionId, Plan, Device, initial.ExecutionSemanticDigest, roots, entries);
        await fixture.Repository.BeginRelocationAsync(manifest, default);
        var reopened = (await fixture.Repository.LoadRelocationAsync(Plan, default))!;
        Assert.Equal(2, reopened.Manifest.Roots.Length);
        Assert.Equal(2, reopened.Manifest.Entries.Length);
        await using var check = new ConfigDbContextFactory(fixture.Path).Create();
        Assert.Equal(4, await check.StorageRelocationRootReservations.CountAsync());
    }

    private sealed class InjectedFailure : Exception;
    private sealed class FailAt(MetadataCommitFaultPoint point) : IMetadataCommitFaultInjector
    {
        public void ThrowIfRequested(MetadataCommitFaultPoint current) { if (current == point) throw new InjectedFailure(); }
    }

    private static PendingPublishIntent Intent(ArchiveVersionId version)
    {
        var d = new DiagnosticFingerprint(Hash("component"));
        var fingerprints = new CandidateArchiveFingerprints(1, new(1, 1, 1), true, new(Hash("entry")), new(Hash("selection")),
            new(Hash("spec")), new(Hash("layout")), new(Hash("semantic")), new(Hash("binding")), new(d, d, d, d, d, d, d, d));
        return PendingPublishIntent.Prepare(ArchiveVersion.Prepare(version, Plan, Unit, PortableArchiveFormat.SevenZip, fingerprints.ArchiveSpec).Verify(Hash("archive"), 42),
            new("unit.7z"), BaselineCandidate.FromCompleteCandidate(fingerprints), fingerprints.OutputLayout, null, HistoryCaptureRequirement.NotRequired);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Path { get; init; }
        public required ConfigDbRepository Repository { get; init; }
        public required ArchiveVersionId Version { get; init; }
        public static async Task<Fixture> Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"stowcrate-relocation-{Guid.NewGuid():N}.db");
            var repository = await ConfigDbOpenCoordinator.OpenAsync(path, Guid.NewGuid(), Device);
            await repository.SaveFileBackedAsync(new(Plan, PlanAuthority.FileBacked, path + ".backupplan", true), default);
            await repository.SaveValidatedAggregateAsync(new(Plan, Device, [], new("/old", "/old", true), null, []), default);
            var version = new ArchiveVersionId(Guid.NewGuid()); var intent = Intent(version);
            await repository.BeginPublishAsync(intent, default);
            var published = intent.MarkCurrentPublished(DateTimeOffset.UnixEpoch);
            await repository.SavePublishProgressAsync(published, default);
            await repository.CompleteMetadataCommitAsync(published.RebuildMetadataCommitPlan(), default);
            await repository.CleanupCompletedPublishIntentsAsync(default);
            return new() { Path = path, Repository = repository, Version = version };
        }
        public StorageRelocationManifest Manifest()
        {
            var transaction = Guid.NewGuid(); var relative = new RelativeStoragePath("unit.7z");
            return new(transaction, Plan, Device, Hash("execution"),
                [new(StorageRootKind.Current, new("/old", "/old"), new("/new", "/new"), Identity("old-root"), Identity("new-root"))],
                [new(Unit, StorageRootKind.Current, new(Version, Hash("archive"), 42), relative, StorageRelocationTempLayout.Create(transaction, Version, relative), Identity("old-file"))]);
        }
        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(Path + suffix)) File.Delete(Path + suffix);
            return ValueTask.CompletedTask;
        }
    }
}
