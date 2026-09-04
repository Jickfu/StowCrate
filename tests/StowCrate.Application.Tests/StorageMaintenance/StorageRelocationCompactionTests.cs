using System.Collections.Immutable;
using StowCrate.Application.LocalState;
using StowCrate.Application.StorageMaintenance;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Application.Tests.StorageMaintenance;

public sealed class StorageRelocationCompactionTests
{
    [Theory]
    [InlineData("success", StorageRelocationCompactionStatus.Compacted)]
    [InlineData("cancel-after-success", StorageRelocationCompactionStatus.Compacted)]
    [InlineData("before", StorageRelocationCompactionStatus.Retained)]
    [InlineData("cancel-before", StorageRelocationCompactionStatus.Retained)]
    [InlineData("after", StorageRelocationCompactionStatus.OutcomeUnknown)]
    [InlineData("reload-unavailable", StorageRelocationCompactionStatus.OutcomeUnknown)]
    [InlineData("replaced", StorageRelocationCompactionStatus.OutcomeUnknown)]
    [InlineData("revision-changed", StorageRelocationCompactionStatus.OutcomeUnknown)]
    public async Task CompactionResponseBoundariesNeverReplay(string failure, StorageRelocationCompactionStatus expected)
    {
        using var cancellation = new CancellationTokenSource();
        var journal = Journal(StorageTransferStage.Completed);
        var store = new Store(journal, failure, cancellation);
        var result = await new StorageRelocationCompactionWorkflow(store, new Physical()).CompactAsync(journal.Manifest.PlanId,
            journal.Manifest.TransactionId, journal.Revision, cancellation.Token);
        Assert.Equal(expected, result.Status);
        Assert.Equal(journal.Manifest.TransactionId, result.TransactionId);
        Assert.Equal(journal.Revision, result.ExpectedRevision);
        Assert.Equal(1, store.CompactCalls);
        Assert.Equal(expected == StorageRelocationCompactionStatus.Compacted ? 1 : 2, store.LoadCalls);
        if (failure == "cancel-before") Assert.Equal("RELOCATION_COMPACTION_CANCELLED", result.Diagnostic);
        if (failure == "before") Assert.Equal("RELOCATION_COMPACTION_REQUIRES_RECONCILIATION", result.Diagnostic);
    }

    [Theory]
    [InlineData(StorageTransferStage.Prepared)]
    [InlineData(StorageTransferStage.TargetsDurable)]
    [InlineData(StorageTransferStage.MetadataCommitted)]
    public async Task IncompleteJournalIsNotAutomaticallyAdvanced(StorageTransferStage stage)
    {
        using var cancellation = new CancellationTokenSource();
        var journal = Journal(stage);
        var store = new Store(journal, "success", cancellation);
        var result = await new StorageRelocationCompactionWorkflow(store, new Physical()).CompactAsync(journal.Manifest.PlanId,
            journal.Manifest.TransactionId, journal.Revision, default);
        Assert.Equal(StorageRelocationCompactionStatus.NotReady, result.Status);
        Assert.Equal(0, store.CompactCalls);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("transaction")]
    [InlineData("revision")]
    public async Task StaleSelectionCannotReleaseAnotherJournal(string mismatch)
    {
        using var cancellation = new CancellationTokenSource();
        var journal = Journal(StorageTransferStage.Completed);
        var store = new Store(journal, "success", cancellation);
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => new StorageRelocationCompactionWorkflow(store, new Physical()).CompactAsync(
            mismatch == "plan" ? new(Guid.NewGuid()) : journal.Manifest.PlanId,
            mismatch == "transaction" ? Guid.NewGuid() : journal.Manifest.TransactionId,
            mismatch == "revision" ? journal.Revision + 1 : journal.Revision, default));
        Assert.Equal(0, store.CompactCalls);
    }

    [Theory]
    [InlineData("missing", StorageRelocationCompactionStatus.NotFound)]
    [InlineData("adapter", StorageRelocationCompactionStatus.AdapterUnavailable)]
    public async Task MissingJournalOrAdapterNeverImpliesSuccessfulCompaction(string missing, StorageRelocationCompactionStatus expected)
    {
        using var cancellation = new CancellationTokenSource();
        var journal = Journal(StorageTransferStage.Completed);
        var store = new Store(missing == "missing" ? null : journal, "success", cancellation);
        var result = await new StorageRelocationCompactionWorkflow(store, null).CompactAsync(journal.Manifest.PlanId,
            journal.Manifest.TransactionId, journal.Revision, default);
        Assert.Equal(expected, result.Status);
        Assert.Equal(0, store.CompactCalls);
    }

    [Theory]
    [InlineData("initial-corrupt", 0)]
    [InlineData("reload-corrupt", 1)]
    public async Task CorruptionIsNeverReportedAsOrdinaryRetainedState(string failure, int expectedCalls)
    {
        using var cancellation = new CancellationTokenSource();
        var journal = Journal(StorageTransferStage.Completed);
        var store = new Store(journal, failure, cancellation);
        await Assert.ThrowsAsync<LocalStateCorruptionException>(() => new StorageRelocationCompactionWorkflow(store, new Physical()).CompactAsync(
            journal.Manifest.PlanId, journal.Manifest.TransactionId, journal.Revision, default));
        Assert.Equal(expectedCalls, store.CompactCalls);
    }

    private static StorageRelocationJournal Journal(StorageTransferStage stage)
    {
        var transaction = Guid.NewGuid(); var plan = new PlanId(Guid.NewGuid());
        var manifest = new StorageRelocationManifest(transaction, plan, new(Guid.NewGuid()), Sha256Digest.Hash("test"u8),
            [new(StorageRootKind.Current, new("/old", "/old"), new("/new", "/new"), new("test", 1, "old"), new("test", 1, "new"))], []);
        var progress = StorageTransferProgress.Prepare(transaction, plan, []);
        if (stage != StorageTransferStage.Prepared) progress = progress.SealTargets();
        if (stage is StorageTransferStage.MetadataCommitted or StorageTransferStage.Completed) progress = progress.MarkMetadataCommitted();
        if (stage == StorageTransferStage.Completed) progress = progress.Complete();
        return new(manifest, progress, 4);
    }

    private sealed class Physical : IStorageRelocationCompletionProbe
    {
        public Task VerifyCompletedAsync(StorageRelocationJournal journal, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // 仅模拟事务响应丢失；真实物理重验与 SQLite 回滚由 Infrastructure 组合测试验证。
    private sealed class Store(StorageRelocationJournal? initial, string failure, CancellationTokenSource cancellation) : IStorageRelocationJournalStore
    {
        private StorageRelocationJournal? journal = initial;
        private readonly StorageRelocationJournal? original = initial;
        public int LoadCalls { get; private set; }
        public int CompactCalls { get; private set; }
        public Task<StorageRelocationJournal?> LoadRelocationAsync(PlanId planId, CancellationToken token)
        {
            LoadCalls++;
            token.ThrowIfCancellationRequested();
            if (failure == "initial-corrupt" || CompactCalls > 0 && failure == "reload-corrupt") throw new LocalStateCorruptionException("corrupt");
            if (CompactCalls > 0 && failure == "reload-unavailable") throw new LocalStateRepositoryException("unavailable");
            if (CompactCalls > 0) Assert.False(token.CanBeCanceled);
            return Task.FromResult(journal);
        }
        public Task CompactRelocationAsync(Guid transactionId, long expectedRevision, IStorageRelocationCompletionProbe physical, CancellationToken token)
        {
            CompactCalls++;
            Assert.Equal(original!.Manifest.TransactionId, transactionId);
            Assert.Equal(original.Revision, expectedRevision);
            if (failure is "success" or "after" or "cancel-after-success") journal = null;
            if (failure is "cancel-before" or "cancel-after-success") cancellation.Cancel();
            if (failure == "cancel-before") token.ThrowIfCancellationRequested();
            if (failure == "replaced") journal = Journal(StorageTransferStage.Completed);
            if (failure == "revision-changed") journal = original with { Revision = original.Revision + 1 };
            if (failure is "success" or "cancel-after-success") return Task.CompletedTask;
            throw new IOException("response lost with private path details");
        }
        public Task<ImmutableArray<StorageRelocationJournal>> ListRelocationsAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> BeginRelocationAsync(StorageRelocationManifest manifest, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> BeginRelocationAsync(StorageRelocationManifest manifest, StorageRelocationConfigurationObservation configuration, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> ResumeRelocationEntryAsync(Guid transactionId, long expectedRevision, ArchiveVersionId versionId, IStorageRelocationPhysicalStore physical, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> CleanupRelocationEntryAsync(Guid transactionId, long expectedRevision, ArchiveVersionId versionId, IStorageRelocationOldCopyStore physical, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> CompleteRelocationAsync(Guid transactionId, long expectedRevision, IStorageRelocationOldCopyStore physical, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> CommitRelocationAsync(Guid transactionId, long expectedRevision, IStorageRelocationPhysicalStore physical, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> RecordRelocationStagedAsync(Guid transactionId, long expectedRevision, StorageTransferProof proof, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> RecordRelocationTargetAsync(Guid transactionId, long expectedRevision, StorageTransferProof proof, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> SealRelocationTargetsAsync(Guid transactionId, long expectedRevision, CancellationToken token) => throw new NotSupportedException();
    }
}
