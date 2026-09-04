using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Application.LocalState;
using StowCrate.Application.StorageMaintenance;

namespace StowCrate.Infrastructure.Persistence.ConfigDb;

public sealed partial class ConfigDbRepository : IStorageRelocationInventoryStore
{
    public async Task<StorageRelocationInventory> ReadRelocationInventoryAsync(StorageRelocationInventoryRequest request,
        StorageRelocationConfigurationObservation configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(configuration);
        if (request.NewCurrentRoot is null && request.NewHistoryRoot is null)
            throw new ArgumentException("At least one relocation root is required.", nameof(request));
        if (!configuration.Snapshot.IsActive || configuration.Snapshot.Plan.Id != request.PlanId)
            throw new LocalStateConcurrencyException("Relocation inventory configuration identity changed.");
        await using var db = factory.Create();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var plan = DurableCodecs.Uuid(request.PlanId.Value);
            await EnsureNoRelocationAsync(db, plan, cancellationToken);
            if (ConfigurationCheckpoint(configuration.Snapshot) != await ReadConfigurationCheckpointAsync(db, plan, cancellationToken))
                throw new LocalStateConcurrencyException("Relocation inventory configuration changed.");
            if (await db.PublishIntents.AnyAsync(x => x.PlanId == plan, cancellationToken)
                || await db.RetentionDeletionIntents.AnyAsync(x => x.PlanId == plan, cancellationToken)
                || await db.MaintenanceStates.AnyAsync(x => x.PlanId == plan && x.Status != "COMPLETED" && x.Kind != "SCHEDULE_RECONCILIATION", cancellationToken))
                throw new LocalStateConcurrencyException("Relocation inventory conflicts with pending maintenance.");
            var bindings = await db.OutputRootLocalBindings.AsNoTracking().Where(x => x.PlanId == plan).ToListAsync(cancellationToken);
            var roots = ImmutableArray.CreateBuilder<StorageRelocationRootPaths>();
            void Add(StorageRootKind kind, ResolvedPhysicalPath? target)
            {
                if (target is null) return;
                var old = bindings.SingleOrDefault(x => x.RootKind == RootToken(kind) && x.IsActive == 1)
                    ?? throw new LocalStateConcurrencyException("Relocation inventory requires an active old root binding.");
                roots.Add(new(kind, new(old.CanonicalPath, old.ComparisonKey), target));
            }
            Add(StorageRootKind.Current, request.NewCurrentRoot);
            Add(StorageRootKind.History, request.NewHistoryRoot);
            var allRoots = roots.SelectMany(x => new[] { x.OldRoot, x.NewRoot }).ToArray();
            for (var i = 0; i < allRoots.Length; i++)
            for (var j = i + 1; j < allRoots.Length; j++)
                if (allRoots[i].Overlaps(allRoots[j])) throw new LocalStateConcurrencyException("Relocation inventory roots overlap.");
            await EnsureReservationsSafeAsync(db, allRoots, cancellationToken);
            await ValidateRelocationOccupiedRootsAsync(db, plan, roots.Select(x => x.Kind).ToArray(), allRoots, cancellationToken);

            var entries = ImmutableArray.CreateBuilder<StorageRelocationPlacement>();
            async Task AddEntry(byte[] unit, byte[] version, string path, StorageRootKind kind)
            {
                var archive = await db.ArchiveVersions.AsNoTracking().SingleAsync(x => x.ArchiveVersionId == version, cancellationToken);
                if (!archive.PlanId.SequenceEqual(plan) || !archive.ArchiveUnitId.SequenceEqual(unit)
                    || archive.Lifecycle != (kind == StorageRootKind.Current ? "PUBLISHED" : "SUPERSEDED")
                    || archive.IntegritySha256 is null || archive.Length is null)
                    throw new LocalStateCorruptionException("Relocation inventory archive facts are inconsistent.");
                entries.Add(new(new(DurableCodecs.Uuid(unit)), kind,
                    new(new(DurableCodecs.Uuid(version)), DurableCodecs.Digest(archive.IntegritySha256), archive.Length.Value), new(path)));
            }
            // 不经 portable unit 列表或 active unit 过滤，removed identity 的 retained archive 也属于迁移集合。
            if (request.NewCurrentRoot is not null)
                foreach (var item in await db.CurrentVersions.AsNoTracking().Where(x => x.PlanId == plan).ToListAsync(cancellationToken))
                    await AddEntry(item.ArchiveUnitId, item.ArchiveVersionId, item.CurrentRelativePath, StorageRootKind.Current);
            if (request.NewHistoryRoot is not null)
                foreach (var item in await db.HistoryVersionPlacements.AsNoTracking().Where(x => x.PlanId == plan).ToListAsync(cancellationToken))
                    await AddEntry(item.ArchiveUnitId, item.ArchiveVersionId, item.HistoryRelativePath, StorageRootKind.History);
            var device = await db.DatabaseMetadata.AsNoTracking().SingleAsync(cancellationToken);
            return new(request.PlanId, new(DurableCodecs.Uuid(device.DeviceId)), roots.ToImmutable(),
                entries.OrderBy(x => x.Artifact.VersionId.Value).ToImmutableArray());
        }
        catch (Exception exception) { throw TranslateRelocation(exception); }
    }
}
