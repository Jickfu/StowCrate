using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using StowCrate.Application.BackupPlans.Documents;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Application.LocalState;
using StowCrate.Application.StorageMaintenance;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Infrastructure.Configuration.BackupPlans;
using StowCrate.Infrastructure.Configuration.BackupPlans.V1;
using StowCrate.Infrastructure.Filesystem;
using StowCrate.Infrastructure.Persistence.ConfigDb;

namespace StowCrate.Infrastructure.Tests.Persistence;

public sealed class ConfigDbWorkflowIntegrationTests
{
    private sealed class UnavailableRelocationComparison(int failureCall) : IStorageRelocationTargetComparisonProbe
    {
        private int calls;
        public Task VerifyTargetsAsync(StorageRelocationPhysicalInventory observation, Guid transactionId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task VerifyLayoutAsync(StorageRelocationManifest manifest, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++calls == failureCall) throw new StorageRelocationComparisonUnavailableException();
            return Task.CompletedTask;
        }
    }

    private sealed class RelocationCapacityProbe(long? available) : IStorageRelocationCapacityProbe
    {
        public Task<StorageCapacityObservation> ObserveAsync(ResolvedPhysicalPath root, CancellationToken cancellationToken)
            => Task.FromResult(new StorageCapacityObservation(StorageRelocationPhysicalStore.InspectIdentity(root.CanonicalPath, true), new("test", 1, "volume"), available));
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("begin-missing-root")]
    [InlineData("begin-missing-durability")]
    [InlineData("begin-unavailable")]
    [InlineData("begin-cancel")]
    [InlineData("begin-occupied")]
    [InlineData("begin-database-fault")]
    [InlineData("begin-lost-response")]
    [InlineData("begin-cancel-after-commit")]
    [InlineData("begin-inactive")]
    [InlineData("compaction-old-reappeared")]
    [InlineData("compaction-temp-reappeared")]
    [InlineData("compaction-missing-adapter")]
    [InlineData("compaction-database-fault")]
    [InlineData("compaction-stale-revision")]
    [InlineData("compaction-cancel")]
    [InlineData("capacity-unavailable")]
    [InlineData("capacity-insufficient")]
    [InlineData("comparison-prepared")]
    [InlineData("comparison-staged")]
    [InlineData("comparison-sealed")]
    [InlineData("comparison-after-rename")]
    [InlineData("explicit-prepared")]
    [InlineData("explicit-staged")]
    [InlineData("explicit-sealed")]
    [InlineData("explicit-wrong-transaction")]
    [InlineData("explicit-no-adapter")]
    [InlineData("explicit-unknown-temp")]
    [InlineData("resume-stage-fault")]
    [InlineData("resume-publish-fault")]
    [InlineData("database-fault")]
    [InlineData("cancel-after-delete")]
    [InlineData("old-reappeared")]
    [InlineData("startup-prepared")]
    [InlineData("startup-staged")]
    [InlineData("startup-sealed")]
    [InlineData("startup-committed")]
    [InlineData("startup-absent")]
    [InlineData("startup-no-adapter")]
    [InlineData("startup-inactive")]
    [InlineData("startup-drift")]
    [InlineData("startup-corrupt")]
    [InlineData("startup-publish-conflict")]
    public async Task RelocationCopiesCommitsAndDurablyReconcilesOldCopy(string scenario)
    {
        await using var database = await WorkflowDatabase.Create(); var (plan, unit) = await database.RegisterFixturePlan();
        var oldRoot = Directory.CreateDirectory(Path.Combine(database.DirectoryPath, "old-root")).FullName;
        var newRoot = Directory.CreateDirectory(Path.Combine(database.DirectoryPath, "new-root")).FullName;
        var bytes = "0123456789"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(oldRoot, "unit.7z"), bytes);
        var device = (await database.Repository.LoadAsync(default))!.DeviceId;
        await database.Repository.SaveValidatedAggregateAsync(new(plan.Id, device, [], new(oldRoot, Key(oldRoot), true), null, []), default);
        var intent = Intent(plan.Id, unit.Id, new(Guid.NewGuid()), Sha256Digest.Hash(bytes));
        await database.Repository.BeginPublishAsync(intent, default);
        var published = intent.MarkCurrentPublished(DateTimeOffset.UnixEpoch);
        await database.Repository.SavePublishProgressAsync(published, default);
        await using var captureDb = new ConfigDbContextFactory(database.Path).Create();
        var capturedPublish = await captureDb.PublishIntents.AsNoTracking().SingleAsync();
        var capturedBaseline = await captureDb.PublishIntentBaselines.AsNoTracking().SingleAsync();
        await database.Repository.CompleteMetadataCommitAsync(published.RebuildMetadataCommitPlan(), default);
        await database.Repository.CleanupCompletedPublishIntentsAsync(default);
        var state = (await database.Repository.LoadAsync(plan.Id, unit.Id, default))!;
        var version = state.Current!.ArchiveVersionId; var transaction = Guid.NewGuid(); var path = new RelativeStoragePath("unit.7z");
        var configuration = await new StorageRelocationConfigurationReader(new(database.Repository, new BackupPlanDocumentSource())).ReadAsync(plan.Id, default);
        var manifest = new StorageRelocationManifest(transaction, plan.Id, device,
            [new(StorageRootKind.Current, new(oldRoot, Key(oldRoot)), new(newRoot, Key(newRoot)),
                StorageRelocationPhysicalStore.InspectIdentity(oldRoot, true), StorageRelocationPhysicalStore.InspectIdentity(newRoot, true))],
            [new(unit.Id, StorageRootKind.Current, new(version, Sha256Digest.Hash(bytes), bytes.Length), path,
                StorageRelocationTempLayout.Create(transaction, version, path), StorageRelocationPhysicalStore.InspectIdentity(Path.Combine(oldRoot, path.Value), false))]);
        var physical = RelocationTestPhysicalStore.Create(new RelocationTestBarrier());
        StorageRelocationJournal journal;
        if (scenario == "normal" || scenario.StartsWith("begin-", StringComparison.Ordinal))
        {
            using var beginCancellation = new CancellationTokenSource();
            if (scenario == "begin-missing-root") Directory.Delete(newRoot);
            var beginPhysical = RelocationTestPhysicalStore.Create(new BeginBarrier(async () =>
            {
                if (scenario == "begin-cancel") beginCancellation.Cancel();
                if (scenario == "begin-occupied") File.WriteAllBytes(Path.Combine(newRoot, path.Value), bytes);
                if (scenario == "begin-inactive")
                    await new AuthoritativePlanWorkflow(database.Repository, new BackupPlanDocumentSource()).SetActiveAsync(plan.Id, false, default);
            }, scenario != "begin-unavailable"));
            var beginRepository = scenario == "begin-database-fault"
                ? new ConfigDbRepository(new ConfigDbContextFactory(database.Path), new BeginFault()) : database.Repository;
            var inspection = new StorageRelocationInspectionWorkflow(new(new(beginRepository, new BackupPlanDocumentSource())),
                beginRepository, beginPhysical, beginPhysical,
                new StorageRelocationTargetComparisonProbe(directory => StorageRelocationPhysicalStore.InspectIdentity(directory, true)),
                scenario == "begin-missing-durability" ? null : beginPhysical);
            var responseStore = new BeginResponseStore(beginRepository, scenario, beginCancellation);
            var begin = new StorageRelocationBeginWorkflow(inspection, responseStore);
            if (scenario is "normal" or "begin-cancel-after-commit")
            {
                journal = await begin.BeginAsync(new(plan.Id, new(newRoot, Key(newRoot)), null), transaction, beginCancellation.Token);
                Assert.Equal(2, journal.Manifest.EncodingVersion);
                Assert.Equal(transaction, journal.Manifest.TransactionId);
                Assert.Equal(1, journal.Revision);
                Assert.Empty(Directory.GetFileSystemEntries(newRoot));
                Assert.Equal(1, responseStore.Calls);
            }
            else
            {
                var error = await Record.ExceptionAsync(() => begin.BeginAsync(new(plan.Id, new(newRoot, Key(newRoot)), null), transaction, beginCancellation.Token));
                if (scenario == "begin-missing-root") Assert.IsType<StorageRelocationTargetRootMissingException>(error);
                else if (scenario is "begin-missing-durability" or "begin-unavailable") Assert.IsType<StorageRelocationDurabilityUnavailableException>(error);
                else if (scenario == "begin-cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
                else if (scenario == "begin-inactive") Assert.IsType<LocalStateConcurrencyException>(error);
                else if (scenario is "begin-database-fault" or "begin-lost-response")
                    Assert.Equal(transaction, Assert.IsType<StorageRelocationBeginOutcomeUnknownException>(error).TransactionId);
                else Assert.IsAssignableFrom<IOException>(error);
                if (scenario == "begin-lost-response")
                    Assert.Equal(transaction, (await database.Repository.LoadRelocationAsync(plan.Id, default))!.Manifest.TransactionId);
                else Assert.Null(await database.Repository.LoadRelocationAsync(plan.Id, default));
                Assert.Equal(scenario is "begin-database-fault" or "begin-lost-response" ? 1 : 0, responseStore.Calls);
                Assert.Equal(oldRoot, (await database.Repository.LoadAsync(plan.Id, default))!.CurrentRoot!.CanonicalPath);
                Assert.Equivalent(state.Baseline, (await database.Repository.LoadAsync(plan.Id, unit.Id, default))!.Baseline);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(oldRoot, path.Value)));
                if (scenario == "begin-missing-root") Assert.False(Directory.Exists(newRoot));
                else if (scenario != "begin-occupied") Assert.Empty(Directory.GetFileSystemEntries(newRoot));
                return;
            }
        }
        else journal = await database.Repository.BeginRelocationAsync(manifest, configuration, default);
        if (scenario.StartsWith("comparison-", StringComparison.Ordinal))
        {
            if (scenario != "comparison-prepared")
                journal = await database.Repository.ResumeRelocationEntryAsync(transaction, journal.Revision, version, physical, default);
            if (scenario == "comparison-sealed")
            {
                journal = await database.Repository.ResumeRelocationEntryAsync(transaction, journal.Revision, version, physical, default);
                journal = await database.Repository.SealRelocationTargetsAsync(transaction, journal.Revision, default);
            }
            var blocked = new StorageRelocationPhysicalStore(new RelocationTestBarrier(),
                comparisonProbe: new UnavailableRelocationComparison(scenario == "comparison-after-rename" ? 3 : 1));
            var result = await new StorageRelocationRecoveryWorkflow(database.Repository, blocked).ResumeAsync(plan.Id, transaction, blocked, default);
            Assert.Equal(StorageRelocationRecoveryStatus.ResumeRequired, result.Status);
            Assert.Equal("RELOCATION_TARGET_COMPARISON_UNAVAILABLE", result.Diagnostic);
            Assert.Equal(journal.Revision, (await database.Repository.LoadRelocationAsync(plan.Id, default))!.Revision);
            Assert.Equal(oldRoot, (await database.Repository.LoadAsync(plan.Id, default))!.CurrentRoot!.CanonicalPath);
            Assert.Equivalent(state.Baseline, (await database.Repository.LoadAsync(plan.Id, unit.Id, default))!.Baseline);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(oldRoot, path.Value)));
            if (scenario == "comparison-prepared") Assert.Empty(Directory.EnumerateFileSystemEntries(newRoot));
            if (scenario == "comparison-after-rename")
            {
                Assert.True(File.Exists(Path.Combine(newRoot, path.Value)));
                Assert.False(File.Exists(Path.Combine(newRoot, manifest.Entries[0].TempRelativePath.Value)));
            }
            // 能力恢复后仅使用原 durable journal 补记/继续，不生成新事务或接纳外来文件。
            var resumed = await new StorageRelocationRecoveryWorkflow(database.Repository, physical).ResumeAsync(plan.Id, transaction, physical, default);
            Assert.Equal(StorageRelocationRecoveryStatus.CompletedReservationsRetained, resumed.Status);
            Assert.Equal(newRoot, (await database.Repository.LoadAsync(plan.Id, default))!.CurrentRoot!.CanonicalPath);
            Assert.Equivalent(state.Baseline, (await database.Repository.LoadAsync(plan.Id, unit.Id, default))!.Baseline);
            Assert.False(File.Exists(Path.Combine(oldRoot, path.Value)));
            return;
        }
        if (scenario is "capacity-unavailable" or "capacity-insufficient")
        {
            var blocked = RelocationTestPhysicalStore.Create(new RelocationTestBarrier(), new RelocationCapacityProbe(scenario == "capacity-unavailable" ? null : 0));
            var result = await new StorageRelocationRecoveryWorkflow(database.Repository, blocked).ResumeAsync(plan.Id, transaction, blocked, default);
            Assert.Equal(StorageRelocationRecoveryStatus.ResumeRequired, result.Status);
            Assert.Equal(scenario == "capacity-unavailable" ? "RELOCATION_CAPACITY_UNAVAILABLE" : "RELOCATION_CAPACITY_INSUFFICIENT", result.Diagnostic);
            Assert.Equal(journal.Revision, (await database.Repository.LoadRelocationAsync(plan.Id, default))!.Revision);
            Assert.Equal(oldRoot, (await database.Repository.LoadAsync(plan.Id, default))!.CurrentRoot!.CanonicalPath);
            Assert.Equivalent(state.Baseline, (await database.Repository.LoadAsync(plan.Id, unit.Id, default))!.Baseline);
            Assert.Empty(Directory.EnumerateFileSystemEntries(newRoot));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(oldRoot, path.Value)));
            return;
        }
        async Task ExplicitResume()
        {
            var workflow = new StorageRelocationRecoveryWorkflow(database.Repository, scenario == "explicit-no-adapter" ? null : physical);
            if (scenario == "explicit-wrong-transaction")
            {
                await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => workflow.ResumeAsync(plan.Id, Guid.NewGuid(), physical, default));
                Assert.Equal(journal.Revision, (await database.Repository.LoadRelocationAsync(plan.Id, default))!.Revision);
                return;
            }
            if (scenario == "explicit-unknown-temp")
                await File.WriteAllBytesAsync(Path.Combine(newRoot, manifest.Entries[0].TempRelativePath.Value), bytes);
            var result = await workflow.ResumeAsync(plan.Id, transaction, physical, default);
            if (scenario == "explicit-unknown-temp")
            {
                Assert.Equal(StorageRelocationRecoveryStatus.ResumeRequired, result.Status);
                Assert.Equal(oldRoot, (await database.Repository.LoadAsync(plan.Id, default))!.CurrentRoot!.CanonicalPath);
                Assert.True(File.Exists(Path.Combine(newRoot, manifest.Entries[0].TempRelativePath.Value)));
            }
            else
            {
                Assert.Equal(scenario == "explicit-no-adapter" ? StorageRelocationRecoveryStatus.CleanupPending
                    : StorageRelocationRecoveryStatus.CompletedReservationsRetained, result.Status);
                Assert.Equal(newRoot, (await database.Repository.LoadAsync(plan.Id, default))!.CurrentRoot!.CanonicalPath);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(newRoot, path.Value)));
                Assert.Equal(scenario == "explicit-no-adapter", File.Exists(Path.Combine(oldRoot, path.Value)));
            }
            Assert.Equivalent(state.Baseline, (await database.Repository.LoadAsync(plan.Id, unit.Id, default))!.Baseline);
            await using var check = new ConfigDbContextFactory(database.Path).Create();
            Assert.Equal(2, await check.StorageRelocationRootReservations.CountAsync());
        }
        if (scenario is "explicit-prepared" or "explicit-wrong-transaction" or "explicit-no-adapter" or "explicit-unknown-temp")
        { await ExplicitResume(); return; }
        async Task<StorageRelocationRecoveryResult> Startup(bool useAdapter = true)
        {
            var startup = new ConfigDatabaseStartupCoordinator(new ConfigDatabaseSessionOpener(), new CurrentArtifactRecoveryProbe(),
                relocationCleanup: useAdapter ? physical : null);
            var result = await startup.StartAsync(new(database.Path), default);
            if (scenario == "startup-publish-conflict")
                Assert.Contains("relocation", Assert.Single(result.UnitRecoveries).Detail!);
            return Assert.Single(result.RelocationRecoveries);
        }
        if (scenario == "startup-prepared")
        {
            Assert.Equal(StorageRelocationRecoveryStatus.ResumeRequired, (await Startup()).Status);
            Assert.False(File.Exists(Path.Combine(newRoot, path.Value)));
            Assert.Equal(journal.Revision, (await database.Repository.LoadRelocationAsync(plan.Id, default))!.Revision);
            return;
        }
        if (scenario == "resume-stage-fault")
        {
            var faulty = new ConfigDbRepository(new(database.Path), new CleanupJournalFault());
            await Assert.ThrowsAsync<IOException>(() => faulty.ResumeRelocationEntryAsync(transaction, journal.Revision, version, physical, default));
            var temp = Path.Combine(newRoot, manifest.Entries[0].TempRelativePath.Value);
            Assert.True(File.Exists(temp));
            Assert.Equal(journal.Revision, (await database.Repository.LoadRelocationAsync(plan.Id, default))!.Revision);
            // 未持久化 staged identity 的文件不因 bytes/name 相同而获接纳。
            await Assert.ThrowsAsync<IOException>(() => database.Repository.ResumeRelocationEntryAsync(transaction, journal.Revision, version, physical, default));
            Assert.True(File.Exists(temp));
            Assert.True(File.Exists(Path.Combine(oldRoot, path.Value)));
            return;
        }
        journal = await database.Repository.ResumeRelocationEntryAsync(transaction, journal.Revision, version, physical, default);
        if (scenario == "explicit-staged") { await ExplicitResume(); return; }
        if (scenario == "startup-staged")
        {
            Assert.Equal(StorageRelocationRecoveryStatus.ResumeRequired, (await Startup()).Status);
            Assert.False(File.Exists(Path.Combine(newRoot, path.Value)));
            Assert.Equal(journal.Revision, (await database.Repository.LoadRelocationAsync(plan.Id, default))!.Revision);
            return;
        }
        if (scenario == "resume-publish-fault")
        {
            var faulty = new ConfigDbRepository(new(database.Path), new CleanupJournalFault());
            await Assert.ThrowsAsync<IOException>(() => faulty.ResumeRelocationEntryAsync(transaction, journal.Revision, version, physical, default));
            Assert.True(File.Exists(Path.Combine(newRoot, path.Value)));
            Assert.Equal(journal.Revision, (await database.Repository.LoadRelocationAsync(plan.Id, default))!.Revision);
        }
        journal = await database.Repository.ResumeRelocationEntryAsync(transaction, journal.Revision, version, physical, default);
        journal = await database.Repository.SealRelocationTargetsAsync(transaction, journal.Revision, default);
        if (scenario == "explicit-sealed") { await ExplicitResume(); return; }
        if (scenario == "startup-sealed")
        {
            Assert.Equal(StorageRelocationRecoveryStatus.ResumeRequired, (await Startup()).Status);
            Assert.Equal(oldRoot, (await database.Repository.LoadAsync(plan.Id, default))!.CurrentRoot!.CanonicalPath);
            Assert.Equal(journal.Revision, (await database.Repository.LoadRelocationAsync(plan.Id, default))!.Revision);
            return;
        }
        var committed = await database.Repository.CommitRelocationAsync(transaction, journal.Revision, physical, default);
        Assert.Equal(StorageTransferStage.MetadataCommitted, committed.Progress.Stage);
        Assert.Equal(newRoot, (await database.Repository.LoadAsync(plan.Id, default))!.CurrentRoot!.CanonicalPath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(oldRoot, path.Value)));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(newRoot, path.Value)));
        Assert.Equivalent(state.Baseline, (await database.Repository.LoadAsync(plan.Id, unit.Id, default))!.Baseline);
        if (scenario.StartsWith("startup-", StringComparison.Ordinal))
        {
            if (scenario == "startup-publish-conflict")
            {
                await using var context = new ConfigDbContextFactory(database.Path).Create();
                context.PublishIntents.Add(capturedPublish);
                context.PublishIntentBaselines.Add(capturedBaseline);
                await context.SaveChangesAsync();
            }
            if (scenario == "startup-absent") await physical.RemoveOldCopyAsync(committed, version, default);
            if (scenario == "startup-inactive")
            {
                await using var context = new ConfigDbContextFactory(database.Path).Create();
                (await context.PlanRegistrations.SingleAsync()).IsActive = 0;
                await context.SaveChangesAsync();
            }
            if (scenario == "startup-drift") await File.WriteAllTextAsync(Path.Combine(oldRoot, path.Value), "changed");
            if (scenario == "startup-corrupt")
            {
                await using var context = new ConfigDbContextFactory(database.Path).Create();
                context.StorageRelocationRootReservations.Remove(await context.StorageRelocationRootReservations.FirstAsync());
                await context.SaveChangesAsync();
                await Assert.ThrowsAsync<LocalStateCorruptionException>(() => Startup());
                Assert.True(File.Exists(Path.Combine(oldRoot, path.Value)));
                return;
            }
            var result = await Startup(scenario != "startup-no-adapter");
            if (scenario is "startup-no-adapter" or "startup-drift" or "startup-publish-conflict")
            {
                Assert.Equal(StorageRelocationRecoveryStatus.CleanupPending, result.Status);
                Assert.True(File.Exists(Path.Combine(oldRoot, path.Value)));
                Assert.DoesNotContain(oldRoot, result.Diagnostic!);
            }
            else
            {
                Assert.Equal(StorageRelocationRecoveryStatus.CompletedReservationsRetained, result.Status);
                Assert.False(File.Exists(Path.Combine(oldRoot, path.Value)));
                // COMPLETED 的重复启动不重新授权删除新出现的文件。
                await File.WriteAllBytesAsync(Path.Combine(oldRoot, path.Value), bytes);
                Assert.Equal(StorageRelocationRecoveryStatus.CompletedReservationsRetained, (await Startup()).Status);
                Assert.True(File.Exists(Path.Combine(oldRoot, path.Value)));
            }
            Assert.Equal(newRoot, (await database.Repository.LoadAsync(plan.Id, default))!.CurrentRoot!.CanonicalPath);
            Assert.Equivalent(state.Baseline, (await database.Repository.LoadAsync(plan.Id, unit.Id, default))!.Baseline);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(newRoot, path.Value)));
            await using var check = new ConfigDbContextFactory(database.Path).Create();
            Assert.Equal(2, await check.StorageRelocationRootReservations.CountAsync());
            return;
        }
        using var cancellation = new CancellationTokenSource();
        var cleanupStore = new CleanupObserver(physical, () => { if (scenario == "cancel-after-delete") cancellation.Cancel(); });
        if (scenario == "database-fault")
        {
            var faulty = new ConfigDbRepository(new(database.Path), new CleanupJournalFault());
            await Assert.ThrowsAsync<IOException>(() => faulty.CleanupRelocationEntryAsync(transaction, committed.Revision, version, cleanupStore, default));
            Assert.False(File.Exists(Path.Combine(oldRoot, path.Value)));
            Assert.Equal(committed.Revision, (await database.Repository.LoadRelocationAsync(plan.Id, default))!.Revision);
        }
        var reopened = await ConfigDbOpenCoordinator.OpenAsync(database.Path);
        var cleaned = await reopened.CleanupRelocationEntryAsync(transaction, committed.Revision, version, cleanupStore, cancellation.Token);
        Assert.Equal(StorageTransferArtifactStage.OldCopyAbsent, cleaned.Progress.Artifacts[0].Stage);
        Assert.False(File.Exists(Path.Combine(oldRoot, path.Value)));
        if (scenario == "old-reappeared")
        {
            await File.WriteAllBytesAsync(Path.Combine(oldRoot, path.Value), bytes);
            await Assert.ThrowsAsync<IOException>(() => reopened.CompleteRelocationAsync(transaction, cleaned.Revision, physical, default));
            Assert.True(File.Exists(Path.Combine(oldRoot, path.Value)));
            Assert.Equal(cleaned.Revision, (await reopened.LoadRelocationAsync(plan.Id, default))!.Revision);
        }
        else
        {
            var completed = await reopened.CompleteRelocationAsync(transaction, cleaned.Revision, physical, default);
            Assert.Equal(StorageTransferStage.Completed, completed.Progress.Stage);
            Assert.Equal(StorageTransferStage.Completed, (await reopened.LoadRelocationAsync(plan.Id, default))!.Progress.Stage);
        }
        Assert.Equal(newRoot, (await reopened.LoadAsync(plan.Id, default))!.CurrentRoot!.CanonicalPath);
        Assert.Equivalent(state.Baseline, (await reopened.LoadAsync(plan.Id, unit.Id, default))!.Baseline);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(newRoot, path.Value)));
        await using var db = new ConfigDbContextFactory(database.Path).Create();
        Assert.Equal(2, await db.StorageRelocationRootReservations.CountAsync());
        if (scenario == "normal" || scenario.StartsWith("compaction-", StringComparison.Ordinal))
        {
            var completed = (await reopened.LoadRelocationAsync(plan.Id, default))!;
            var oldPath = Path.Combine(oldRoot, path.Value);
            var tempPath = Path.Combine(newRoot, manifest.Entries[0].TempRelativePath.Value);
            var unknownPath = Path.Combine(oldRoot, "untracked.7z");
            await File.WriteAllTextAsync(unknownPath, "keep unknown");
            if (scenario == "compaction-old-reappeared") await File.WriteAllBytesAsync(oldPath, bytes);
            if (scenario == "compaction-temp-reappeared") await File.WriteAllBytesAsync(tempPath, bytes);
            using var compactCancellation = new CancellationTokenSource();
            IStorageRelocationCompletionProbe? completion = scenario == "compaction-missing-adapter" ? null
                : new CompletionObserver(physical, () => { if (scenario == "compaction-cancel") compactCancellation.Cancel(); });
            var repository = scenario == "compaction-database-fault" ? new ConfigDbRepository(new(database.Path), new CleanupJournalFault()) : reopened;
            var workflow = new StorageRelocationCompactionWorkflow(repository, completion);
            if (scenario == "compaction-stale-revision")
                await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => workflow.CompactAsync(plan.Id, transaction, completed.Revision - 1, default));
            else
            {
                var result = await workflow.CompactAsync(plan.Id, transaction, completed.Revision, compactCancellation.Token);
                Assert.Equal(scenario == "normal" ? StorageRelocationCompactionStatus.Compacted
                    : scenario == "compaction-missing-adapter" ? StorageRelocationCompactionStatus.AdapterUnavailable : StorageRelocationCompactionStatus.Retained, result.Status);
                if (scenario == "compaction-cancel") Assert.Equal("RELOCATION_COMPACTION_CANCELLED", result.Diagnostic);
            }
            Assert.Equal("keep unknown", await File.ReadAllTextAsync(unknownPath));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(newRoot, path.Value)));
            Assert.Equal(newRoot, (await reopened.LoadAsync(plan.Id, default))!.CurrentRoot!.CanonicalPath);
            Assert.Equivalent(state.Baseline, (await reopened.LoadAsync(plan.Id, unit.Id, default))!.Baseline);
            if (scenario != "normal")
            {
                Assert.Equal(completed.Revision, (await reopened.LoadRelocationAsync(plan.Id, default))!.Revision);
                Assert.Equal(2, await db.StorageRelocationRootReservations.CountAsync());
                Assert.Equal(scenario == "compaction-old-reappeared", File.Exists(oldPath));
                Assert.Equal(scenario == "compaction-temp-reappeared", File.Exists(tempPath));
                return;
            }
            Assert.Null(await reopened.LoadRelocationAsync(plan.Id, default));
            Assert.Equal(0, await db.StorageRelocationRootReservations.CountAsync());
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(newRoot, path.Value)));
            Assert.Equivalent(state.Baseline, (await reopened.LoadAsync(plan.Id, unit.Id, default))!.Baseline);
            Assert.Equal(StorageRelocationCompactionStatus.NotFound,
                (await workflow.CompactAsync(plan.Id, transaction, completed.Revision, default)).Status);
        }
    }

    private sealed class CompletionObserver(IStorageRelocationCompletionProbe inner, Action afterCheck) : IStorageRelocationCompletionProbe
    {
        public async Task VerifyCompletedAsync(StorageRelocationJournal journal, CancellationToken token)
        {
            await inner.VerifyCompletedAsync(journal, token);
            afterCheck();
        }
    }

    private sealed class BeginFault : IMetadataCommitFaultInjector
    {
        public void ThrowIfRequested(MetadataCommitFaultPoint point)
        {
            if (point == MetadataCommitFaultPoint.AfterRelocationIntent) throw new IOException("injected Begin failure");
        }
    }

    // 真实 SQLite 提交后注入响应丢失/取消；其余方法故意拒绝，证明 Begin 不自动恢复或重试。
    private sealed class BeginResponseStore(IStorageRelocationJournalStore inner, string scenario, CancellationTokenSource cancellation) : IStorageRelocationJournalStore
    {
        public int Calls { get; private set; }
        public async Task<StorageRelocationJournal> BeginRelocationAsync(StorageRelocationManifest manifest, StorageRelocationConfigurationObservation configuration, CancellationToken token)
        {
            Calls++;
            var result = await inner.BeginRelocationAsync(manifest, configuration, token);
            if (scenario == "begin-lost-response") throw new IOException("response lost after commit");
            if (scenario == "begin-cancel-after-commit") cancellation.Cancel();
            return result;
        }
        public Task<StorageRelocationJournal?> LoadRelocationAsync(PlanId planId, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> CommitRelocationAsync(Guid transactionId, long expectedRevision, IStorageRelocationPhysicalStore physical, CancellationToken token) => throw new NotSupportedException();
        public Task CompactRelocationAsync(Guid transactionId, long expectedRevision, IStorageRelocationCompletionProbe physical, CancellationToken token) => throw new NotSupportedException();
        public Task<ImmutableArray<StorageRelocationJournal>> ListRelocationsAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> BeginRelocationAsync(StorageRelocationManifest manifest, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> ResumeRelocationEntryAsync(Guid transactionId, long expectedRevision, ArchiveVersionId versionId, IStorageRelocationPhysicalStore physical, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> CleanupRelocationEntryAsync(Guid transactionId, long expectedRevision, ArchiveVersionId versionId, IStorageRelocationOldCopyStore physical, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> CompleteRelocationAsync(Guid transactionId, long expectedRevision, IStorageRelocationOldCopyStore physical, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> RecordRelocationStagedAsync(Guid transactionId, long expectedRevision, StorageTransferProof proof, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> RecordRelocationTargetAsync(Guid transactionId, long expectedRevision, StorageTransferProof proof, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> SealRelocationTargetsAsync(Guid transactionId, long expectedRevision, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class BeginBarrier(Func<Task> callback, bool completed) : StowCrate.Application.Publishing.IArchivePublishMetadataDurabilityBarrier
    {
        public async Task<StowCrate.Application.Publishing.PublishMetadataDurabilityProof> FlushDirectoryMetadataAsync(string destinationDirectory, CancellationToken cancellationToken)
        {
            await callback();
            return new(completed, "test-begin");
        }
    }

    private sealed class CleanupJournalFault : IMetadataCommitFaultInjector
    {
        public void ThrowIfRequested(MetadataCommitFaultPoint point)
        { if (point == MetadataCommitFaultPoint.AfterRelocationProgress) throw new IOException("injected journal failure after deletion"); }
    }

    private sealed class CleanupObserver(IStorageRelocationOldCopyStore inner, Action afterProof) : IStorageRelocationOldCopyStore
    {
        public async Task<StorageRelocationOldCopyAbsenceProof> RemoveOldCopyAsync(StorageRelocationJournal journal, ArchiveVersionId version, CancellationToken token)
        {
            var proof = await inner.RemoveOldCopyAsync(journal, version, token);
            afterProof();
            return proof;
        }
    }

    // 组合流程测试使用真实文件/SQLite，但注入 barrier，不声称证明平台突然断电持久性。
    private sealed class RelocationTestBarrier : StowCrate.Application.Publishing.IArchivePublishMetadataDurabilityBarrier
    {
        public Task<StowCrate.Application.Publishing.PublishMetadataDurabilityProof> FlushDirectoryMetadataAsync(string path, CancellationToken token)
            => Task.FromResult(new StowCrate.Application.Publishing.PublishMetadataDurabilityProof(true, "test-only"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RelocationConfigurationReadRequiresNeitherInputBindingsNorSecrets(bool fileBacked)
    {
        await using var database = await WorkflowDatabase.Create(registerDefaultPlan: false);
        var source = new BackupPlanDocumentSource(); var basis = await ReadFixturePlan();
        var slot = new SecretSlotId(Guid.NewGuid());
        var plan = new PortableBackupPlan(basis.Id, basis.Name, basis.Description, basis.Semantics, basis.Sources,
            basis.GlobalRules, basis.PlanRules, basis.ArchiveSpecDefault with { Protection = new SecureProtection(slot) },
            [new FileManagedArchiveUnit(basis.ArchiveUnits[0].Id, basis.Sources[0].Id, basis.ArchiveUnits[0].Path, null, null)],
            [new(slot, "offline-secret")], basis.LinkPolicy, basis.ChangeDetection, basis.HistoryDefault, basis.Schedule, basis.ExternalSources);
        var workflow = new AuthoritativePlanWorkflow(database.Repository, source);
        if (fileBacked)
        {
            var path = Path.Combine(database.DirectoryPath, "relocation.backupplan");
            await File.WriteAllBytesAsync(path, source.Write(plan).CanonicalUtf8Payload.ToArray());
            await workflow.RegisterFileBackedAsync(path, default);
        }
        else await workflow.CreateManagedAsync(plan, default);
        var observation = await new StorageRelocationConfigurationReader(workflow).ReadAsync(plan.Id, default);
        Assert.Equal(plan.Id, observation.Snapshot.Plan.Id);
        Assert.IsType<SecureProtection>(observation.Snapshot.Plan.ArchiveSpecDefault.Protection);
        Assert.IsType<FileManagedArchiveUnit>(observation.Snapshot.Plan.ArchiveUnits[0]);
        // 根本未提供 Source/Secret binding；这只证明配置读取不依赖备份 readiness，不代表迁移整体已就绪。
        var bindings = await database.Repository.LoadAsync(plan.Id, default);
        Assert.True(bindings is null || bindings.Sources.IsEmpty);
        Assert.Empty(await ((ISecretBindingMetadataStore)database.Repository).LoadAsync(plan.Id, default));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("identity")]
    public async Task RelocationConfigurationNeverFallsBackAfterFileChanges(string drift)
    {
        await using var database = await WorkflowDatabase.Create(registerDefaultPlan: false);
        var source = new BackupPlanDocumentSource(); var plan = await ReadFixturePlan();
        var workflow = new AuthoritativePlanWorkflow(database.Repository, source);
        var path = Path.Combine(database.DirectoryPath, "relocation.backupplan");
        await File.WriteAllBytesAsync(path, source.Write(plan).CanonicalUtf8Payload.ToArray());
        await workflow.RegisterFileBackedAsync(path, default);
        var reader = new StorageRelocationConfigurationReader(workflow);
        var captured = await reader.ReadAsync(plan.Id, default);
        await File.WriteAllBytesAsync(path, source.Write(CopyWithName(plan, "renamed during relocation")).CanonicalUtf8Payload.ToArray());
        Assert.Equal("renamed during relocation", (await reader.RevalidateAsync(captured, default)).Snapshot.Plan.Name);
        if (drift == "missing") File.Move(path, path + ".unavailable");
        else if (drift == "invalid") await File.WriteAllTextAsync(path, "{ invalid");
        else await File.WriteAllBytesAsync(path, source.Write(CloneWithNewIdentities(plan)).CanonicalUtf8Payload.ToArray());
        await Assert.ThrowsAsync<BackupPlanDocumentSourceException>(() => reader.RevalidateAsync(captured, default));
    }

    [Fact]
    public async Task RelocationConfigurationRereadsSemanticChangesAndRejectsInactivePlan()
    {
        await using var database = await WorkflowDatabase.Create(); var (plan, _) = await database.RegisterFixturePlan();
        var workflow = new AuthoritativePlanWorkflow(database.Repository, new BackupPlanDocumentSource());
        var reader = new StorageRelocationConfigurationReader(workflow);
        var before = await reader.ReadAsync(plan.Id, default);
        await workflow.UpdateManagedAsync(CopyWithName(plan, plan.Name + " changed"), before.Snapshot.ManagedRevision!.Value, default);
        var after = await reader.ReadAsync(plan.Id, default);
        Assert.NotEqual(before.ConfigurationFingerprint, after.ConfigurationFingerprint);
        Assert.NotEqual(before.Snapshot.ManagedRevision, after.Snapshot.ManagedRevision);
        Assert.Equal(after.ConfigurationFingerprint, (await reader.RevalidateAsync(before, default)).ConfigurationFingerprint);
        var changedLayout = new PortableBackupPlan(plan.Id, plan.Name, plan.Description, plan.Semantics,
            [plan.Sources[0] with { SourceOutputPath = new("changed-output") }], plan.GlobalRules, plan.PlanRules,
            plan.ArchiveSpecDefault, plan.ArchiveUnits, plan.SecretSlots, plan.LinkPolicy, plan.ChangeDetection,
            plan.HistoryDefault, plan.Schedule, plan.ExternalSources);
        await workflow.UpdateManagedAsync(changedLayout, after.Snapshot.ManagedRevision!.Value, default);
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => reader.RevalidateAsync(before, default));
        await workflow.SetActiveAsync(plan.Id, false, default);
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => reader.ReadAsync(plan.Id, default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(plan.Id, cancelled.Token));
    }

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

    [Fact]
    public async Task RetentionDeletionIntentAtomicallyRemovesPlacementAndPreservesArchiveVersion()
    {
        await using var database = await WorkflowDatabase.Create(); var (plan, unit) = await database.RegisterFixturePlan(); var f = Fingerprints();
        var first = Intent(plan.Id, unit.Id, new(Guid.NewGuid()), Sha256Digest.Hash("old"u8));
        await database.Repository.BeginPublishAsync(first, CancellationToken.None); var firstPublished = first.MarkCurrentPublished(DateTimeOffset.UnixEpoch);
        await database.Repository.SavePublishProgressAsync(firstPublished, CancellationToken.None); await database.Repository.CompleteMetadataCommitAsync(firstPublished.RebuildMetadataCommitPlan(), CancellationToken.None);
        var oldState = (await database.Repository.LoadAsync(plan.Id, unit.Id, CancellationToken.None))!;
        var nextArchive = ArchiveVersion.Prepare(new(Guid.NewGuid()), plan.Id, unit.Id, PortableArchiveFormat.SevenZip, f.ArchiveSpec).Verify(Sha256Digest.Hash("new"u8), 10);
        var next = PendingPublishIntent.Prepare(nextArchive, new("unit.7z"), BaselineCandidate.FromCompleteCandidate(f), f.OutputLayout,
            new(oldState.CurrentArchive!, oldState.Current!), HistoryCaptureRequirement.Required);
        await database.Repository.BeginPublishAsync(next, CancellationToken.None);
        var placement = new HistoryVersionPlacement(plan.Id, unit.Id, oldState.CurrentArchive!.Id, new($"history-v1/{unit.Id.Value:D}/old.7z"));
        var captured = next.MarkHistoryCaptured(new(oldState.CurrentArchive.Id, oldState.CurrentArchive.Integrity!.Value, placement));
        await database.Repository.SavePublishProgressAsync(captured, CancellationToken.None); var currentPublished = captured.MarkCurrentPublished(DateTimeOffset.FromUnixTimeSeconds(1));
        await database.Repository.SavePublishProgressAsync(currentPublished, CancellationToken.None); await database.Repository.CompleteMetadataCommitAsync(currentPublished.RebuildMetadataCommitPlan(), CancellationToken.None);

        var snapshot = await database.Repository.LoadRetentionSnapshotAsync(plan.Id, unit.Id, CancellationToken.None); var victim = Assert.Single(snapshot.Entries);
        await database.Repository.BeginDeletionIntentsAsync(new(Guid.NewGuid()), plan.Id, unit.Id, 1, [victim], CancellationToken.None);
        var intent = Assert.Single(await database.Repository.ListDeletionIntentsAsync(false, CancellationToken.None));
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => database.Repository.CompleteDeletionAsync(
            intent with { SelectionId = new(Guid.NewGuid()) }, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Single((await database.Repository.LoadAsync(plan.Id, unit.Id, CancellationToken.None))!.History);
        Assert.Equal(RetentionDeletionStage.Prepared, Assert.Single(await database.Repository.ListDeletionIntentsAsync(false, CancellationToken.None)).Stage);
        var faulty = new ConfigDbRepository(new ConfigDbContextFactory(database.Path), new ThrowAtRetentionCompletion());
        await Assert.ThrowsAsync<InvalidOperationException>(() => faulty.CompleteDeletionAsync(intent, DateTimeOffset.UtcNow, CancellationToken.None));
        var reopened = await ConfigDbOpenCoordinator.OpenAsync(database.Path);
        Assert.Single((await reopened.LoadAsync(plan.Id, unit.Id, CancellationToken.None))!.History);
        Assert.Equal(RetentionDeletionStage.Prepared, Assert.Single(await reopened.ListDeletionIntentsAsync(false, CancellationToken.None)).Stage);
        await database.Repository.CompleteDeletionAsync(intent, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Empty((await database.Repository.LoadAsync(plan.Id, unit.Id, CancellationToken.None))!.History);
        Assert.Equal(ArchiveVersionLifecycle.Superseded, victim.Archive.Lifecycle);
        Assert.Equal(RetentionDeletionStage.Completed, Assert.Single(await database.Repository.ListDeletionIntentsAsync(true, CancellationToken.None)).Stage);

        var factory = new ConfigDbContextFactory(database.Path);
        await using (var context = factory.Create())
        {
            context.HistoryVersionPlacements.Add(new()
            {
                ArchiveVersionId = DurableCodecs.Uuid(victim.Archive.Id.Value),
                PlanId = DurableCodecs.Uuid(plan.Id.Value),
                ArchiveUnitId = DurableCodecs.Uuid(unit.Id.Value),
                HistoryRelativePath = victim.Placement.RelativePath.Value
            });
            await context.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => database.Repository.CompactCompletedDeletionIntentsAsync(
            [victim.Archive.Id], CancellationToken.None));
        Assert.Equal(RetentionDeletionStage.Completed,
            Assert.Single(await database.Repository.ListDeletionIntentsAsync(true, CancellationToken.None)).Stage);
    }

    [Fact]
    public async Task RetentionVictimAuthorizationRollsBackWholeSelectionWhenOnePlacementDrifts()
    {
        await using var database = await WorkflowDatabase.Create(); var (plan, unit) = await database.RegisterFixturePlan(); var f = Fingerprints();
        var versions = new[]
        {
            ArchiveVersion.Prepare(new(Guid.NewGuid()), plan.Id, unit.Id, PortableArchiveFormat.SevenZip, f.ArchiveSpec).Verify(Sha256Digest.Hash("one"u8), 3).Publish(DateTimeOffset.UnixEpoch).Supersede(),
            ArchiveVersion.Prepare(new(Guid.NewGuid()), plan.Id, unit.Id, PortableArchiveFormat.SevenZip, f.ArchiveSpec).Verify(Sha256Digest.Hash("two"u8), 3).Publish(DateTimeOffset.FromUnixTimeSeconds(1)).Supersede(),
        };
        var factory = new ConfigDbContextFactory(database.Path);
        await using (var context = factory.Create())
        {
            foreach (var version in versions)
            {
                context.ArchiveVersions.Add(new()
                {
                    ArchiveVersionId = DurableCodecs.Uuid(version.Id.Value),
                    PlanId = DurableCodecs.Uuid(plan.Id.Value),
                    ArchiveUnitId = DurableCodecs.Uuid(unit.Id.Value),
                    ArchiveFormat = "SEVEN_ZIP",
                    ArchiveSpecFingerprint = DurableCodecs.Digest(version.ArchiveSpecFingerprint.Digest),
                    Lifecycle = "SUPERSEDED",
                    IntegritySha256 = DurableCodecs.Digest(version.Integrity!.Value),
                    Length = version.Length,
                    PublishedAtUtcMs = DurableCodecs.Utc(version.PublishedAtUtc!.Value)
                });
                context.HistoryVersionPlacements.Add(new() { ArchiveVersionId = DurableCodecs.Uuid(version.Id.Value), PlanId = DurableCodecs.Uuid(plan.Id.Value), ArchiveUnitId = DurableCodecs.Uuid(unit.Id.Value), HistoryRelativePath = $"history-v1/{unit.Id.Value:D}/{version.Id.Value:D}.7z" });
            }
            await context.SaveChangesAsync();
        }
        var snapshot = await database.Repository.LoadRetentionSnapshotAsync(plan.Id, unit.Id, CancellationToken.None);
        await using (var context = factory.Create())
        {
            var drifted = await context.HistoryVersionPlacements.SingleAsync(x => x.ArchiveVersionId == DurableCodecs.Uuid(versions[1].Id.Value));
            drifted.HistoryRelativePath = "history-v1/drifted.7z"; await context.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => database.Repository.BeginDeletionIntentsAsync(
            new(Guid.NewGuid()), plan.Id, unit.Id, 1, snapshot.Entries, CancellationToken.None));
        Assert.Empty(await database.Repository.ListDeletionIntentsAsync(true, CancellationToken.None));
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
        public string DirectoryPath { get; }
        public string Path { get; }
        public ConfigDbRepository Repository { get; }
        public static async Task<WorkflowDatabase> Create(bool registerDefaultPlan = true)
        {
            var directory = Directory.CreateTempSubdirectory("stowcrate-workflow-"); var path = System.IO.Path.Combine(directory.FullName, "config.db"); var repository = await ConfigDbOpenCoordinator.OpenAsync(path, Guid.NewGuid(), new DeviceId(Guid.NewGuid())); var value = new WorkflowDatabase(directory.FullName, path, repository); if (registerDefaultPlan) await value.RegisterFixturePlan(); return value;
        }
        public async Task<(PortableBackupPlan Plan, AuthoredArchiveUnit Unit)> RegisterFixturePlan() { var plan = await ReadFixturePlan(); var source = new BackupPlanDocumentSource(); var existing = await ((IPlanRegistrationStore)Repository).LoadAsync(plan.Id, CancellationToken.None); if (existing is null) await new AuthoritativePlanWorkflow(Repository, source).CreateManagedAsync(plan, CancellationToken.None); return (plan, plan.ArchiveUnits[0]); }
        public ValueTask DisposeAsync() { Directory.Delete(DirectoryPath, recursive: true); return ValueTask.CompletedTask; }
    }
    private sealed class ThrowAtRetentionCompletion : IMetadataCommitFaultInjector
    { public void ThrowIfRequested(MetadataCommitFaultPoint point) { if (point is MetadataCommitFaultPoint.AfterRetentionCompletionMutation) throw new InvalidOperationException("injected"); } }
}
