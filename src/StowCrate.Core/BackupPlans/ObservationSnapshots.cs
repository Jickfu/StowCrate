using System.Collections.Immutable;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;

namespace StowCrate.Core.BackupPlans;

public enum ObservationCompleteness { Complete, Incomplete }
public enum ObservationIssueSeverity { Info, Warning, Fatal }

public sealed record ObservationIssue(
    ObservationIssueSeverity Severity,
    string Code,
    LogicalPath? Path,
    string Message);

public sealed class ObservationResult<TSnapshot> where TSnapshot : class
{
    public ObservationResult(TSnapshot? snapshot, IEnumerable<ObservationIssue> issues)
    {
        Snapshot = snapshot;
        Issues = [.. issues];
    }

    public TSnapshot? Snapshot { get; }
    public ImmutableArray<ObservationIssue> Issues { get; }
    public bool HasSnapshot => Snapshot is not null;
}

public sealed record ObservedFileSystemEntry(
    LogicalPath Path,
    FileSystemEntryKind Kind,
    long Length,
    string? TextContent,
    string ContentFingerprint,
    DateTimeOffset? LastWriteTimeUtc,
    LinkInfo? Link,
    SourceMetadata MetadataFlags);

public sealed class SourceObservationSnapshot
{
    public SourceObservationSnapshot(
        SourceId sourceId,
        CaseSensitivity fileSystemCaseSensitivity,
        IEnumerable<ObservedFileSystemEntry> entries,
        IEnumerable<ObservationIssue> issues,
        ObservationCompleteness completeness)
    {
        if (fileSystemCaseSensitivity is CaseSensitivity.Auto)
        {
            throw new ArgumentException("Source observation requires resolved case sensitivity.", nameof(fileSystemCaseSensitivity));
        }

        SourceId = sourceId;
        FileSystemCaseSensitivity = fileSystemCaseSensitivity;
        Entries = FreezeEntries(entries, allowRoot: false);
        Issues = [.. issues];
        Completeness = completeness;
    }

    public SourceId SourceId { get; }
    public CaseSensitivity FileSystemCaseSensitivity { get; }
    public ImmutableArray<ObservedFileSystemEntry> Entries { get; }
    public ImmutableArray<ObservationIssue> Issues { get; }
    public ObservationCompleteness Completeness { get; }

    internal static ImmutableArray<ObservedFileSystemEntry> FreezeEntries(
        IEnumerable<ObservedFileSystemEntry> entries,
        bool allowRoot)
    {
        var frozen = entries.OrderBy(entry => entry.Path.Value, StringComparer.Ordinal).ToImmutableArray();
        if ((!allowRoot && frozen.Any(entry => entry.Path.IsRoot))
            || frozen.GroupBy(entry => entry.Path).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Observation contains an invalid or duplicate logical path.", nameof(entries));
        }

        return frozen;
    }
}

public enum ExternalObservedRootKind { File, Directory }

public sealed class ExternalSourceSnapshot
{
    public ExternalSourceSnapshot(
        ExternalSourceId externalSourceId,
        ExternalObservedRootKind rootKind,
        IEnumerable<ObservedFileSystemEntry> entries,
        IEnumerable<ObservationIssue> issues,
        ObservationCompleteness completeness)
    {
        ExternalSourceId = externalSourceId;
        RootKind = rootKind;
        Entries = SourceObservationSnapshot.FreezeEntries(entries, allowRoot: rootKind is ExternalObservedRootKind.File);
        if (rootKind is ExternalObservedRootKind.File
            && (Entries.Length != 1 || !Entries[0].Path.IsRoot || Entries[0].Kind is not FileSystemEntryKind.File))
        {
            throw new ArgumentException("External File observation requires exactly one regular-file root entry.", nameof(entries));
        }

        Issues = [.. issues];
        Completeness = completeness;
    }

    public ExternalSourceId ExternalSourceId { get; }
    public ExternalObservedRootKind RootKind { get; }
    public ImmutableArray<ObservedFileSystemEntry> Entries { get; }
    public ImmutableArray<ObservationIssue> Issues { get; }
    public ObservationCompleteness Completeness { get; }
}
