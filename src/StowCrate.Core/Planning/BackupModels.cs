using System.Collections.ObjectModel;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;

namespace StowCrate.Core.Planning;

public sealed record BackupSource
{
    public BackupSource(string id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var namePath = new RelativePath(name);
        if (namePath.IsRoot || namePath.Value.Contains('/'))
        {
            throw new ArgumentException("BackupSource name 必须是单个逻辑路径 segment。", nameof(name));
        }

        Id = id;
        Name = namePath.Value;
    }

    public string Id { get; }

    public string Name { get; }
}

public sealed class BackupPlan
{
    public BackupPlan(
        string id,
        BackupSource source,
        IEnumerable<BackupRule>? globalRules = null,
        IEnumerable<BackupRule>? planRules = null,
        IEnumerable<ArchiveUnitDefinition>? archiveUnits = null,
        RetentionPolicy? retentionPolicy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(source);

        Id = id;
        Source = source;
        GlobalRules = Freeze(globalRules);
        PlanRules = Freeze(planRules);
        ArchiveUnits = Freeze(archiveUnits);
        RetentionPolicy = retentionPolicy ?? RetentionPolicy.None;
    }

    public string Id { get; }

    public BackupSource Source { get; }

    public IReadOnlyList<BackupRule> GlobalRules { get; }

    public IReadOnlyList<BackupRule> PlanRules { get; }

    public IReadOnlyList<ArchiveUnitDefinition> ArchiveUnits { get; }

    public RetentionPolicy RetentionPolicy { get; }

    private static ReadOnlyCollection<T> Freeze<T>(IEnumerable<T>? values)
    {
        return new ReadOnlyCollection<T>((values ?? []).ToArray());
    }
}

public sealed record ArchiveUnitDefinition
{
    public ArchiveUnitDefinition(LogicalPath root, RuleSet localRules)
    {
        ArgumentNullException.ThrowIfNull(localRules);
        Root = root;
        LocalRules = localRules;
    }

    public LogicalPath Root { get; }

    public RuleSet LocalRules { get; }
}

public sealed record RetentionPolicy
{
    private RetentionPolicy(bool enabled)
    {
        Enabled = enabled;
    }

    public static RetentionPolicy None { get; } = new(enabled: false);

    public static RetentionPolicy KeepHistory { get; } = new(enabled: true);

    public bool Enabled { get; }
}
