using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using StowCrate.Application.BackupPlans.Documents;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Application.LocalState;
using StowCrate.Application.StorageMaintenance;
using StowCrate.Infrastructure.Configuration.BackupPlans;

namespace StowCrate.Infrastructure.Persistence.ConfigDb;

public sealed partial class ConfigDbRepository
{
    private static StorageRelocationCodec.ConfigurationDto ConfigurationCheckpoint(AuthoritativePlanSnapshot snapshot)
        => new(1, StorageRelocationConfigurationFingerprint.EncodingVersion,
            snapshot.Authority switch { PlanAuthority.Managed => "MANAGED", PlanAuthority.FileBacked => "FILE_BACKED", _ => throw new LocalStateCorruptionException("Unknown Plan authority.") },
            snapshot.FileDocumentPath, StorageRelocationConfigurationFingerprint.Compute(snapshot.Plan).Digest.Value);

    private static async Task<StorageRelocationCodec.ConfigurationDto> ReadConfigurationCheckpointAsync(ConfigDbContext db, byte[] plan, CancellationToken token)
    {
        var registration = await db.PlanRegistrations.AsNoTracking().SingleAsync(x => x.PlanId == plan, token);
        if (registration.IsActive != 1) throw new LocalStateConcurrencyException("Relocation Plan is inactive.");
        var source = new BackupPlanDocumentSource();
        ValidatedBackupPlanDocument document;
        if (registration.Authority == "MANAGED")
        {
            var managed = await db.ManagedPlanDocuments.AsNoTracking().SingleAsync(x => x.PlanId == plan, token);
            if (!SHA256.HashData(managed.CanonicalUtf8Payload).AsSpan().SequenceEqual(managed.PayloadSha256))
                throw new LocalStateCorruptionException("Managed configuration integrity mismatch.");
            document = source.ReadCanonicalPayload(managed.CanonicalUtf8Payload);
        }
        else if (registration.Authority == "FILE_BACKED" && registration.FileDocumentPath is not null)
            document = await source.ReadFileAsync(registration.FileDocumentPath, token);
        else throw new LocalStateCorruptionException("Invalid relocation Plan authority.");
        if (document.Plan.Id.Value != DurableCodecs.Uuid(plan)) throw new LocalStateConcurrencyException("Relocation document identity changed.");
        return ConfigurationCheckpoint(new(document.Plan, registration.Authority == "MANAGED" ? PlanAuthority.Managed : PlanAuthority.FileBacked,
            null, registration.FileDocumentPath, true));
    }

    public async Task<StorageRelocationJournal> CommitRelocationAsync(Guid transactionId, long expectedRevision,
        IStorageRelocationPhysicalStore physical, CancellationToken cancellationToken)
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
            if (journal.Progress.Stage != StorageTransferStage.TargetsDurable || row.ConfigurationPayload is null || row.ConfigurationSha256 is null)
                throw new LocalStateConcurrencyException("Relocation needs sealed targets and a durable configuration checkpoint.");
            var checkpoint = StorageRelocationCodec.ReadConfiguration(row.ConfigurationPayload, row.ConfigurationSha256);
            if (checkpoint != await ReadConfigurationCheckpointAsync(db, row.PlanId, cancellationToken))
                throw new LocalStateConcurrencyException("Relocation configuration changed.");
            await ValidateRelocationPlacementsAsync(db, journal.Manifest, cancellationToken);
            var roots = await ValidateCommitRootsAsync(db, journal.Manifest, cancellationToken);
            if (await db.PublishIntents.AnyAsync(x => x.PlanId == row.PlanId, cancellationToken)
                || await db.RetentionDeletionIntents.AnyAsync(x => x.PlanId == row.PlanId, cancellationToken)
                || await db.MaintenanceStates.AnyAsync(x => x.PlanId == row.PlanId && x.Status != "COMPLETED" && x.Kind != "SCHEDULE_RECONCILIATION", cancellationToken))
                throw new LocalStateConcurrencyException("Relocation conflicts with pending maintenance.");

            // SQLite 事务保持 expected metadata 稳定；文件系统不属于该事务，必须现场全量验证。
            await physical.VerifyForCommitAsync(journal, cancellationToken);
            // File-backed 文档不受数据库锁保护，昂贵的物理验证之后必须再次重读。
            if (checkpoint != await ReadConfigurationCheckpointAsync(db, row.PlanId, cancellationToken))
                throw new LocalStateConcurrencyException("Relocation configuration changed during verification.");
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var root in journal.Manifest.Roots)
            {
                var binding = roots.Single(x => x.RootKind == RootToken(root.Kind));
                binding.CanonicalPath = root.NewRoot.CanonicalPath;
                binding.ComparisonKey = root.NewRoot.ComparisonKey;
            }
            await db.SaveChangesAsync(cancellationToken);
            faultInjector.ThrowIfRequested(MetadataCommitFaultPoint.AfterRelocationBindingSwitch);
            var progress = journal.Progress.MarkMetadataCommitted();
            row.ProgressPayload = StorageRelocationCodec.Encode(progress);
            row.ProgressSha256 = SHA256.HashData(row.ProgressPayload);
            row.Stage = StageToken(progress.Stage);
            row.Revision = checked(row.Revision + 1);
            await db.SaveChangesAsync(cancellationToken);
            faultInjector.ThrowIfRequested(MetadataCommitFaultPoint.AfterRelocationProgress);
            // 进入永久成功点后不让 caller cancellation 把已提交结果误报为取消。
            cancellationToken.ThrowIfCancellationRequested();
            await tx.CommitAsync(CancellationToken.None);
            return new(journal.Manifest, progress, row.Revision);
        }
        catch (Exception exception) { throw TranslateRelocation(exception); }
    }

    private static async Task<List<OutputRootLocalBindingEntity>> ValidateCommitRootsAsync(ConfigDbContext db, StorageRelocationManifest manifest, CancellationToken token, bool committed = false)
    {
        var plan = DurableCodecs.Uuid(manifest.PlanId.Value);
        var roots = await db.OutputRootLocalBindings.Where(x => x.PlanId == plan).ToListAsync(token);
        foreach (var root in manifest.Roots)
        {
            var old = roots.SingleOrDefault(x => x.RootKind == RootToken(root.Kind));
            var expected = committed ? root.NewRoot : root.OldRoot;
            if (old is null || old.IsActive != 1 || old.CanonicalPath != expected.CanonicalPath || old.ComparisonKey != expected.ComparisonKey)
                throw new LocalStateConcurrencyException("Relocation binding changed.");
        }
        var proposed = manifest.Roots.SelectMany(x => new[] { x.OldRoot, x.NewRoot }).ToArray();
        foreach (var other in await db.StorageRelocationIntents.AsNoTracking().Where(x => x.TransactionId != DurableCodecs.Uuid(manifest.TransactionId)).ToListAsync(token))
        {
            var reserved = await ReadRelocationAsync(db, other, token);
            if (reserved.Manifest.Roots.SelectMany(x => new[] { x.OldRoot, x.NewRoot }).Any(x => proposed.Any(x.Overlaps)))
                throw new LocalStateConcurrencyException("Relocation root reservation conflict.");
        }
        await ValidateRelocationOccupiedRootsAsync(db, plan, manifest.Roots.Select(x => x.Kind).ToArray(), proposed, token);
        return roots;
    }

    private static async Task ValidateRelocationOccupiedRootsAsync(ConfigDbContext db, byte[] plan,
        IReadOnlyCollection<StorageRootKind> selectedKinds, ResolvedPhysicalPath[] proposed, CancellationToken token)
    {
        var active = await db.PlanRegistrations.Where(x => x.IsActive == 1).Select(x => x.PlanId).ToListAsync(token);
        active.AddRange(await db.PublishIntents.Select(x => x.PlanId).ToListAsync(token));
        active.AddRange(await db.RetentionDeletionIntents.Select(x => x.PlanId).ToListAsync(token));
        active.AddRange(await db.MaintenanceStates.Where(x => x.Status != "COMPLETED" && x.Kind != "SCHEDULE_RECONCILIATION").Select(x => x.PlanId).ToListAsync(token));
        var sources = await db.SourceLocalBindings.AsNoTracking().Where(x => x.IsActive == 1).ToListAsync(token);
        var external = await db.ExternalLocalBindings.AsNoTracking().Where(x => x.IsActive == 1).ToListAsync(token);
        var outputs = await db.OutputRootLocalBindings.AsNoTracking().Where(x => x.IsActive == 1).ToListAsync(token);
        var occupied = sources.Where(x => active.Any(p => p.SequenceEqual(x.PlanId))).Select(x => new ResolvedPhysicalPath(x.CanonicalPath, x.ComparisonKey))
            .Concat(external.Where(x => active.Any(p => p.SequenceEqual(x.PlanId))).Select(x => new ResolvedPhysicalPath(x.CanonicalPath, x.ComparisonKey)))
            .Concat(outputs.Where(x => active.Any(p => p.SequenceEqual(x.PlanId))
                && !(x.PlanId.SequenceEqual(plan) && selectedKinds.Any(kind => RootToken(kind) == x.RootKind)))
                .Select(x => new ResolvedPhysicalPath(x.CanonicalPath, x.ComparisonKey)));
        if (occupied.Any(x => proposed.Any(x.Overlaps))) throw new LocalStateConcurrencyException("Relocation roots overlap active storage.");
    }
}
