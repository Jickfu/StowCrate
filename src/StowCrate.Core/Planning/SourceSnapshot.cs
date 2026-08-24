using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;

namespace StowCrate.Core.Planning;

public sealed record SourceEntry
{
    public SourceEntry(
        LogicalPath path,
        FileSystemEntryKind kind,
        long length = 0,
        string? textContent = null,
        string? contentFingerprint = null,
        DateTimeOffset? lastWriteTimeUtc = null,
        LinkInfo? link = null,
        SourceMetadata metadataFlags = SourceMetadata.None)
    {
        if (path.IsRoot)
        {
            throw new ArgumentException("Source entry 不能使用 root 路径。", nameof(path));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (kind is not FileSystemEntryKind.File && length != 0)
        {
            throw new ArgumentException("只有 File entry 可以具有非零 length。", nameof(length));
        }

        if ((kind is FileSystemEntryKind.Link) != (link is not null))
        {
            throw new ArgumentException("Link entry 必须且只有 Link entry 可以携带 LinkInfo。", nameof(link));
        }

        Path = path;
        Kind = kind;
        Length = length;
        TextContent = textContent;
        LastWriteTimeUtc = lastWriteTimeUtc?.ToUniversalTime();
        Link = link;
        MetadataFlags = metadataFlags;
        ContentFingerprint = contentFingerprint ?? ComputeDefaultFingerprint();
    }

    public LogicalPath Path { get; }

    public FileSystemEntryKind Kind { get; }

    public long Length { get; }

    public string? TextContent { get; }

    public DateTimeOffset? LastWriteTimeUtc { get; }

    public LinkInfo? Link { get; }

    public SourceMetadata MetadataFlags { get; }

    public string ContentFingerprint { get; }

    private string ComputeDefaultFingerprint()
    {
        var canonical = string.Join(
            '\n',
            Path.Value,
            Kind,
            Length,
            LastWriteTimeUtc?.ToString("O") ?? string.Empty,
            MetadataFlags,
            Link?.Kind.ToString() ?? string.Empty,
            Link?.Target ?? string.Empty,
            Link?.TargetScope.ToString() ?? string.Empty,
            Link?.IsDangling.ToString() ?? string.Empty,
            TextContent ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public sealed class SourceSnapshot
{
    public SourceSnapshot(
        BackupSource source,
        CaseSensitivity fileSystemCaseSensitivity,
        IEnumerable<SourceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(entries);

        if (fileSystemCaseSensitivity is CaseSensitivity.Auto)
        {
            throw new ArgumentException("SourceSnapshot 必须记录已解析的 case sensitivity。", nameof(fileSystemCaseSensitivity));
        }

        var sortedEntries = entries
            .OrderBy(entry => entry.Path.Value, StringComparer.Ordinal)
            .ThenBy(entry => entry.Kind)
            .ToArray();
        var duplicatePath = sortedEntries
            .GroupBy(entry => entry.Path)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicatePath is not null)
        {
            throw new ArgumentException($"SourceSnapshot 包含重复路径：{duplicatePath.Key}", nameof(entries));
        }

        Source = source;
        FileSystemCaseSensitivity = fileSystemCaseSensitivity;
        Entries = new ReadOnlyCollection<SourceEntry>(sortedEntries);
    }

    public BackupSource Source { get; }

    public CaseSensitivity FileSystemCaseSensitivity { get; }

    public IReadOnlyList<SourceEntry> Entries { get; }
}
