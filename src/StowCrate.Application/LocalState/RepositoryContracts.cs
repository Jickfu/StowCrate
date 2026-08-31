using System.Collections.Immutable;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Application.LocalState;

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
    Task<ManagedPlanDocument> SaveManagedAsync(PlanRegistration registration, ReadOnlyMemory<byte> canonicalUtf8Payload, long? expectedRevision, CancellationToken cancellationToken);
    Task SaveFileBackedAsync(PlanRegistration registration, CancellationToken cancellationToken);
}

public sealed record SourceLocalBinding(SourceId SourceId, string CanonicalPath, string ComparisonKey, bool IsActive);
public sealed record ExternalLocalBinding(ExternalSourceId ExternalSourceId, string CanonicalPath, string ComparisonKey, bool IsActive);
public sealed record OutputRootLocalBinding(string CanonicalPath, string ComparisonKey, bool IsActive);
public sealed record SecretBindingMetadata(SecretSlotId SecretSlotId, string ProviderToken, string OpaqueReference, SecretRevision Revision, bool IsActive);
public sealed record DevicePlanLocalBindings(PlanId PlanId, DeviceId DeviceId, ImmutableArray<SourceLocalBinding> Sources,
    OutputRootLocalBinding? CurrentRoot, OutputRootLocalBinding? HistoryRoot, ImmutableArray<ExternalLocalBinding> ExternalSources,
    ImmutableArray<SecretBindingMetadata> Secrets);

public interface IDevicePlanBindingStore
{
    Task<DevicePlanLocalBindings?> LoadAsync(PlanId planId, DeviceId deviceId, CancellationToken cancellationToken);
    Task SaveValidatedAggregateAsync(DevicePlanLocalBindings bindings, CancellationToken cancellationToken);
    Task<ImmutableArray<DevicePlanLocalBindings>> ListActiveRootFactsAsync(DeviceId deviceId, CancellationToken cancellationToken);
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
    Task BeginPublishAsync(PendingPublishIntent intent, CancellationToken cancellationToken);
    Task SavePublishProgressAsync(PendingPublishIntent intent, CancellationToken cancellationToken);
    Task<DurableUnitMetadataCommitResult> CompleteMetadataCommitAsync(DurableUnitMetadataCommitPlan commit, CancellationToken cancellationToken);
    Task CommitOutputReorganizationAsync(OutputReorganizationResult reorganization, CancellationToken cancellationToken);
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
