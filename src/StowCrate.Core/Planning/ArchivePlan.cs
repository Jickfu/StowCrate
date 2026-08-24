using System.Collections.ObjectModel;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;

namespace StowCrate.Core.Planning;

public sealed record ArchiveEntry(
    LogicalPath SourcePath,
    RelativePath ArchivePath,
    FileSystemEntryKind Kind,
    long Length,
    string SourceFingerprint,
    DateTimeOffset? LastWriteTimeUtc,
    LinkInfo? Link,
    SourceMetadata MetadataFlags);

public sealed class PlannedArchive
{
    public PlannedArchive(
        ArchiveUnit archiveUnit,
        RelativePath outputPath,
        IEnumerable<ArchiveEntry> entries,
        string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(archiveUnit);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        ArchiveUnit = archiveUnit;
        OutputPath = outputPath;
        Entries = new ReadOnlyCollection<ArchiveEntry>(
            entries
                .OrderBy(entry => entry.ArchivePath.Value, StringComparer.Ordinal)
                .ThenBy(entry => entry.Kind)
                .ToArray());
        Fingerprint = fingerprint;
    }

    public ArchiveUnit ArchiveUnit { get; }

    public RelativePath OutputPath { get; }

    public IReadOnlyList<ArchiveEntry> Entries { get; }

    public string Fingerprint { get; }
}

public sealed class ArchivePlan
{
    public ArchivePlan(
        string backupPlanId,
        BackupSource source,
        ArchiveUnitTree archiveUnitTree,
        IEnumerable<PlannedArchive> archives,
        string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPlanId);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(archiveUnitTree);
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        BackupPlanId = backupPlanId;
        Source = source;
        ArchiveUnitTree = archiveUnitTree;
        Archives = new ReadOnlyCollection<PlannedArchive>(
            archives.OrderBy(archive => archive.OutputPath.Value, StringComparer.Ordinal).ToArray());
        Fingerprint = fingerprint;
    }

    public string BackupPlanId { get; }

    public BackupSource Source { get; }

    public ArchiveUnitTree ArchiveUnitTree { get; }

    public IReadOnlyList<PlannedArchive> Archives { get; }

    public string Fingerprint { get; }
}
