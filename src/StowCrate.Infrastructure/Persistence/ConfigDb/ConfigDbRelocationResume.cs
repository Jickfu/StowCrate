using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using StowCrate.Application.LocalState;
using StowCrate.Application.StorageMaintenance;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Infrastructure.Persistence.ConfigDb;

public sealed partial class ConfigDbRepository
{
    public async Task<StorageRelocationJournal> ResumeRelocationEntryAsync(Guid transactionId, long expectedRevision,
        ArchiveVersionId versionId, IStorageRelocationPhysicalStore physical, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(physical);
        await using var db = factory.Create();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var id = DurableCodecs.Uuid(transactionId);
            // 显式获取写锁，即使 provider 将来使用 deferred transaction，也不能两个 reader 同时进入物理动作。
            var affected = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE StorageRelocationIntent SET Revision=Revision WHERE TransactionId={id} AND Revision={expectedRevision}", cancellationToken);
            if (affected != 1) throw new LocalStateConcurrencyException("Relocation journal or revision changed.");
            var row = await db.StorageRelocationIntents.SingleAsync(x => x.TransactionId == id, cancellationToken);
            var journal = await ReadRelocationAsync(db, row, cancellationToken);
            var artifact = journal.Progress.Artifacts.SingleOrDefault(x => x.Artifact.VersionId == versionId);
            if (journal.Progress.Stage != StorageTransferStage.Prepared || artifact is null
                || artifact.Stage is not (StorageTransferArtifactStage.Pending or StorageTransferArtifactStage.Staged)
                || row.ConfigurationPayload is null || row.ConfigurationSha256 is null)
                throw new LocalStateConcurrencyException("Relocation entry requires resumable progress and a configuration checkpoint.");
            var checkpoint = StorageRelocationCodec.ReadConfiguration(row.ConfigurationPayload, row.ConfigurationSha256);
            if (checkpoint != await ReadConfigurationCheckpointAsync(db, row.PlanId, cancellationToken))
                throw new LocalStateConcurrencyException("Relocation configuration changed.");
            await ValidateRelocationPlacementsAsync(db, journal.Manifest, cancellationToken);
            await ValidateCommitRootsAsync(db, journal.Manifest, cancellationToken);
            if (await db.PublishIntents.AnyAsync(x => x.PlanId == row.PlanId, cancellationToken)
                || await db.RetentionDeletionIntents.AnyAsync(x => x.PlanId == row.PlanId, cancellationToken)
                || await db.MaintenanceStates.AnyAsync(x => x.PlanId == row.PlanId && x.Status != "COMPLETED" && x.Kind != "SCHEDULE_RECONCILIATION", cancellationToken))
                throw new LocalStateConcurrencyException("Relocation resume conflicts with pending maintenance.");
            var staging = artifact.Stage == StorageTransferArtifactStage.Pending;
            var proof = staging
                ? await physical.StageAsync(journal, versionId, cancellationToken)
                : await physical.PublishTargetAsync(journal, versionId, cancellationToken);
            var next = staging ? journal.Progress.RecordStaged(proof) : journal.Progress.RecordTargetDurable(proof);
            // 先落 ownership/progress，再允许下一步。文件系统成功后取消不扩大未记账窗口。
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
}
