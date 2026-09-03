namespace StowCrate.Infrastructure.Persistence.ConfigDb;

internal sealed class StorageRelocationIntentEntity
{
    public byte[] TransactionId { get; set; } = [];
    public byte[] PlanId { get; set; } = [];
    public byte[] DeviceId { get; set; } = [];
    public long ProtocolVersion { get; set; }
    public long Revision { get; set; }
    public string Stage { get; set; } = "";
    public byte[] ManifestPayload { get; set; } = [];
    public byte[] ManifestSha256 { get; set; } = [];
    public byte[] ProgressPayload { get; set; } = [];
    public byte[] ProgressSha256 { get; set; } = [];
    public byte[]? ConfigurationPayload { get; set; }
    public byte[]? ConfigurationSha256 { get; set; }
}
internal sealed class StorageRelocationRootReservationEntity
{
    public byte[] TransactionId { get; set; } = [];
    public string Slot { get; set; } = "";
    public string CanonicalPath { get; set; } = "";
    public string ComparisonKey { get; set; } = "";
}

internal sealed class DatabaseMetadataEntity { public long SingletonKey { get; set; } public long SchemaVersion { get; set; } public byte[] DatabaseId { get; set; } = []; public byte[] DeviceId { get; set; } = []; public long CreatedAtUtcMs { get; set; } }
internal sealed class PlanRegistrationEntity { public byte[] PlanId { get; set; } = []; public string Authority { get; set; } = ""; public string? FileDocumentPath { get; set; } public long IsActive { get; set; } public long RegisteredAtUtcMs { get; set; } }
internal sealed class ManagedPlanDocumentEntity { public byte[] PlanId { get; set; } = []; public long Revision { get; set; } public byte[] CanonicalUtf8Payload { get; set; } = []; public byte[] PayloadSha256 { get; set; } = []; public long UpdatedAtUtcMs { get; set; } }
internal sealed class SourceLocalBindingEntity { public byte[] PlanId { get; set; } = []; public byte[] SourceId { get; set; } = []; public string CanonicalPath { get; set; } = ""; public string ComparisonKey { get; set; } = ""; public long IsActive { get; set; } }
internal sealed class ExternalLocalBindingEntity { public byte[] PlanId { get; set; } = []; public byte[] ExternalSourceId { get; set; } = []; public string CanonicalPath { get; set; } = ""; public string ComparisonKey { get; set; } = ""; public long IsActive { get; set; } }
internal sealed class OutputRootLocalBindingEntity { public byte[] PlanId { get; set; } = []; public string RootKind { get; set; } = ""; public string CanonicalPath { get; set; } = ""; public string ComparisonKey { get; set; } = ""; public long IsActive { get; set; } }
internal sealed class SecretBindingEntity { public byte[] PlanId { get; set; } = []; public byte[] SecretSlotId { get; set; } = []; public string ProviderToken { get; set; } = ""; public string OpaqueReference { get; set; } = ""; public long SecretRevision { get; set; } public long IsActive { get; set; } }
internal sealed class FileManagedArchiveUnitRegistrationEntity { public byte[] PlanId { get; set; } = []; public byte[] SourceId { get; set; } = []; public byte[] ArchiveUnitId { get; set; } = []; public string LogicalUnitPath { get; set; } = ""; public string IdentityOrigin { get; set; } = ""; public long IsActive { get; set; } }
internal sealed class ArchiveVersionEntity { public byte[] ArchiveVersionId { get; set; } = []; public byte[] PlanId { get; set; } = []; public byte[] ArchiveUnitId { get; set; } = []; public string ArchiveFormat { get; set; } = ""; public byte[] ArchiveSpecFingerprint { get; set; } = []; public string Lifecycle { get; set; } = ""; public byte[]? IntegritySha256 { get; set; } public long? Length { get; set; } public long? PublishedAtUtcMs { get; set; } }
internal sealed class CurrentVersionEntity { public byte[] PlanId { get; set; } = []; public byte[] ArchiveUnitId { get; set; } = []; public byte[] ArchiveVersionId { get; set; } = []; public string CurrentRelativePath { get; set; } = ""; }
internal sealed class HistoryVersionPlacementEntity { public byte[] ArchiveVersionId { get; set; } = []; public byte[] PlanId { get; set; } = []; public byte[] ArchiveUnitId { get; set; } = []; public string HistoryRelativePath { get; set; } = ""; }
internal sealed class CommittedArchiveUnitBaselineEntity
{
    public byte[] PlanId { get; set; } = []; public byte[] ArchiveUnitId { get; set; } = []; public byte[] ArchiveVersionId { get; set; } = [];
    public long FingerprintEncodingVersion { get; set; } public long RulesSemanticsVersion { get; set; } public long ArchiveSemanticsVersion { get; set; } public long OutputPathEncodingVersion { get; set; }
    public byte[] EntrySetFingerprint { get; set; } = []; public byte[] SelectionFingerprint { get; set; } = []; public byte[] ArchiveSpecFingerprint { get; set; } = [];
    public byte[] RulesComponent { get; set; } = []; public byte[] BoundaryComponent { get; set; } = []; public byte[] LinkPolicyComponent { get; set; } = []; public byte[] ExternalMappingComponent { get; set; } = [];
    public byte[] FormatComponent { get; set; } = []; public byte[] CompressionComponent { get; set; } = []; public byte[] ProtectionComponent { get; set; } = []; public byte[] ManifestComponent { get; set; } = [];
}
internal sealed class CommittedOutputLayoutStateEntity { public byte[] PlanId { get; set; } = []; public byte[] ArchiveUnitId { get; set; } = []; public byte[] OutputLayoutFingerprint { get; set; } = []; }
internal sealed class PublishIntentEntity
{
    public byte[] PlanId { get; set; } = []; public byte[] ArchiveUnitId { get; set; } = []; public byte[] NewArchiveVersionId { get; set; } = []; public string Stage { get; set; } = "";
    public string NewArchiveFormat { get; set; } = ""; public byte[] NewArchiveSpecFingerprint { get; set; } = []; public byte[] ExpectedNewIntegritySha256 { get; set; } = []; public long NewLength { get; set; }
    public string CurrentRelativePath { get; set; } = ""; public byte[] OutputLayoutFingerprint { get; set; } = []; public long? CurrentPublishedAtUtcMs { get; set; }
    public string HistoryCaptureRequirement { get; set; } = "";
    public byte[]? OldArchiveVersionId { get; set; } public string? OldArchiveFormat { get; set; } public byte[]? OldArchiveSpecFingerprint { get; set; } public byte[]? OldIntegritySha256 { get; set; } public long? OldLength { get; set; } public long? OldPublishedAtUtcMs { get; set; } public string? OldCurrentRelativePath { get; set; }
    public string? HistoryRelativePath { get; set; } public byte[]? HistoryVerifiedIntegritySha256 { get; set; }
}
internal sealed class PublishIntentBaselineEntity
{
    public byte[] PlanId { get; set; } = []; public byte[] ArchiveUnitId { get; set; } = []; public long FingerprintEncodingVersion { get; set; } public long RulesSemanticsVersion { get; set; } public long ArchiveSemanticsVersion { get; set; } public long OutputPathEncodingVersion { get; set; }
    public byte[] EntrySetFingerprint { get; set; } = []; public byte[] SelectionFingerprint { get; set; } = []; public byte[] ArchiveSpecFingerprint { get; set; } = []; public byte[] OutputLayoutFingerprint { get; set; } = []; public byte[] ExecutionSemanticFingerprint { get; set; } = []; public byte[] ExecutionBindingFingerprint { get; set; } = [];
    public byte[] RulesComponent { get; set; } = []; public byte[] BoundaryComponent { get; set; } = []; public byte[] LinkPolicyComponent { get; set; } = []; public byte[] ExternalMappingComponent { get; set; } = []; public byte[] FormatComponent { get; set; } = []; public byte[] CompressionComponent { get; set; } = []; public byte[] ProtectionComponent { get; set; } = []; public byte[] ManifestComponent { get; set; } = [];
}
internal sealed class ScheduleInstallationEntity { public byte[] PlanId { get; set; } = []; public byte[] DeviceId { get; set; } = []; public string Status { get; set; } = ""; public string? AdapterToken { get; set; } public string? OpaqueInstallationId { get; set; } public byte[]? InstalledIntentDigest { get; set; } public long UpdatedAtUtcMs { get; set; } public string? LastError { get; set; } }
internal sealed class MaintenanceStateEntity { public long MaintenanceStateRowId { get; set; } public byte[] PlanId { get; set; } = []; public byte[]? ArchiveUnitId { get; set; } public string Kind { get; set; } = ""; public string Status { get; set; } = ""; public string? Detail { get; set; } public long UpdatedAtUtcMs { get; set; } }
internal sealed class RetentionDeletionIntentEntity
{
    public byte[] ArchiveVersionId { get; set; } = []; public byte[] PlanId { get; set; } = []; public byte[] ArchiveUnitId { get; set; } = [];
    public byte[] SelectionId { get; set; } = []; public string Stage { get; set; } = ""; public string HistoryRelativePath { get; set; } = "";
    public byte[] ExpectedIntegritySha256 { get; set; } = []; public long ExpectedLength { get; set; } public long RetentionSemanticsVersion { get; set; }
    public long KeepLastVersionsCount { get; set; } public long SelectedAtUtcMs { get; set; } public long? CompletedAtUtcMs { get; set; }
}
