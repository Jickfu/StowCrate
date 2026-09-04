using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using StowCrate.Application.LocalState;
using StowCrate.Application.StorageMaintenance;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Infrastructure.Persistence.ConfigDb;

public sealed partial class ConfigDbRepository
{
    public async Task CompactRelocationAsync(Guid transactionId, long expectedRevision,
        IStorageRelocationCompletionProbe physical, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(physical);
        await using var db = factory.Create();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var id = DurableCodecs.Uuid(transactionId);
            var row = await db.StorageRelocationIntents.SingleOrDefaultAsync(x => x.TransactionId == id, cancellationToken)
                ?? throw new LocalStateConcurrencyException("Relocation journal does not exist.");
            if (row.Revision != expectedRevision) throw new LocalStateConcurrencyException("Relocation revision changed.");
            var journal = await ReadRelocationAsync(db, row, cancellationToken);
            if (journal.Progress.Stage != StorageTransferStage.Completed)
                throw new LocalStateConcurrencyException("Relocation compaction requires completed cleanup.");
            await ValidateRelocationPlacementsAsync(db, journal.Manifest, cancellationToken);
            await ValidateCommitRootsAsync(db, journal.Manifest, cancellationToken, committed: true);
            if (await db.PublishIntents.AnyAsync(x => x.PlanId == row.PlanId, cancellationToken)
                || await db.RetentionDeletionIntents.AnyAsync(x => x.PlanId == row.PlanId, cancellationToken)
                || await db.MaintenanceStates.AnyAsync(x => x.PlanId == row.PlanId && x.Status != "COMPLETED" && x.Kind != "SCHEDULE_RECONCILIATION", cancellationToken))
                throw new LocalStateConcurrencyException("Relocation compaction conflicts with pending maintenance.");
            await physical.VerifyCompletedAsync(journal, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // reservation 与日志必须同事务移除；FK restrict 要求先删除 dependent rows。
            db.StorageRelocationRootReservations.RemoveRange(await db.StorageRelocationRootReservations.Where(x => x.TransactionId == id).ToListAsync(cancellationToken));
            await db.SaveChangesAsync(cancellationToken);
            faultInjector.ThrowIfRequested(MetadataCommitFaultPoint.AfterRelocationProgress);
            db.StorageRelocationIntents.Remove(row);
            await db.SaveChangesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await tx.CommitAsync(CancellationToken.None);
        }
        catch (Exception exception) { throw TranslateRelocation(exception); }
    }

    public Task<StorageRelocationJournal> CleanupRelocationEntryAsync(Guid transactionId, long expectedRevision,
        ArchiveVersionId versionId, IStorageRelocationOldCopyStore physical, CancellationToken cancellationToken)
        => CleanupRelocationAsync(transactionId, expectedRevision, versionId, physical, cancellationToken);

    public Task<StorageRelocationJournal> CompleteRelocationAsync(Guid transactionId, long expectedRevision,
        IStorageRelocationOldCopyStore physical, CancellationToken cancellationToken)
        => CleanupRelocationAsync(transactionId, expectedRevision, null, physical, cancellationToken);

    private async Task<StorageRelocationJournal> CleanupRelocationAsync(Guid transactionId, long expectedRevision,
        ArchiveVersionId? versionId, IStorageRelocationOldCopyStore physical, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(physical);
        await using var db = factory.Create();
        await using var tx = await db.Database.BeginTransactionAsync(token);
        try
        {
            var id = DurableCodecs.Uuid(transactionId);
            var row = await db.StorageRelocationIntents.SingleOrDefaultAsync(x => x.TransactionId == id, token)
                ?? throw new LocalStateConcurrencyException("Relocation journal does not exist.");
            if (row.Revision != expectedRevision) throw new LocalStateConcurrencyException("Relocation revision changed.");
            var journal = await ReadRelocationAsync(db, row, token);
            if (journal.Progress.Stage != StorageTransferStage.MetadataCommitted)
                throw new LocalStateConcurrencyException("Relocation cleanup requires committed metadata.");
            if (versionId is { } requested
                ? !journal.Progress.Artifacts.Any(x => x.Artifact.VersionId == requested && x.Stage == StorageTransferArtifactStage.TargetDurable)
                : journal.Progress.Artifacts.Any(x => x.Stage != StorageTransferArtifactStage.OldCopyAbsent))
                throw new LocalStateConcurrencyException("Relocation cleanup progress does not permit this operation.");
            // 新 binding 已是永久 authority；不能重新套用 pre-commit 配置/旧 binding 检查。
            await ValidateRelocationPlacementsAsync(db, journal.Manifest, token);
            await ValidateCommitRootsAsync(db, journal.Manifest, token, committed: true);
            if (await db.PublishIntents.AnyAsync(x => x.PlanId == row.PlanId, token)
                || await db.RetentionDeletionIntents.AnyAsync(x => x.PlanId == row.PlanId, token)
                || await db.MaintenanceStates.AnyAsync(x => x.PlanId == row.PlanId && x.Status != "COMPLETED" && x.Kind != "SCHEDULE_RECONCILIATION", token))
                throw new LocalStateConcurrencyException("Relocation cleanup conflicts with pending maintenance.");

            StorageTransferProgress next;
            if (versionId is { } version)
            {
                // 在任何物理删除前先校验 identity/stage，重复写入不会额外授权删除。
                next = journal.Progress.RecordOldCopyAbsent(version);
                ValidateAbsenceProof(journal, version, await physical.RemoveOldCopyAsync(journal, version, token));
            }
            else
            {
                next = journal.Progress.Complete();
                foreach (var artifact in journal.Progress.Artifacts)
                    ValidateAbsenceProof(journal, artifact.Artifact.VersionId,
                        await physical.RemoveOldCopyAsync(journal, artifact.Artifact.VersionId, token));
            }

            // 删除后的落日志失败不撤销新 authority；重试会按真实 absence 补记。
            // 成功 proof 返回后取消不能中断稳定状态的持久化。
            row.ProgressPayload = StorageRelocationCodec.Encode(next);
            row.ProgressSha256 = SHA256.HashData(row.ProgressPayload);
            row.Stage = StageToken(next.Stage);
            row.Revision = checked(row.Revision + 1);
            await db.SaveChangesAsync(CancellationToken.None);
            faultInjector.ThrowIfRequested(MetadataCommitFaultPoint.AfterRelocationProgress);
            await tx.CommitAsync(CancellationToken.None);
            return new(journal.Manifest, next, row.Revision);
        }
        catch (Exception exception) { throw TranslateRelocation(exception); }
    }

    private static void ValidateAbsenceProof(StorageRelocationJournal journal, ArchiveVersionId version,
        StorageRelocationOldCopyAbsenceProof proof)
    {
        var entry = journal.Manifest.Entries.Single(x => x.Artifact.VersionId == version);
        var root = journal.Manifest.Roots.Single(x => x.Kind == entry.RootKind);
        var target = journal.Progress.Artifacts.Single(x => x.Artifact.VersionId == version).StagedIdentity;
        if (proof is null || proof.TransactionId != journal.Manifest.TransactionId || proof.PlanId != journal.Manifest.PlanId
            || proof.JournalRevision != journal.Revision || proof.Artifact != entry.Artifact
            || proof.OldRootIdentity != root.OldIdentity || proof.OldIdentity != entry.OldIdentity || proof.TargetIdentity != target)
            throw new LocalStateConcurrencyException("Relocation absence proof mismatch.");
    }
}
