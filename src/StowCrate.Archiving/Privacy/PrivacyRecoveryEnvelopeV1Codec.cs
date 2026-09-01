using System.Security.Cryptography;
using System.Text.Json;
using StowCrate.Application.Archiving;
using StowCrate.Core.BackupPlans;

namespace StowCrate.Archiving.Privacy;

/// <summary>Privacy 恢复材料随归档公开携带，不提供机密性保证，也不得进入日志或 diagnostics。</summary>
public sealed class PrivacyRecoveryEnvelopeV1Codec : IPrivacyRecoveryEnvelopeCodec
{
    public const int SchemaVersion = 1;
    public const int PrivacySemanticsVersion = 1;
    public const int CarrierSemanticsVersion = 1;
    public const string Encoding = "base64url-no-padding";
    private static readonly string[] Names = ["schemaVersion", "privacySemanticsVersion", "carrierSemanticsVersion", "archiveFormat", "recoveryMaterialEncoding", "recoveryMaterial"];

    public ReadOnlyMemory<byte> Create(PortableArchiveFormat archiveFormat)
    {
        Span<byte> material = stackalloc byte[32];
        RandomNumberGenerator.Fill(material);
        try
        {
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                json.WriteNumber("schemaVersion", SchemaVersion);
                json.WriteNumber("privacySemanticsVersion", PrivacySemanticsVersion);
                json.WriteNumber("carrierSemanticsVersion", CarrierSemanticsVersion);
                json.WriteString("archiveFormat", archiveFormat.ToString());
                json.WriteString("recoveryMaterialEncoding", Encoding);
                json.WriteString("recoveryMaterial", Base64Url(material));
                json.WriteEndObject();
            }
            return stream.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(material); }
    }

    public PrivacyRecoveryEnvelopeValidationResult ReadAndValidate(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object) throw new FormatException("Recovery envelope must be an object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!Names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new FormatException("Unknown or duplicate recovery envelope property.");
            if (seen.Count != Names.Length) throw new FormatException("Recovery envelope properties are incomplete.");
            if (root.GetProperty("schemaVersion").GetInt32() != SchemaVersion
                || root.GetProperty("privacySemanticsVersion").GetInt32() != PrivacySemanticsVersion
                || root.GetProperty("carrierSemanticsVersion").GetInt32() != CarrierSemanticsVersion) throw new FormatException("Unsupported recovery envelope semantics.");
            var format = root.GetProperty("archiveFormat").GetString() switch
            {
                "SevenZip" => PortableArchiveFormat.SevenZip,
                "Zip" => PortableArchiveFormat.Zip,
                "TarZstd" => PortableArchiveFormat.TarZstd,
                _ => throw new FormatException("Unknown recovery archiveFormat token.")
            };
            var encoding = root.GetProperty("recoveryMaterialEncoding").GetString()!;
            if (encoding != Encoding) throw new FormatException("Unsupported recovery material encoding.");
            var material = root.GetProperty("recoveryMaterial").GetString()!;
            if (material.Contains('=') || Decode(material).Length != 32) throw new FormatException("Recovery material must be 32-byte unpadded base64url.");
            return new(new(SchemaVersion, PrivacySemanticsVersion, CarrierSemanticsVersion, format, encoding, material), []);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            return new(null, [new(ArchiveBuildFailureCode.ManifestInvalid, $"Privacy Recovery Envelope v1 validation failed: {ex.Message}")]);
        }
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string value)
    {
        if (value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))) throw new FormatException("Recovery material is not base64url ASCII.");
        var padded = value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}
