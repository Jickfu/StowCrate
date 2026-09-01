using System.Collections.Immutable;
using System.Text.Json;
using StowCrate.Application.Archiving;
using StowCrate.Application.BackupPlans.Candidates;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;

namespace StowCrate.Archiving.Manifest;

/// <summary>Manifest v1 canonical JSON codec。属性顺序、枚举拼写与 UTC 时间格式均为冻结 contract。</summary>
public sealed class ArchiveManifestV1Codec : IArchiveManifestCodec
{
    public ReadOnlyMemory<byte> Write(ArchiveBuildRequest request)
    {
        var candidate = request.Archive.Candidate;
        var sourceId = candidate.Unit.SourceId;
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", 1);
            json.WriteNumber("archiveSemanticsVersion", candidate.GeneratedMetadata.ArchiveSemanticsVersion);
            json.WriteString("planId", request.PlanId.Value.ToString("D"));
            json.WriteString("sourceId", sourceId.Value.ToString("D"));
            json.WriteString("archiveUnitId", candidate.Unit.ArchiveUnitId.Value.ToString("D"));
            json.WriteString("unitLogicalPath", candidate.Unit.Root.Value);
            WriteSpec(json, candidate.Unit.ArchiveSpec);
            json.WriteStartArray("entries");
            foreach (var entry in candidate.Entries.Where(x => x.OwnerKind is not CandidateEntryOwnerKind.Generated).OrderBy(x => x.ArchivePath.Value, StringComparer.Ordinal)) WriteEntry(json, entry);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return stream.ToArray();
    }

    public ArchiveManifestValidationResult ReadAndValidate(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            var options = new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow };
            using var document = JsonDocument.Parse(bytes, options);
            RejectDuplicatesAndUnknown(document.RootElement, RootNames);
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new FormatException("Unsupported manifest schemaVersion.");
            var specElement = root.GetProperty("archiveSpec");
            RejectDuplicatesAndUnknown(specElement, SpecNames);
            var protectionElement = specElement.GetProperty("protection");
            RejectDuplicatesAndUnknown(protectionElement, ProtectionNames);
            var protectionKind = RequiredString(protectionElement, "kind");
            AuthoredProtection protection = protectionKind switch
            {
                "None" => new NoProtection(),
                "Privacy" => new PrivacyProtection(),
                "Secure" => new SecureProtection(ParseId<SecretSlotId>(RequiredString(protectionElement, "secretSlotId"), x => new(x))),
                _ => throw new FormatException("Unknown protection kind.")
            };
            if (protectionKind != "Secure" && protectionElement.TryGetProperty("secretSlotId", out _)) throw new FormatException("secretSlotId is only valid for Secure.");
            if (protectionKind == "Secure" && !protectionElement.TryGetProperty("secretSlotId", out _)) throw new FormatException("Secure requires secretSlotId.");
            var spec = new EffectiveArchiveSpec(ParseEnum<PortableArchiveFormat>(specElement, "format"), ParseEnum<PortableCompressionPreset>(specElement, "compressionPreset"), protection);
            var entries = ImmutableArray.CreateBuilder<ArchiveManifestEntry>();
            string? previous = null;
            foreach (var item in root.GetProperty("entries").EnumerateArray())
            {
                RejectDuplicatesAndUnknown(item, EntryNames);
                var path = new RelativePath(RequiredString(item, "path"));
                if (previous is not null && StringComparer.Ordinal.Compare(previous, path.Value) >= 0) throw new FormatException("Manifest entries must be unique and ordinally ordered.");
                previous = path.Value;
                DateTimeOffset? mtime = item.GetProperty("lastWriteTimeUtc").ValueKind is JsonValueKind.Null ? null : DateTimeOffset.Parse(item.GetProperty("lastWriteTimeUtc").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
                entries.Add(new ArchiveManifestEntry(path, ParseEnum<FileSystemEntryKind>(item, "kind"), ParseEnum<CandidateEntryOwnerKind>(item, "owner"),
                    item.GetProperty("length").GetInt64(), mtime, (SourceMetadata)item.GetProperty("metadata").GetInt32(), OptionalString(item, "linkTarget"), OptionalDigest(item, "fullContentSha256"), OptionalDigest(item, "rawFileSha256")));
            }
            var manifest = new ArchiveManifestV1(1, root.GetProperty("archiveSemanticsVersion").GetInt32(),
                ParseId<PlanId>(RequiredString(root, "planId"), x => new(x)), ParseId<SourceId>(RequiredString(root, "sourceId"), x => new(x)),
                ParseId<ArchiveUnitId>(RequiredString(root, "archiveUnitId"), x => new(x)), new LogicalPath(RequiredString(root, "unitLogicalPath")), spec, entries.ToImmutable());
            return new(manifest, []);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            return new(null, [new(ArchiveBuildFailureCode.ManifestInvalid, $"Manifest v1 validation failed: {ex.Message}")]);
        }
    }

    private static void WriteSpec(Utf8JsonWriter json, EffectiveArchiveSpec spec)
    {
        json.WriteStartObject("archiveSpec");
        json.WriteString("format", spec.Format.ToString()); json.WriteString("compressionPreset", spec.CompressionPreset.ToString());
        json.WriteStartObject("protection");
        json.WriteString("kind", spec.Protection switch { NoProtection => "None", PrivacyProtection => "Privacy", SecureProtection => "Secure", _ => throw new InvalidOperationException() });
        if (spec.Protection is SecureProtection secure) json.WriteString("secretSlotId", secure.SecretSlotId.Value.ToString("D"));
        json.WriteEndObject(); json.WriteEndObject();
    }

    private static void WriteEntry(Utf8JsonWriter json, CandidateArchiveEntry entry)
    {
        json.WriteStartObject(); json.WriteString("path", entry.ArchivePath.Value); json.WriteString("kind", entry.Kind.ToString()); json.WriteString("owner", entry.OwnerKind.ToString());
        json.WriteNumber("length", entry.Length); if (entry.LastWriteTimeUtc is null) json.WriteNull("lastWriteTimeUtc"); else json.WriteString("lastWriteTimeUtc", entry.LastWriteTimeUtc.Value.ToUniversalTime().ToString("O"));
        json.WriteNumber("metadata", (int)entry.MetadataFlags); WriteOptional(json, "linkTarget", entry.Link?.Target); WriteOptional(json, "fullContentSha256", entry.ContentIdentity.FullContentDigest?.Value); WriteOptional(json, "rawFileSha256", entry.RawFileSha256?.Value);
        json.WriteEndObject();
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value) { if (value is null) writer.WriteNull(name); else writer.WriteString(name, value); }
    private static string RequiredString(JsonElement element, string name) => element.GetProperty(name).GetString() ?? throw new FormatException($"{name} is required.");
    private static string? OptionalString(JsonElement element, string name) => element.GetProperty(name).ValueKind == JsonValueKind.Null ? null : element.GetProperty(name).GetString();
    private static Sha256Digest? OptionalDigest(JsonElement element, string name) => OptionalString(element, name) is { } value ? new(value) : null;
    private static T ParseEnum<T>(JsonElement element, string name) where T : struct, Enum => Enum.TryParse<T>(RequiredString(element, name), false, out var value) && Enum.IsDefined(value) ? value : throw new FormatException($"Unknown {name}.");
    private static T ParseId<T>(string value, Func<Guid, T> create) => Guid.TryParseExact(value, "D", out var id) && id.ToString("D") == value && id.Version == 4 ? create(id) : throw new FormatException("Identity must be canonical UUID v4.");
    private static void RejectDuplicatesAndUnknown(JsonElement element, HashSet<string> allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException("Object expected.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!allowed.Contains(property.Name) || !seen.Add(property.Name)) throw new FormatException("Unknown or duplicate property.");
        if (!allowed.All(name => seen.Contains(name)) && !ReferenceEquals(allowed, ProtectionNames)) throw new FormatException("Required property missing.");
    }
    private static readonly HashSet<string> RootNames = new(["schemaVersion","archiveSemanticsVersion","planId","sourceId","archiveUnitId","unitLogicalPath","archiveSpec","entries"], StringComparer.Ordinal);
    private static readonly HashSet<string> SpecNames = new(["format","compressionPreset","protection"], StringComparer.Ordinal);
    private static readonly HashSet<string> ProtectionNames = new(["kind","secretSlotId"], StringComparer.Ordinal);
    private static readonly HashSet<string> EntryNames = new(["path","kind","owner","length","lastWriteTimeUtc","metadata","linkTarget","fullContentSha256","rawFileSha256"], StringComparer.Ordinal);
}
