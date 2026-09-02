using System.Text;
using StowCrate.Application.LocalState;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Core.Paths;

namespace StowCrate.Infrastructure.Persistence.ConfigDb;

internal static class DurableCodecs
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Uuid(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!value.TryWriteBytes(bytes, bigEndian: true, out var written) || written != 16) throw new LocalStateCorruptionException("UUID could not be encoded.");
        return bytes.ToArray();
    }

    public static Guid Uuid(byte[] bytes)
    {
        RequireLength(bytes, 16, "UUID");
        return new Guid(bytes, bigEndian: true);
    }

    public static byte[] Digest(Sha256Digest value) => Convert.FromHexString(value.Value);
    public static Sha256Digest Digest(byte[] bytes) { RequireLength(bytes, 32, "digest"); return new(Convert.ToHexStringLower(bytes)); }
    public static long Utc(DateTimeOffset value) => value.ToUniversalTime().ToUnixTimeMilliseconds();
    public static DateTimeOffset Utc(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);
    public static long Boolean(bool value) => value ? 1L : 0L;
    public static bool Boolean(long value) => value switch { 0 => false, 1 => true, _ => throw new LocalStateCorruptionException("Boolean must be encoded as 0 or 1.") };
    public static string LogicalPath(string value) => new LogicalPath(value.Normalize(NormalizationForm.FormC)).Value;
    public static string RelativePath(string value) => new RelativeStoragePath(value.Normalize(NormalizationForm.FormC)).Value;
    public static byte[] Utf8(ReadOnlyMemory<byte> value)
    {
        try { _ = StrictUtf8.GetCharCount(value.Span); return value.ToArray(); }
        catch (DecoderFallbackException exception) { throw new LocalStateCorruptionException("Payload is not strict UTF-8.", exception); }
    }

    public static string Token(PlanAuthority value) => value switch { PlanAuthority.Managed => "MANAGED", PlanAuthority.FileBacked => "FILE_BACKED", _ => throw Unknown(value) };
    public static PlanAuthority Authority(string value) => value switch { "MANAGED" => PlanAuthority.Managed, "FILE_BACKED" => PlanAuthority.FileBacked, _ => throw Unknown(value) };
    public static string Token(PortableArchiveFormat value) => value switch { PortableArchiveFormat.SevenZip => "SEVEN_ZIP", PortableArchiveFormat.Zip => "ZIP", PortableArchiveFormat.TarZstd => "TAR_ZSTD", _ => throw Unknown(value) };
    public static PortableArchiveFormat ArchiveFormat(string value) => value switch { "SEVEN_ZIP" => PortableArchiveFormat.SevenZip, "ZIP" => PortableArchiveFormat.Zip, "TAR_ZSTD" => PortableArchiveFormat.TarZstd, _ => throw Unknown(value) };
    public static string Token(ArchiveVersionLifecycle value) => value switch { ArchiveVersionLifecycle.Prepared => "PREPARED", ArchiveVersionLifecycle.Verified => "VERIFIED", ArchiveVersionLifecycle.Published => "PUBLISHED", ArchiveVersionLifecycle.Superseded => "SUPERSEDED", _ => throw Unknown(value) };
    public static ArchiveVersionLifecycle Lifecycle(string value) => value switch { "PREPARED" => ArchiveVersionLifecycle.Prepared, "VERIFIED" => ArchiveVersionLifecycle.Verified, "PUBLISHED" => ArchiveVersionLifecycle.Published, "SUPERSEDED" => ArchiveVersionLifecycle.Superseded, _ => throw Unknown(value) };
    public static string Token(PublishIntentStage value) => value switch { PublishIntentStage.Prepared => "PREPARED", PublishIntentStage.HistoryCaptured => "HISTORY_CAPTURED", PublishIntentStage.CurrentPublished => "CURRENT_PUBLISHED", PublishIntentStage.MetadataCommitted => "METADATA_COMMITTED", _ => throw Unknown(value) };
    public static PublishIntentStage PublishStage(string value) => value switch { "PREPARED" => PublishIntentStage.Prepared, "HISTORY_CAPTURED" => PublishIntentStage.HistoryCaptured, "CURRENT_PUBLISHED" => PublishIntentStage.CurrentPublished, "METADATA_COMMITTED" => PublishIntentStage.MetadataCommitted, _ => throw Unknown(value) };
    public static string Token(HistoryCaptureRequirement value) => value switch { HistoryCaptureRequirement.Required => "REQUIRED", HistoryCaptureRequirement.NotRequired => "NOT_REQUIRED", HistoryCaptureRequirement.UnknownLegacy => "UNKNOWN_LEGACY", _ => throw Unknown(value) };
    public static HistoryCaptureRequirement HistoryRequirement(string value) => value switch { "REQUIRED" => HistoryCaptureRequirement.Required, "NOT_REQUIRED" => HistoryCaptureRequirement.NotRequired, "UNKNOWN_LEGACY" => HistoryCaptureRequirement.UnknownLegacy, _ => throw Unknown(value) };
    public static string Token(Application.LocalState.RetentionDeletionStage value) => value switch { Application.LocalState.RetentionDeletionStage.Prepared => "PREPARED", Application.LocalState.RetentionDeletionStage.Completed => "COMPLETED", _ => throw Unknown(value) };
    public static RetentionDeletionStage RetentionDeletionStage(string value) => value switch { "PREPARED" => Application.LocalState.RetentionDeletionStage.Prepared, "COMPLETED" => Application.LocalState.RetentionDeletionStage.Completed, _ => throw Unknown(value) };
    public static string Token(ScheduleInstallationStatus value) => value switch { ScheduleInstallationStatus.NotInstalled => "NOT_INSTALLED", ScheduleInstallationStatus.Installed => "INSTALLED", ScheduleInstallationStatus.OutOfSync => "OUT_OF_SYNC", ScheduleInstallationStatus.Error => "ERROR", _ => throw Unknown(value) };
    public static ScheduleInstallationStatus ScheduleStatus(string value) => value switch { "NOT_INSTALLED" => ScheduleInstallationStatus.NotInstalled, "INSTALLED" => ScheduleInstallationStatus.Installed, "OUT_OF_SYNC" => ScheduleInstallationStatus.OutOfSync, "ERROR" => ScheduleInstallationStatus.Error, _ => throw Unknown(value) };
    public static string Token(MaintenanceStatus value) => value switch { Application.LocalState.MaintenanceStatus.Pending => "PENDING", Application.LocalState.MaintenanceStatus.OutOfSync => "OUT_OF_SYNC", Application.LocalState.MaintenanceStatus.Completed => "COMPLETED", _ => throw Unknown(value) };
    public static MaintenanceStatus MaintenanceStatus(string value) => value switch { "PENDING" => Application.LocalState.MaintenanceStatus.Pending, "OUT_OF_SYNC" => Application.LocalState.MaintenanceStatus.OutOfSync, "COMPLETED" => Application.LocalState.MaintenanceStatus.Completed, _ => throw Unknown(value) };
    public static string Token(MaintenanceKind value) => value switch { Application.LocalState.MaintenanceKind.HistoryRetention => "HISTORY_RETENTION", Application.LocalState.MaintenanceKind.OldCurrentPathCleanup => "OLD_CURRENT_PATH_CLEANUP", Application.LocalState.MaintenanceKind.StorageRelocation => "STORAGE_RELOCATION", Application.LocalState.MaintenanceKind.OutputReorganization => "OUTPUT_REORGANIZATION", Application.LocalState.MaintenanceKind.ScheduleReconciliation => "SCHEDULE_RECONCILIATION", _ => throw Unknown(value) };
    public static MaintenanceKind MaintenanceKind(string value) => value switch { "HISTORY_RETENTION" => Application.LocalState.MaintenanceKind.HistoryRetention, "OLD_CURRENT_PATH_CLEANUP" => Application.LocalState.MaintenanceKind.OldCurrentPathCleanup, "STORAGE_RELOCATION" => Application.LocalState.MaintenanceKind.StorageRelocation, "OUTPUT_REORGANIZATION" => Application.LocalState.MaintenanceKind.OutputReorganization, "SCHEDULE_RECONCILIATION" => Application.LocalState.MaintenanceKind.ScheduleReconciliation, _ => throw Unknown(value) };

    private static void RequireLength(byte[]? bytes, int length, string kind) { if (bytes is null || bytes.Length != length) throw new LocalStateCorruptionException($"{kind} must contain exactly {length} bytes."); }
    private static LocalStateCorruptionException Unknown(object value) => new($"Unknown durable token '{value}'.");
}
