using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Application.LocalState;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Infrastructure.Configuration.BackupPlans.V1;

namespace StowCrate.Infrastructure.Persistence.ConfigDb;

internal enum MetadataCommitFaultPoint { AfterNewArchive, AfterHistory, AfterCurrent, AfterBaseline, AfterLayout, AfterIntentCompletion, AfterRetentionCompletionMutation, AfterRelocationIntent, AfterRelocationProgress, AfterRelocationBindingSwitch }
internal interface IMetadataCommitFaultInjector { void ThrowIfRequested(MetadataCommitFaultPoint point); }
internal sealed class NoMetadataCommitFaultInjector : IMetadataCommitFaultInjector { public static NoMetadataCommitFaultInjector Instance { get; } = new(); public void ThrowIfRequested(MetadataCommitFaultPoint point) { } }

public sealed partial class ConfigDbRepository : IConfigDatabaseIdentityStore, IPlanRegistrationStore, IDevicePlanBindingStore,
    ISecretBindingMetadataStore, IFileManagedArchiveUnitRegistrationStore, IArchiveUnitDurableStateStore, IScheduleInstallationStore, IMaintenanceStateStore,
    IHistoryRetentionDurableStore
{
    private readonly ConfigDbContextFactory factory;
    private readonly IMetadataCommitFaultInjector faultInjector;
    internal ConfigDbRepository(ConfigDbContextFactory factory, IMetadataCommitFaultInjector? faultInjector = null) { this.factory = factory; this.faultInjector = faultInjector ?? NoMetadataCommitFaultInjector.Instance; }

    public async Task<ConfigDatabaseIdentity?> LoadAsync(CancellationToken cancellationToken)
    {
        await using var db = factory.Create();
        try { var row = await db.DatabaseMetadata.AsNoTracking().SingleOrDefaultAsync(cancellationToken); return row is null ? null : new(DurableCodecs.Uuid(row.DatabaseId), new(DurableCodecs.Uuid(row.DeviceId)), checked((int)row.SchemaVersion), DurableCodecs.Utc(row.CreatedAtUtcMs)); }
        catch (Exception exception) { throw Translate(exception, "Database identity is corrupt."); }
    }

    public async Task<ConfigDatabaseIdentity> InitializeAsync(Guid databaseId, DeviceId deviceId, CancellationToken cancellationToken)
    {
        await using var db = factory.Create();
        if (await db.DatabaseMetadata.AnyAsync(cancellationToken)) throw new LocalStateConcurrencyException("Config database is already initialized.");
        var now = DateTimeOffset.UtcNow; db.DatabaseMetadata.Add(new() { SingletonKey = 1, SchemaVersion = ConfigDbOpenCoordinator.SupportedSchemaVersion, DatabaseId = DurableCodecs.Uuid(databaseId), DeviceId = DurableCodecs.Uuid(deviceId.Value), CreatedAtUtcMs = DurableCodecs.Utc(now) });
        await SaveAsync(db, "Database identity could not be initialized.", cancellationToken); return new(databaseId, deviceId, ConfigDbOpenCoordinator.SupportedSchemaVersion, DurableCodecs.Utc(DurableCodecs.Utc(now)));
    }

    async Task<RegisteredPlanState?> IPlanRegistrationStore.LoadAsync(PlanId planId, CancellationToken cancellationToken)
    {
        await using var db = factory.Create(); var id = DurableCodecs.Uuid(planId.Value);
        try
        {
            var row = await db.PlanRegistrations.AsNoTracking().SingleOrDefaultAsync(x => x.PlanId == id, cancellationToken); if (row is null) return null;
            var document = await db.ManagedPlanDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.PlanId == id, cancellationToken);
            var authority = DurableCodecs.Authority(row.Authority);
            if ((authority is PlanAuthority.Managed) != (document is not null)) throw new LocalStateCorruptionException("Plan authority and Managed payload disagree.");
            if (document is not null && (!document.PayloadSha256.AsSpan().SequenceEqual(SHA256.HashData(document.CanonicalUtf8Payload)) || document.PayloadSha256.Length != 32))
                throw new LocalStateCorruptionException("Managed payload digest verification failed.");
            return new(new(planId, authority, row.FileDocumentPath, DurableCodecs.Boolean(row.IsActive)), document is null ? null : new(planId, document.Revision, document.CanonicalUtf8Payload));
        }
        catch (Exception exception) { throw Translate(exception, "Plan registration is corrupt."); }
    }

    public async Task<ImmutableArray<PlanRegistration>> ListRegisteredAsync(bool activeOnly, CancellationToken cancellationToken)
    {
        await using var db = factory.Create();
        try
        {
            var query = db.PlanRegistrations.AsNoTracking();
            if (activeOnly) query = query.Where(x => x.IsActive == 1);
            var rows = await query.OrderBy(x => x.PlanId).ToListAsync(cancellationToken);
            return [.. rows.Select(x => new PlanRegistration(new(DurableCodecs.Uuid(x.PlanId)), DurableCodecs.Authority(x.Authority), x.FileDocumentPath, DurableCodecs.Boolean(x.IsActive)))];
        }
        catch (Exception exception) { throw Translate(exception, "Plan registrations could not be listed."); }
    }

    public async Task SetActiveAsync(PlanId planId, bool isActive, CancellationToken cancellationToken)
    {
        await using var db = factory.Create(); await using var tx = await db.Database.BeginTransactionAsync(cancellationToken); var id = DurableCodecs.Uuid(planId.Value);
        try
        {
            await EnsureNoRelocationAsync(db, id, cancellationToken);
            if (isActive) await EnsureActivationReservationsSafeAsync(db, id, cancellationToken);
            var changed = await db.PlanRegistrations.Where(x => x.PlanId == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.IsActive, DurableCodecs.Boolean(isActive)), cancellationToken);
            if (changed != 1) throw new LocalStateConcurrencyException("Plan registration does not exist.");
            await tx.CommitAsync(cancellationToken);
        }
        catch (Exception exception) { throw Translate(exception, "Plan activation state could not be changed."); }
    }

    public async Task<ManagedPlanDocument> SaveManagedAsync(PlanRegistration registration, ReadOnlyMemory<byte> canonicalUtf8Payload, long? expectedRevision, CancellationToken cancellationToken)
    {
        if (registration.Authority is not PlanAuthority.Managed || registration.FileDocumentPath is not null) throw new ArgumentException("Managed registration is invalid.", nameof(registration));
        var canonical = ValidateCanonicalDocument(registration.PlanId, canonicalUtf8Payload);
        await using var db = factory.Create(); await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var id = DurableCodecs.Uuid(registration.PlanId.Value);
            await EnsureNoRelocationAsync(db, id, cancellationToken);
            if (registration.IsActive) await EnsureActivationReservationsSafeAsync(db, id, cancellationToken);
            var plan = await db.PlanRegistrations.SingleOrDefaultAsync(x => x.PlanId == id, cancellationToken);
            if (plan is null) { plan = new() { PlanId = id, RegisteredAtUtcMs = DurableCodecs.Utc(DateTimeOffset.UtcNow) }; db.PlanRegistrations.Add(plan); }
            plan.Authority = "MANAGED"; plan.FileDocumentPath = null; plan.IsActive = DurableCodecs.Boolean(registration.IsActive);
            var document = await db.ManagedPlanDocuments.SingleOrDefaultAsync(x => x.PlanId == id, cancellationToken);
            var actual = document?.Revision;
            if (actual != expectedRevision) throw new LocalStateConcurrencyException($"Managed Plan revision mismatch. Expected {expectedRevision?.ToString(CultureInfo.InvariantCulture) ?? "new"}, actual {actual?.ToString(CultureInfo.InvariantCulture) ?? "missing"}.");
            var revision = checked((actual ?? 0) + 1); var digest = SHA256.HashData(canonical);
            if (document is null) { document = new() { PlanId = id }; db.ManagedPlanDocuments.Add(document); }
            document.Revision = revision; document.CanonicalUtf8Payload = canonical; document.PayloadSha256 = digest; document.UpdatedAtUtcMs = DurableCodecs.Utc(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return new(registration.PlanId, revision, canonical);
        }
        catch (Exception exception) { throw Translate(exception, "Managed Plan could not be saved."); }
    }

    public async Task SaveFileBackedAsync(PlanRegistration registration, CancellationToken cancellationToken)
    {
        if (registration.Authority is not PlanAuthority.FileBacked || string.IsNullOrWhiteSpace(registration.FileDocumentPath)) throw new ArgumentException("File-backed registration is invalid.", nameof(registration));
        await using var db = factory.Create(); await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var id = DurableCodecs.Uuid(registration.PlanId.Value);
            await EnsureNoRelocationAsync(db, id, cancellationToken);
            if (registration.IsActive) await EnsureActivationReservationsSafeAsync(db, id, cancellationToken);
            var row = await db.PlanRegistrations.SingleOrDefaultAsync(x => x.PlanId == id, cancellationToken);
            if (row is null) { row = new() { PlanId = id, RegisteredAtUtcMs = DurableCodecs.Utc(DateTimeOffset.UtcNow) }; db.PlanRegistrations.Add(row); }
            var document = await db.ManagedPlanDocuments.SingleOrDefaultAsync(x => x.PlanId == id, cancellationToken); if (document is not null) db.ManagedPlanDocuments.Remove(document);
            row.Authority = "FILE_BACKED"; row.FileDocumentPath = registration.FileDocumentPath; row.IsActive = DurableCodecs.Boolean(registration.IsActive);
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) { throw Translate(exception, "File-backed registration could not be saved."); }
    }

    public async Task<DevicePlanLocalBindings?> LoadAsync(PlanId planId, CancellationToken cancellationToken)
    {
        await using var db = factory.Create(); var id = DurableCodecs.Uuid(planId.Value);
        try
        {
            var metadata = await db.DatabaseMetadata.AsNoTracking().SingleAsync(cancellationToken);
            var deviceId = new DeviceId(DurableCodecs.Uuid(metadata.DeviceId));
            var sources = await db.SourceLocalBindings.AsNoTracking().Where(x => x.PlanId == id).ToListAsync(cancellationToken);
            var external = await db.ExternalLocalBindings.AsNoTracking().Where(x => x.PlanId == id).ToListAsync(cancellationToken);
            var roots = await db.OutputRootLocalBindings.AsNoTracking().Where(x => x.PlanId == id).ToListAsync(cancellationToken);
            if (sources.Count + external.Count + roots.Count == 0) return null;
            OutputRootLocalBinding? Root(string token) { var x = roots.SingleOrDefault(root => root.RootKind == token); return x is null ? null : new(x.CanonicalPath, x.ComparisonKey, DurableCodecs.Boolean(x.IsActive)); }
            return new(planId, deviceId,
                [.. sources.Select(x => new SourceLocalBinding(new(DurableCodecs.Uuid(x.SourceId)), x.CanonicalPath, x.ComparisonKey, DurableCodecs.Boolean(x.IsActive)))], Root("CURRENT"), Root("HISTORY"),
                [.. external.Select(x => new ExternalLocalBinding(new(DurableCodecs.Uuid(x.ExternalSourceId)), x.CanonicalPath, x.ComparisonKey, DurableCodecs.Boolean(x.IsActive)))]);
        }
        catch (Exception exception) { throw Translate(exception, "Local bindings are corrupt."); }
    }

    public async Task SaveValidatedAggregateAsync(DevicePlanLocalBindings bindings, CancellationToken cancellationToken)
    {
        await using var db = factory.Create(); await using var tx = await db.Database.BeginTransactionAsync(cancellationToken); var plan = DurableCodecs.Uuid(bindings.PlanId.Value);
        try
        {
            var metadata = await db.DatabaseMetadata.AsNoTracking().SingleAsync(cancellationToken);
            if (DurableCodecs.Uuid(metadata.DeviceId) != bindings.DeviceId.Value) throw new LocalStateCorruptionException("Binding aggregate DeviceId differs from config database identity.");
            await EnsureNoRelocationAsync(db, plan, cancellationToken);
            await EnsureReservationsSafeAsync(db, bindings.Sources.Where(x => x.IsActive).Select(x => new ResolvedPhysicalPath(x.CanonicalPath, x.ComparisonKey))
                .Concat(new[] { bindings.CurrentRoot, bindings.HistoryRoot }.OfType<OutputRootLocalBinding>().Where(x => x.IsActive)
                    .Select(x => new ResolvedPhysicalPath(x.CanonicalPath, x.ComparisonKey))), cancellationToken);
            await ValidateOutputRootChangesAsync(db, plan, bindings, cancellationToken);
            await UpsertBindings(db, plan, bindings, cancellationToken); await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken);
        }
        catch (Exception exception) { throw Translate(exception, "Local binding aggregate could not be saved."); }
    }

    public async Task<ImmutableArray<DevicePlanLocalBindings>> ListActiveRootFactsAsync(CancellationToken cancellationToken)
    {
        await using var db = factory.Create(); var ids = await db.PlanRegistrations.AsNoTracking().Where(x => x.IsActive == 1).Select(x => x.PlanId).ToListAsync(cancellationToken); var result = ImmutableArray.CreateBuilder<DevicePlanLocalBindings>();
        foreach (var id in ids) { var loaded = await LoadAsync(new(DurableCodecs.Uuid(id)), cancellationToken); if (loaded is not null) result.Add(loaded); }
        return result.ToImmutable();
    }

    async Task<ImmutableArray<SecretBindingMetadata>> ISecretBindingMetadataStore.LoadAsync(PlanId planId, CancellationToken cancellationToken)
    {
        await using var db = factory.Create(); var id = DurableCodecs.Uuid(planId.Value);
        try
        {
            var rows = await db.SecretBindings.AsNoTracking().Where(x => x.PlanId == id).OrderBy(x => x.SecretSlotId).ToListAsync(cancellationToken);
            return [.. rows.Select(MapSecret)];
        }
        catch (Exception exception) { throw Translate(exception, "Secret binding metadata is corrupt."); }
    }

    public Task<SecretBindingMetadata> BindAsync(PlanId planId, SecretSlotId slotId, string providerToken, string opaqueReference, CancellationToken cancellationToken)
        => SwitchSecretAsync(planId, slotId, null, providerToken, opaqueReference, requireActive: null, cancellationToken);

    public Task<SecretBindingMetadata> ReplaceAsync(PlanId planId, SecretSlotId slotId, SecretRevision expectedRevision, string providerToken, string opaqueReference, CancellationToken cancellationToken)
        => SwitchSecretAsync(planId, slotId, expectedRevision, providerToken, opaqueReference, requireActive: true, cancellationToken, requireSameProvider: true);

    public Task<SecretBindingMetadata> RebindAsync(PlanId planId, SecretSlotId slotId, SecretRevision expectedRevision, string providerToken, string opaqueReference, CancellationToken cancellationToken)
        => SwitchSecretAsync(planId, slotId, expectedRevision, providerToken, opaqueReference, requireActive: null, cancellationToken);

    public async Task<SecretBindingMetadata> DeactivateAsync(PlanId planId, SecretSlotId slotId, SecretRevision expectedRevision, CancellationToken cancellationToken)
    {
        await using var db = factory.Create(); await using var tx = await db.Database.BeginTransactionAsync(cancellationToken); var plan = DurableCodecs.Uuid(planId.Value); var slot = DurableCodecs.Uuid(slotId.Value);
        try
        {
            await EnsureNoRelocationAsync(db, plan, cancellationToken);
            var changed = await db.SecretBindings.Where(x => x.PlanId == plan && x.SecretSlotId == slot && x.SecretRevision == expectedRevision.Value && x.IsActive == 1)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.IsActive, 0L), cancellationToken);
            if (changed != 1) throw new LocalStateConcurrencyException("Secret binding deactivate CAS failed.");
            var row = await db.SecretBindings.AsNoTracking().SingleAsync(x => x.PlanId == plan && x.SecretSlotId == slot, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return MapSecret(row);
        }
        catch (Exception exception) { throw Translate(exception, "Secret binding could not be deactivated."); }
    }

    async Task<ImmutableArray<FileManagedArchiveUnitRegistration>> IFileManagedArchiveUnitRegistrationStore.ListAsync(PlanId planId, CancellationToken cancellationToken)
    {
        await using var db = factory.Create(); var id = DurableCodecs.Uuid(planId.Value); var rows = await db.FileManagedArchiveUnitRegistrations.AsNoTracking().Where(x => x.PlanId == id).ToListAsync(cancellationToken);
        return [.. rows.Select(x => new FileManagedArchiveUnitRegistration(planId, new(DurableCodecs.Uuid(x.SourceId)), new(DurableCodecs.Uuid(x.ArchiveUnitId)), x.LogicalUnitPath, x.IdentityOrigin, DurableCodecs.Boolean(x.IsActive)))];
    }

    public async Task ReplaceActiveRegistrationsAsync(PlanId planId, IReadOnlyCollection<FileManagedArchiveUnitRegistration> registrations, CancellationToken cancellationToken)
    {
        await using var db = factory.Create(); await using var tx = await db.Database.BeginTransactionAsync(cancellationToken); var id = DurableCodecs.Uuid(planId.Value); var rows = await db.FileManagedArchiveUnitRegistrations.Where(x => x.PlanId == id).ToListAsync(cancellationToken); foreach (var row in rows) row.IsActive = 0;
        await EnsureNoRelocationAsync(db, id, cancellationToken);
        foreach (var item in registrations) { var unit = DurableCodecs.Uuid(item.ArchiveUnitId.Value); var row = rows.SingleOrDefault(x => x.ArchiveUnitId.SequenceEqual(unit)); if (row is null) { row = new() { PlanId = id, ArchiveUnitId = unit }; db.FileManagedArchiveUnitRegistrations.Add(row); } row.SourceId = DurableCodecs.Uuid(item.SourceId.Value); row.LogicalUnitPath = DurableCodecs.LogicalPath(item.LogicalUnitPath); row.IdentityOrigin = item.IdentityOriginToken; row.IsActive = DurableCodecs.Boolean(item.IsActive); }
        await SaveAsync(db, "FILE_MANAGED registrations could not be saved.", cancellationToken); await tx.CommitAsync(cancellationToken);
    }

    public async Task<ArchiveUnitDurableState?> LoadAsync(PlanId planId, ArchiveUnitId archiveUnitId, CancellationToken cancellationToken)
    {
        await using var db = factory.Create(); var plan = DurableCodecs.Uuid(planId.Value); var unit = DurableCodecs.Uuid(archiveUnitId.Value);
        try
        {
            var current = await db.CurrentVersions.AsNoTracking().SingleOrDefaultAsync(x => x.PlanId == plan && x.ArchiveUnitId == unit, cancellationToken);
            var versions = await db.ArchiveVersions.AsNoTracking().Where(x => x.PlanId == plan && x.ArchiveUnitId == unit).ToListAsync(cancellationToken);
            var history = await db.HistoryVersionPlacements.AsNoTracking().Where(x => x.PlanId == plan && x.ArchiveUnitId == unit).ToListAsync(cancellationToken);
            var baseline = await db.CommittedArchiveUnitBaselines.AsNoTracking().SingleOrDefaultAsync(x => x.PlanId == plan && x.ArchiveUnitId == unit, cancellationToken);
            var layout = await db.CommittedOutputLayoutStates.AsNoTracking().SingleOrDefaultAsync(x => x.PlanId == plan && x.ArchiveUnitId == unit, cancellationToken);
            var intent = await LoadIntent(db, planId, archiveUnitId, cancellationToken);
            if (current is null && versions.Count == 0 && history.Count == 0 && baseline is null && layout is null && intent is null) return null;
            var currentArchive = current is null ? null : MapArchive(versions.Single(x => x.ArchiveVersionId.SequenceEqual(current.ArchiveVersionId)));
            if ((baseline is null) != (current is null) || (baseline is not null && !baseline.ArchiveVersionId.SequenceEqual(current!.ArchiveVersionId))) throw new LocalStateCorruptionException("Baseline and Current version disagree.");
            return new(currentArchive, current is null ? null : new(planId, archiveUnitId, new(DurableCodecs.Uuid(current.ArchiveVersionId)), new(current.CurrentRelativePath)),
                [.. history.Select(x => (MapArchive(versions.Single(v => v.ArchiveVersionId.SequenceEqual(x.ArchiveVersionId))), new HistoryVersionPlacement(planId, archiveUnitId, new(DurableCodecs.Uuid(x.ArchiveVersionId)), new(x.HistoryRelativePath))))],
                baseline is null ? null : MapBaseline(baseline), layout is null ? null : new(planId, archiveUnitId, new(DurableCodecs.Digest(layout.OutputLayoutFingerprint))), intent);
        }
        catch (Exception exception) { throw Translate(exception, "Archive Unit durable state is corrupt."); }
    }

    public async Task<ImmutableArray<PendingPublishIntent>> ListIncompletePublishIntentsAsync(CancellationToken cancellationToken)
    {
        await using var db = factory.Create();
        try
        {
            var keys = await db.PublishIntents.AsNoTracking().Where(x => x.Stage != "METADATA_COMMITTED")
                .Select(x => new { x.PlanId, x.ArchiveUnitId }).ToListAsync(cancellationToken);
            var result = ImmutableArray.CreateBuilder<PendingPublishIntent>(keys.Count);
            foreach (var key in keys)
            {
                var intent = await LoadIntent(db, new(DurableCodecs.Uuid(key.PlanId)), new(DurableCodecs.Uuid(key.ArchiveUnitId)), cancellationToken);
                if (intent is null) throw new LocalStateCorruptionException("Incomplete PublishIntent disappeared during startup query.");
                result.Add(intent);
            }
            return result.ToImmutable();
        }
        catch (Exception exception) { throw Translate(exception, "Incomplete PublishIntents could not be listed."); }
    }

    public async Task<int> CleanupCompletedPublishIntentsAsync(CancellationToken cancellationToken)
    {
        await using var db = factory.Create(); await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var completed = await db.PublishIntents.AsNoTracking().Where(x => x.Stage == "METADATA_COMMITTED")
                .Select(x => new { x.PlanId, x.ArchiveUnitId }).ToListAsync(cancellationToken);
            foreach (var key in completed)
                await db.PublishIntentBaselines.Where(x => x.PlanId == key.PlanId && x.ArchiveUnitId == key.ArchiveUnitId).ExecuteDeleteAsync(cancellationToken);
            var deleted = await db.PublishIntents.Where(x => x.Stage == "METADATA_COMMITTED").ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken); return deleted;
        }
        catch (Exception exception) { throw Translate(exception, "Completed PublishIntents could not be cleaned up."); }
    }

    public Task BeginPublishAsync(PendingPublishIntent intent, CancellationToken cancellationToken) => SaveIntent(intent, expectedPrevious: null, cancellationToken);
    public Task SavePublishProgressAsync(PendingPublishIntent intent, CancellationToken cancellationToken)
    {
        var previous = intent.Stage switch { PublishIntentStage.HistoryCaptured => PublishIntentStage.Prepared, PublishIntentStage.CurrentPublished => intent.HistoryCapture is null ? PublishIntentStage.Prepared : PublishIntentStage.HistoryCaptured, PublishIntentStage.MetadataCommitted => PublishIntentStage.CurrentPublished, _ => throw new ArgumentException("Progress must advance a journal stage.", nameof(intent)) };
        return SaveIntent(intent, previous, cancellationToken);
    }

    public async Task AbortIncompletePublishAsync(PendingPublishIntent intent, PublishIntentStage expectedStage, CancellationToken cancellationToken)
    {
        if (expectedStage is PublishIntentStage.CurrentPublished or PublishIntentStage.MetadataCommitted)
            throw new ArgumentException("A physically published Current cannot be aborted.", nameof(expectedStage));
        await using var db = factory.Create(); await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var plan = DurableCodecs.Uuid(intent.PlanId.Value); var unit = DurableCodecs.Uuid(intent.ArchiveUnitId.Value);
        var expectedToken = DurableCodecs.Token(expectedStage);
        if (!await db.PublishIntents.AsNoTracking().AnyAsync(x => x.PlanId == plan && x.ArchiveUnitId == unit && x.Stage == expectedToken, cancellationToken))
            throw new LocalStateConcurrencyException("PublishIntent was not in the expected abort stage.");
        await db.PublishIntentBaselines.Where(x => x.PlanId == plan && x.ArchiveUnitId == unit).ExecuteDeleteAsync(cancellationToken);
        var changed = await db.PublishIntents.Where(x => x.PlanId == plan && x.ArchiveUnitId == unit && x.Stage == expectedToken)
            .ExecuteDeleteAsync(cancellationToken);
        if (changed != 1) throw new LocalStateConcurrencyException("PublishIntent changed during abort.");
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<DurableUnitMetadataCommitResult> CompleteMetadataCommitAsync(DurableUnitMetadataCommitPlan commit, CancellationToken cancellationToken)
    {
        var projected = DurableUnitMetadataCommit.ConfirmCommitted(commit); await using var db = factory.Create(); await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await EnsureNoRelocationAsync(db, DurableCodecs.Uuid(commit.CurrentVersion.PlanId.Value), cancellationToken);
            await EnsureActivationReservationsSafeAsync(db, DurableCodecs.Uuid(commit.CurrentVersion.PlanId.Value), cancellationToken);
            db.ArchiveVersions.Add(MapArchive(commit.PublishedArchive)); await db.SaveChangesAsync(cancellationToken); faultInjector.ThrowIfRequested(MetadataCommitFaultPoint.AfterNewArchive);
            if (commit.SupersededArchive is not null && commit.HistoryPlacement is not null) { var oldId = DurableCodecs.Uuid(commit.SupersededArchive.Id.Value); var old = await db.ArchiveVersions.SingleAsync(x => x.ArchiveVersionId == oldId, cancellationToken); ApplyArchive(old, commit.SupersededArchive); db.HistoryVersionPlacements.Add(MapHistory(commit.HistoryPlacement)); await db.SaveChangesAsync(cancellationToken); }
            faultInjector.ThrowIfRequested(MetadataCommitFaultPoint.AfterHistory);
            await db.CurrentVersions.Where(x => x.PlanId == DurableCodecs.Uuid(commit.CurrentVersion.PlanId.Value) && x.ArchiveUnitId == DurableCodecs.Uuid(commit.CurrentVersion.ArchiveUnitId.Value)).ExecuteDeleteAsync(cancellationToken); db.CurrentVersions.Add(MapCurrent(commit.CurrentVersion)); await db.SaveChangesAsync(cancellationToken); faultInjector.ThrowIfRequested(MetadataCommitFaultPoint.AfterCurrent);
            await ReplaceBaseline(db, commit, cancellationToken); faultInjector.ThrowIfRequested(MetadataCommitFaultPoint.AfterBaseline);
            await ReplaceLayout(db, commit.OutputLayout, cancellationToken); faultInjector.ThrowIfRequested(MetadataCommitFaultPoint.AfterLayout);
            var changed = await db.PublishIntents.Where(x => x.PlanId == DurableCodecs.Uuid(commit.CurrentPublishedIntent.PlanId.Value) && x.ArchiveUnitId == DurableCodecs.Uuid(commit.CurrentPublishedIntent.ArchiveUnitId.Value) && x.Stage == "CURRENT_PUBLISHED").ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Stage, "METADATA_COMMITTED"), cancellationToken);
            if (changed != 1) throw new LocalStateConcurrencyException("PublishIntent was not in expected CURRENT_PUBLISHED stage.");
            faultInjector.ThrowIfRequested(MetadataCommitFaultPoint.AfterIntentCompletion); await tx.CommitAsync(cancellationToken); return projected;
        }
        catch (Exception exception) { throw Translate(exception, "Archive Unit metadata commit failed."); }
    }

    public async Task CommitOutputReorganizationAsync(OutputReorganizationResult reorganization, CancellationToken cancellationToken)
    {
        await using var db = factory.Create(); await using var tx = await db.Database.BeginTransactionAsync(cancellationToken); var plan = DurableCodecs.Uuid(reorganization.CurrentVersion.PlanId.Value); var unit = DurableCodecs.Uuid(reorganization.CurrentVersion.ArchiveUnitId.Value);
        await EnsureNoRelocationAsync(db, plan, cancellationToken);
        var current = await db.CurrentVersions.SingleOrDefaultAsync(x => x.PlanId == plan && x.ArchiveUnitId == unit, cancellationToken) ?? throw new LocalStateConcurrencyException("CurrentVersion is missing.");
        if (!current.ArchiveVersionId.SequenceEqual(DurableCodecs.Uuid(reorganization.CurrentVersion.ArchiveVersionId.Value))) throw new LocalStateConcurrencyException("Current ArchiveVersion changed during reorganization.");
        current.CurrentRelativePath = DurableCodecs.RelativePath(reorganization.CurrentVersion.RelativePath.Value); await ReplaceLayout(db, reorganization.OutputLayout, cancellationToken); await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken);
    }

    async Task<ScheduleInstallationState?> IScheduleInstallationStore.LoadAsync(PlanId planId, DeviceId deviceId, CancellationToken cancellationToken)
    {
        await using var db = factory.Create(); var row = await db.ScheduleInstallations.AsNoTracking().SingleOrDefaultAsync(x => x.PlanId == DurableCodecs.Uuid(planId.Value) && x.DeviceId == DurableCodecs.Uuid(deviceId.Value), cancellationToken); return row is null ? null : new(planId, deviceId, DurableCodecs.ScheduleStatus(row.Status), row.AdapterToken, row.OpaqueInstallationId, row.InstalledIntentDigest is null ? null : DurableCodecs.Digest(row.InstalledIntentDigest), DurableCodecs.Utc(row.UpdatedAtUtcMs), row.LastError);
    }

    public async Task SaveAsync(ScheduleInstallationState state, CancellationToken cancellationToken)
    {
        await using var db = factory.Create(); var plan = DurableCodecs.Uuid(state.PlanId.Value); var device = DurableCodecs.Uuid(state.DeviceId.Value); var row = await db.ScheduleInstallations.SingleOrDefaultAsync(x => x.PlanId == plan && x.DeviceId == device, cancellationToken); if (row is null) { row = new() { PlanId = plan, DeviceId = device }; db.ScheduleInstallations.Add(row); }
        row.Status = DurableCodecs.Token(state.Status); row.AdapterToken = state.AdapterToken; row.OpaqueInstallationId = state.OpaqueInstallationId; row.InstalledIntentDigest = state.InstalledIntentDigest is null ? null : DurableCodecs.Digest(state.InstalledIntentDigest.Value); row.UpdatedAtUtcMs = DurableCodecs.Utc(state.UpdatedAtUtc); row.LastError = state.LastError; await SaveAsync(db, "Schedule state could not be saved.", cancellationToken);
    }

    public async Task<ImmutableArray<MaintenanceState>> ListPendingAsync(PlanId planId, CancellationToken cancellationToken)
    {
        await using var db = factory.Create(); var rows = await db.MaintenanceStates.AsNoTracking().Where(x => x.PlanId == DurableCodecs.Uuid(planId.Value) && x.Status != "COMPLETED").ToListAsync(cancellationToken); return [.. rows.Select(x => new MaintenanceState(planId, x.ArchiveUnitId is null ? null : new(DurableCodecs.Uuid(x.ArchiveUnitId)), DurableCodecs.MaintenanceKind(x.Kind), DurableCodecs.MaintenanceStatus(x.Status), x.Detail, DurableCodecs.Utc(x.UpdatedAtUtcMs)))];
    }

    public async Task SaveAsync(MaintenanceState state, CancellationToken cancellationToken)
    {
        await using var db = factory.Create(); var plan = DurableCodecs.Uuid(state.PlanId.Value); var unit = state.ArchiveUnitId is null ? null : DurableCodecs.Uuid(state.ArchiveUnitId.Value.Value); var kind = DurableCodecs.Token(state.Kind); var rows = await db.MaintenanceStates.Where(x => x.PlanId == plan && x.Kind == kind).ToListAsync(cancellationToken); var row = rows.SingleOrDefault(x => (unit is null && x.ArchiveUnitId is null) || (unit is not null && x.ArchiveUnitId != null && x.ArchiveUnitId.SequenceEqual(unit))); if (row is null) { row = new() { PlanId = plan, ArchiveUnitId = unit, Kind = kind }; db.MaintenanceStates.Add(row); }
        row.Status = DurableCodecs.Token(state.Status); row.Detail = state.Detail; row.UpdatedAtUtcMs = DurableCodecs.Utc(state.UpdatedAtUtc); await SaveAsync(db, "Maintenance state could not be saved.", cancellationToken);
    }

    public async Task<HistoryRetentionSnapshot> LoadRetentionSnapshotAsync(PlanId planId, ArchiveUnitId archiveUnitId, CancellationToken cancellationToken)
    {
        await using var db = factory.Create(); var plan = DurableCodecs.Uuid(planId.Value); var unit = DurableCodecs.Uuid(archiveUnitId.Value);
        var rows = await (from placement in db.HistoryVersionPlacements.AsNoTracking()
                          join archive in db.ArchiveVersions.AsNoTracking() on placement.ArchiveVersionId equals archive.ArchiveVersionId
                          where placement.PlanId == plan && placement.ArchiveUnitId == unit
                              && archive.Lifecycle == "SUPERSEDED"
                              && !db.RetentionDeletionIntents.Any(intent => intent.ArchiveVersionId == placement.ArchiveVersionId)
                          select new { placement, archive }).ToListAsync(cancellationToken);
        try
        {
            return new(planId, archiveUnitId, [.. rows.Select(x => new HistoryRetentionEntry(MapArchive(x.archive),
                new(planId, archiveUnitId, new(DurableCodecs.Uuid(x.placement.ArchiveVersionId)), new(x.placement.HistoryRelativePath))))]);
        }
        catch (Exception exception) { throw Translate(exception, "History retention snapshot is corrupt."); }
    }

    public async Task<HistoryInventorySnapshot> LoadHistoryInventorySnapshotAsync(PlanId planId, CancellationToken cancellationToken)
    {
        await using var db = factory.Create(); var plan = DurableCodecs.Uuid(planId.Value);
        var archives = await db.ArchiveVersions.AsNoTracking().Where(x => x.PlanId == plan && x.Lifecycle == "SUPERSEDED").ToListAsync(cancellationToken);
        var placements = await db.HistoryVersionPlacements.AsNoTracking().Where(x => x.PlanId == plan).ToListAsync(cancellationToken);
        var publishPaths = await db.PublishIntents.AsNoTracking().Where(x => x.PlanId == plan && x.Stage != "METADATA_COMMITTED" && x.HistoryRelativePath != null)
            .Select(x => x.HistoryRelativePath!).ToListAsync(cancellationToken);
        try
        {
            var mapped = archives.Select(MapArchive).ToImmutableArray();
            var byId = mapped.ToDictionary(x => x.Id);
            var entries = placements.Select(x => new HistoryRetentionEntry(byId[new(DurableCodecs.Uuid(x.ArchiveVersionId))],
                new(planId, new(DurableCodecs.Uuid(x.ArchiveUnitId)), new(DurableCodecs.Uuid(x.ArchiveVersionId)), new(x.HistoryRelativePath)))).ToImmutableArray();
            return new(planId, entries, mapped, [.. publishPaths.Select(x => new RelativeStoragePath(x))]);
        }
        catch (Exception exception) { throw Translate(exception, "History inventory metadata is corrupt."); }
    }

    public async Task BeginDeletionIntentsAsync(RetentionSelectionId selectionId, PlanId planId, ArchiveUnitId archiveUnitId,
        int keepLastVersionsCount, IReadOnlyCollection<HistoryRetentionEntry> victims, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keepLastVersionsCount, 1); if (victims.Count == 0) return;
        await using var db = factory.Create(); await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var plan = DurableCodecs.Uuid(planId.Value); var unit = DurableCodecs.Uuid(archiveUnitId.Value); var now = DateTimeOffset.UtcNow;
        await EnsureNoRelocationAsync(db, plan, cancellationToken);
        foreach (var victim in victims)
        {
            if (victim.Archive.PlanId != planId || victim.Archive.ArchiveUnitId != archiveUnitId || victim.Archive.Lifecycle is not ArchiveVersionLifecycle.Superseded
                || victim.Archive.Integrity is null || victim.Archive.Length is null || victim.Placement.ArchiveVersionId != victim.Archive.Id)
                throw new ArgumentException("Retention victim is not a complete superseded History entry.", nameof(victims));
            var id = DurableCodecs.Uuid(victim.Archive.Id.Value);
            var placement = await db.HistoryVersionPlacements.AsNoTracking().SingleOrDefaultAsync(x => x.ArchiveVersionId == id && x.PlanId == plan && x.ArchiveUnitId == unit, cancellationToken);
            var archive = await db.ArchiveVersions.AsNoTracking().SingleOrDefaultAsync(x => x.ArchiveVersionId == id && x.PlanId == plan && x.ArchiveUnitId == unit, cancellationToken);
            if (placement is null || archive is null || placement.HistoryRelativePath != victim.Placement.RelativePath.Value
                || archive.Lifecycle != "SUPERSEDED" || !archive.IntegritySha256!.SequenceEqual(DurableCodecs.Digest(victim.Archive.Integrity.Value)) || archive.Length != victim.Archive.Length)
                throw new LocalStateConcurrencyException("History retention selection changed before authorization.");
            if (await db.RetentionDeletionIntents.AnyAsync(x => x.ArchiveVersionId == id, cancellationToken))
                throw new LocalStateConcurrencyException("History version already has a retention deletion intent.");
            db.RetentionDeletionIntents.Add(new()
            {
                ArchiveVersionId = id,
                PlanId = plan,
                ArchiveUnitId = unit,
                SelectionId = DurableCodecs.Uuid(selectionId.Value),
                Stage = "PREPARED",
                HistoryRelativePath = placement.HistoryRelativePath,
                ExpectedIntegritySha256 = archive.IntegritySha256!,
                ExpectedLength = archive.Length!.Value,
                RetentionSemanticsVersion = 1,
                KeepLastVersionsCount = keepLastVersionsCount,
                SelectedAtUtcMs = DurableCodecs.Utc(now)
            });
        }
        await SaveAsync(db, "Retention deletion intents could not be created.", cancellationToken); await tx.CommitAsync(cancellationToken);
    }

    public async Task<ImmutableArray<RetentionDeletionIntent>> ListDeletionIntentsAsync(bool includeCompleted, CancellationToken cancellationToken)
    {
        await using var db = factory.Create(); var query = db.RetentionDeletionIntents.AsNoTracking().AsQueryable();
        if (!includeCompleted) query = query.Where(x => x.Stage == "PREPARED");
        var rows = await query.ToListAsync(cancellationToken);
        try { return [.. rows.Select(MapRetentionIntent)]; }
        catch (Exception exception) { throw Translate(exception, "Retention deletion intents are corrupt."); }
    }

    public async Task CompleteDeletionAsync(RetentionDeletionIntent intent, DateTimeOffset completedAtUtc, CancellationToken cancellationToken)
    {
        if (intent.Stage is not RetentionDeletionStage.Prepared) throw new ArgumentException("Only a prepared deletion can complete.", nameof(intent));
        await using var db = factory.Create(); await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var id = DurableCodecs.Uuid(intent.ArchiveVersionId.Value); var plan = DurableCodecs.Uuid(intent.PlanId.Value); var unit = DurableCodecs.Uuid(intent.ArchiveUnitId.Value);
        var row = await db.RetentionDeletionIntents.SingleOrDefaultAsync(x => x.ArchiveVersionId == id && x.Stage == "PREPARED", cancellationToken)
            ?? throw new LocalStateConcurrencyException("Retention deletion intent is no longer PREPARED.");
        var placement = await db.HistoryVersionPlacements.SingleOrDefaultAsync(x => x.ArchiveVersionId == id && x.PlanId == plan && x.ArchiveUnitId == unit, cancellationToken)
            ?? throw new LocalStateConcurrencyException("History placement disappeared before deletion completion.");
        var archive = await db.ArchiveVersions.AsNoTracking().SingleOrDefaultAsync(x => x.ArchiveVersionId == id && x.PlanId == plan && x.ArchiveUnitId == unit, cancellationToken);
        if (!row.PlanId.SequenceEqual(plan) || !row.ArchiveUnitId.SequenceEqual(unit)
            || !row.SelectionId.SequenceEqual(DurableCodecs.Uuid(intent.SelectionId.Value))
            || placement.HistoryRelativePath != intent.HistoryRelativePath.Value || row.HistoryRelativePath != intent.HistoryRelativePath.Value
            || !row.ExpectedIntegritySha256.SequenceEqual(DurableCodecs.Digest(intent.ExpectedIntegrity)) || row.ExpectedLength != intent.ExpectedLength
            || row.RetentionSemanticsVersion != intent.RetentionSemanticsVersion || row.KeepLastVersionsCount != intent.KeepLastVersionsCount
            || row.SelectedAtUtcMs != DurableCodecs.Utc(intent.SelectedAtUtc)
            || archive is null || archive.Lifecycle != "SUPERSEDED" || archive.IntegritySha256 is null
            || !archive.IntegritySha256.SequenceEqual(row.ExpectedIntegritySha256) || archive.Length != row.ExpectedLength)
            throw new LocalStateConcurrencyException("Retention deletion facts changed before completion.");
        db.HistoryVersionPlacements.Remove(placement); row.Stage = "COMPLETED"; row.CompletedAtUtcMs = DurableCodecs.Utc(completedAtUtc);
        await SaveAsync(db, "Retention deletion completion failed.", cancellationToken);
        faultInjector.ThrowIfRequested(MetadataCommitFaultPoint.AfterRetentionCompletionMutation); await tx.CommitAsync(cancellationToken);
    }

    public async Task<int> CompactCompletedDeletionIntentsAsync(IReadOnlyCollection<ArchiveVersionId> confirmedAbsentVersions, CancellationToken cancellationToken)
    {
        if (confirmedAbsentVersions.Count == 0) return 0; await using var db = factory.Create(); await using var tx = await db.Database.BeginTransactionAsync(cancellationToken); var deleted = 0;
        foreach (var version in confirmedAbsentVersions)
        {
            var id = DurableCodecs.Uuid(version.Value);
            if (await db.HistoryVersionPlacements.AnyAsync(x => x.ArchiveVersionId == id, cancellationToken))
                throw new LocalStateConcurrencyException("Completed retention intent still has a History placement and cannot be compacted.");
            var changed = await db.RetentionDeletionIntents.Where(x => x.ArchiveVersionId == id && x.Stage == "COMPLETED").ExecuteDeleteAsync(cancellationToken);
            if (changed != 1) throw new LocalStateConcurrencyException("Completed retention intent changed before compaction."); deleted += changed;
        }
        await tx.CommitAsync(cancellationToken); return deleted;
    }

    private static RetentionDeletionIntent MapRetentionIntent(RetentionDeletionIntentEntity row) => new(
        new(DurableCodecs.Uuid(row.SelectionId)), new(DurableCodecs.Uuid(row.PlanId)), new(DurableCodecs.Uuid(row.ArchiveUnitId)), new(DurableCodecs.Uuid(row.ArchiveVersionId)),
        DurableCodecs.RetentionDeletionStage(row.Stage), new(row.HistoryRelativePath), DurableCodecs.Digest(row.ExpectedIntegritySha256), row.ExpectedLength,
        checked((int)row.RetentionSemanticsVersion), checked((int)row.KeepLastVersionsCount), DurableCodecs.Utc(row.SelectedAtUtcMs), row.CompletedAtUtcMs is null ? null : DurableCodecs.Utc(row.CompletedAtUtcMs.Value));

    private static byte[] ValidateCanonicalDocument(PlanId planId, ReadOnlyMemory<byte> supplied)
    {
        var bytes = DurableCodecs.Utf8(supplied); var read = new BackupPlanDocumentV1Reader().Read(bytes); if (!read.IsSuccess) throw new LocalStateRepositoryException($"Managed payload is invalid: {read.Error!.Message}");
        var semantic = BackupPlanDocumentV1Mapper.Map(read.Document!); if (!semantic.IsSuccess) throw new LocalStateRepositoryException("Managed payload is semantically invalid."); if (semantic.Plan!.Id != planId) throw new LocalStateRepositoryException("Managed payload PlanId does not match registration.");
        var written = new BackupPlanDocumentV1Writer().Write(semantic.Plan); if (!written.IsSuccess || !bytes.AsSpan().SequenceEqual(written.Bytes)) throw new LocalStateRepositoryException("Managed payload is not canonical deterministic writer output."); return bytes;
    }

    private async Task SaveIntent(PendingPublishIntent intent, PublishIntentStage? expectedPrevious, CancellationToken cancellationToken)
    {
        await using var db = factory.Create(); await using var tx = await db.Database.BeginTransactionAsync(cancellationToken); var plan = DurableCodecs.Uuid(intent.PlanId.Value); var unit = DurableCodecs.Uuid(intent.ArchiveUnitId.Value);
        try
        {
            await EnsureNoRelocationAsync(db, plan, cancellationToken);
            await EnsureActivationReservationsSafeAsync(db, plan, cancellationToken);
            var existing = await db.PublishIntents.SingleOrDefaultAsync(x => x.PlanId == plan && x.ArchiveUnitId == unit, cancellationToken);
            if (expectedPrevious is null) { if (existing is not null && existing.Stage != "METADATA_COMMITTED") throw new LocalStateConcurrencyException("A non-complete PublishIntent already exists for this unit."); if (existing is not null) { var payload = await db.PublishIntentBaselines.SingleAsync(x => x.PlanId == plan && x.ArchiveUnitId == unit, cancellationToken); db.PublishIntentBaselines.Remove(payload); db.PublishIntents.Remove(existing); await db.SaveChangesAsync(cancellationToken); existing = null; } }
            else if (existing is null || existing.Stage != DurableCodecs.Token(expectedPrevious.Value)) throw new LocalStateConcurrencyException("PublishIntent stage CAS failed.");
            if (existing is null) { existing = new() { PlanId = plan, ArchiveUnitId = unit }; db.PublishIntents.Add(existing); db.PublishIntentBaselines.Add(MapIntentBaseline(intent)); }
            ApplyIntent(existing, intent);
            existing.HistoryCaptureRequirement = DurableCodecs.Token(intent.HistoryRequirement);
            await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken);
        }
        catch (Exception exception) { throw Translate(exception, "PublishIntent could not be saved."); }
    }

    private static async Task<PendingPublishIntent?> LoadIntent(ConfigDbContext db, PlanId planId, ArchiveUnitId unitId, CancellationToken cancellationToken)
    {
        var plan = DurableCodecs.Uuid(planId.Value); var unit = DurableCodecs.Uuid(unitId.Value); var row = await db.PublishIntents.AsNoTracking().SingleOrDefaultAsync(x => x.PlanId == plan && x.ArchiveUnitId == unit, cancellationToken); if (row is null) return null; var payload = await db.PublishIntentBaselines.AsNoTracking().SingleAsync(x => x.PlanId == plan && x.ArchiveUnitId == unit, cancellationToken);
        var baseline = MapCandidate(payload); var verified = ArchiveVersion.Prepare(new(DurableCodecs.Uuid(row.NewArchiveVersionId)), planId, unitId, DurableCodecs.ArchiveFormat(row.NewArchiveFormat), new(DurableCodecs.Digest(row.NewArchiveSpecFingerprint))).Verify(DurableCodecs.Digest(row.ExpectedNewIntegritySha256), row.NewLength);
        OldCurrentFacts? old = null; HistoryCaptureProof? history = null;
        if (row.OldArchiveVersionId is not null)
        {
            var oldVersion = ArchiveVersion.Prepare(new(DurableCodecs.Uuid(row.OldArchiveVersionId)), planId, unitId, DurableCodecs.ArchiveFormat(row.OldArchiveFormat!), new(DurableCodecs.Digest(row.OldArchiveSpecFingerprint!))).Verify(DurableCodecs.Digest(row.OldIntegritySha256!), row.OldLength!.Value).Publish(DurableCodecs.Utc(row.OldPublishedAtUtcMs!.Value));
            old = new(oldVersion, new(planId, unitId, oldVersion.Id, new(row.OldCurrentRelativePath!)));
            if (row.HistoryRelativePath is not null) { var placement = new HistoryVersionPlacement(planId, unitId, oldVersion.Id, new(row.HistoryRelativePath)); history = new(oldVersion.Id, DurableCodecs.Digest(row.HistoryVerifiedIntegritySha256!), placement); }
        }
        return PendingPublishIntent.Restore(verified, new(row.CurrentRelativePath), baseline, new(DurableCodecs.Digest(row.OutputLayoutFingerprint)), old,
            DurableCodecs.HistoryRequirement(row.HistoryCaptureRequirement), DurableCodecs.PublishStage(row.Stage),
            row.CurrentPublishedAtUtcMs is null ? null : DurableCodecs.Utc(row.CurrentPublishedAtUtcMs.Value), history);
    }

    private static ArchiveVersion MapArchive(ArchiveVersionEntity row)
    {
        var archive = ArchiveVersion.Prepare(new(DurableCodecs.Uuid(row.ArchiveVersionId)), new(DurableCodecs.Uuid(row.PlanId)), new(DurableCodecs.Uuid(row.ArchiveUnitId)), DurableCodecs.ArchiveFormat(row.ArchiveFormat), new(DurableCodecs.Digest(row.ArchiveSpecFingerprint)));
        var lifecycle = DurableCodecs.Lifecycle(row.Lifecycle);
        if (lifecycle is ArchiveVersionLifecycle.Prepared)
        {
            if (row.IntegritySha256 is not null || row.Length is not null || row.PublishedAtUtcMs is not null) throw new LocalStateCorruptionException("Prepared ArchiveVersion contains committed metadata.");
            return archive;
        }
        if (row.IntegritySha256 is null || row.Length is null || row.Length < 0) throw new LocalStateCorruptionException("Verified ArchiveVersion metadata is incomplete.");
        archive = archive.Verify(DurableCodecs.Digest(row.IntegritySha256), row.Length.Value);
        if (lifecycle is ArchiveVersionLifecycle.Verified)
        {
            if (row.PublishedAtUtcMs is not null) throw new LocalStateCorruptionException("Verified ArchiveVersion contains PublishedAt.");
            return archive;
        }
        if (row.PublishedAtUtcMs is null) throw new LocalStateCorruptionException("Published ArchiveVersion lacks PublishedAt.");
        archive = archive.Publish(DurableCodecs.Utc(row.PublishedAtUtcMs.Value)); return lifecycle is ArchiveVersionLifecycle.Superseded ? archive.Supersede() : archive;
    }
    private static ArchiveVersionEntity MapArchive(ArchiveVersion value) { var row = new ArchiveVersionEntity { ArchiveVersionId = DurableCodecs.Uuid(value.Id.Value), PlanId = DurableCodecs.Uuid(value.PlanId.Value), ArchiveUnitId = DurableCodecs.Uuid(value.ArchiveUnitId.Value) }; ApplyArchive(row, value); return row; }
    private static void ApplyArchive(ArchiveVersionEntity row, ArchiveVersion value) { row.ArchiveFormat = DurableCodecs.Token(value.ArchiveFormat); row.ArchiveSpecFingerprint = DurableCodecs.Digest(value.ArchiveSpecFingerprint.Digest); row.Lifecycle = DurableCodecs.Token(value.Lifecycle); row.IntegritySha256 = value.Integrity is null ? null : DurableCodecs.Digest(value.Integrity.Value); row.Length = value.Length; row.PublishedAtUtcMs = value.PublishedAtUtc is null ? null : DurableCodecs.Utc(value.PublishedAtUtc.Value); }
    private static CurrentVersionEntity MapCurrent(CurrentVersion value) => new() { PlanId = DurableCodecs.Uuid(value.PlanId.Value), ArchiveUnitId = DurableCodecs.Uuid(value.ArchiveUnitId.Value), ArchiveVersionId = DurableCodecs.Uuid(value.ArchiveVersionId.Value), CurrentRelativePath = DurableCodecs.RelativePath(value.RelativePath.Value) };
    private static HistoryVersionPlacementEntity MapHistory(HistoryVersionPlacement value) => new() { PlanId = DurableCodecs.Uuid(value.PlanId.Value), ArchiveUnitId = DurableCodecs.Uuid(value.ArchiveUnitId.Value), ArchiveVersionId = DurableCodecs.Uuid(value.ArchiveVersionId.Value), HistoryRelativePath = DurableCodecs.RelativePath(value.RelativePath.Value) };
    private static CommittedArchiveUnitBaseline MapBaseline(CommittedArchiveUnitBaselineEntity x) => CommittedArchiveUnitBaseline.Restore(new(DurableCodecs.Uuid(x.PlanId)), new(DurableCodecs.Uuid(x.ArchiveUnitId)), new(DurableCodecs.Uuid(x.ArchiveVersionId)), checked((int)x.FingerprintEncodingVersion), new(checked((int)x.RulesSemanticsVersion), checked((int)x.ArchiveSemanticsVersion), checked((int)x.OutputPathEncodingVersion)), new(DurableCodecs.Digest(x.EntrySetFingerprint)), new(DurableCodecs.Digest(x.SelectionFingerprint)), new(DurableCodecs.Digest(x.ArchiveSpecFingerprint)), MapComponents(x));
    private static CandidateComponentFingerprints MapComponents(CommittedArchiveUnitBaselineEntity x) => new(new(DurableCodecs.Digest(x.RulesComponent)), new(DurableCodecs.Digest(x.BoundaryComponent)), new(DurableCodecs.Digest(x.LinkPolicyComponent)), new(DurableCodecs.Digest(x.ExternalMappingComponent)), new(DurableCodecs.Digest(x.FormatComponent)), new(DurableCodecs.Digest(x.CompressionComponent)), new(DurableCodecs.Digest(x.ProtectionComponent)), new(DurableCodecs.Digest(x.ManifestComponent)));
    private static CandidateComponentFingerprints MapComponents(PublishIntentBaselineEntity x) => new(new(DurableCodecs.Digest(x.RulesComponent)), new(DurableCodecs.Digest(x.BoundaryComponent)), new(DurableCodecs.Digest(x.LinkPolicyComponent)), new(DurableCodecs.Digest(x.ExternalMappingComponent)), new(DurableCodecs.Digest(x.FormatComponent)), new(DurableCodecs.Digest(x.CompressionComponent)), new(DurableCodecs.Digest(x.ProtectionComponent)), new(DurableCodecs.Digest(x.ManifestComponent)));
    private static BaselineCandidate MapCandidate(PublishIntentBaselineEntity x) => BaselineCandidate.FromCompleteCandidate(new(checked((int)x.FingerprintEncodingVersion), new(checked((int)x.RulesSemanticsVersion), checked((int)x.ArchiveSemanticsVersion), checked((int)x.OutputPathEncodingVersion)), true, new(DurableCodecs.Digest(x.EntrySetFingerprint)), new(DurableCodecs.Digest(x.SelectionFingerprint)), new(DurableCodecs.Digest(x.ArchiveSpecFingerprint)), new(DurableCodecs.Digest(x.OutputLayoutFingerprint)), new(DurableCodecs.Digest(x.ExecutionSemanticFingerprint)), new(DurableCodecs.Digest(x.ExecutionBindingFingerprint)), MapComponents(x)));

    private static void ApplyIntent(PublishIntentEntity row, PendingPublishIntent x) { row.NewArchiveVersionId = DurableCodecs.Uuid(x.NewVersionId.Value); row.Stage = DurableCodecs.Token(x.Stage); row.NewArchiveFormat = DurableCodecs.Token(x.NewArchive.ArchiveFormat); row.NewArchiveSpecFingerprint = DurableCodecs.Digest(x.NewArchive.ArchiveSpecFingerprint.Digest); row.ExpectedNewIntegritySha256 = DurableCodecs.Digest(x.ExpectedNewIntegrity); row.NewLength = x.NewArchive.Length!.Value; row.CurrentRelativePath = DurableCodecs.RelativePath(x.CurrentRelativePath.Value); row.OutputLayoutFingerprint = DurableCodecs.Digest(x.OutputLayoutFingerprint.Digest); row.CurrentPublishedAtUtcMs = x.CurrentPublishedAtUtc is null ? null : DurableCodecs.Utc(x.CurrentPublishedAtUtc.Value); var old = x.OldCurrent; row.OldArchiveVersionId = old is null ? null : DurableCodecs.Uuid(old.ArchiveVersion.Id.Value); row.OldArchiveFormat = old is null ? null : DurableCodecs.Token(old.ArchiveVersion.ArchiveFormat); row.OldArchiveSpecFingerprint = old is null ? null : DurableCodecs.Digest(old.ArchiveVersion.ArchiveSpecFingerprint.Digest); row.OldIntegritySha256 = old?.ArchiveVersion.Integrity is null ? null : DurableCodecs.Digest(old.ArchiveVersion.Integrity.Value); row.OldLength = old?.ArchiveVersion.Length; row.OldPublishedAtUtcMs = old?.ArchiveVersion.PublishedAtUtc is null ? null : DurableCodecs.Utc(old.ArchiveVersion.PublishedAtUtc.Value); row.OldCurrentRelativePath = old?.Placement.RelativePath.Value; row.HistoryRelativePath = x.HistoryCapture?.Placement.RelativePath.Value; row.HistoryVerifiedIntegritySha256 = x.HistoryCapture is null ? null : DurableCodecs.Digest(x.HistoryCapture.VerifiedIntegrity); }
    private static PublishIntentBaselineEntity MapIntentBaseline(PendingPublishIntent x) { var f = x.BaselineCandidate.Fingerprints; var c = f.Components; return new() { PlanId = DurableCodecs.Uuid(x.PlanId.Value), ArchiveUnitId = DurableCodecs.Uuid(x.ArchiveUnitId.Value), FingerprintEncodingVersion = f.EncodingVersion, RulesSemanticsVersion = f.Semantics.Rules, ArchiveSemanticsVersion = f.Semantics.Archive, OutputPathEncodingVersion = f.Semantics.OutputPathEncoding, EntrySetFingerprint = DurableCodecs.Digest(f.EntrySet.Digest), SelectionFingerprint = DurableCodecs.Digest(f.Selection.Digest), ArchiveSpecFingerprint = DurableCodecs.Digest(f.ArchiveSpec.Digest), OutputLayoutFingerprint = DurableCodecs.Digest(f.OutputLayout.Digest), ExecutionSemanticFingerprint = DurableCodecs.Digest(f.ExecutionSemantic.Digest), ExecutionBindingFingerprint = DurableCodecs.Digest(f.ExecutionBinding.Digest), RulesComponent = DurableCodecs.Digest(c.Rules.Digest), BoundaryComponent = DurableCodecs.Digest(c.Boundary.Digest), LinkPolicyComponent = DurableCodecs.Digest(c.LinkPolicy.Digest), ExternalMappingComponent = DurableCodecs.Digest(c.ExternalMapping.Digest), FormatComponent = DurableCodecs.Digest(c.Format.Digest), CompressionComponent = DurableCodecs.Digest(c.Compression.Digest), ProtectionComponent = DurableCodecs.Digest(c.Protection.Digest), ManifestComponent = DurableCodecs.Digest(c.Manifest.Digest) }; }

    private static async Task ReplaceBaseline(ConfigDbContext db, DurableUnitMetadataCommitPlan commit, CancellationToken token) { var plan = DurableCodecs.Uuid(commit.PublishedArchive.PlanId.Value); var unit = DurableCodecs.Uuid(commit.PublishedArchive.ArchiveUnitId.Value); await db.CommittedArchiveUnitBaselines.Where(x => x.PlanId == plan && x.ArchiveUnitId == unit).ExecuteDeleteAsync(token); var f = commit.BaselineCandidate.Fingerprints; var c = f.Components; db.CommittedArchiveUnitBaselines.Add(new() { PlanId = plan, ArchiveUnitId = unit, ArchiveVersionId = DurableCodecs.Uuid(commit.PublishedArchive.Id.Value), FingerprintEncodingVersion = f.EncodingVersion, RulesSemanticsVersion = f.Semantics.Rules, ArchiveSemanticsVersion = f.Semantics.Archive, OutputPathEncodingVersion = f.Semantics.OutputPathEncoding, EntrySetFingerprint = DurableCodecs.Digest(f.EntrySet.Digest), SelectionFingerprint = DurableCodecs.Digest(f.Selection.Digest), ArchiveSpecFingerprint = DurableCodecs.Digest(f.ArchiveSpec.Digest), RulesComponent = DurableCodecs.Digest(c.Rules.Digest), BoundaryComponent = DurableCodecs.Digest(c.Boundary.Digest), LinkPolicyComponent = DurableCodecs.Digest(c.LinkPolicy.Digest), ExternalMappingComponent = DurableCodecs.Digest(c.ExternalMapping.Digest), FormatComponent = DurableCodecs.Digest(c.Format.Digest), CompressionComponent = DurableCodecs.Digest(c.Compression.Digest), ProtectionComponent = DurableCodecs.Digest(c.Protection.Digest), ManifestComponent = DurableCodecs.Digest(c.Manifest.Digest) }); await db.SaveChangesAsync(token); }
    private static async Task ReplaceLayout(ConfigDbContext db, CommittedOutputLayoutState value, CancellationToken token) { var plan = DurableCodecs.Uuid(value.PlanId.Value); var unit = DurableCodecs.Uuid(value.ArchiveUnitId.Value); var row = await db.CommittedOutputLayoutStates.SingleOrDefaultAsync(x => x.PlanId == plan && x.ArchiveUnitId == unit, token); if (row is null) { row = new() { PlanId = plan, ArchiveUnitId = unit }; db.CommittedOutputLayoutStates.Add(row); } row.OutputLayoutFingerprint = DurableCodecs.Digest(value.Fingerprint.Digest); await db.SaveChangesAsync(token); }

    private static async Task ValidateOutputRootChangesAsync(ConfigDbContext db, byte[] plan, DevicePlanLocalBindings value, CancellationToken token)
    {
        var roots = await db.OutputRootLocalBindings.AsNoTracking().Where(x => x.PlanId == plan).ToListAsync(token);
        bool Changed(string kind, OutputRootLocalBinding? proposed)
        {
            var previous = roots.SingleOrDefault(x => x.RootKind == kind);
            if (previous is null) return proposed is not null;
            // 省略 root 的现有保存语义是停用而非删除；已停用且未改路径才是无变化。
            if (proposed is null) return previous.IsActive != 0;
            return previous.CanonicalPath != proposed.CanonicalPath || previous.ComparisonKey != proposed.ComparisonKey
                || previous.IsActive != DurableCodecs.Boolean(proposed.IsActive);
        }

        var currentChanged = Changed("CURRENT", value.CurrentRoot);
        var historyChanged = Changed("HISTORY", value.HistoryRoot);
        if (!currentChanged && !historyChanged) return;

        // 日志尚存时不能借普通 binding save 改变恢复时的 root 解释，包括已完成但尚未 compact 的删除授权。
        // 此检查与 UpsertBindings 共用 SQLite 事务，不能依赖 UI 或事务外的预检查。
        var hasJournal = await db.PublishIntents.AnyAsync(x => x.PlanId == plan, token)
            || await db.RetentionDeletionIntents.AnyAsync(x => x.PlanId == plan, token)
            || await db.MaintenanceStates.AnyAsync(x => x.PlanId == plan && x.Status != "COMPLETED"
                && (x.Kind == "OLD_CURRENT_PATH_CLEANUP" || x.Kind == "STORAGE_RELOCATION" || x.Kind == "OUTPUT_REORGANIZATION"), token);
        if (currentChanged && (hasJournal || await db.CurrentVersions.AnyAsync(x => x.PlanId == plan, token)))
            throw new StorageRelocationRequiredException(value.PlanId, "CURRENT");
        if (historyChanged && (hasJournal || await db.HistoryVersionPlacements.AnyAsync(x => x.PlanId == plan, token)))
            throw new StorageRelocationRequiredException(value.PlanId, "HISTORY");
    }

    private static async Task UpsertBindings(ConfigDbContext db, byte[] plan, DevicePlanLocalBindings value, CancellationToken token)
    {
        var sources = await db.SourceLocalBindings.Where(x => x.PlanId == plan).ToListAsync(token); foreach (var row in sources) row.IsActive = 0; foreach (var item in value.Sources) { var id = DurableCodecs.Uuid(item.SourceId.Value); var row = sources.SingleOrDefault(x => x.SourceId.SequenceEqual(id)); if (row is null) { row = new() { PlanId = plan, SourceId = id }; db.SourceLocalBindings.Add(row); } row.CanonicalPath = item.CanonicalPath; row.ComparisonKey = item.ComparisonKey; row.IsActive = DurableCodecs.Boolean(item.IsActive); }
        var external = await db.ExternalLocalBindings.Where(x => x.PlanId == plan).ToListAsync(token); foreach (var row in external) row.IsActive = 0; foreach (var item in value.ExternalSources) { var id = DurableCodecs.Uuid(item.ExternalSourceId.Value); var row = external.SingleOrDefault(x => x.ExternalSourceId.SequenceEqual(id)); if (row is null) { row = new() { PlanId = plan, ExternalSourceId = id }; db.ExternalLocalBindings.Add(row); } row.CanonicalPath = item.CanonicalPath; row.ComparisonKey = item.ComparisonKey; row.IsActive = DurableCodecs.Boolean(item.IsActive); }
        var roots = await db.OutputRootLocalBindings.Where(x => x.PlanId == plan).ToListAsync(token); foreach (var row in roots) row.IsActive = 0; void Root(string kind, OutputRootLocalBinding? item) { if (item is null) return; var row = roots.SingleOrDefault(x => x.RootKind == kind); if (row is null) { row = new() { PlanId = plan, RootKind = kind }; db.OutputRootLocalBindings.Add(row); } row.CanonicalPath = item.CanonicalPath; row.ComparisonKey = item.ComparisonKey; row.IsActive = DurableCodecs.Boolean(item.IsActive); }
        Root("CURRENT", value.CurrentRoot); Root("HISTORY", value.HistoryRoot);
    }

    private async Task<SecretBindingMetadata> SwitchSecretAsync(PlanId planId, SecretSlotId slotId, SecretRevision? expectedRevision,
        string providerToken, string opaqueReference, bool? requireActive, CancellationToken cancellationToken, bool requireSameProvider = false)
    {
        if (string.IsNullOrWhiteSpace(providerToken) || string.IsNullOrWhiteSpace(opaqueReference)) throw new ArgumentException("Secret locator metadata is required.");
        await using var db = factory.Create(); await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var plan = DurableCodecs.Uuid(planId.Value); var slot = DurableCodecs.Uuid(slotId.Value);
        try
        {
            await EnsureNoRelocationAsync(db, plan, cancellationToken);
            var row = await db.SecretBindings.SingleOrDefaultAsync(x => x.PlanId == plan && x.SecretSlotId == slot, cancellationToken);
            if (expectedRevision is null)
            {
                if (row is not null) throw new LocalStateConcurrencyException("Secret binding already exists; use Replace or Rebind.");
                row = new() { PlanId = plan, SecretSlotId = slot, SecretRevision = 1 }; db.SecretBindings.Add(row);
            }
            else
            {
                if (row is null || row.SecretRevision != expectedRevision.Value.Value || (requireActive is not null && DurableCodecs.Boolean(row.IsActive) != requireActive.Value))
                    throw new LocalStateConcurrencyException("Secret binding revision/state CAS failed.");
                if (requireSameProvider && !string.Equals(row.ProviderToken, providerToken, StringComparison.Ordinal))
                    throw new LocalStateConcurrencyException("Replace cannot change the Secret Store provider; use Rebind.");
                row.SecretRevision = checked(row.SecretRevision + 1);
            }
            row.ProviderToken = providerToken; row.OpaqueReference = opaqueReference; row.IsActive = 1;
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return MapSecret(row);
        }
        catch (Exception exception) { throw Translate(exception, "Secret binding CAS switch failed."); }
    }

    private static SecretBindingMetadata MapSecret(SecretBindingEntity row) => new(new(DurableCodecs.Uuid(row.SecretSlotId)), row.ProviderToken,
        row.OpaqueReference, new(row.SecretRevision), DurableCodecs.Boolean(row.IsActive));

    private static async Task SaveAsync(ConfigDbContext db, string message, CancellationToken token) { try { await db.SaveChangesAsync(token); } catch (Exception exception) { throw Translate(exception, message); } }
    private static Exception Translate(Exception exception, string message) => exception switch { LocalStateRepositoryException => exception, DbUpdateConcurrencyException => new LocalStateConcurrencyException(message, exception), DbUpdateException => new LocalStateRepositoryException(message, exception), Microsoft.Data.Sqlite.SqliteException => new LocalStateRepositoryException(message, exception), InvalidOperationException or ArgumentException or OverflowException => new LocalStateCorruptionException(message, exception), _ => exception };
}
