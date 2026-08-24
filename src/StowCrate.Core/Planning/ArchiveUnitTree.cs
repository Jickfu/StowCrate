using System.Collections.ObjectModel;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;

namespace StowCrate.Core.Planning;

public sealed record ArchiveUnit(
    LogicalPath Root,
    RuleSource RuleSource,
    RuleSet LocalRules,
    EffectiveRuleSet EffectiveRules);

public sealed record ArchiveBoundary(LogicalPath Parent, LogicalPath Child);

public sealed class ArchiveUnitTree
{
    public ArchiveUnitTree(IEnumerable<ArchiveUnit> units, IEnumerable<ArchiveBoundary> boundaries)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(boundaries);

        Units = new ReadOnlyCollection<ArchiveUnit>(
            units.OrderBy(unit => unit.Root.Value, StringComparer.Ordinal).ToArray());
        Boundaries = new ReadOnlyCollection<ArchiveBoundary>(
            boundaries
                .OrderBy(boundary => boundary.Parent.Value, StringComparer.Ordinal)
                .ThenBy(boundary => boundary.Child.Value, StringComparer.Ordinal)
                .ToArray());
    }

    public IReadOnlyList<ArchiveUnit> Units { get; }

    public IReadOnlyList<ArchiveBoundary> Boundaries { get; }

    public IReadOnlyList<ArchiveUnit> GetDirectChildren(ArchiveUnit parent)
    {
        ArgumentNullException.ThrowIfNull(parent);

        var childRoots = Boundaries
            .Where(boundary => boundary.Parent == parent.Root)
            .Select(boundary => boundary.Child)
            .ToHashSet();

        return new ReadOnlyCollection<ArchiveUnit>(
            Units.Where(unit => childRoots.Contains(unit.Root)).ToArray());
    }
}
