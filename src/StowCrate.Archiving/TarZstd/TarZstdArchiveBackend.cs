using System.Collections.Immutable;
using System.Formats.Tar;
using System.Security.Cryptography;
using StowCrate.Application.Archiving;
using StowCrate.Application.BackupPlans.Candidates;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace StowCrate.Archiving.TarZstd;

public static class TarZstdSemantics
{
    public const string Version = "tar-pax-zstd-v1";
    public const string ZstdSharpVersion = "0.8.8";
    public const string ZstdVersion = "1.5.7";

    public static int Level(PortableCompressionPreset preset) => preset switch
    {
        PortableCompressionPreset.Fast => 1,
        PortableCompressionPreset.Standard => 6,
        PortableCompressionPreset.Extreme => 19,
        PortableCompressionPreset.Store => throw new NotSupportedException("TarZstd Store is unsupported; zstd level 0 is default compression, not storage."),
        _ => throw new ArgumentOutOfRangeException(nameof(preset))
    };
}

public sealed class TarZstdCapabilityResolver : IArchiveCapabilityResolver
{
    public ArchiveCapabilityResolution Resolve(ArchiveCapabilityRequirements requirements, int archiveSemanticsVersion)
    {
        var spec = requirements.ArchiveSpec;
        if (spec.Format is not PortableArchiveFormat.TarZstd) return new(null, "Managed TarZstd backend supports only TarZstd.");
        if (spec.CompressionPreset is PortableCompressionPreset.Store) return new(null, "TarZstd Store is unsupported.");
        if (spec.Protection is not NoProtection) return new(null, "TarZstd v1 supports None protection only; Privacy and Secure are unsupported.");
        if (requirements.RequiresSymbolicLinks && OperatingSystem.IsWindows()) return new(null, "TarZstd symbolic-link materialization is not enabled on Windows.");

        var flags = SourceMetadata.ReadOnly;
        if (!OperatingSystem.IsWindows()) flags |= SourceMetadata.Executable;
        var metadata = new ArchiveMetadataFeatures(true, flags);
        if (!metadata.Satisfies(requirements.RequiredMetadataFeatures)) return new(null, "TarZstd cannot preserve all required metadata flags on this RID.");
        var links = OperatingSystem.IsWindows() ? ArchiveLinkSemantics.NoLinks : ArchiveLinkSemantics.PreserveSymbolicLinks;
        var semantics = $"{TarZstdSemantics.Version};archive={archiveSemanticsVersion};zstdsharp={TarZstdSemantics.ZstdSharpVersion};zstd={TarZstdSemantics.ZstdVersion};level={TarZstdSemantics.Level(spec.CompressionPreset)};checksum=true;links={links};mtime=true;metadataFlags={flags};volume=single;protection=None";
        return new(new(spec.Format, spec.CompressionPreset, spec.Protection, links, metadata, true, semantics), null);
    }
}

public sealed class TarZstdArchiveFormatWriter : IArchiveFormatWriter
{
    public async Task WriteAsync(ArchiveWriteRequest request, CancellationToken cancellationToken)
    {
        if (request.ArchiveSpec.Format is not PortableArchiveFormat.TarZstd || request.ArchiveSpec.Protection is not NoProtection)
            throw new NotSupportedException("This writer exposes only TarZstd None v1.");

        await using var output = new FileStream(request.Input.Workspace.PartialArtifactPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        await using var zstd = new CompressionStream(output, TarZstdSemantics.Level(request.ArchiveSpec.CompressionPreset), 128 * 1024, true);
        zstd.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        await using var writer = new TarWriter(zstd, TarEntryFormat.Pax, true);
        foreach (var source in request.Input.Entries.OrderBy(x => x.ArchivePath.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = source.Kind switch
            {
                FileSystemEntryKind.File => TarEntryType.RegularFile,
                FileSystemEntryKind.Directory => TarEntryType.Directory,
                FileSystemEntryKind.Link => TarEntryType.SymbolicLink,
                _ => throw new NotSupportedException("TarZstd does not support special objects.")
            };
            var entry = new PaxTarEntry(type, source.ArchivePath.Value)
            {
                ModificationTime = source.LastWriteTimeUtc ?? DateTimeOffset.UnixEpoch,
                Uid = 0,
                Gid = 0,
                UserName = string.Empty,
                GroupName = string.Empty,
                Mode = Mode(source)
            };
            if (source.Kind is FileSystemEntryKind.File) entry.DataStream = File.OpenRead(source.StagedPath);
            if (source.Kind is FileSystemEntryKind.Link) entry.LinkName = source.Link?.Target ?? throw new InvalidOperationException("Materialized link identity is missing.");
            try { await writer.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false); }
            finally { if (entry.DataStream is not null) await entry.DataStream.DisposeAsync().ConfigureAwait(false); }
        }
    }

    private static UnixFileMode Mode(MaterializedArchiveEntry entry)
    {
        var mode = entry.Kind is FileSystemEntryKind.Directory
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        if (entry.MetadataFlags.HasFlag(SourceMetadata.ReadOnly)) mode &= ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite);
        if (entry.MetadataFlags.HasFlag(SourceMetadata.Executable)) mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        return mode;
    }
}

public sealed class TarZstdArchiveArtifactVerifier : IArchiveArtifactVerifier
{
    public async Task<ArchiveArtifactVerification> VerifyAsync(ArchiveVerificationRequest request, CancellationToken cancellationToken)
    {
        var entries = ImmutableArray.CreateBuilder<ArchiveArtifactEntry>();
        byte[]? manifest = null;
        byte[]? recovery = null;
        try
        {
            await using var input = File.OpenRead(request.PartialArtifactPath);
            await using var zstd = new DecompressionStream(input, 128 * 1024, true, false);
            using var reader = new TarReader(zstd, true);
            TarEntry? entry;
            while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false)) is not null)
            {
                var path = new RelativePath(entry.Name.TrimEnd('/'));
                var kind = entry.EntryType switch
                {
                    TarEntryType.Directory => FileSystemEntryKind.Directory,
                    TarEntryType.SymbolicLink => FileSystemEntryKind.Link,
                    TarEntryType.RegularFile or TarEntryType.V7RegularFile => FileSystemEntryKind.File,
                    _ => throw new InvalidDataException("Unsupported Tar entry kind.")
                };
                entries.Add(new(path, kind));
                if (path == request.ManifestPath)
                {
                    if (manifest is not null) throw new InvalidDataException("Archive contains duplicate manifest entries.");
                    using var buffer = new MemoryStream();
                    if (entry.DataStream is null) throw new InvalidDataException("Manifest has no data stream.");
                    await entry.DataStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                    manifest = buffer.ToArray();
                }
                else if (path == request.RecoveryEnvelopePath)
                {
                    if (recovery is not null) throw new InvalidDataException("Archive contains duplicate recovery entries.");
                    using var buffer = new MemoryStream();
                    if (entry.DataStream is null) throw new InvalidDataException("Recovery envelope has no data stream.");
                    await entry.DataStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                    recovery = buffer.ToArray();
                }
                else if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsArchiveCorruption(ex))
        {
            return new(false, [], ReadOnlyMemory<byte>.Empty, default, -1);
        }

        await using var artifact = File.OpenRead(request.PartialArtifactPath);
        var hash = new Sha256Digest(Convert.ToHexStringLower(await SHA256.HashDataAsync(artifact, cancellationToken).ConfigureAwait(false)));
        return new(true, entries.OrderBy(x => x.Path.Value, StringComparer.Ordinal).ToImmutableArray(), manifest ?? [], hash, artifact.Length, recovery);
    }

    private static bool IsArchiveCorruption(Exception exception) => exception is
        InvalidDataException or EndOfStreamException or IOException or ZstdException;
}
