namespace StowCrate.Infrastructure.Configuration.BackupPlans;

public enum BackupPlanDocumentErrorCode
{
    InvalidUtf8,
    MalformedJson,
    DuplicateProperty,
    MissingSchemaVersion,
    InvalidSchemaVersion,
    UnsupportedSchemaVersion,
    SchemaValidationFailed,
    DeserializationFailed
}

public sealed record BackupPlanDocumentError(
    BackupPlanDocumentErrorCode Code,
    string Message,
    string? Location = null);

public sealed record BackupPlanDocumentReadResult<T>(T? Document, BackupPlanDocumentError? Error)
    where T : class
{
    public bool IsSuccess => Document is not null;
}

public static class BackupPlanDocumentReadResult
{
    public static BackupPlanDocumentReadResult<T> Success<T>(T document) where T : class => new(document, null);

    public static BackupPlanDocumentReadResult<T> Failure<T>(BackupPlanDocumentError error) where T : class => new(null, error);
}
