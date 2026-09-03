using System.Collections.Immutable;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Application.LocalState;

public class LocalStateRepositoryException(string message, Exception? innerException = null) : Exception(message, innerException);
public sealed class LocalStateConcurrencyException(string message, Exception? innerException = null) : LocalStateRepositoryException(message, innerException);
public sealed class LocalStateCorruptionException(string message, Exception? innerException = null) : LocalStateRepositoryException(message, innerException);
public sealed class StorageRelocationRequiredException(PlanId planId, string rootKind)
    : LocalStateRepositoryException("Existing storage authority requires controlled relocation before changing an output root.")
{
    public PlanId PlanId { get; } = planId;
    public string RootKind { get; } = rootKind;
}
public sealed class UnsupportedConfigDatabaseVersionException(int version)
    : LocalStateRepositoryException($"Config database schema version {version} is not supported.")
{
    public int Version { get; } = version;
}

public enum PlanAuthority { Managed, FileBacked }
public sealed record ConfigDatabaseIdentity(Guid DatabaseId, DeviceId DeviceId, int SchemaVersion, DateTimeOffset CreatedAtUtc);
public sealed record PlanRegistration(PlanId PlanId, PlanAuthority Authority, string? FileDocumentPath, bool IsActive);
public sealed record ManagedPlanDocument(PlanId PlanId, long Revision, ReadOnlyMemory<byte> CanonicalUtf8Payload);
public sealed record RegisteredPlanState(PlanRegistration Registration, ManagedPlanDocument? ManagedDocument);

public interface IConfigDatabaseIdentityStore
{
    Task<ConfigDatabaseIdentity?> LoadAsync(CancellationToken cancellationToken);
    Task<ConfigDatabaseIdentity> InitializeAsync(Guid databaseId, DeviceId deviceId, CancellationToken cancellationToken);
}

/// <summary>Plan registration 与 Managed authoritative payload 是同一个 authority consistency boundary。</summary>
public interface IPlanRegistrationStore
{
    Task<RegisteredPlanState?> LoadAsync(PlanId planId, CancellationToken cancellationToken);
    Task<ImmutableArray<PlanRegistration>> ListRegisteredAsync(bool activeOnly, CancellationToken cancellationToken);
    Task<ManagedPlanDocument> SaveManagedAsync(PlanRegistration registration, ReadOnlyMemory<byte> canonicalUtf8Payload, long? expectedRevision, CancellationToken cancellationToken);
    Task SaveFileBackedAsync(PlanRegistration registration, CancellationToken cancellationToken);
    Task SetActiveAsync(PlanId planId, bool isActive, CancellationToken cancellationToken);
}

public sealed record SourceLocalBinding(SourceId SourceId, string CanonicalPath, string ComparisonKey, bool IsActive);
public sealed record ExternalLocalBinding(ExternalSourceId ExternalSourceId, string CanonicalPath, string ComparisonKey, bool IsActive);
public sealed record OutputRootLocalBinding(string CanonicalPath, string ComparisonKey, bool IsActive);
public sealed record SecretBindingMetadata(SecretSlotId SecretSlotId, string ProviderToken, string OpaqueReference, SecretRevision Revision, bool IsActive);
public sealed record DevicePlanLocalBindings(PlanId PlanId, DeviceId DeviceId, ImmutableArray<SourceLocalBinding> Sources,
    OutputRootLocalBinding? CurrentRoot, OutputRootLocalBinding? HistoryRoot, ImmutableArray<ExternalLocalBinding> ExternalSources);

public interface IDevicePlanBindingStore
{
    Task<DevicePlanLocalBindings?> LoadAsync(PlanId planId, CancellationToken cancellationToken);
    /// <summary>普通保存不得重定向已有 placement 或恢复日志依赖的输出根；检查与写入必须在同一事务。</summary>
    Task SaveValidatedAggregateAsync(DevicePlanLocalBindings bindings, CancellationToken cancellationToken);
    Task<ImmutableArray<DevicePlanLocalBindings>> ListActiveRootFactsAsync(CancellationToken cancellationToken);
}

/// <summary>Secret metadata 与 path/storage bindings 是不同 consistency boundary。</summary>
public interface ISecretBindingMetadataStore
{
    Task<ImmutableArray<SecretBindingMetadata>> LoadAsync(PlanId planId, CancellationToken cancellationToken);
    Task<SecretBindingMetadata> BindAsync(PlanId planId, SecretSlotId slotId, string providerToken, string opaqueReference, CancellationToken cancellationToken);
    Task<SecretBindingMetadata> ReplaceAsync(PlanId planId, SecretSlotId slotId, SecretRevision expectedRevision, string providerToken, string opaqueReference, CancellationToken cancellationToken);
    Task<SecretBindingMetadata> RebindAsync(PlanId planId, SecretSlotId slotId, SecretRevision expectedRevision, string providerToken, string opaqueReference, CancellationToken cancellationToken);
    Task<SecretBindingMetadata> DeactivateAsync(PlanId planId, SecretSlotId slotId, SecretRevision expectedRevision, CancellationToken cancellationToken);
}

public sealed record FileManagedArchiveUnitRegistration(PlanId PlanId, SourceId SourceId, ArchiveUnitId ArchiveUnitId,
    string LogicalUnitPath, string IdentityOriginToken, bool IsActive);

public interface IFileManagedArchiveUnitRegistrationStore
{
    Task<ImmutableArray<FileManagedArchiveUnitRegistration>> ListAsync(PlanId planId, CancellationToken cancellationToken);
    Task ReplaceActiveRegistrationsAsync(PlanId planId, IReadOnlyCollection<FileManagedArchiveUnitRegistration> registrations, CancellationToken cancellationToken);
}

public sealed record ArchiveUnitDurableState(ArchiveVersion? CurrentArchive, CurrentVersion? Current,
    ImmutableArray<(ArchiveVersion Archive, HistoryVersionPlacement Placement)> History,
    CommittedArchiveUnitBaseline? Baseline, CommittedOutputLayoutState? OutputLayout, PendingPublishIntent? PublishIntent);

/// <summary>Archive Unit publish metadata 只能作为一个 transaction-shaped aggregate 提交。</summary>
public interface IArchiveUnitDurableStateStore
{
    Task<ArchiveUnitDurableState?> LoadAsync(PlanId planId, ArchiveUnitId archiveUnitId, CancellationToken cancellationToken);
    Task<ImmutableArray<PendingPublishIntent>> ListIncompletePublishIntentsAsync(CancellationToken cancellationToken);
    Task<int> CleanupCompletedPublishIntentsAsync(CancellationToken cancellationToken);
    Task BeginPublishAsync(PendingPublishIntent intent, CancellationToken cancellationToken);
    Task SavePublishProgressAsync(PendingPublishIntent intent, CancellationToken cancellationToken);
    Task AbortIncompletePublishAsync(PendingPublishIntent intent, PublishIntentStage expectedStage, CancellationToken cancellationToken);
    Task<DurableUnitMetadataCommitResult> CompleteMetadataCommitAsync(DurableUnitMetadataCommitPlan commit, CancellationToken cancellationToken);
    Task CommitOutputReorganizationAsync(OutputReorganizationResult reorganization, CancellationToken cancellationToken);
}

public sealed record ConfigDatabaseOpenRequest(string DatabasePath, Guid? NewDatabaseId = null, DeviceId? NewDeviceId = null);
public sealed record ConfigDatabaseSession(
    ConfigDatabaseIdentity Identity,
    IPlanRegistrationStore Plans,
    IDevicePlanBindingStore Bindings,
    ISecretBindingMetadataStore Secrets,
    IArchiveUnitDurableStateStore ArchiveUnits,
    IHistoryRetentionDurableStore HistoryRetention,
    StowCrate.Application.StorageMaintenance.IStorageRelocationJournalStore Relocations);

public interface IConfigDatabaseSessionOpener
{
    Task<ConfigDatabaseSession> OpenAsync(ConfigDatabaseOpenRequest request, CancellationToken cancellationToken);
}

public enum ScheduleInstallationStatus { NotInstalled, Installed, OutOfSync, Error }
public sealed record ScheduleInstallationState(PlanId PlanId, DeviceId DeviceId, ScheduleInstallationStatus Status,
    string? AdapterToken, string? OpaqueInstallationId, Sha256Digest? InstalledIntentDigest, DateTimeOffset UpdatedAtUtc, string? LastError);

public interface IScheduleInstallationStore
{
    Task<ScheduleInstallationState?> LoadAsync(PlanId planId, DeviceId deviceId, CancellationToken cancellationToken);
    Task SaveAsync(ScheduleInstallationState state, CancellationToken cancellationToken);
}

public enum MaintenanceKind { HistoryRetention, OldCurrentPathCleanup, StorageRelocation, OutputReorganization, ScheduleReconciliation }
public enum MaintenanceStatus { Pending, OutOfSync, Completed }
public sealed record MaintenanceState(PlanId PlanId, ArchiveUnitId? ArchiveUnitId, MaintenanceKind Kind,
    MaintenanceStatus Status, string? Detail, DateTimeOffset UpdatedAtUtc);

public interface IMaintenanceStateStore
{
    Task<ImmutableArray<MaintenanceState>> ListPendingAsync(PlanId planId, CancellationToken cancellationToken);
    Task SaveAsync(MaintenanceState state, CancellationToken cancellationToken);
}

public readonly record struct RetentionSelectionId
{
    public RetentionSelectionId(Guid value) { if (value == Guid.Empty) throw new ArgumentException("RetentionSelectionId must not be empty.", nameof(value)); Value = value; }
    public Guid Value { get; }
}

public enum RetentionDeletionStage { Prepared, Completed }
public sealed record HistoryRetentionEntry(ArchiveVersion Archive, HistoryVersionPlacement Placement);
public sealed record HistoryRetentionSnapshot(PlanId PlanId, ArchiveUnitId ArchiveUnitId, ImmutableArray<HistoryRetentionEntry> Entries);
public sealed record HistoryInventorySnapshot(PlanId PlanId, ImmutableArray<HistoryRetentionEntry> Placements,
    ImmutableArray<ArchiveVersion> SupersededVersions, ImmutableArray<RelativeStoragePath> LivePublishHistoryPaths);
public sealed record RetentionDeletionIntent(
    RetentionSelectionId SelectionId, PlanId PlanId, ArchiveUnitId ArchiveUnitId, ArchiveVersionId ArchiveVersionId,
    RetentionDeletionStage Stage, RelativeStoragePath HistoryRelativePath, Sha256Digest ExpectedIntegrity, long ExpectedLength,
    int RetentionSemanticsVersion, int KeepLastVersionsCount, DateTimeOffset SelectedAtUtc, DateTimeOffset? CompletedAtUtc = null);

/// <summary>History retention 选择与逐制品删除完成都通过事务形状端口提交，禁止暴露表级 CRUD。</summary>
public interface IHistoryRetentionDurableStore
{
    Task<HistoryRetentionSnapshot> LoadRetentionSnapshotAsync(PlanId planId, ArchiveUnitId archiveUnitId, CancellationToken cancellationToken);
    Task<HistoryInventorySnapshot> LoadHistoryInventorySnapshotAsync(PlanId planId, CancellationToken cancellationToken);
    Task BeginDeletionIntentsAsync(RetentionSelectionId selectionId, PlanId planId, ArchiveUnitId archiveUnitId,
        int keepLastVersionsCount, IReadOnlyCollection<HistoryRetentionEntry> victims, CancellationToken cancellationToken);
    Task<ImmutableArray<RetentionDeletionIntent>> ListDeletionIntentsAsync(bool includeCompleted, CancellationToken cancellationToken);
    Task CompleteDeletionAsync(RetentionDeletionIntent intent, DateTimeOffset completedAtUtc, CancellationToken cancellationToken);
    Task<int> CompactCompletedDeletionIntentsAsync(IReadOnlyCollection<ArchiveVersionId> confirmedAbsentVersions, CancellationToken cancellationToken);
}
