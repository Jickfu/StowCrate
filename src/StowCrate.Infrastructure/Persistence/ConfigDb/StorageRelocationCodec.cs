using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using StowCrate.Application.LocalState;
using StowCrate.Application.StorageMaintenance;

namespace StowCrate.Infrastructure.Persistence.ConfigDb;

internal static class StorageRelocationCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };
    private sealed record RootDto(int Kind, string OldPath, string OldKey, string NewPath, string NewKey,
        StorageObjectIdentity OldIdentity, StorageObjectIdentity NewIdentity);
    private sealed record EntryDto(Guid UnitId, int RootKind, Guid VersionId, string Sha256, long Length,
        string RelativePath, string TempRelativePath, StorageObjectIdentity OldIdentity);
    private sealed record ManifestV1Dto(int Version, Guid TransactionId, Guid PlanId, Guid DeviceId, string ExecutionDigest, RootDto[] Roots, EntryDto[] Entries);
    private sealed record ManifestV2Dto(int Version, Guid TransactionId, Guid PlanId, Guid DeviceId, RootDto[] Roots, EntryDto[] Entries);
    private sealed record ProgressEntryDto(Guid VersionId, int Stage, StorageObjectIdentity? Identity);
    private sealed record ProgressDto(int Version, int Stage, ProgressEntryDto[] Entries);
    internal sealed record ConfigurationDto(int Version, int FingerprintVersion, string Authority, string? Path, string Digest);

    internal static byte[] Encode(ConfigurationDto value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    internal static ConfigurationDto ReadConfiguration(byte[] bytes, byte[] digest)
    {
        var value = Read<ConfigurationDto>(bytes, digest);
        if (value.Version != 1 || value.FingerprintVersion != StorageRelocationConfigurationFingerprint.EncodingVersion
            || value.Authority is not ("MANAGED" or "FILE_BACKED")
            || (value.Authority == "MANAGED" ? value.Path is not null : string.IsNullOrWhiteSpace(value.Path)))
            throw new LocalStateCorruptionException("Invalid relocation configuration checkpoint.");
        _ = new StowCrate.Core.ChangeDetection.Sha256Digest(value.Digest);
        RequireCanonical(bytes, Encode(value));
        return value;
    }

    public static byte[] Encode(StorageRelocationManifest value)
    {
        var roots = value.Roots.Select(x => new RootDto((int)x.Kind, x.OldRoot.CanonicalPath, x.OldRoot.ComparisonKey, x.NewRoot.CanonicalPath,
            x.NewRoot.ComparisonKey, x.OldIdentity, x.NewIdentity)).ToArray();
        var entries = value.Entries.Select(x => new EntryDto(x.UnitId.Value, (int)x.RootKind, x.Artifact.VersionId.Value, x.Artifact.Integrity.Value,
            x.Artifact.Length, x.RelativePath.Value, x.TempRelativePath.Value, x.OldIdentity)).ToArray();
        return value.LegacyExecutionSemanticDigest is { } legacy
            ? JsonSerializer.SerializeToUtf8Bytes(new ManifestV1Dto(1, value.TransactionId, value.PlanId.Value, value.DeviceId.Value, legacy.Value, roots, entries), Options)
            : JsonSerializer.SerializeToUtf8Bytes(new ManifestV2Dto(2, value.TransactionId, value.PlanId.Value, value.DeviceId.Value, roots, entries), Options);
    }

    public static byte[] Encode(StorageTransferProgress value) => JsonSerializer.SerializeToUtf8Bytes(new ProgressDto(
        value.Stage == StorageTransferStage.Completed || value.Artifacts.Any(x => x.Stage == StorageTransferArtifactStage.OldCopyAbsent) ? 3
            : value.Stage == StorageTransferStage.MetadataCommitted ? 2 : 1, (int)value.Stage,
        [.. value.Artifacts.Select(x => new ProgressEntryDto(x.Artifact.VersionId.Value, (int)x.Stage, x.StagedIdentity))]), Options);

    public static StorageRelocationManifest ReadManifest(byte[] bytes, byte[] digest)
    {
        // 先验证完整 bytes 和显式编码版本；不同版本使用不同 closed-world DTO，不能把旧字段当 optional bag。
        var envelope = Read<JsonElement>(bytes, digest);
        if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("Version", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number))
            throw new LocalStateCorruptionException("Missing relocation manifest encoding version.");
        StorageRelocationManifest value;
        if (number == 1)
        {
            var dto = Read<ManifestV1Dto>(bytes, digest);
            value = new(dto.TransactionId, new(dto.PlanId), new(dto.DeviceId), new(dto.ExecutionDigest), Roots(dto.Roots), Entries(dto.Entries));
        }
        else if (number == 2)
        {
            var dto = Read<ManifestV2Dto>(bytes, digest);
            value = new(dto.TransactionId, new(dto.PlanId), new(dto.DeviceId), Roots(dto.Roots), Entries(dto.Entries));
        }
        else throw new LocalStateCorruptionException("Unknown relocation manifest encoding version.");
        RequireCanonical(bytes, Encode(value));
        return value;
    }

    private static IEnumerable<StorageRelocationRoot> Roots(RootDto[] roots)
        => roots.Select(x => new StorageRelocationRoot((StorageRootKind)x.Kind, new(x.OldPath, x.OldKey), new(x.NewPath, x.NewKey), x.OldIdentity, x.NewIdentity));

    private static IEnumerable<StorageRelocationEntry> Entries(EntryDto[] entries)
        => entries.Select(x => new StorageRelocationEntry(new(x.UnitId), (StorageRootKind)x.RootKind, new(new(x.VersionId), new(x.Sha256), x.Length),
            new(x.RelativePath), new(x.TempRelativePath), x.OldIdentity));

    public static StorageTransferProgress ReadProgress(StorageRelocationManifest manifest, byte[] bytes, byte[] digest)
    {
        var dto = Read<ProgressDto>(bytes, digest);
        if (!(dto.Version == 1 && dto.Stage is 0 or 1 || dto.Version == 2 && dto.Stage == 2 && dto.Entries.All(x => x.Stage == 2)
            || dto.Version == 3 && dto.Stage is 2 or 3)
            || dto.Entries.Length != manifest.Entries.Length) throw new LocalStateCorruptionException("Relocation progress manifest mismatch.");
        var items = manifest.Entries.ToDictionary(x => x.Artifact.VersionId.Value);
        var value = StorageTransferProgress.Restore(manifest.TransactionId, manifest.PlanId, (StorageTransferStage)dto.Stage,
            dto.Entries.Select(x => new StorageTransferArtifactProgress(items[x.VersionId].Artifact, (StorageTransferArtifactStage)x.Stage, x.Identity)));
        RequireCanonical(bytes, Encode(value));
        return value;
    }

    private static T Read<T>(byte[] bytes, byte[] digest)
    {
        if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(digest)) throw new LocalStateCorruptionException("Relocation payload integrity mismatch.");
        return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new LocalStateCorruptionException("Relocation payload is null.");
    }
    private static void RequireCanonical(byte[] actual, byte[] expected)
    {
        // canonical 重编码不可能保留重复字段、未知字段或属性重排；不默默接受数据库 payload 漂移。
        if (!actual.AsSpan().SequenceEqual(expected)) throw new LocalStateCorruptionException("Relocation payload is not canonical.");
    }
}
