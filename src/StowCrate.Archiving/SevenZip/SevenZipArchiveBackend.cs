using System.Collections.Immutable;
using System.Security.Cryptography;
using StowCrate.Application.Archiving;
using StowCrate.Application.BackupPlans.Candidates;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;

namespace StowCrate.Archiving.SevenZip;

public static class SevenZipArgumentMapping
{
    public static int Level(PortableCompressionPreset preset) => preset switch
    {
        PortableCompressionPreset.Store => 0, PortableCompressionPreset.Fast => 3,
        PortableCompressionPreset.Standard => 5, PortableCompressionPreset.Extreme => 9,
        _ => throw new ArgumentOutOfRangeException(nameof(preset))
    };

    public static ImmutableArray<string> Create(EffectiveArchiveSpec spec, string archivePath, string input = ".")
    {
        if (spec.Protection is not NoProtection) throw new NotSupportedException("This backend instance exposes only proven None protection.");
        var arguments = new List<string> { "a", spec.Format is PortableArchiveFormat.SevenZip ? "-t7z" : "-tzip", $"-mx={Level(spec.CompressionPreset)}", "-bd", "-bb0", "-y" };
        if (spec.Format is PortableArchiveFormat.SevenZip) { arguments.Add("-m0=lzma2"); arguments.Add("-ms=on"); }
        else if (spec.Format is PortableArchiveFormat.Zip)
        {
            arguments.Add(spec.CompressionPreset is PortableCompressionPreset.Store ? "-mm=Copy" : "-mm=Deflate");
            arguments.Add("-mcu=on");
        }
        else throw new NotSupportedException("TarZstd is not a 7-Zip M4.2 packing backend.");
        arguments.Add("-scsUTF-8"); arguments.Add(archivePath); arguments.Add(input);
        return [.. arguments];
    }
}

public sealed class SevenZipArchiveFormatWriter(string executablePath, SevenZipProcessRunner runner) : IArchiveFormatWriter
{
    public async Task WriteAsync(ArchiveWriteRequest request, CancellationToken cancellationToken)
    {
        if (request.SecretLease is not null) throw new NotSupportedException("Secure stdin transport is not proven for 7-Zip 26.02.");
        var listPath = request.Input.Workspace.PartialArtifactPath + ".inputs";
        try
        {
            await File.WriteAllLinesAsync(listPath, request.Input.Entries.Select(entry => entry.ArchivePath.Value), new System.Text.UTF8Encoding(false, true), cancellationToken).ConfigureAwait(false);
            var result = await runner.RunAsync(new(executablePath, request.Input.Workspace.StagingRoot,
                SevenZipArgumentMapping.Create(request.ArchiveSpec, request.Input.Workspace.PartialArtifactPath, "@" + listPath), null), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new SevenZipBackendException("7-Zip archive creation failed.", result.ExitCode);
        }
        finally { try { File.Delete(listPath); } catch { } }
    }
}

public sealed class SevenZipArchiveArtifactVerifier(string executablePath, SevenZipProcessRunner runner) : IArchiveArtifactVerifier
{
    public async Task<ArchiveArtifactVerification> VerifyAsync(ArchiveVerificationRequest request, CancellationToken cancellationToken)
    {
        if (request.SecretLease is not null) throw new NotSupportedException("Secure stdin transport is not proven for 7-Zip 26.02.");
        var type = request.ArchiveSpec.Format is PortableArchiveFormat.SevenZip ? "-t7z" : "-tzip";
        var test = await runner.RunAsync(new(executablePath, null, ["t", type, "-bd", "-bb0", request.PartialArtifactPath], null), cancellationToken).ConfigureAwait(false);
        if (test.ExitCode != 0) return new(false, [], ReadOnlyMemory<byte>.Empty, default, -1);
        var list = await runner.RunAsync(new(executablePath, null, ["l", type, "-slt", "-ba", request.PartialArtifactPath], null), cancellationToken).ConfigureAwait(false);
        if (list.ExitCode != 0) throw new SevenZipBackendException("7-Zip technical listing failed.", list.ExitCode);
        var entries = SevenZipTechnicalListParser.Parse(list.StandardOutput);
        var manifest = await runner.RunBinaryAsync(new(executablePath, null,
            ["x", type, "-so", "-bd", "-bb0", request.PartialArtifactPath, request.ManifestPath.Value], null), cancellationToken).ConfigureAwait(false);
        if (manifest.ExitCode != 0) throw new SevenZipBackendException("7-Zip manifest read failed.", manifest.ExitCode);
        await using var stream = File.OpenRead(request.PartialArtifactPath);
        var hash = new Sha256Digest(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)));
        return new(true, entries, manifest.StandardOutput, hash, stream.Length);
    }
}

public static class SevenZipTechnicalListParser
{
    public static ImmutableArray<ArchiveArtifactEntry> Parse(string text)
    {
        var entries = ImmutableArray.CreateBuilder<ArchiveArtifactEntry>();
        string? path = null; bool? folder = null;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Length == 0) { Add(); continue; }
            var separator = raw.IndexOf(" = ", StringComparison.Ordinal); if (separator < 0) continue;
            var key = raw[..separator]; var value = raw[(separator + 3)..];
            if (key == "Path") path = value.Replace('\\', '/');
            else if (key == "Folder") folder = value == "+";
            else if (key == "Attributes") folder = value.Contains('D', StringComparison.Ordinal);
        }
        Add();
        return entries.OrderBy(x => x.Path.Value, StringComparer.Ordinal).ToImmutableArray();
        void Add()
        {
            if (path is not null && folder is not null && path != ".") entries.Add(new(new RelativePath(path.TrimEnd('/')), folder.Value ? FileSystemEntryKind.Directory : FileSystemEntryKind.File));
            path = null; folder = null;
        }
    }
}

public sealed class SevenZipBackendException(string message, int exitCode) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
