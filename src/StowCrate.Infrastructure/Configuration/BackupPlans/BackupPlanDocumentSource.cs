using StowCrate.Application.BackupPlans.Documents;
using StowCrate.Core.BackupPlans;
using StowCrate.Infrastructure.Configuration.BackupPlans.V1;

namespace StowCrate.Infrastructure.Configuration.BackupPlans;

public sealed class BackupPlanDocumentSource : IBackupPlanDocumentSource
{
    public async Task<ValidatedBackupPlanDocument> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonicalPath = Path.GetFullPath(path);
        try
        {
            var bytes = await File.ReadAllBytesAsync(canonicalPath, cancellationToken).ConfigureAwait(false);
            var validated = Read(bytes);
            return validated with { CanonicalSourcePath = canonicalPath };
        }
        catch (BackupPlanDocumentSourceException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new BackupPlanDocumentSourceException("Authoritative File-backed document could not be read.", exception);
        }
    }

    public ValidatedBackupPlanDocument ReadCanonicalPayload(ReadOnlyMemory<byte> payload)
    {
        var validated = Read(payload);
        if (!payload.Span.SequenceEqual(validated.CanonicalUtf8Payload.Span))
            throw new BackupPlanDocumentSourceException("Managed payload is valid but not canonical deterministic writer output.");
        return validated;
    }

    public ValidatedBackupPlanDocument Write(PortableBackupPlan plan)
    {
        var result = new BackupPlanDocumentV1Writer().Write(plan);
        if (!result.IsSuccess) throw new BackupPlanDocumentSourceException(result.Error!.Message);
        return new(plan, result.Bytes!);
    }

    private static ValidatedBackupPlanDocument Read(ReadOnlyMemory<byte> payload)
    {
        var read = new BackupPlanDocumentV1Reader().Read(payload.ToArray());
        if (!read.IsSuccess) throw new BackupPlanDocumentSourceException(read.Error!.Message);
        var semantic = BackupPlanDocumentV1Mapper.Map(read.Document!);
        if (!semantic.IsSuccess) throw new BackupPlanDocumentSourceException("Backup Plan document is semantically invalid.");
        var written = new BackupPlanDocumentV1Writer().Write(semantic.Plan!);
        if (!written.IsSuccess) throw new BackupPlanDocumentSourceException(written.Error!.Message);
        return new(semantic.Plan!, written.Bytes!);
    }
}
