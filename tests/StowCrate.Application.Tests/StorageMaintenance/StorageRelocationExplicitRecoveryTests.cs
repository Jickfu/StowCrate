using System.Collections.Immutable;
using StowCrate.Application.LocalState;
using StowCrate.Application.StorageMaintenance;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Application.Tests.StorageMaintenance;

public sealed class StorageRelocationExplicitRecoveryTests
{
    [Theory]
    [InlineData("before", StorageRelocationRecoveryStatus.ResumeRequired)]
    [InlineData("after", StorageRelocationRecoveryStatus.CleanupPending)]
    [InlineData("reload-unavailable", StorageRelocationRecoveryStatus.OutcomeUnknown)]
    [InlineData("cancel-after", StorageRelocationRecoveryStatus.CleanupPending)]
    public async Task LostResponseIsClassifiedByDurableStateWithoutReplay(string failure, StorageRelocationRecoveryStatus expected)
    {
        using var cancellation = new CancellationTokenSource();
        var journal = SealedJournal();
        var store = new Store(journal, failure, cancellation);
        var result = await new StorageRelocationRecoveryWorkflow(store, null).ResumeAsync(journal.Manifest.PlanId,
            journal.Manifest.TransactionId, new Physical(), cancellation.Token);
        Assert.Equal(expected, result.Status);
        Assert.Equal(1, store.CommitCalls);
        Assert.Equal(journal.Manifest.TransactionId, result.TransactionId);
    }

    [Fact]
    public async Task CorruptReloadIsNotDowngradedToOrdinaryPending()
    {
        using var cancellation = new CancellationTokenSource();
        var journal = SealedJournal();
        var store = new Store(journal, "reload-corrupt", cancellation);
        await Assert.ThrowsAsync<LocalStateCorruptionException>(() => new StorageRelocationRecoveryWorkflow(store, null)
            .ResumeAsync(journal.Manifest.PlanId, journal.Manifest.TransactionId, new Physical(), default));
        Assert.Equal(1, store.CommitCalls);
    }

    private static StorageRelocationJournal SealedJournal()
    {
        var transaction = Guid.NewGuid(); var plan = new PlanId(Guid.NewGuid());
        var manifest = new StorageRelocationManifest(transaction, plan, new(Guid.NewGuid()), Sha256Digest.Hash("test"u8),
            [new(StorageRootKind.Current, new("/old", "/old"), new("/new", "/new"), new("test", 1, "old"), new("test", 1, "new"))], []);
        return new(manifest, StorageTransferProgress.Prepare(transaction, plan, []).SealTargets(), 2);
    }

    private sealed class Physical : IStorageRelocationPhysicalStore
    {
        public Task<StorageTransferProof> StageAsync(StorageRelocationJournal journal, ArchiveVersionId versionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageTransferProof> PublishTargetAsync(StorageRelocationJournal journal, ArchiveVersionId versionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task VerifyForCommitAsync(StorageRelocationJournal journal, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // 只模拟提交响应丢失边界；真实 SQLite/文件系统的物理语义由组合测试覆盖。
    private sealed class Store(StorageRelocationJournal initial, string failure, CancellationTokenSource cancellation) : IStorageRelocationJournalStore
    {
        private StorageRelocationJournal journal = initial;
        public int CommitCalls { get; private set; }
        public Task<StorageRelocationJournal?> LoadRelocationAsync(PlanId planId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (CommitCalls > 0 && failure == "reload-unavailable") throw new LocalStateRepositoryException("unavailable");
            if (CommitCalls > 0 && failure == "reload-corrupt") throw new LocalStateCorruptionException("corrupt");
            return Task.FromResult<StorageRelocationJournal?>(journal);
        }
        public Task<StorageRelocationJournal> CommitRelocationAsync(Guid transactionId, long expectedRevision, IStorageRelocationPhysicalStore physical, CancellationToken token)
        {
            CommitCalls++;
            if (failure is "after" or "cancel-after") journal = journal with { Progress = journal.Progress.MarkMetadataCommitted(), Revision = journal.Revision + 1 };
            if (failure == "cancel-after") { cancellation.Cancel(); return Task.FromResult(journal); }
            throw new IOException("response lost");
        }
        public Task CompactRelocationAsync(Guid transactionId, long expectedRevision, IStorageRelocationCompletionProbe physical, CancellationToken token) => throw new NotSupportedException();
        public Task<ImmutableArray<StorageRelocationJournal>> ListRelocationsAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> BeginRelocationAsync(StorageRelocationManifest manifest, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> BeginRelocationAsync(StorageRelocationManifest manifest, StorageRelocationConfigurationObservation configuration, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> ResumeRelocationEntryAsync(Guid transactionId, long expectedRevision, ArchiveVersionId versionId, IStorageRelocationPhysicalStore physical, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> CleanupRelocationEntryAsync(Guid transactionId, long expectedRevision, ArchiveVersionId versionId, IStorageRelocationOldCopyStore physical, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> CompleteRelocationAsync(Guid transactionId, long expectedRevision, IStorageRelocationOldCopyStore physical, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> RecordRelocationStagedAsync(Guid transactionId, long expectedRevision, StorageTransferProof proof, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> RecordRelocationTargetAsync(Guid transactionId, long expectedRevision, StorageTransferProof proof, CancellationToken token) => throw new NotSupportedException();
        public Task<StorageRelocationJournal> SealRelocationTargetsAsync(Guid transactionId, long expectedRevision, CancellationToken token) => throw new NotSupportedException();
    }
}
