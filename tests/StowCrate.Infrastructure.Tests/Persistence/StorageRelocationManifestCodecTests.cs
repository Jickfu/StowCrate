using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StowCrate.Application.LocalState;
using StowCrate.Application.StorageMaintenance;
using StowCrate.Core.ChangeDetection;
using StowCrate.Infrastructure.Persistence.ConfigDb;

namespace StowCrate.Infrastructure.Tests.Persistence;

public sealed class StorageRelocationManifestCodecTests
{
    [Fact]
    public void LegacyGoldenBytesRemainUnchangedAndV2OmitsLegacyField()
    {
        const string transaction = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        const string plan = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        const string device = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
        var digest = Sha256Digest.Hash("legacy"u8);
        var text = $$$"""{"Version":1,"TransactionId":"{{{transaction}}}","PlanId":"{{{plan}}}","DeviceId":"{{{device}}}","ExecutionDigest":"{{{digest.Value}}}","Roots":[{"Kind":0,"OldPath":"/old","OldKey":"/old","NewPath":"/new","NewKey":"/new","OldIdentity":{"Provider":"test","EncodingVersion":1,"Value":"old"},"NewIdentity":{"Provider":"test","EncodingVersion":1,"Value":"new"}}],"Entries":[]}""";
        var bytes = Encoding.UTF8.GetBytes(text);
        var legacy = StorageRelocationCodec.ReadManifest(bytes, SHA256.HashData(bytes));
        Assert.Equal(1, legacy.EncodingVersion);
        Assert.Equal(digest, legacy.LegacyExecutionSemanticDigest);
        Assert.Equal(bytes, StorageRelocationCodec.Encode(legacy));
        var current = new StorageRelocationManifest(legacy.TransactionId, legacy.PlanId, legacy.DeviceId, legacy.Roots, legacy.Entries);
        var encoded = StorageRelocationCodec.Encode(current);
        using var document = JsonDocument.Parse(encoded);
        Assert.Equal(2, document.RootElement.GetProperty("Version").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("ExecutionDigest", out _));
        var restored = StorageRelocationCodec.ReadManifest(encoded, SHA256.HashData(encoded));
        Assert.Null(restored.LegacyExecutionSemanticDigest);
        Assert.Equal(encoded, StorageRelocationCodec.Encode(restored));
        Assert.Equal(bytes, StorageRelocationCodec.Encode(legacy));
    }

    [Theory]
    [InlineData("future")]
    [InlineData("duplicate")]
    [InlineData("legacy-null")]
    [InlineData("legacy-value")]
    [InlineData("missing")]
    [InlineData("string-version")]
    [InlineData("wrong-reader")]
    [InlineData("hash")]
    public void V2RejectsAmbiguousOrWrongVersionPayload(string failure)
    {
        var manifest = new StorageRelocationManifest(Guid.NewGuid(), new(Guid.NewGuid()), new(Guid.NewGuid()),
            [new(StorageRootKind.Current, new("/old", "/old"), new("/new", "/new"), new("test", 1, "old"), new("test", 1, "new"))], []);
        var original = StorageRelocationCodec.Encode(manifest);
        var text = Encoding.UTF8.GetString(original);
        var replacement = failure switch
        {
            "future" => "\"Version\":3",
            "duplicate" => "\"Version\":2,\"Version\":2",
            "legacy-null" => "\"Version\":2,\"ExecutionDigest\":null",
            "legacy-value" => "\"Version\":2,\"ExecutionDigest\":\"" + Sha256Digest.Hash("legacy"u8).Value + "\"",
            "missing" => "\"Other\":2",
            "string-version" => "\"Version\":\"2\"",
            "wrong-reader" => "\"Version\":1",
            _ => "\"Version\":2",
        };
        var bytes = Encoding.UTF8.GetBytes(text.Replace("\"Version\":2", replacement, StringComparison.Ordinal));
        var hash = failure == "hash" ? new byte[32] : SHA256.HashData(bytes);
        var error = Record.Exception(() => StorageRelocationCodec.ReadManifest(bytes, hash));
        Assert.True(error is LocalStateCorruptionException or JsonException, error?.ToString() ?? "Expected strict rejection.");
        Assert.Equal(original, StorageRelocationCodec.Encode(manifest));
    }
}
