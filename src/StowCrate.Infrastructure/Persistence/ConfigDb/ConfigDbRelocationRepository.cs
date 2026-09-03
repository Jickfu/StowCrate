using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Application.LocalState;
using StowCrate.Application.StorageMaintenance;
using StowCrate.Core.BackupPlans;

namespace StowCrate.Infrastructure.Persistence.ConfigDb;

public sealed partial class ConfigDbRepository : IStorageRelocationJournalStore
{
    public Task<StorageRelocationJournal> BeginRelocationAsync(StorageRelocationManifest manifest, CancellationToken cancellationToken)
        => BeginRelocationCoreAsync(manifest, null, cancellationToken);

    public Task<StorageRelocationJournal> BeginRelocationAsync(StorageRelocationManifest manifest, StorageRelocationConfigurationObservation configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return BeginRelocationCoreAsync(manifest, configuration, cancellationToken);
    }

    private async Task<StorageRelocationJournal> BeginRelocationCoreAsync(StorageRelocationManifest manifest, StorageRelocationConfigurationObservation? configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        await using var db = factory.Create();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var plan = DurableCodecs.Uuid(manifest.PlanId.Value);
            await EnsureNoRelocationAsync(db, plan, cancellationToken);
            var device = await db.DatabaseMetadata.SingleAsync(cancellationToken);
            if (DurableCodecs.Uuid(device.DeviceId) != manifest.DeviceId.Value) throw new LocalStateConcurrencyException("Relocation device changed.");
            if (!await db.PlanRegistrations.AnyAsync(x => x.PlanId == plan && x.IsActive == 1, cancellationToken))
                throw new LocalStateConcurrencyException("Relocation requires an active Plan.");
            if (await db.PublishIntents.AnyAsync(x => x.PlanId == plan, cancellationToken)
                || await db.RetentionDeletionIntents.AnyAsync(x => x.PlanId == plan, cancellationToken)
                || await db.MaintenanceStates.AnyAsync(x => x.PlanId == plan && x.Status != "COMPLETED" && x.Kind != "SCHEDULE_RECONCILIATION", cancellationToken))
                throw new LocalStateConcurrencyException("Storage maintenance must be reconciled before relocation.");

            var allRoots = manifest.Roots.SelectMany(x => new[] { x.OldRoot, x.NewRoot }).ToArray();
            await EnsureReservationsSafeAsync(db, allRoots, cancellationToken);
            var bindings = await db.OutputRootLocalBindings.AsNoTracking().Where(x => x.PlanId == plan).ToListAsync(cancellationToken);
            foreach (var root in manifest.Roots)
            {
                var old = bindings.SingleOrDefault(x => x.RootKind == RootToken(root.Kind));
                if (old is null || old.IsActive != 1 || old.CanonicalPath != root.OldRoot.CanonicalPath || old.ComparisonKey != root.OldRoot.ComparisonKey)
                    throw new LocalStateConcurrencyException("Relocation old binding changed.");
            }
            // 同时检查旧/新根与所有 active source/output，包括同 Plan 未迁移的根。
            var activeIds = await db.PlanRegistrations.Where(x => x.IsActive == 1).Select(x => x.PlanId).ToListAsync(cancellationToken);
            // inactive Plan 仍可能有未收敛的物理恢复工作；不能让新 reservation 抢占它依赖的根。
            activeIds.AddRange(await db.PublishIntents.Select(x => x.PlanId).ToListAsync(cancellationToken));
            activeIds.AddRange(await db.RetentionDeletionIntents.Select(x => x.PlanId).ToListAsync(cancellationToken));
            activeIds.AddRange(await db.MaintenanceStates.Where(x => x.Status != "COMPLETED" && x.Kind != "SCHEDULE_RECONCILIATION").Select(x => x.PlanId).ToListAsync(cancellationToken));
            var sourceRows = await db.SourceLocalBindings.AsNoTracking().Where(x => x.IsActive == 1).ToListAsync(cancellationToken);
            var outputRows = await db.OutputRootLocalBindings.AsNoTracking().Where(x => x.IsActive == 1).ToListAsync(cancellationToken);
            var occupied = sourceRows.Where(x => activeIds.Any(id => id.SequenceEqual(x.PlanId)))
                .Select(x => new ResolvedPhysicalPath(x.CanonicalPath, x.ComparisonKey)).ToList();
            occupied.AddRange(outputRows.Where(x => activeIds.Any(id => id.SequenceEqual(x.PlanId))
                && !(x.PlanId.SequenceEqual(plan) && manifest.Roots.Any(r => RootToken(r.Kind) == x.RootKind)))
                .Select(x => new ResolvedPhysicalPath(x.CanonicalPath, x.ComparisonKey)));
            if (allRoots.Any(x => occupied.Any(x.Overlaps))) throw new LocalStateConcurrencyException("Relocation roots overlap active storage or sources.");
            await ValidateRelocationPlacementsAsync(db, manifest, cancellationToken);

            byte[]? configurationPayload = null;
            if (configuration is not null)
            {
                if (!configuration.Snapshot.IsActive || configuration.Snapshot.Plan.Id != manifest.PlanId)
                    throw new LocalStateConcurrencyException("Relocation configuration identity changed.");
                var captured = ConfigurationCheckpoint(configuration.Snapshot);
                var current = await ReadConfigurationCheckpointAsync(db, plan, cancellationToken);
                if (captured != current) throw new LocalStateConcurrencyException("Relocation configuration changed before Begin.");
                configurationPayload = StorageRelocationCodec.Encode(current);
            }

            var progress = StorageTransferProgress.Prepare(manifest.TransactionId, manifest.PlanId, manifest.Entries.Select(x => x.Artifact));
            var payload = StorageRelocationCodec.Encode(manifest);
            var state = StorageRelocationCodec.Encode(progress);
            db.StorageRelocationIntents.Add(new()
            {
                TransactionId = DurableCodecs.Uuid(manifest.TransactionId),
                PlanId = plan,
                DeviceId = device.DeviceId,
                ProtocolVersion = 1,
                Revision = 1,
                Stage = "PREPARED",
                ManifestPayload = payload,
                ManifestSha256 = SHA256.HashData(payload),
                ProgressPayload = state,
                ProgressSha256 = SHA256.HashData(state),
                ConfigurationPayload = configurationPayload,
                ConfigurationSha256 = configurationPayload is null ? null : SHA256.HashData(configurationPayload),
            });
            await db.SaveChangesAsync(cancellationToken);
            faultInjector.ThrowIfRequested(MetadataCommitFaultPoint.AfterRelocationIntent);
            db.StorageRelocationRootReservations.AddRange(Reservations(manifest));
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new(manifest, progress, 1);
        }
        catch (Exception exception) { throw TranslateRelocation(exception); }
    }

    public async Task<StorageRelocationJournal?> LoadRelocationAsync(PlanId planId, CancellationToken cancellationToken)
    {
        await using var db = factory.Create();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var plan = DurableCodecs.Uuid(planId.Value);
            var row = await db.StorageRelocationIntents.AsNoTracking().SingleOrDefaultAsync(x => x.PlanId == plan, cancellationToken);
            return row is null ? null : await ReadRelocationAsync(db, row, cancellationToken);
        }
        catch (Exception exception) { throw TranslateRelocation(exception); }
    }

    public Task<StorageRelocationJournal> RecordRelocationStagedAsync(Guid transactionId, long expectedRevision, StorageTransferProof proof, CancellationToken cancellationToken)
        => AdvanceRelocationAsync(transactionId, expectedRevision, x => x.RecordStaged(proof), cancellationToken);
    public Task<StorageRelocationJournal> RecordRelocationTargetAsync(Guid transactionId, long expectedRevision, StorageTransferProof proof, CancellationToken cancellationToken)
        => AdvanceRelocationAsync(transactionId, expectedRevision, x => x.RecordTargetDurable(proof), cancellationToken);
    public Task<StorageRelocationJournal> SealRelocationTargetsAsync(Guid transactionId, long expectedRevision, CancellationToken cancellationToken)
        => AdvanceRelocationAsync(transactionId, expectedRevision, x => x.SealTargets(), cancellationToken);

    private async Task<StorageRelocationJournal> AdvanceRelocationAsync(Guid transactionId, long expectedRevision,
        Func<StorageTransferProgress, StorageTransferProgress> transition, CancellationToken token)
    {
        await using var db = factory.Create(); await using var tx = await db.Database.BeginTransactionAsync(token);
        try
        {
            var id = DurableCodecs.Uuid(transactionId);
            var row = await db.StorageRelocationIntents.SingleOrDefaultAsync(x => x.TransactionId == id, token)
                ?? throw new LocalStateConcurrencyException("Relocation journal does not exist.");
            if (row.Revision != expectedRevision) throw new LocalStateConcurrencyException("Relocation revision changed.");
            var journal = await ReadRelocationAsync(db, row, token);
            var progress = transition(journal.Progress);
            row.ProgressPayload = StorageRelocationCodec.Encode(progress);
            row.ProgressSha256 = SHA256.HashData(row.ProgressPayload);
            row.Stage = StageToken(progress.Stage);
            row.Revision = checked(row.Revision + 1);
            await db.SaveChangesAsync(token);
            faultInjector.ThrowIfRequested(MetadataCommitFaultPoint.AfterRelocationProgress);
            await tx.CommitAsync(token);
            return new(journal.Manifest, progress, row.Revision);
        }
        catch (Exception exception) { throw TranslateRelocation(exception); }
    }

    private static async Task ValidateRelocationPlacementsAsync(ConfigDbContext db, StorageRelocationManifest manifest, CancellationToken token)
    {
        var plan = DurableCodecs.Uuid(manifest.PlanId.Value);
        var current = await db.CurrentVersions.AsNoTracking().Where(x => x.PlanId == plan).ToListAsync(token);
        var history = await db.HistoryVersionPlacements.AsNoTracking().Where(x => x.PlanId == plan).ToListAsync(token);
        var expected = new List<(StorageRootKind Kind, Guid Unit, Guid Version, string Path)>();
        if (manifest.Roots.Any(x => x.Kind == StorageRootKind.Current))
            expected.AddRange(current.Select(x => (StorageRootKind.Current, DurableCodecs.Uuid(x.ArchiveUnitId), DurableCodecs.Uuid(x.ArchiveVersionId), x.CurrentRelativePath)));
        if (manifest.Roots.Any(x => x.Kind == StorageRootKind.History))
            expected.AddRange(history.Select(x => (StorageRootKind.History, DurableCodecs.Uuid(x.ArchiveUnitId), DurableCodecs.Uuid(x.ArchiveVersionId), x.HistoryRelativePath)));
        if (expected.Count != manifest.Entries.Length) throw new LocalStateConcurrencyException("Relocation placement set is incomplete.");
        foreach (var entry in manifest.Entries)
        {
            if (!expected.Contains((entry.RootKind, entry.UnitId.Value, entry.Artifact.VersionId.Value, entry.RelativePath.Value)))
                throw new LocalStateConcurrencyException("Relocation placement changed.");
            var id = DurableCodecs.Uuid(entry.Artifact.VersionId.Value);
            var archive = await db.ArchiveVersions.AsNoTracking().SingleAsync(x => x.ArchiveVersionId == id, token);
            if (!archive.PlanId.SequenceEqual(plan) || !archive.ArchiveUnitId.SequenceEqual(DurableCodecs.Uuid(entry.UnitId.Value))
                || archive.Lifecycle != (entry.RootKind == StorageRootKind.Current ? "PUBLISHED" : "SUPERSEDED")
                || archive.Length != entry.Artifact.Length || archive.IntegritySha256 is null
                || DurableCodecs.Digest(archive.IntegritySha256) != entry.Artifact.Integrity)
                throw new LocalStateConcurrencyException("Relocation ArchiveVersion changed.");
        }
    }

    private static async Task<StorageRelocationJournal> ReadRelocationAsync(ConfigDbContext db, StorageRelocationIntentEntity row, CancellationToken token)
    {
        try
        {
            var manifest = StorageRelocationCodec.ReadManifest(row.ManifestPayload, row.ManifestSha256);
            var progress = StorageRelocationCodec.ReadProgress(manifest, row.ProgressPayload, row.ProgressSha256);
            if (row.ConfigurationPayload is not null && row.ConfigurationSha256 is not null)
                _ = StorageRelocationCodec.ReadConfiguration(row.ConfigurationPayload, row.ConfigurationSha256);
            else if (row.ConfigurationPayload is not null || row.ConfigurationSha256 is not null || progress.IsMetadataCommitted)
                throw new LocalStateCorruptionException("Relocation configuration checkpoint is missing.");
            var device = await db.DatabaseMetadata.AsNoTracking().SingleAsync(token);
            if (row.ProtocolVersion != 1 || row.Revision < 1 || DurableCodecs.Uuid(row.TransactionId) != manifest.TransactionId
                || DurableCodecs.Uuid(row.PlanId) != manifest.PlanId.Value || DurableCodecs.Uuid(row.DeviceId) != manifest.DeviceId.Value
                || !row.DeviceId.SequenceEqual(device.DeviceId) || row.Stage != StageToken(progress.Stage))
                throw new LocalStateCorruptionException("Relocation row identity/stage mismatch.");
            var reservations = await db.StorageRelocationRootReservations.AsNoTracking().Where(x => x.TransactionId == row.TransactionId).ToListAsync(token);
            var expected = Reservations(manifest).ToArray();
            if (reservations.Count != expected.Length || expected.Any(x => !reservations.Any(r => r.Slot == x.Slot && r.CanonicalPath == x.CanonicalPath && r.ComparisonKey == x.ComparisonKey)))
                throw new LocalStateCorruptionException("Relocation root reservation mismatch.");
            return new(manifest, progress, row.Revision);
        }
        catch (Exception exception) { throw TranslateRelocation(exception); }
    }

    private static IEnumerable<StorageRelocationRootReservationEntity> Reservations(StorageRelocationManifest manifest)
    {
        foreach (var root in manifest.Roots)
        {
            yield return new() { TransactionId = DurableCodecs.Uuid(manifest.TransactionId), Slot = RootToken(root.Kind) + "_OLD", CanonicalPath = root.OldRoot.CanonicalPath, ComparisonKey = root.OldRoot.ComparisonKey };
            yield return new() { TransactionId = DurableCodecs.Uuid(manifest.TransactionId), Slot = RootToken(root.Kind) + "_NEW", CanonicalPath = root.NewRoot.CanonicalPath, ComparisonKey = root.NewRoot.ComparisonKey };
        }
    }
    private static string RootToken(StorageRootKind kind) => kind switch { StorageRootKind.Current => "CURRENT", StorageRootKind.History => "HISTORY", _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
    private static string StageToken(StorageTransferStage stage) => stage switch { StorageTransferStage.Prepared => "PREPARED", StorageTransferStage.TargetsDurable => "TARGETS_DURABLE", StorageTransferStage.MetadataCommitted => "METADATA_COMMITTED", _ => throw new LocalStateCorruptionException("Unsupported persisted relocation stage.") };
    private static Exception TranslateRelocation(Exception exception) => exception is System.Text.Json.JsonException or KeyNotFoundException or NullReferenceException
        ? new LocalStateCorruptionException("Relocation journal payload is invalid.", exception) : Translate(exception, "Relocation journal operation failed.");

    private static async Task EnsureNoRelocationAsync(ConfigDbContext db, byte[] plan, CancellationToken token)
    {
        if (await db.StorageRelocationIntents.AnyAsync(x => x.PlanId == plan, token))
            throw new LocalStateConcurrencyException("Plan has a pending storage relocation.");
    }

    private static async Task EnsureReservationsSafeAsync(ConfigDbContext db, IEnumerable<ResolvedPhysicalPath> paths, CancellationToken token)
    {
        var candidates = paths.ToArray();
        // 从完整验证的 manifest 取 reservation；投影丢行/漂移时失败，不能把损坏当作无保留路径。
        foreach (var row in await db.StorageRelocationIntents.AsNoTracking().ToListAsync(token))
        {
            var journal = await ReadRelocationAsync(db, row, token);
            if (journal.Manifest.Roots.SelectMany(x => new[] { x.OldRoot, x.NewRoot }).Any(x => candidates.Any(x.Overlaps)))
                throw new LocalStateConcurrencyException("Paths overlap a reserved relocation root.");
        }
    }

    private static async Task EnsureActivationReservationsSafeAsync(ConfigDbContext db, byte[] plan, CancellationToken token)
    {
        var sources = await db.SourceLocalBindings.AsNoTracking().Where(x => x.PlanId == plan && x.IsActive == 1).ToListAsync(token);
        var outputs = await db.OutputRootLocalBindings.AsNoTracking().Where(x => x.PlanId == plan && x.IsActive == 1).ToListAsync(token);
        await EnsureReservationsSafeAsync(db, sources.Select(x => new ResolvedPhysicalPath(x.CanonicalPath, x.ComparisonKey))
            .Concat(outputs.Select(x => new ResolvedPhysicalPath(x.CanonicalPath, x.ComparisonKey))), token);
    }
}
