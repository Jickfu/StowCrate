using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Json.Schema;

namespace StowCrate.Infrastructure.Configuration.BackupPlans.V1;

public sealed class BackupPlanDocumentV1Reader
{
    private const string SchemaResourceName =
        "StowCrate.Infrastructure.Configuration.BackupPlans.V1.backupplan-v1.schema.json";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly JsonSchema schema = LoadSchema();
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    public BackupPlanDocumentReadResult<BackupPlanDocumentV1> Read(ReadOnlySpan<byte> bytes)
    {
        var utf8 = StripBom(bytes);
        try
        {
            _ = StrictUtf8.GetCharCount(utf8);
        }
        catch (DecoderFallbackException exception)
        {
            return Failure(BackupPlanDocumentErrorCode.InvalidUtf8, "Document is not valid UTF-8.", exception);
        }

        try
        {
            DetectDuplicateProperties(utf8);
        }
        catch (DuplicateJsonPropertyException exception)
        {
            return BackupPlanDocumentReadResult.Failure<BackupPlanDocumentV1>(
                new BackupPlanDocumentError(
                    BackupPlanDocumentErrorCode.DuplicateProperty,
                    $"Duplicate JSON property '{exception.PropertyName}'.",
                    $"byte:{exception.BytePosition}"));
        }
        catch (JsonException exception)
        {
            return Failure(BackupPlanDocumentErrorCode.MalformedJson, "Document contains malformed JSON.", exception);
        }

        try
        {
            using var json = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });

            var versionError = DispatchSchemaVersion(json.RootElement);
            if (versionError is not null)
            {
                return BackupPlanDocumentReadResult.Failure<BackupPlanDocumentV1>(versionError);
            }

            var schemaResult = schema.Evaluate(json.RootElement, new EvaluationOptions
            {
                OutputFormat = OutputFormat.List
            });
            if (!schemaResult.IsValid)
            {
                return BackupPlanDocumentReadResult.Failure<BackupPlanDocumentV1>(
                    new BackupPlanDocumentError(
                        BackupPlanDocumentErrorCode.SchemaValidationFailed,
                        "Document does not satisfy the Backup Plan v1 structural schema.",
                        schemaResult.InstanceLocation.ToString()));
            }

            var document = json.RootElement.Deserialize<BackupPlanDocumentV1>(SerializerOptions);
            return document is null
                ? BackupPlanDocumentReadResult.Failure<BackupPlanDocumentV1>(
                    new BackupPlanDocumentError(
                        BackupPlanDocumentErrorCode.DeserializationFailed,
                        "Document could not be materialized as BackupPlanDocumentV1."))
                : BackupPlanDocumentReadResult.Success(document);
        }
        catch (JsonException exception)
        {
            return Failure(BackupPlanDocumentErrorCode.DeserializationFailed, "Document could not be materialized as BackupPlanDocumentV1.", exception);
        }
    }

    public BackupPlanDocumentReadResult<BackupPlanDocumentV1> Read(byte[] bytes) => Read(bytes.AsSpan());

    public BackupPlanDocumentReadResult<BackupPlanDocumentV1> Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Read(buffer.ToArray());
    }

    public async Task<BackupPlanDocumentReadResult<BackupPlanDocumentV1>> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Read(buffer.ToArray());
    }

    private static BackupPlanDocumentError? DispatchSchemaVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version))
        {
            return new BackupPlanDocumentError(
                BackupPlanDocumentErrorCode.MissingSchemaVersion,
                "Required property 'schemaVersion' is missing.",
                "/schemaVersion");
        }

        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt64(out var value) || value < 1)
        {
            return new BackupPlanDocumentError(
                BackupPlanDocumentErrorCode.InvalidSchemaVersion,
                "Property 'schemaVersion' must be a positive integer.",
                "/schemaVersion");
        }

        return value == 1
            ? null
            : new BackupPlanDocumentError(
                BackupPlanDocumentErrorCode.UnsupportedSchemaVersion,
                $"Schema version {value} is not supported by this reader.",
                "/schemaVersion");
    }

    private static void DetectDuplicateProperties(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });
        var objectProperties = new Stack<HashSet<string>>();

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objectProperties.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    _ = objectProperties.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var propertyName = reader.GetString()!;
                    if (!objectProperties.Peek().Add(propertyName))
                    {
                        throw new DuplicateJsonPropertyException(propertyName, reader.TokenStartIndex);
                    }

                    break;
            }
        }
    }

    private static ReadOnlySpan<byte> StripBom(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(Encoding.UTF8.Preamble) ? bytes[Encoding.UTF8.Preamble.Length..] : bytes;

    private static JsonSchema LoadSchema()
    {
        using var stream = typeof(BackupPlanDocumentV1Reader).Assembly.GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException($"Embedded schema resource '{SchemaResourceName}' was not found.");
        using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: true);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    private static BackupPlanDocumentReadResult<BackupPlanDocumentV1> Failure(
        BackupPlanDocumentErrorCode code,
        string message,
        Exception exception) => BackupPlanDocumentReadResult.Failure<BackupPlanDocumentV1>(
            new BackupPlanDocumentError(code, message, exception.Message));

    private sealed class DuplicateJsonPropertyException(string propertyName, long bytePosition) : Exception
    {
        public string PropertyName { get; } = propertyName;
        public long BytePosition { get; } = bytePosition;
    }
}
