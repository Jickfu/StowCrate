using System.Security.Cryptography;
using StowCrate.Application.Archiving;
using StowCrate.Application.BackupPlans.Candidates;
using StowCrate.Core.ChangeDetection;
using StowCrate.Core.Filesystem;

namespace StowCrate.Infrastructure.Filesystem;

/// <summary>将已观察输入重新 no-follow 验证并复制到 private staging；writer 此后只接触 staging。</summary>
public sealed class ArchiveInputMaterializer : IArchiveInputMaterializer
{
    private readonly IArchiveBuildWorkspaceFactory workspaces;
    private readonly IPhysicalFileSystem fileSystem;
    public ArchiveInputMaterializer(IArchiveBuildWorkspaceFactory workspaces, IPhysicalFileSystem? fileSystem = null)
    {
        this.workspaces = workspaces; this.fileSystem = fileSystem ?? new SystemPhysicalFileSystem();
    }
    public async Task<MaterializedArchiveInput> MaterializeAsync(ArchiveBuildRequest request, ArchiveGeneratedContent generatedContent, CancellationToken cancellationToken)
    {
        var workspace = await workspaces.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new List<MaterializedArchiveEntry>();
            foreach (var entry in request.Archive.Candidate.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var staged = SafeCombine(workspace.StagingRoot, entry.ArchivePath.Value);
                if (entry.OwnerKind is CandidateEntryOwnerKind.Generated)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                    var bytes = entry.ArchivePath == request.Archive.Candidate.GeneratedMetadata.ManifestPath
                        ? generatedContent.ManifestBytes
                        : request.Archive.Candidate.GeneratedMetadata.RecoveryEnvelopePath is { } recoveryPath && entry.ArchivePath == recoveryPath && generatedContent.RecoveryEnvelopeBytes is { } recovery
                            ? recovery : throw new ArchiveMaterializationException(ArchiveBuildFailureCode.MaterializationFailed, "Generated archive content is missing.", entry.ArchivePath);
                    await File.WriteAllBytesAsync(staged, bytes, cancellationToken).ConfigureAwait(false);
                    result.Add(new(entry.ArchivePath, FileSystemEntryKind.File, staged));
                    continue;
                }
                var binding = ResolveBinding(request, entry);
                var source = entry.OwnerKind is CandidateEntryOwnerKind.Normal
                    ? SafeCombine(binding.PhysicalRoot, entry.ObservedPath!.Value.Value)
                    : entry.ObservedPath!.Value.IsRoot ? binding.PhysicalRoot : SafeCombine(binding.PhysicalRoot, entry.ObservedPath.Value.Value);
                await MaterializeEntryAsync(entry, source, staged, request.Archive.Capability.MetadataFeatures, cancellationToken).ConfigureAwait(false);
                result.Add(new(entry.ArchivePath, entry.Kind, staged, entry.LastWriteTimeUtc, entry.MetadataFlags, entry.Link));
            }
            // 子项创建会改变父目录 mtime；全部 materialize 后按深度逆序再次投影并验证 staging metadata。
            foreach (var entry in request.Archive.Candidate.Entries.Where(x => x.OwnerKind is not CandidateEntryOwnerKind.Generated)
                         .OrderByDescending(x => x.ArchivePath.Value.Count(character => character == '/')))
            {
                var staged = result.Single(x => x.ArchivePath == entry.ArchivePath).StagedPath;
                ApplyMetadata(entry, staged);
                Validate(entry, fileSystem.Inspect(staged), request.Archive.Capability.MetadataFeatures);
            }
            return new(workspace, result);
        }
        catch
        {
            try { await workspace.CleanupAsync(false, CancellationToken.None).ConfigureAwait(false); } catch { }
            await workspace.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task MaterializeEntryAsync(CandidateArchiveEntry expected, string source, string staged, ArchiveMetadataFeatures metadataFeatures, CancellationToken token)
    {
        var before = fileSystem.Inspect(source);
        Validate(expected, before, metadataFeatures);
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        switch (expected.Kind)
        {
            case FileSystemEntryKind.Directory: Directory.CreateDirectory(staged); break;
            case FileSystemEntryKind.File:
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                break;
            case FileSystemEntryKind.Link:
                if (expected.Link is null) Drift(expected, "Candidate link identity is missing.");
                if (expected.Link!.Kind is not LinkKind.SymbolicLink) Drift(expected, "Only symbolic links can be faithfully staged by this portable materializer.");
                if (before.LinkTargetIsDirectory) Directory.CreateSymbolicLink(staged, expected.Link.Target); else File.CreateSymbolicLink(staged, expected.Link.Target);
                break;
            default: Drift(expected, "Special objects cannot be materialized."); break;
        }
        ApplyMetadata(expected, staged);
        var after = fileSystem.Inspect(source);
        Validate(expected, after, metadataFeatures);
        var stagedObservation = fileSystem.Inspect(staged);
        Validate(expected, stagedObservation, metadataFeatures);
        if (expected.Kind is FileSystemEntryKind.File)
        {
            var stagedLength = new FileInfo(staged).Length;
            if (stagedLength != expected.Length) Drift(expected, "Staged length differs from Candidate.");
            if (expected.ContentIdentity.FullContentDigest is { } strict && await HashAsync(staged, token).ConfigureAwait(false) != strict) Drift(expected, "Strict staged SHA-256 differs from Candidate.");
            if (expected.ArchivePath.Name == ".backupignore")
            {
                if (expected.RawFileSha256 is null) Drift(expected, ".backupignore Candidate raw-byte SHA-256 is missing.");
                var raw = expected.RawFileSha256!.Value;
                if (await HashAsync(staged, token).ConfigureAwait(false) != raw) Drift(expected, ".backupignore raw-byte SHA-256 differs from Candidate.");
            }
        }
    }

    private static void ApplyMetadata(CandidateArchiveEntry expected, string staged)
    {
        if (expected.Kind is FileSystemEntryKind.Link) return;
        if (expected.LastWriteTimeUtc is { } mtime) File.SetLastWriteTimeUtc(staged, mtime.UtcDateTime);
        var attributes = File.GetAttributes(staged);
        attributes = expected.MetadataFlags.HasFlag(SourceMetadata.ReadOnly) ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly;
        attributes = expected.MetadataFlags.HasFlag(SourceMetadata.Hidden) ? attributes | FileAttributes.Hidden : attributes & ~FileAttributes.Hidden;
        File.SetAttributes(staged, attributes);
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(staged);
            const UnixFileMode execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            mode = expected.MetadataFlags.HasFlag(SourceMetadata.Executable) ? mode | execute : mode & ~execute;
            File.SetUnixFileMode(staged, mode);
        }
    }

    private static void Validate(CandidateArchiveEntry expected, PhysicalFileSystemEntry actual, ArchiveMetadataFeatures features)
    {
        if (actual.Kind != expected.Kind || actual.Length != expected.Length
            || (expected.Kind is not FileSystemEntryKind.Link && actual.LastWriteTimeUtc?.ToUniversalTime() != expected.LastWriteTimeUtc?.ToUniversalTime())
            || actual.MetadataFlags != expected.MetadataFlags || !StringComparer.Ordinal.Equals(actual.LinkTarget, expected.Link?.Target)
            || (expected.Link is not null && actual.LinkKind != expected.Link.Kind))
            Drift(expected, $"Input kind/size/UTC mtime/metadata/link identity drifted under mtime={features.PreservesMtime}, flags={features.PreservedFlags} semantics.");
    }

    private static ArchiveInputBinding ResolveBinding(ArchiveBuildRequest request, CandidateArchiveEntry entry) => request.InputBindings.SingleOrDefault(x =>
        x.OwnerKind == entry.OwnerKind && x.SourceId == entry.SourceId && x.ExternalSourceId == entry.ExternalSourceId)
        ?? throw new ArchiveMaterializationException(ArchiveBuildFailureCode.MaterializationFailed, "Physical input binding is missing.", entry.ArchivePath);

    private static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root); var combined = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException("Path escaped its runtime root.");
        return combined;
    }
    private static async Task<Sha256Digest> HashAsync(string path, CancellationToken token) { await using var stream = File.OpenRead(path); return new(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false))); }
    private static void Drift(CandidateArchiveEntry entry, string message) => throw new ArchiveMaterializationException(ArchiveBuildFailureCode.InputChangedDuringMaterialization, message, entry.ArchivePath);
}
