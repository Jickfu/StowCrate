using Microsoft.EntityFrameworkCore;

namespace StowCrate.Infrastructure.Persistence.ConfigDb;

public sealed class ConfigDbContext(DbContextOptions<ConfigDbContext> options) : DbContext(options)
{
    internal DbSet<DatabaseMetadataEntity> DatabaseMetadata => Set<DatabaseMetadataEntity>();
    internal DbSet<PlanRegistrationEntity> PlanRegistrations => Set<PlanRegistrationEntity>();
    internal DbSet<ManagedPlanDocumentEntity> ManagedPlanDocuments => Set<ManagedPlanDocumentEntity>();
    internal DbSet<SourceLocalBindingEntity> SourceLocalBindings => Set<SourceLocalBindingEntity>();
    internal DbSet<ExternalLocalBindingEntity> ExternalLocalBindings => Set<ExternalLocalBindingEntity>();
    internal DbSet<OutputRootLocalBindingEntity> OutputRootLocalBindings => Set<OutputRootLocalBindingEntity>();
    internal DbSet<SecretBindingEntity> SecretBindings => Set<SecretBindingEntity>();
    internal DbSet<FileManagedArchiveUnitRegistrationEntity> FileManagedArchiveUnitRegistrations => Set<FileManagedArchiveUnitRegistrationEntity>();
    internal DbSet<ArchiveVersionEntity> ArchiveVersions => Set<ArchiveVersionEntity>();
    internal DbSet<CurrentVersionEntity> CurrentVersions => Set<CurrentVersionEntity>();
    internal DbSet<HistoryVersionPlacementEntity> HistoryVersionPlacements => Set<HistoryVersionPlacementEntity>();
    internal DbSet<CommittedArchiveUnitBaselineEntity> CommittedArchiveUnitBaselines => Set<CommittedArchiveUnitBaselineEntity>();
    internal DbSet<CommittedOutputLayoutStateEntity> CommittedOutputLayoutStates => Set<CommittedOutputLayoutStateEntity>();
    internal DbSet<PublishIntentEntity> PublishIntents => Set<PublishIntentEntity>();
    internal DbSet<PublishIntentBaselineEntity> PublishIntentBaselines => Set<PublishIntentBaselineEntity>();
    internal DbSet<ScheduleInstallationEntity> ScheduleInstallations => Set<ScheduleInstallationEntity>();
    internal DbSet<MaintenanceStateEntity> MaintenanceStates => Set<MaintenanceStateEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureIdentity(modelBuilder);
        ConfigureBindings(modelBuilder);
        ConfigureArchiveState(modelBuilder);
        ConfigureJournal(modelBuilder);
        ConfigureLocalState(modelBuilder);
    }

    private static void ConfigureIdentity(ModelBuilder model)
    {
        var metadata = model.Entity<DatabaseMetadataEntity>();
        metadata.ToTable("DatabaseMetadata", table => { table.HasCheckConstraint("CK_DatabaseMetadata_Singleton", "SingletonKey = 1"); table.HasCheckConstraint("CK_DatabaseMetadata_SchemaVersion", "SchemaVersion > 0"); table.HasCheckConstraint("CK_DatabaseMetadata_Ids", "length(DatabaseId)=16 AND length(DeviceId)=16"); });
        metadata.HasKey(x => x.SingletonKey); metadata.HasIndex(x => x.DatabaseId).IsUnique();

        var plan = model.Entity<PlanRegistrationEntity>();
        plan.ToTable("PlanRegistration", table => { table.HasCheckConstraint("CK_PlanRegistration_Id", "length(PlanId)=16"); table.HasCheckConstraint("CK_PlanRegistration_Active", "IsActive IN (0,1)"); table.HasCheckConstraint("CK_PlanRegistration_Authority", "(Authority='MANAGED' AND FileDocumentPath IS NULL) OR (Authority='FILE_BACKED' AND FileDocumentPath IS NOT NULL)"); });
        plan.HasKey(x => x.PlanId);

        var document = model.Entity<ManagedPlanDocumentEntity>();
        document.ToTable("ManagedPlanDocument", table => { table.HasCheckConstraint("CK_ManagedPlanDocument_Revision", "Revision > 0"); table.HasCheckConstraint("CK_ManagedPlanDocument_Digest", "length(PayloadSha256)=32"); table.HasCheckConstraint("CK_ManagedPlanDocument_Payload", "length(CanonicalUtf8Payload)>0"); });
        document.HasKey(x => x.PlanId); document.HasOne<PlanRegistrationEntity>().WithOne().HasForeignKey<ManagedPlanDocumentEntity>(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureBindings(ModelBuilder model)
    {
        ConfigureBinding<SourceLocalBindingEntity>(model, "SourceLocalBinding", x => new { x.PlanId, x.SourceId });
        ConfigureBinding<ExternalLocalBindingEntity>(model, "ExternalLocalBinding", x => new { x.PlanId, x.ExternalSourceId });
        ConfigureBinding<OutputRootLocalBindingEntity>(model, "OutputRootLocalBinding", x => new { x.PlanId, x.RootKind });
        ConfigureBinding<SecretBindingEntity>(model, "SecretBinding", x => new { x.PlanId, x.SecretSlotId });
        model.Entity<SecretBindingEntity>().ToTable(table => table.HasCheckConstraint("CK_SecretBinding_Revision", "SecretRevision > 0"));

        var registration = model.Entity<FileManagedArchiveUnitRegistrationEntity>();
        registration.ToTable("FileManagedArchiveUnitRegistration", table => { table.HasCheckConstraint("CK_FileManagedRegistration_Ids", "length(PlanId)=16 AND length(SourceId)=16 AND length(ArchiveUnitId)=16"); table.HasCheckConstraint("CK_FileManagedRegistration_Active", "IsActive IN (0,1)"); table.HasCheckConstraint("CK_FileManagedRegistration_Origin", "IdentityOrigin IN ('DIRECTIVE','LOCAL_REGISTRATION')"); });
        registration.HasKey(x => new { x.PlanId, x.ArchiveUnitId }); registration.HasIndex(x => new { x.PlanId, x.SourceId, x.LogicalUnitPath }).IsUnique(); registration.HasOne<PlanRegistrationEntity>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureArchiveState(ModelBuilder model)
    {
        var version = model.Entity<ArchiveVersionEntity>();
        version.ToTable("ArchiveVersion", table => { table.HasCheckConstraint("CK_ArchiveVersion_Ids", "length(ArchiveVersionId)=16 AND length(PlanId)=16 AND length(ArchiveUnitId)=16"); table.HasCheckConstraint("CK_ArchiveVersion_Digests", "length(ArchiveSpecFingerprint)=32 AND (IntegritySha256 IS NULL OR length(IntegritySha256)=32)"); table.HasCheckConstraint("CK_ArchiveVersion_Format", "ArchiveFormat IN ('SEVEN_ZIP','ZIP','TAR_ZSTD')"); table.HasCheckConstraint("CK_ArchiveVersion_Lifecycle", "Lifecycle IN ('PREPARED','VERIFIED','PUBLISHED','SUPERSEDED')"); table.HasCheckConstraint("CK_ArchiveVersion_Metadata", "(Lifecycle='PREPARED' AND IntegritySha256 IS NULL AND Length IS NULL AND PublishedAtUtcMs IS NULL) OR (Lifecycle='VERIFIED' AND IntegritySha256 IS NOT NULL AND Length>=0 AND PublishedAtUtcMs IS NULL) OR (Lifecycle IN ('PUBLISHED','SUPERSEDED') AND IntegritySha256 IS NOT NULL AND Length>=0 AND PublishedAtUtcMs IS NOT NULL)"); });
        version.HasKey(x => x.ArchiveVersionId); version.HasAlternateKey(x => new { x.PlanId, x.ArchiveUnitId, x.ArchiveVersionId }); version.HasOne<PlanRegistrationEntity>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);

        var current = model.Entity<CurrentVersionEntity>(); current.ToTable("CurrentVersion"); current.HasKey(x => new { x.PlanId, x.ArchiveUnitId }); current.HasIndex(x => x.ArchiveVersionId).IsUnique(); current.HasOne<ArchiveVersionEntity>().WithMany().HasForeignKey(x => new { x.PlanId, x.ArchiveUnitId, x.ArchiveVersionId }).HasPrincipalKey(x => new { x.PlanId, x.ArchiveUnitId, x.ArchiveVersionId }).OnDelete(DeleteBehavior.Restrict);
        var history = model.Entity<HistoryVersionPlacementEntity>(); history.ToTable("HistoryVersionPlacement"); history.HasKey(x => x.ArchiveVersionId); history.HasIndex(x => new { x.PlanId, x.HistoryRelativePath }).IsUnique(); history.HasOne<ArchiveVersionEntity>().WithMany().HasForeignKey(x => new { x.PlanId, x.ArchiveUnitId, x.ArchiveVersionId }).HasPrincipalKey(x => new { x.PlanId, x.ArchiveUnitId, x.ArchiveVersionId }).OnDelete(DeleteBehavior.Restrict);

        var baseline = model.Entity<CommittedArchiveUnitBaselineEntity>(); baseline.ToTable("CommittedArchiveUnitBaseline", table => table.HasCheckConstraint("CK_Baseline_Digests", BaselineDigestCheck())); baseline.HasKey(x => new { x.PlanId, x.ArchiveUnitId }); baseline.HasOne<ArchiveVersionEntity>().WithMany().HasForeignKey(x => new { x.PlanId, x.ArchiveUnitId, x.ArchiveVersionId }).HasPrincipalKey(x => new { x.PlanId, x.ArchiveUnitId, x.ArchiveVersionId }).OnDelete(DeleteBehavior.Restrict);
        var layout = model.Entity<CommittedOutputLayoutStateEntity>(); layout.ToTable("CommittedOutputLayoutState", table => table.HasCheckConstraint("CK_OutputLayout_Digest", "length(OutputLayoutFingerprint)=32")); layout.HasKey(x => new { x.PlanId, x.ArchiveUnitId }); layout.HasOne<PlanRegistrationEntity>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureJournal(ModelBuilder model)
    {
        var intent = model.Entity<PublishIntentEntity>();
        intent.ToTable("PublishIntent", table => { table.HasCheckConstraint("CK_PublishIntent_Stage", "Stage IN ('PREPARED','HISTORY_CAPTURED','CURRENT_PUBLISHED','METADATA_COMMITTED')"); table.HasCheckConstraint("CK_PublishIntent_Ids", "length(PlanId)=16 AND length(ArchiveUnitId)=16 AND length(NewArchiveVersionId)=16 AND (OldArchiveVersionId IS NULL OR length(OldArchiveVersionId)=16)"); table.HasCheckConstraint("CK_PublishIntent_Digests", "length(NewArchiveSpecFingerprint)=32 AND length(ExpectedNewIntegritySha256)=32 AND length(OutputLayoutFingerprint)=32 AND (OldArchiveSpecFingerprint IS NULL OR length(OldArchiveSpecFingerprint)=32) AND (OldIntegritySha256 IS NULL OR length(OldIntegritySha256)=32) AND (HistoryVerifiedIntegritySha256 IS NULL OR length(HistoryVerifiedIntegritySha256)=32)"); table.HasCheckConstraint("CK_PublishIntent_OldFacts", "(OldArchiveVersionId IS NULL AND OldArchiveFormat IS NULL AND OldArchiveSpecFingerprint IS NULL AND OldIntegritySha256 IS NULL AND OldLength IS NULL AND OldPublishedAtUtcMs IS NULL AND OldCurrentRelativePath IS NULL AND HistoryRelativePath IS NULL AND HistoryVerifiedIntegritySha256 IS NULL) OR (OldArchiveVersionId IS NOT NULL AND OldArchiveFormat IS NOT NULL AND OldArchiveSpecFingerprint IS NOT NULL AND OldIntegritySha256 IS NOT NULL AND OldLength>=0 AND OldPublishedAtUtcMs IS NOT NULL AND OldCurrentRelativePath IS NOT NULL)"); table.HasCheckConstraint("CK_PublishIntent_StageFacts", "(Stage='PREPARED' AND CurrentPublishedAtUtcMs IS NULL AND HistoryRelativePath IS NULL AND HistoryVerifiedIntegritySha256 IS NULL) OR (Stage='HISTORY_CAPTURED' AND CurrentPublishedAtUtcMs IS NULL AND OldArchiveVersionId IS NOT NULL AND HistoryRelativePath IS NOT NULL AND HistoryVerifiedIntegritySha256 IS NOT NULL) OR (Stage IN ('CURRENT_PUBLISHED','METADATA_COMMITTED') AND CurrentPublishedAtUtcMs IS NOT NULL AND ((OldArchiveVersionId IS NULL AND HistoryRelativePath IS NULL AND HistoryVerifiedIntegritySha256 IS NULL) OR (OldArchiveVersionId IS NOT NULL AND HistoryRelativePath IS NOT NULL AND HistoryVerifiedIntegritySha256 IS NOT NULL)))"); });
        intent.HasKey(x => new { x.PlanId, x.ArchiveUnitId }); intent.HasIndex(x => x.NewArchiveVersionId).IsUnique(); intent.HasOne<PlanRegistrationEntity>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
        var payload = model.Entity<PublishIntentBaselineEntity>(); payload.ToTable("PublishIntentBaseline", table => table.HasCheckConstraint("CK_PublishIntentBaseline_Digests", BaselineDigestCheck() + " AND length(OutputLayoutFingerprint)=32 AND length(ExecutionSemanticFingerprint)=32 AND length(ExecutionBindingFingerprint)=32")); payload.HasKey(x => new { x.PlanId, x.ArchiveUnitId }); payload.HasOne<PublishIntentEntity>().WithOne().HasForeignKey<PublishIntentBaselineEntity>(x => new { x.PlanId, x.ArchiveUnitId }).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureLocalState(ModelBuilder model)
    {
        var schedule = model.Entity<ScheduleInstallationEntity>(); schedule.ToTable("ScheduleInstallation", table => { table.HasCheckConstraint("CK_Schedule_Status", "Status IN ('NOT_INSTALLED','INSTALLED','OUT_OF_SYNC','ERROR')"); table.HasCheckConstraint("CK_Schedule_Digest", "InstalledIntentDigest IS NULL OR length(InstalledIntentDigest)=32"); }); schedule.HasKey(x => new { x.PlanId, x.DeviceId }); schedule.HasOne<PlanRegistrationEntity>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
        var maintenance = model.Entity<MaintenanceStateEntity>(); maintenance.ToTable("MaintenanceState", table => { table.HasCheckConstraint("CK_Maintenance_Status", "Status IN ('PENDING','OUT_OF_SYNC','COMPLETED')"); table.HasCheckConstraint("CK_Maintenance_Kind", "Kind IN ('HISTORY_RETENTION','OLD_CURRENT_PATH_CLEANUP','STORAGE_RELOCATION','OUTPUT_REORGANIZATION','SCHEDULE_RECONCILIATION')"); }); maintenance.HasKey(x => x.MaintenanceStateRowId); maintenance.Property(x => x.MaintenanceStateRowId).ValueGeneratedOnAdd(); maintenance.HasIndex(x => new { x.PlanId, x.Kind }).IsUnique().HasFilter("ArchiveUnitId IS NULL").HasDatabaseName("UX_MaintenanceState_PlanScope"); maintenance.HasIndex(x => new { x.PlanId, x.ArchiveUnitId, x.Kind }).IsUnique().HasFilter("ArchiveUnitId IS NOT NULL").HasDatabaseName("UX_MaintenanceState_UnitScope"); maintenance.HasOne<PlanRegistrationEntity>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureBinding<TEntity>(ModelBuilder model, string tableName, System.Linq.Expressions.Expression<Func<TEntity, object?>> key) where TEntity : class
    {
        var entity = model.Entity<TEntity>(); entity.ToTable(tableName, table => table.HasCheckConstraint($"CK_{tableName}_Active", "IsActive IN (0,1)")); entity.HasKey(key); entity.HasOne<PlanRegistrationEntity>().WithMany().HasForeignKey("PlanId").OnDelete(DeleteBehavior.Restrict);
    }

    private static string BaselineDigestCheck() => "length(EntrySetFingerprint)=32 AND length(SelectionFingerprint)=32 AND length(ArchiveSpecFingerprint)=32 AND length(RulesComponent)=32 AND length(BoundaryComponent)=32 AND length(LinkPolicyComponent)=32 AND length(ExternalMappingComponent)=32 AND length(FormatComponent)=32 AND length(CompressionComponent)=32 AND length(ProtectionComponent)=32 AND length(ManifestComponent)=32";
}
