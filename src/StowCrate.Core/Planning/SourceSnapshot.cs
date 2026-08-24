using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;

namespace StowCrate.Core.Planning;

public sealed record SourceEntry
{
    public SourceEntry(
        LogicalPath path,
        SourceEntryKind kind,
        long length = 0,
        string? textContent = null,
        string? contentFingerprint = null)
    {
        if (path.IsRoot)
        {
            throw new ArgumentException("Source entry 不能使用 root 路径。", nameof(path));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (kind is SourceEntryKind.Directory && length != 0)
        {
            throw new ArgumentException("目录 entry 的 length 必须为 0。", nameof(length));
        }

        Path = path;
        Kind = kind;
        Length = length;
        TextContent = textContent;
        ContentFingerprint = contentFingerprint ?? ComputeDefaultFingerprint();
    }

    public LogicalPath Path { get; }

    public SourceEntryKind Kind { get; }

    public long Length { get; }

    public string? TextContent { get; }

    public string ContentFingerprint { get; }

    private string ComputeDefaultFingerprint()
    {
        var canonical = $"{Path.Value}\n{Kind}\n{Length}\n{TextContent}";
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
