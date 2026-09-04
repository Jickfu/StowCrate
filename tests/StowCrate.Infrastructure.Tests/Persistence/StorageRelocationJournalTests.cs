using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Application.LocalState;
using StowCrate.Application.StorageMaintenance;
using StowCrate.Application.BackupPlans.Documents;
using StowCrate.Infrastructure.Configuration.BackupPlans;
using StowCrate.Core.Rules;
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
            Assert.Equal(6, (await repository.LoadAsync(default))!.SchemaVersion);
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
        var inventory = await fixture.Repository.ReadRelocationInventoryAsync(new(Plan, new("/new", "/new"), new("/new-history", "/new-history")),
            await fixture.Configuration(), default);
        Assert.Equal(2, inventory.Entries.Length);
        Assert.Contains(inventory.Entries, x => x.Artifact.VersionId == historyVersion && x.RootKind == StorageRootKind.History);
        var missing = new StorageRelocationManifest(initial.TransactionId, Plan, Device, initial.ExecutionSemanticDigest, roots, initial.Entries);
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => fixture.Repository.BeginRelocationAsync(missing, default));
        var path = new RelativeStoragePath("history-v1/old.7z");
        var entries = initial.Entries.Add(new(Unit, StorageRootKind.History, new(historyVersion, Hash("archive"), 42), path,
            StorageRelocationTempLayout.Create(initial.TransactionId, historyVersion, path), Identity("old-history-file")));
        var manifest = new StorageRelocationManifest(initial.TransactionId, Plan, Device, initial.ExecutionSemanticDigest, roots, entries);
        await fixture.Repository.BeginRelocationAsync(manifest, await fixture.Configuration(), default);
        var reopened = (await fixture.Repository.LoadRelocationAsync(Plan, default))!;
        Assert.Equal(2, reopened.Manifest.Roots.Length);
        Assert.Equal(2, reopened.Manifest.Entries.Length);
        await using var check = new ConfigDbContextFactory(fixture.Path).Create();
        Assert.Equal(4, await check.StorageRelocationRootReservations.CountAsync());
        var journal = reopened;
        foreach (var entry in manifest.Entries)
        {
            var proof = new StorageTransferProof(manifest.TransactionId, Plan, entry.Artifact.VersionId, entry.Artifact.Integrity,
                entry.Artifact.Length, Identity(entry.Artifact.VersionId.Value.ToString()), true, true);
            journal = await fixture.Repository.RecordRelocationStagedAsync(manifest.TransactionId, journal.Revision, proof, default);
            journal = await fixture.Repository.RecordRelocationTargetAsync(manifest.TransactionId, journal.Revision, proof, default);
        }
        journal = await fixture.Repository.SealRelocationTargetsAsync(manifest.TransactionId, journal.Revision, default);
        journal = await fixture.Repository.CommitRelocationAsync(manifest.TransactionId, journal.Revision, new CommitProbe(), default);
        var absence = new AbsenceProbe();
        journal = await fixture.Repository.CleanupRelocationEntryAsync(manifest.TransactionId, journal.Revision, fixture.Version, absence, default);
        var partial = (await fixture.Repository.LoadRelocationAsync(Plan, default))!;
        Assert.Single(partial.Progress.Artifacts.Where(x => x.Stage == StorageTransferArtifactStage.OldCopyAbsent));
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => fixture.Repository.CompleteRelocationAsync(manifest.TransactionId, partial.Revision, absence, default));
        journal = await fixture.Repository.CleanupRelocationEntryAsync(manifest.TransactionId, journal.Revision, historyVersion, absence, default);
        journal = await fixture.Repository.CompleteRelocationAsync(manifest.TransactionId, journal.Revision, absence, default);
        Assert.Equal(StorageTransferStage.Completed, journal.Progress.Stage);
        Assert.Equal(4, await check.StorageRelocationRootReservations.CountAsync());
        var switched = (await fixture.Repository.LoadAsync(Plan, default))!;
        Assert.Equal("/new", switched.CurrentRoot!.CanonicalPath);
        Assert.Equal("/new-history", switched.HistoryRoot!.CanonicalPath);
        Assert.Equal(4, await check.StorageRelocationRootReservations.CountAsync());
    }

    [Fact]
    public async Task CommitAtomicallySwitchesRootAndJournalWithoutChangingArchiveFacts()
    {
        await using var fixture = await Fixture.Create();
        var before = (await fixture.Repository.LoadAsync(Plan, Unit, default))!;
        var journal = await Seal(fixture, false);
        var physical = new CommitProbe(async () => { await fixture.Configuration(name: "renamed"); });
        var committed = await fixture.Repository.CommitRelocationAsync(journal.Manifest.TransactionId, journal.Revision, physical, default);
        Assert.True(physical.Called);
        Assert.Equal(StorageTransferStage.MetadataCommitted, committed.Progress.Stage);
        var reopened = await ConfigDbOpenCoordinator.OpenAsync(fixture.Path);
        Assert.Equal("/new", (await reopened.LoadAsync(Plan, default))!.CurrentRoot!.CanonicalPath);
        Assert.Equal(StorageTransferStage.MetadataCommitted, (await reopened.LoadRelocationAsync(Plan, default))!.Progress.Stage);
        var after = (await reopened.LoadAsync(Plan, Unit, default))!;
        Assert.Equal(before.Current, after.Current);
        Assert.Equivalent(before.CurrentArchive, after.CurrentArchive);
        Assert.Equal(before.Baseline!.ArchiveVersionId, after.Baseline!.ArchiveVersionId);
        Assert.Equal(before.Baseline.EntrySet, after.Baseline.EntrySet);
        Assert.Equal(before.OutputLayout, after.OutputLayout);
        await using var db = new ConfigDbContextFactory(fixture.Path).Create();
        Assert.Equal(2, await db.StorageRelocationRootReservations.CountAsync());
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => reopened.CommitRelocationAsync(journal.Manifest.TransactionId, committed.Revision, physical, default));
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => reopened.SetActiveAsync(Plan, false, default));
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("revision")]
    [InlineData("layout")]
    [InlineData("layout-during-probe")]
    [InlineData("physical")]
    [InlineData("binding-fault")]
    [InlineData("journal-fault")]
    [InlineData("cancel")]
    public async Task FailedCommitPreservesOldRootAndSealedJournal(string failure)
    {
        await using var fixture = await Fixture.Create();
        var journal = await Seal(fixture, failure == "legacy");
        if (failure == "layout") await fixture.Configuration(output: "changed");
        var probe = new CommitProbe(async () =>
        {
            if (failure == "layout-during-probe") await fixture.Configuration(output: "changed");
            if (failure == "physical") throw new IOException("injected physical mismatch");
        });
        var repository = failure switch
        {
            "binding-fault" => new ConfigDbRepository(new(fixture.Path), new FailAt(MetadataCommitFaultPoint.AfterRelocationBindingSwitch)),
            "journal-fault" => new ConfigDbRepository(new(fixture.Path), new FailAt(MetadataCommitFaultPoint.AfterRelocationProgress)),
            _ => fixture.Repository
        };
        using var cancellation = new CancellationTokenSource();
        if (failure == "cancel") cancellation.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => repository.CommitRelocationAsync(journal.Manifest.TransactionId,
            failure == "revision" ? journal.Revision - 1 : journal.Revision, probe, cancellation.Token));
        Assert.Equal("/old", (await fixture.Repository.LoadAsync(Plan, default))!.CurrentRoot!.CanonicalPath);
        var after = (await fixture.Repository.LoadRelocationAsync(Plan, default))!;
        Assert.Equal(journal.Revision, after.Revision);
        Assert.Equal(StorageTransferStage.TargetsDurable, after.Progress.Stage);
        if (failure is "legacy" or "revision" or "layout" or "cancel") Assert.False(probe.Called);
    }

    [Fact]
    public async Task V4JournalSurvivesV5MigrationWithoutConfigurationAdoption()
    {
        await using var fixture = await Fixture.Create();
        var journal = await Seal(fixture, true);
        byte[] original;
        await using (var db = new ConfigDbContextFactory(fixture.Path).Create())
        {
            original = (await db.StorageRelocationIntents.SingleAsync()).ManifestPayload;
            await db.GetService<IMigrator>().MigrateAsync("20260903091419_AddStorageRelocationJournalV4");
        }
        var reopened = await ConfigDbOpenCoordinator.OpenAsync(fixture.Path);
        var restored = (await reopened.LoadRelocationAsync(Plan, default))!;
        Assert.Equal(journal.Revision, restored.Revision);
        await using var check = new ConfigDbContextFactory(fixture.Path).Create();
        var row = await check.StorageRelocationIntents.SingleAsync();
        Assert.Equal(original, row.ManifestPayload);
        Assert.Null(row.ConfigurationPayload);
        Assert.Equal(2, await check.StorageRelocationRootReservations.CountAsync());
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => reopened.CommitRelocationAsync(journal.Manifest.TransactionId, journal.Revision, new CommitProbe(), default));
    }

    [Fact]
    public async Task DowngradeCannotDiscardConfigurationCheckpoint()
    {
        await using var fixture = await Fixture.Create();
        var journal = await Seal(fixture, false);
        await using (var db = new ConfigDbContextFactory(fixture.Path).Create())
            await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20260903091419_AddStorageRelocationJournalV4"));
        // EF 按 migration 提交：v6 -> v5 合法，但 v5 -> v4 被 checkpoint 拒绝。
        Assert.Equal(5, (await fixture.Repository.LoadAsync(default))!.SchemaVersion);
        var reopened = await ConfigDbOpenCoordinator.OpenAsync(fixture.Path);
        Assert.Equal(6, (await reopened.LoadAsync(default))!.SchemaVersion);
        Assert.Equal(journal.Revision, (await fixture.Repository.LoadRelocationAsync(Plan, default))!.Revision);
    }

    [Theory]
    [InlineData("transaction")]
    [InlineData("plan")]
    [InlineData("revision")]
    [InlineData("artifact")]
    [InlineData("old-root")]
    [InlineData("old-object")]
    [InlineData("target")]
    public async Task CleanupRejectsMismatchedProof(string mismatch)
    {
        await using var fixture = await Fixture.Create();
        var journal = await Seal(fixture, false);
        journal = await fixture.Repository.CommitRelocationAsync(journal.Manifest.TransactionId, journal.Revision, new CommitProbe(), default);
        var probe = new AbsenceProbe(proof => mismatch switch
        {
            "transaction" => proof with { TransactionId = Guid.NewGuid() },
            "plan" => proof with { PlanId = new(Guid.NewGuid()) },
            "revision" => proof with { JournalRevision = proof.JournalRevision + 1 },
            "artifact" => proof with { Artifact = proof.Artifact with { Length = 43 } },
            "old-root" => proof with { OldRootIdentity = Identity("wrong") },
            "old-object" => proof with { OldIdentity = Identity("wrong") },
            _ => proof with { TargetIdentity = Identity("wrong") }
        });
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => fixture.Repository.CleanupRelocationEntryAsync(
            journal.Manifest.TransactionId, journal.Revision, fixture.Version, probe, default));
        Assert.Equal(journal.Revision, (await fixture.Repository.LoadRelocationAsync(Plan, default))!.Revision);
        Assert.Equal("/new", (await fixture.Repository.LoadAsync(Plan, default))!.CurrentRoot!.CanonicalPath);
    }

    [Theory]
    [InlineData("precommit")]
    [InlineData("revision")]
    [InlineData("binding")]
    [InlineData("placement")]
    [InlineData("reservation")]
    [InlineData("version")]
    [InlineData("incomplete")]
    public async Task CleanupChecksDurableAuthorityBeforePhysicalCall(string failure)
    {
        await using var fixture = await Fixture.Create();
        var journal = await Seal(fixture, false);
        if (failure != "precommit")
            journal = await fixture.Repository.CommitRelocationAsync(journal.Manifest.TransactionId, journal.Revision, new CommitProbe(), default);
        await using (var db = new ConfigDbContextFactory(fixture.Path).Create())
        {
            if (failure == "binding") (await db.OutputRootLocalBindings.SingleAsync()).CanonicalPath = "/changed";
            if (failure == "placement") (await db.CurrentVersions.SingleAsync()).CurrentRelativePath = "changed.7z";
            if (failure == "reservation") db.StorageRelocationRootReservations.Remove(await db.StorageRelocationRootReservations.FirstAsync());
            await db.SaveChangesAsync();
        }
        var probe = new AbsenceProbe();
        await Assert.ThrowsAnyAsync<Exception>(() => failure == "incomplete"
            ? fixture.Repository.CompleteRelocationAsync(journal.Manifest.TransactionId, journal.Revision, probe, default)
            : fixture.Repository.CleanupRelocationEntryAsync(journal.Manifest.TransactionId,
                journal.Revision + (failure == "revision" ? 1 : 0), failure == "version" ? new(Guid.NewGuid()) : fixture.Version, probe, default));
        Assert.Equal(0, probe.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupProgressSurvivesReopenAndCannotDowngrade(bool complete)
    {
        await using var fixture = await Fixture.Create();
        var journal = await Seal(fixture, false);
        journal = await fixture.Repository.CommitRelocationAsync(journal.Manifest.TransactionId, journal.Revision, new CommitProbe(), default);
        // v5 的已提交记录应原字节跨升级保存，不推断任何清理已完成。
        await using (var db = new ConfigDbContextFactory(fixture.Path).Create())
            await db.GetService<IMigrator>().MigrateAsync("20260903155016_AddRelocationCommitV5");
        var reopened = await ConfigDbOpenCoordinator.OpenAsync(fixture.Path);
        Assert.Equal(StorageTransferArtifactStage.TargetDurable, (await reopened.LoadRelocationAsync(Plan, default))!.Progress.Artifacts[0].Stage);
        File.Delete(fixture.Path + ".backupplan"); // 提交后清理不再依赖原始配置文件。
        var probe = new AbsenceProbe();
        journal = await reopened.CleanupRelocationEntryAsync(journal.Manifest.TransactionId, journal.Revision, fixture.Version, probe, default);
        if (complete) journal = await reopened.CompleteRelocationAsync(journal.Manifest.TransactionId, journal.Revision, probe, default);
        Assert.Equal(complete ? 2 : 1, probe.Calls);
        await using (var db = new ConfigDbContextFactory(fixture.Path).Create())
        {
            Assert.Equal(2, await db.StorageRelocationRootReservations.CountAsync());
            await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20260903155016_AddRelocationCommitV5"));
        }
        reopened = await ConfigDbOpenCoordinator.OpenAsync(fixture.Path);
        var restored = (await reopened.LoadRelocationAsync(Plan, default))!;
        Assert.Equal(journal.Revision, restored.Revision);
        Assert.Equal(journal.Progress.Stage, restored.Progress.Stage);
        Assert.Equal(StorageTransferArtifactStage.OldCopyAbsent, restored.Progress.Artifacts[0].Stage);
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => reopened.SetActiveAsync(Plan, false, default));
    }

    [Fact]
    public async Task RecoveryWithNoJournalDoesNotCallPhysicalStore()
    {
        await using var fixture = await Fixture.Create();
        var physical = new AbsenceProbe();
        var result = await new StorageRelocationRecoveryWorkflow(fixture.Repository, physical).RecoverAsync(Plan, default);
        Assert.Equal(StorageRelocationRecoveryStatus.NotFound, result.Status);
        Assert.Equal(0, physical.Calls);
        Assert.Empty(await fixture.Repository.ListRelocationsAsync(default));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("database")]
    [InlineData("concurrency")]
    [InlineData("cancel")]
    [InlineData("corruption")]
    [InlineData("foreign-cancel")]
    public async Task RecoveryDistinguishesPendingCleanupFromCorruption(string failure)
    {
        await using var fixture = await Fixture.Create();
        var journal = await Seal(fixture, false);
        journal = await fixture.Repository.CommitRelocationAsync(journal.Manifest.TransactionId, journal.Revision, new CommitProbe(), default);
        using var cancellation = new CancellationTokenSource();
        var physical = new AbsenceProbe(proof =>
        {
            if (failure == "cancel") { cancellation.Cancel(); return proof; }
            throw failure switch
            {
                "io" => new IOException("sensitive-path"),
                "access" => new UnauthorizedAccessException("sensitive-path"),
                "database" => new LocalStateRepositoryException("sensitive-path"),
                "concurrency" => new LocalStateConcurrencyException("sensitive-path"),
                "corruption" => new LocalStateCorruptionException("corrupt"),
                _ => new OperationCanceledException()
            };
        });
        var workflow = new StorageRelocationRecoveryWorkflow(fixture.Repository, physical);
        if (failure == "corruption")
            await Assert.ThrowsAsync<LocalStateCorruptionException>(() => workflow.RecoverAsync(Plan, cancellation.Token));
        else if (failure == "foreign-cancel")
            await Assert.ThrowsAsync<OperationCanceledException>(() => workflow.RecoverAsync(Plan, cancellation.Token));
        else
        {
            var result = await workflow.RecoverAsync(Plan, cancellation.Token);
            Assert.Equal(StorageRelocationRecoveryStatus.CleanupPending, result.Status);
            Assert.DoesNotContain("sensitive-path", result.Diagnostic!);
        }
        var restored = (await fixture.Repository.LoadRelocationAsync(Plan, default))!;
        Assert.Equal(StorageTransferStage.MetadataCommitted, restored.Progress.Stage);
        Assert.Equal(failure == "cancel" ? journal.Revision + 1 : journal.Revision, restored.Revision);
        Assert.Equal("/new", (await fixture.Repository.LoadAsync(Plan, default))!.CurrentRoot!.CanonicalPath);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("stage")]
    [InlineData("revision")]
    [InlineData("physical")]
    [InlineData("database")]
    [InlineData("cancel")]
    public async Task CompactionAtomicallyReleasesOnlyVerifiedCompletedJournal(string failure)
    {
        await using var fixture = await Fixture.Create();
        var journal = await Seal(fixture, false);
        journal = await fixture.Repository.CommitRelocationAsync(journal.Manifest.TransactionId, journal.Revision, new CommitProbe(), default);
        var absence = new AbsenceProbe();
        if (failure != "stage")
        {
            journal = await fixture.Repository.CleanupRelocationEntryAsync(journal.Manifest.TransactionId, journal.Revision, fixture.Version, absence, default);
            journal = await fixture.Repository.CompleteRelocationAsync(journal.Manifest.TransactionId, journal.Revision, absence, default);
        }
        var before = (await fixture.Repository.LoadAsync(Plan, Unit, default))!;
        using var cancellation = new CancellationTokenSource();
        var probe = new CompletionProbe(() =>
        {
            if (failure == "physical") throw new IOException("injected");
            if (failure == "cancel") cancellation.Cancel();
        });
        var repository = failure == "database" ? new ConfigDbRepository(new(fixture.Path), new FailAt(MetadataCommitFaultPoint.AfterRelocationProgress)) : fixture.Repository;
        Task Compact() => repository.CompactRelocationAsync(journal.Manifest.TransactionId,
            journal.Revision + (failure == "revision" ? 1 : 0), probe, cancellation.Token);
        if (failure == "none") await Compact();
        else await Assert.ThrowsAnyAsync<Exception>(Compact);
        var reopened = await ConfigDbOpenCoordinator.OpenAsync(fixture.Path);
        await using var db = new ConfigDbContextFactory(fixture.Path).Create();
        Assert.Equal(failure == "none" ? 0 : 2, await db.StorageRelocationRootReservations.CountAsync());
        if (failure == "none")
        {
            Assert.Null(await reopened.LoadRelocationAsync(Plan, default));
            await reopened.SetActiveAsync(Plan, false, default);
        }
        else Assert.Equal(journal.Revision, (await reopened.LoadRelocationAsync(Plan, default))!.Revision);
        if (failure is "stage" or "revision") Assert.False(probe.Called);
        Assert.Equal("/new", (await reopened.LoadAsync(Plan, default))!.CurrentRoot!.CanonicalPath);
        Assert.Equivalent(before, await reopened.LoadAsync(Plan, Unit, default));
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("revision")]
    [InlineData("legacy")]
    [InlineData("configuration")]
    [InlineData("cancel-after-proof")]
    public async Task ResumeUsesDurableAuthorityAndStableProofBoundary(string scenario)
    {
        await using var fixture = await Fixture.Create();
        var configuration = await fixture.Configuration();
        var journal = scenario == "legacy" ? await fixture.Repository.BeginRelocationAsync(fixture.Manifest(), default)
            : await fixture.Repository.BeginRelocationAsync(fixture.Manifest(), configuration, default);
        if (scenario == "configuration") await fixture.Configuration(output: "changed");
        using var cancellation = new CancellationTokenSource();
        var probe = new ResumeProbe(() => { if (scenario == "cancel-after-proof") cancellation.Cancel(); });
        Task<StorageRelocationJournal> Resume() => fixture.Repository.ResumeRelocationEntryAsync(journal.Manifest.TransactionId,
            journal.Revision + (scenario == "revision" ? 1 : 0), fixture.Version, probe, cancellation.Token);
        if (scenario is "revision" or "legacy" or "configuration")
        {
            await Assert.ThrowsAsync<LocalStateConcurrencyException>(Resume);
            Assert.Equal(0, probe.Calls);
        }
        else
        {
            var next = await Resume();
            Assert.Equal(StorageTransferArtifactStage.Staged, next.Progress.Artifacts[0].Stage);
            Assert.Equal(journal.Revision + 1, next.Revision);
            await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => fixture.Repository.ResumeRelocationEntryAsync(
                journal.Manifest.TransactionId, journal.Revision, fixture.Version, probe, default));
            Assert.Equal(1, probe.Calls);
        }
        Assert.Equal("/old", (await fixture.Repository.LoadAsync(Plan, default))!.CurrentRoot!.CanonicalPath);
    }

    [Fact]
    public async Task ConcurrentResumeWithSameRevisionExecutesPhysicalStageOnlyOnce()
    {
        await using var fixture = await Fixture.Create();
        var journal = await fixture.Repository.BeginRelocationAsync(fixture.Manifest(), await fixture.Configuration(), default);
        var probe = new ResumeProbe(() => { });
        async Task<bool> Attempt()
        {
            try
            {
                await fixture.Repository.ResumeRelocationEntryAsync(journal.Manifest.TransactionId, journal.Revision, fixture.Version, probe, default);
                return true;
            }
            catch (LocalStateConcurrencyException) { return false; }
        }
        var results = await Task.WhenAll(Task.Run(Attempt), Task.Run(Attempt));
        Assert.Single(results, x => x);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(journal.Revision + 1, (await fixture.Repository.LoadRelocationAsync(Plan, default))!.Revision);
    }

    [Theory]
    [InlineData("/new")]
    [InlineData("/new/input")]
    [InlineData("/old/input")]
    public async Task ExternalBindingsCannotBeOccupiedByRelocation(string path)
    {
        await using var fixture = await Fixture.Create();
        var other = new PlanId(Guid.NewGuid());
        await fixture.Repository.SaveFileBackedAsync(new(other, PlanAuthority.FileBacked, "/other.backupplan", true), default);
        await fixture.Repository.SaveValidatedAggregateAsync(new(other, Device, [], new("/other-output", "/other-output", true), null,
            [new(new(Guid.NewGuid()), path, path, true)]), default);
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => fixture.Repository.BeginRelocationAsync(fixture.Manifest(), default));
        Assert.Empty(await fixture.Repository.ListRelocationsAsync(default));
    }

    [Fact]
    public async Task ExternalBindingSaveAndActivationRespectExistingReservations()
    {
        await using var fixture = await Fixture.Create();
        var other = new PlanId(Guid.NewGuid());
        await fixture.Repository.SaveFileBackedAsync(new(other, PlanAuthority.FileBacked, "/other.backupplan", false), default);
        var overlapping = new DevicePlanLocalBindings(other, Device, [], new("/other-output", "/other-output", true), null,
            [new(new(Guid.NewGuid()), "/new/input", "/new/input", true)]);
        await fixture.Repository.SaveValidatedAggregateAsync(overlapping, default);
        await fixture.Repository.BeginRelocationAsync(fixture.Manifest(), default);
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => fixture.Repository.SetActiveAsync(other, true, default));
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => fixture.Repository.SaveValidatedAggregateAsync(overlapping, default));
        Assert.False((await ((IPlanRegistrationStore)fixture.Repository).LoadAsync(other, default))!.Registration.IsActive);
    }

    [Fact]
    public async Task InventoryIsReadOnlyAndIncludesRetainedUndeclaredCurrent()
    {
        await using var fixture = await Fixture.Create();
        var configuration = await fixture.Configuration();
        var p = configuration.Snapshot.Plan;
        var withoutUnits = new PortableBackupPlan(p.Id, p.Name, p.Description, p.Semantics, p.Sources, p.GlobalRules, p.PlanRules,
            p.ArchiveSpecDefault, [], p.SecretSlots, p.LinkPolicy, p.ChangeDetection, p.HistoryDefault, p.Schedule, p.ExternalSources);
        var documents = new BackupPlanDocumentSource();
        await File.WriteAllBytesAsync(fixture.Path + ".backupplan", documents.Write(withoutUnits).CanonicalUtf8Payload.ToArray());
        configuration = await new StorageRelocationConfigurationReader(new(fixture.Repository, documents)).ReadAsync(Plan, default);
        // 使用真实 retained Current，不依赖源绑定、源扫描或密钥。
        var inventory = await fixture.Repository.ReadRelocationInventoryAsync(new(Plan, new("/new", "/new"), null), configuration, default);
        Assert.Equal(fixture.Version, Assert.Single(inventory.Entries).Artifact.VersionId);
        Assert.Equal(42, inventory.Entries[0].Artifact.Length);
        Assert.Equal("/old", Assert.Single(inventory.Roots).OldRoot.CanonicalPath);
        Assert.Equal(Device, inventory.DeviceId);
        Assert.Empty(await fixture.Repository.ListRelocationsAsync(default));
        await using var db = new ConfigDbContextFactory(fixture.Path).Create();
        Assert.Empty(await db.StorageRelocationRootReservations.ToListAsync());
    }

    [Theory]
    [InlineData("no-roots")]
    [InlineData("same-root")]
    [InlineData("missing-history")]
    [InlineData("stale")]
    [InlineData("pending")]
    public async Task InventoryRejectsInvalidOrStaleRequestWithoutWriting(string failure)
    {
        await using var fixture = await Fixture.Create();
        var configuration = await fixture.Configuration();
        if (failure == "stale") await fixture.Configuration(output: "changed");
        if (failure == "pending") await fixture.Repository.BeginRelocationAsync(fixture.Manifest(), configuration, default);
        var request = failure switch
        {
            "no-roots" => new StorageRelocationInventoryRequest(Plan, null, null),
            "same-root" => new(Plan, new("/old", "/old"), null),
            "missing-history" => new(Plan, null, new("/new-history", "/new-history")),
            _ => new(Plan, new("/new", "/new"), null)
        };
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Repository.ReadRelocationInventoryAsync(request, configuration, default));
        Assert.Equal(failure == "pending" ? 1 : 0, (await fixture.Repository.ListRelocationsAsync(default)).Length);
        Assert.Equal("/old", (await fixture.Repository.LoadAsync(Plan, default))!.CurrentRoot!.CanonicalPath);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("name")]
    [InlineData("layout")]
    [InlineData("placement")]
    [InlineData("pending")]
    [InlineData("missing-config")]
    public async Task InspectionRevalidatesConfigurationAndMetadataAfterPhysicalIo(string drift)
    {
        await using var fixture = await Fixture.Create();
        await fixture.Configuration();
        var before = (await fixture.Repository.LoadAsync(Plan, Unit, default))!;
        var probe = new InventoryProbe(async () =>
        {
            if (drift == "name") await fixture.Configuration(name: "renamed");
            if (drift == "layout") await fixture.Configuration(output: "changed");
            if (drift == "placement")
                await fixture.Repository.CommitOutputReorganizationAsync(OutputReorganization.Commit(before.Current!, before.OutputLayout!,
                    new("moved.7z"), new(Hash("layout2"))), default);
            if (drift == "pending") await fixture.Repository.BeginPublishAsync(Intent(new(Guid.NewGuid())), default);
            if (drift == "missing-config") File.Delete(fixture.Path + ".backupplan");
        });
        var workflow = new StorageRelocationInspectionWorkflow(new(new(fixture.Repository, new BackupPlanDocumentSource())), fixture.Repository, probe);
        if (drift is "none" or "name")
        {
            var observed = await workflow.InspectAsync(new(Plan, new("/new", "/new"), null), default);
            Assert.Equal(fixture.Version, Assert.Single(observed.Inventory.Entries).Artifact.VersionId);
        }
        else await Assert.ThrowsAnyAsync<Exception>(() => workflow.InspectAsync(new(Plan, new("/new", "/new"), null), default));
        Assert.Equal(1, probe.Calls);
        Assert.Empty(await fixture.Repository.ListRelocationsAsync(default));
        Assert.Equal("/old", (await fixture.Repository.LoadAsync(Plan, default))!.CurrentRoot!.CanonicalPath);
        Assert.Equal(before.Baseline!.ArchiveVersionId, (await fixture.Repository.LoadAsync(Plan, Unit, default))!.Baseline!.ArchiveVersionId);
        await using var db = new ConfigDbContextFactory(fixture.Path).Create();
        Assert.Empty(await db.StorageRelocationRootReservations.ToListAsync());
    }

    // 仅注入物理 I/O 期间的并发变更；真实 no-follow/hash/容量观察由 filesystem fixtures 验证。
    private sealed class InventoryProbe(Func<Task> duringObservation) : IStorageRelocationInventoryProbe
    {
        public int Calls { get; private set; }
        public async Task<StorageRelocationPhysicalInventory> ObserveInventoryAsync(StorageRelocationInventory inventory, CancellationToken cancellationToken)
        {
            Calls++;
            await duringObservation();
            return new(inventory, [], [], []);
        }
    }

    private sealed class ResumeProbe(Action afterProof) : IStorageRelocationPhysicalStore
    {
        public int Calls { get; private set; }
        public Task<StorageTransferProof> StageAsync(StorageRelocationJournal journal, ArchiveVersionId version, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            var artifact = journal.Manifest.Entries.Single(x => x.Artifact.VersionId == version).Artifact;
            afterProof();
            return Task.FromResult(new StorageTransferProof(journal.Manifest.TransactionId, journal.Manifest.PlanId, version,
                artifact.Integrity, artifact.Length, Identity("staged"), true, true));
        }
        public Task<StorageTransferProof> PublishTargetAsync(StorageRelocationJournal journal, ArchiveVersionId version, CancellationToken token) => throw new NotSupportedException();
        public Task VerifyForCommitAsync(StorageRelocationJournal journal, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class CompletionProbe(Action action) : IStorageRelocationCompletionProbe
    {
        public bool Called { get; private set; }
        public Task VerifyCompletedAsync(StorageRelocationJournal journal, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Called = true; action(); return Task.CompletedTask; }
    }

    private sealed class AbsenceProbe(Func<StorageRelocationOldCopyAbsenceProof, StorageRelocationOldCopyAbsenceProof>? transform = null) : IStorageRelocationOldCopyStore
    {
        public int Calls { get; private set; }
        public Task<StorageRelocationOldCopyAbsenceProof> RemoveOldCopyAsync(StorageRelocationJournal journal, ArchiveVersionId version, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            var entry = journal.Manifest.Entries.Single(x => x.Artifact.VersionId == version);
            var proof = new StorageRelocationOldCopyAbsenceProof(journal.Manifest.TransactionId, journal.Manifest.PlanId, journal.Revision,
                entry.Artifact, journal.Manifest.Roots.Single(x => x.Kind == entry.RootKind).OldIdentity, entry.OldIdentity,
                journal.Progress.Artifacts.Single(x => x.Artifact.VersionId == version).StagedIdentity!);
            return Task.FromResult(transform is null ? proof : transform(proof));
        }
    }

    private static async Task<StorageRelocationJournal> Seal(Fixture fixture, bool legacy)
    {
        var configuration = await fixture.Configuration();
        var manifest = fixture.Manifest();
        var journal = legacy ? await fixture.Repository.BeginRelocationAsync(manifest, default)
            : await fixture.Repository.BeginRelocationAsync(manifest, configuration, default);
        foreach (var entry in manifest.Entries)
        {
            var proof = new StorageTransferProof(manifest.TransactionId, Plan, entry.Artifact.VersionId,
                entry.Artifact.Integrity, entry.Artifact.Length, Identity(entry.Artifact.VersionId.Value.ToString()), true, true);
            journal = await fixture.Repository.RecordRelocationStagedAsync(manifest.TransactionId, journal.Revision, proof, default);
            journal = await fixture.Repository.RecordRelocationTargetAsync(manifest.TransactionId, journal.Revision, proof, default);
        }
        return await fixture.Repository.SealRelocationTargetsAsync(manifest.TransactionId, journal.Revision, default);
    }

    // SQLite 原子性测试注入物理检查；不据此声称完成真实磁盘端到端验收。
    private sealed class CommitProbe(Func<Task>? action = null) : IStorageRelocationPhysicalStore
    {
        public bool Called { get; private set; }
        public async Task VerifyForCommitAsync(StorageRelocationJournal journal, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Called = true; if (action is not null) await action(); }
        public Task<StorageTransferProof> StageAsync(StorageRelocationJournal journal, ArchiveVersionId version, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageTransferProof> PublishTargetAsync(StorageRelocationJournal journal, ArchiveVersionId version, CancellationToken token) => throw new NotSupportedException();
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
        public async Task<StorageRelocationConfigurationObservation> Configuration(string name = "plan", string output = "out")
        {
            var source = new SourceId(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
            var plan = new PortableBackupPlan(Plan, name, null, new(1, 1, 1), [new(source, "source", new(output))], new([], null), [],
                new(PortableArchiveFormat.SevenZip, PortableCompressionPreset.Standard, new NoProtection()),
                [new UiManagedArchiveUnit(Unit, source, new("unit"), new RuleSet(), null, null)], [],
                PortableLinkPolicy.Preserve, PortableChangeDetectionMode.Standard, new HistoryDisabled(), new ManualOnlySchedule(), []);
            var documents = new BackupPlanDocumentSource();
            await File.WriteAllBytesAsync(Path + ".backupplan", documents.Write(plan).CanonicalUtf8Payload.ToArray());
            return await new StorageRelocationConfigurationReader(new(Repository, documents)).ReadAsync(Plan, default);
        }
        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm", ".backupplan" }) if (File.Exists(Path + suffix)) File.Delete(Path + suffix);
            return ValueTask.CompletedTask;
        }
    }
}
