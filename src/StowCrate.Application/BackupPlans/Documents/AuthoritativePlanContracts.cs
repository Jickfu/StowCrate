using StowCrate.Core.BackupPlans;

namespace StowCrate.Application.BackupPlans.Documents;

public sealed record ValidatedBackupPlanDocument(PortableBackupPlan Plan, ReadOnlyMemory<byte> CanonicalUtf8Payload, string? CanonicalSourcePath = null);

/// <summary>Application 只依赖 portable plan；strict DTO/schema/reader 均封装在 Infrastructure。</summary>
public interface IBackupPlanDocumentSource
{
    Task<ValidatedBackupPlanDocument> ReadFileAsync(string path, CancellationToken cancellationToken);
    ValidatedBackupPlanDocument ReadCanonicalPayload(ReadOnlyMemory<byte> payload);
    ValidatedBackupPlanDocument Write(PortableBackupPlan plan);
}

public sealed class BackupPlanDocumentSourceException(string message, Exception? innerException = null) : Exception(message, innerException);
