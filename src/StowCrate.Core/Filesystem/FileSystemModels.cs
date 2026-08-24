namespace StowCrate.Core.Filesystem;

public enum FileSystemEntryKind
{
    File,
    Directory,
    Link,
    Special,
}

public enum LinkKind
{
    SymbolicLink,
    Junction,
    MountPoint,
    Other,
}

public enum LinkTargetScope
{
    WithinArchiveUnit,
    WithinSource,
    OutsideSource,
    Unresolved,
}

public enum LinkPolicy
{
    Preserve,
    Skip,
}

public enum FileSystemBoundaryPolicy
{
    StayOnSourceFileSystem,
    CrossFileSystems,
}

[Flags]
public enum SourceMetadata
{
    None = 0,
    ReadOnly = 1,
    Hidden = 2,
    Executable = 4,
    DirectoryTarget = 8,
}

public sealed record LinkInfo
{
    public LinkInfo(LinkKind kind, string target, LinkTargetScope targetScope, bool isDangling)
    {
        ArgumentNullException.ThrowIfNull(target);
        Kind = kind;
        Target = target;
        TargetScope = targetScope;
        IsDangling = isDangling;
    }

    public LinkKind Kind { get; }

    public string Target { get; }

    public LinkTargetScope TargetScope { get; }

    public bool IsDangling { get; }
}
