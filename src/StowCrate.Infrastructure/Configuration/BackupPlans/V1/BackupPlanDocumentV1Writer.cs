using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using StowCrate.Core.BackupPlans;
using StowCrate.Infrastructure.Configuration.BackupPlans;

namespace StowCrate.Infrastructure.Configuration.BackupPlans.V1;

public enum BackupPlanDocumentWriteErrorCode
{
    SemanticValidationFailed,
    PostconditionValidationFailed
}

public sealed record BackupPlanDocumentWriteError(
    BackupPlanDocumentWriteErrorCode Code,
    string Message,
    IReadOnlyList<BackupPlanSemanticError>? SemanticErrors = null,
    BackupPlanDocumentError? DocumentError = null);

public sealed record BackupPlanDocumentWriteResult(byte[]? Bytes, BackupPlanDocumentWriteError? Error)
{
    public bool IsSuccess => Bytes is not null;
}

public sealed class BackupPlanDocumentV1Writer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Default,
        NewLine = "\n",
        IndentCharacter = ' ',
        IndentSize = 2
    };

    private readonly BackupPlanDocumentV1Reader reader = new();

    public BackupPlanDocumentWriteResult Write(PortableBackupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        BackupPlanDocumentV1 document;
        try
        {
            document = BackupPlanDocumentV1Projector.Project(plan);
        }
        catch (BackupPlanDocumentProjectionException exception)
        {
            return new BackupPlanDocumentWriteResult(
                null,
                new BackupPlanDocumentWriteError(
                    BackupPlanDocumentWriteErrorCode.SemanticValidationFailed,
                    exception.Message,
                    exception.Errors));
        }

        var serialized = JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions);
        var bytes = new byte[serialized.Length + 1];
        serialized.CopyTo(bytes, 0);
        bytes[^1] = (byte)'\n';

        var postcondition = reader.Read(bytes);
        if (!postcondition.IsSuccess)
        {
            return new BackupPlanDocumentWriteResult(
                null,
                new BackupPlanDocumentWriteError(
                    BackupPlanDocumentWriteErrorCode.PostconditionValidationFailed,
                    "Generated document failed strict reader/Schema postcondition validation.",
                    DocumentError: postcondition.Error));
        }

        return new BackupPlanDocumentWriteResult(bytes, null);
    }

    public BackupPlanDocumentWriteError? Write(Stream stream, PortableBackupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var result = Write(plan);
        if (!result.IsSuccess)
        {
            return result.Error;
        }

        stream.Write(result.Bytes!);
        return null;
    }

    public async Task<BackupPlanDocumentWriteError?> WriteAsync(
        Stream stream,
        PortableBackupPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var result = Write(plan);
        if (!result.IsSuccess)
        {
            return result.Error;
        }

        await stream.WriteAsync(result.Bytes!, cancellationToken).ConfigureAwait(false);
        return null;
    }
}
