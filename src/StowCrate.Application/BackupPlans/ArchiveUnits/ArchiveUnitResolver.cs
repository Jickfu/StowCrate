using System.Collections.Immutable;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;

namespace StowCrate.Application.BackupPlans.ArchiveUnits;

public sealed class ArchiveUnitResolver(IArchiveUnitIdGenerator idGenerator) : IArchiveUnitResolver
{
    public ArchiveUnitResolutionResult Resolve(
        ResolvedPlanSnapshot plan,
        IReadOnlyCollection<SourceObservationSnapshot> sourceObservations,
        IReadOnlyCollection<ExternalSourceSnapshot> externalObservations,
        IReadOnlyCollection<LocalArchiveUnitIdentityRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sourceObservations);
        ArgumentNullException.ThrowIfNull(externalObservations);
        ArgumentNullException.ThrowIfNull(registrations);

        var issues = new List<ArchiveUnitResolutionIssue>();
        var pending = new List<PendingArchiveUnitRegistration>();
        var sources = Unique(sourceObservations, observation => observation.SourceId, ArchiveUnitResolutionIssueCode.DuplicateSourceObservation, issues);
        var externals = Unique(externalObservations, observation => observation.ExternalSourceId, ArchiveUnitResolutionIssueCode.DuplicateExternalObservation, issues);

        foreach (var source in plan.Sources)
        {
            if (!sources.TryGetValue(source.SourceId, out var observation))
            {
                issues.Add(Issue(ArchiveUnitResolutionIssueCode.MissingSourceObservation, "Required Source observation is missing.", source.SourceId));
            }
            else if (observation.Completeness is ObservationCompleteness.Incomplete)
            {
                issues.Add(Issue(ArchiveUnitResolutionIssueCode.IncompleteObservation, "Incomplete Source observation cannot form a complete resolved unit set.", source.SourceId));
            }
        }

        foreach (var external in plan.ExternalSources)
        {
            if (!externals.TryGetValue(external.ExternalSourceId, out var observation))
            {
                issues.Add(new ArchiveUnitResolutionIssue(
                    ArchiveUnitResolutionIssueCode.MissingExternalObservation,
                    "Required External Source observation is missing.",
                    ExternalSourceId: external.ExternalSourceId));
            }
            else if (observation.Completeness is ObservationCompleteness.Incomplete)
            {
                issues.Add(new ArchiveUnitResolutionIssue(
                    ArchiveUnitResolutionIssueCode.IncompleteObservation,
                    "Incomplete External observation cannot form a complete resolved unit set.",
                    ExternalSourceId: external.ExternalSourceId));
            }
        }

        if (issues.Any(IsHardFailure))
        {
            return new ArchiveUnitResolutionResult(null, pending, issues);
        }

        var registrationsByPath = registrations
            .GroupBy(registration => (registration.SourceId, registration.Path))
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var duplicate in registrationsByPath.Where(pair => pair.Value.Length > 1))
        {
            issues.Add(new ArchiveUnitResolutionIssue(
                ArchiveUnitResolutionIssueCode.IdentityConflict,
                "Multiple local Archive Unit identity registrations exist for the same Source/path.",
                duplicate.Key.SourceId,
                duplicate.Key.Path));
        }

        var declarations = plan.DeclaredArchiveUnits.ToDictionary(unit => (unit.SourceId, unit.Path));
        var discovered = DiscoverFileManagedUnits(sources, issues);
        ValidateObservedIdentities(discovered, issues);
        ValidateDeclarations(declarations, discovered, issues);

        var provisional = new List<ProvisionalUnit>();
        var knownIds = plan.DeclaredArchiveUnits.Select(unit => unit.ArchiveUnitId)
            .Concat(discovered.Select(unit => unit.Parsed.ArchiveUnitId).OfType<ArchiveUnitId>())
            .Concat(registrations.Select(registration => registration.ArchiveUnitId))
            .ToHashSet();
        foreach (var declaration in plan.DeclaredArchiveUnits.OfType<PreparedUiManagedArchiveUnit>())
        {
            if (!discovered.Any(unit => unit.SourceId == declaration.SourceId && unit.Path == declaration.Path))
            {
                var observation = sources[declaration.SourceId];
                provisional.Add(new ProvisionalUnit(
                    declaration.ArchiveUnitId,
                    declaration.SourceId,
                    declaration.Path,
                    RuleSource.UiManaged,
                    declaration.LocalRules,
                    new EffectiveRuleSet(plan.GlobalRules, plan.PlanRules, declaration.LocalRules, observation.FileSystemCaseSensitivity),
                    declaration.ArchiveSpec,
                    declaration.History,
                    null));
            }
        }

        foreach (var unit in discovered)
        {
            if (declarations.TryGetValue((unit.SourceId, unit.Path), out var declaration)
                && declaration is PreparedUiManagedArchiveUnit)
            {
                continue;
            }

            var fileDeclaration = declaration as PreparedFileManagedArchiveUnit;
            registrationsByPath.TryGetValue((unit.SourceId, unit.Path), out var registrationCandidates);
            var registration = registrationCandidates is { Length: 1 } ? registrationCandidates[0] : null;

            var identityCandidates = new List<ArchiveUnitId>();
            if (fileDeclaration is not null)
            {
                identityCandidates.Add(fileDeclaration.ArchiveUnitId);
            }

            if (unit.Parsed.ArchiveUnitId is { } fileId)
            {
                identityCandidates.Add(fileId);
            }

            if (registration is not null)
            {
                identityCandidates.Add(registration.ArchiveUnitId);
            }

            if (identityCandidates.Distinct().Skip(1).Any())
            {
                issues.Add(new ArchiveUnitResolutionIssue(
                    ArchiveUnitResolutionIssueCode.IdentityConflict,
                    "Declaration, @id and local registration identities disagree.",
                    unit.SourceId,
                    unit.Path));
                continue;
            }

            ArchiveUnitId archiveUnitId;
            if (identityCandidates.Count != 0)
            {
                archiveUnitId = identityCandidates[0];
            }
            else
            {
                archiveUnitId = idGenerator.Generate();
                if (!knownIds.Add(archiveUnitId))
                {
                    issues.Add(new ArchiveUnitResolutionIssue(
                        ArchiveUnitResolutionIssueCode.GeneratedIdentityCollision,
                        "Generated ArchiveUnitId collides with an existing identity.",
                        unit.SourceId,
                        unit.Path));
                    continue;
                }

                pending.Add(new PendingArchiveUnitRegistration(unit.SourceId, unit.Path, archiveUnitId));
            }

            var policy = fileDeclaration is null
                ? (plan.DefaultUnitPolicy.ArchiveSpec, plan.DefaultUnitPolicy.History)
                : (fileDeclaration.ArchiveSpec, fileDeclaration.History);
            var observation = sources[unit.SourceId];
            provisional.Add(new ProvisionalUnit(
                archiveUnitId,
                unit.SourceId,
                unit.Path,
                RuleSource.FileManaged,
                unit.Parsed.RuleSet,
                new EffectiveRuleSet(plan.GlobalRules, plan.PlanRules, unit.Parsed.RuleSet, observation.FileSystemCaseSensitivity),
                policy.Item1,
                policy.Item2,
                unit.Fingerprint));
        }

        foreach (var duplicate in provisional.GroupBy(unit => unit.ArchiveUnitId).Where(group => group.Count() > 1))
        {
            issues.Add(new ArchiveUnitResolutionIssue(
                ArchiveUnitResolutionIssueCode.DuplicateObservedArchiveUnitId,
                $"ArchiveUnitId '{duplicate.Key.Value:D}' resolves to multiple units."));
        }

        ValidateRelocations(plan.DeclaredArchiveUnits, discovered, registrationsByPath, issues);
        if (issues.Any(IsHardFailure))
        {
            return new ArchiveUnitResolutionResult(null, pending, issues);
        }

        var units = BuildBoundaries(provisional);
        ValidateExternalBoundaries(plan, units, issues);
        if (issues.Any(IsHardFailure))
        {
            return new ArchiveUnitResolutionResult(null, pending, issues);
        }

        return new ArchiveUnitResolutionResult(
            new ResolvedArchiveUnitSet(units, sourceObservations, externalObservations),
            pending,
            issues);
    }

    private static List<DiscoveredUnit> DiscoverFileManagedUnits(
        IReadOnlyDictionary<SourceId, SourceObservationSnapshot> sources,
        List<ArchiveUnitResolutionIssue> issues)
    {
        var result = new List<DiscoveredUnit>();
        foreach (var observation in sources.Values)
        {
            foreach (var entry in observation.Entries.Where(entry => entry.Path.Name == ".backupignore"))
            {
                var root = entry.Path.Parent;
                if (entry.Kind is not FileSystemEntryKind.File || entry.TextContent is null)
                {
                    issues.Add(new ArchiveUnitResolutionIssue(
                        ArchiveUnitResolutionIssueCode.InvalidBackupIgnore,
                        ".backupignore observation must be a readable regular file.",
                        observation.SourceId,
                        root));
                    continue;
                }

                try
                {
                    result.Add(new DiscoveredUnit(
                        observation.SourceId,
                        root,
                        BackupIgnoreParser.ParseDocument(entry.TextContent),
                        entry.ContentFingerprint));
                }
                catch (BackupIgnoreParseException exception)
                {
                    issues.Add(new ArchiveUnitResolutionIssue(
                        ArchiveUnitResolutionIssueCode.InvalidBackupIgnore,
                        exception.Message,
                        observation.SourceId,
                        root));
                }
            }
        }

        return result;
    }

    private static void ValidateObservedIdentities(
        IEnumerable<DiscoveredUnit> discovered,
        List<ArchiveUnitResolutionIssue> issues)
    {
        foreach (var duplicate in discovered
            .Where(unit => unit.Parsed.ArchiveUnitId is not null)
            .GroupBy(unit => unit.Parsed.ArchiveUnitId!.Value)
            .Where(group => group.Count() > 1))
        {
            issues.Add(new ArchiveUnitResolutionIssue(
                ArchiveUnitResolutionIssueCode.DuplicateObservedArchiveUnitId,
                $"@id '{duplicate.Key.Value:D}' appears in multiple .backupignore files."));
        }
    }

    private static void ValidateDeclarations(
        IReadOnlyDictionary<(SourceId SourceId, LogicalPath Path), PreparedDeclaredArchiveUnit> declarations,
        IReadOnlyCollection<DiscoveredUnit> discovered,
        List<ArchiveUnitResolutionIssue> issues)
    {
        foreach (var unit in discovered)
        {
            if (declarations.TryGetValue((unit.SourceId, unit.Path), out var declaration))
            {
                if (declaration is PreparedUiManagedArchiveUnit)
                {
                    issues.Add(new ArchiveUnitResolutionIssue(
                        ArchiveUnitResolutionIssueCode.RuleSourceConflict,
                        "UI_MANAGED declaration path contains .backupignore.",
                        unit.SourceId,
                        unit.Path));
                }
                else if (unit.Parsed.ArchiveUnitId is { } parsedId && parsedId != declaration.ArchiveUnitId)
                {
                    issues.Add(new ArchiveUnitResolutionIssue(
                        ArchiveUnitResolutionIssueCode.BackupIgnoreDeclarationIdMismatch,
                        ".backupignore @id does not match FILE_MANAGED declaration identity.",
                        unit.SourceId,
                        unit.Path));
                }
            }
        }

        foreach (var declaration in declarations.Values.OfType<PreparedFileManagedArchiveUnit>())
        {
            if (!discovered.Any(unit => unit.SourceId == declaration.SourceId && unit.Path == declaration.Path)
                && !discovered.Any(unit => unit.SourceId == declaration.SourceId
                    && unit.Parsed.ArchiveUnitId == declaration.ArchiveUnitId))
            {
                issues.Add(new ArchiveUnitResolutionIssue(
                    ArchiveUnitResolutionIssueCode.MissingFileManagedRuleSource,
                    "FILE_MANAGED declaration has no observed .backupignore at its declared path.",
                    declaration.SourceId,
                    declaration.Path));
            }
        }
    }

    private static void ValidateRelocations(
        IEnumerable<PreparedDeclaredArchiveUnit> declarations,
        IEnumerable<DiscoveredUnit> discovered,
        Dictionary<(SourceId SourceId, LogicalPath Path), LocalArchiveUnitIdentityRegistration[]> registrations,
        List<ArchiveUnitResolutionIssue> issues)
    {
        foreach (var declaration in declarations.OfType<PreparedFileManagedArchiveUnit>())
        {
            foreach (var unit in discovered.Where(unit => unit.SourceId == declaration.SourceId && unit.Path != declaration.Path))
            {
                registrations.TryGetValue((unit.SourceId, unit.Path), out var registration);
                if (unit.Parsed.ArchiveUnitId == declaration.ArchiveUnitId
                    || registration is { Length: 1 } && registration[0].ArchiveUnitId == declaration.ArchiveUnitId)
                {
                    issues.Add(new ArchiveUnitResolutionIssue(
                        ArchiveUnitResolutionIssueCode.ArchiveUnitRelocated,
                        "Declared ArchiveUnitId was discovered at a different logical path.",
                        declaration.SourceId,
                        unit.Path));
                }
            }
        }
    }

    private static ImmutableArray<ResolvedArchiveUnit> BuildBoundaries(IEnumerable<ProvisionalUnit> provisional)
    {
        var units = provisional.OrderBy(unit => unit.SourceId.Value.ToString("D"), StringComparer.Ordinal)
            .ThenBy(unit => unit.Path.Value, StringComparer.Ordinal)
            .ToArray();
        return units.Select(unit =>
        {
            var ancestors = units.Where(candidate => candidate.SourceId == unit.SourceId && unit.Path.IsDescendantOf(candidate.Path)).ToArray();
            var parent = ancestors.OrderByDescending(candidate => candidate.Path.Value.Length).FirstOrDefault();
            var children = units.Where(candidate => candidate.SourceId == unit.SourceId)
                .Where(candidate =>
                {
                    var candidateAncestors = units.Where(other => other.SourceId == candidate.SourceId && candidate.Path.IsDescendantOf(other.Path));
                    return candidateAncestors.OrderByDescending(other => other.Path.Value.Length).FirstOrDefault()?.ArchiveUnitId == unit.ArchiveUnitId;
                })
                .Select(candidate => candidate.ArchiveUnitId)
                .OrderBy(id => id.Value.ToString("D"), StringComparer.Ordinal)
                .ToImmutableArray();
            return new ResolvedArchiveUnit(
                unit.ArchiveUnitId,
                unit.SourceId,
                unit.Path,
                unit.RuleSource,
                unit.LocalRuleSet,
                unit.EffectiveRuleSet,
                unit.ArchiveSpec,
                unit.History,
                unit.Fingerprint,
                parent?.ArchiveUnitId,
                children);
        }).ToImmutableArray();
    }

    private static void ValidateExternalBoundaries(
        ResolvedPlanSnapshot plan,
        IReadOnlyCollection<ResolvedArchiveUnit> units,
        List<ArchiveUnitResolutionIssue> issues)
    {
        var byId = units.ToDictionary(unit => unit.ArchiveUnitId);
        foreach (var external in plan.ExternalSources)
        {
            if (!byId.TryGetValue(external.TargetArchiveUnitId, out var target))
            {
                continue;
            }

            foreach (var child in units.Where(unit => unit.SourceId == target.SourceId && unit.Root.IsDescendantOf(target.Root)))
            {
                var boundary = new LogicalPath(child.Root.RelativeTo(target.Root).Value);
                if (external.ArchiveDestination.IsSameOrDescendantOf(boundary))
                {
                    issues.Add(new ArchiveUnitResolutionIssue(
                        ArchiveUnitResolutionIssueCode.ExternalCrossesDiscoveredChildBoundary,
                        "External destination crosses a resolved child Archive Boundary.",
                        target.SourceId,
                        child.Root,
                        external.ExternalSourceId));
                    break;
                }
            }
        }
    }

    private static Dictionary<TKey, TValue> Unique<TValue, TKey>(
        IEnumerable<TValue> values,
        Func<TValue, TKey> keySelector,
        ArchiveUnitResolutionIssueCode duplicateCode,
        List<ArchiveUnitResolutionIssue> issues)
        where TKey : notnull
    {
        var result = new Dictionary<TKey, TValue>();
        foreach (var value in values)
        {
            var key = keySelector(value);
            if (!result.TryAdd(key, value))
            {
                issues.Add(new ArchiveUnitResolutionIssue(duplicateCode, $"Duplicate observation '{key}'."));
            }
        }

        return result;
    }

    private static ArchiveUnitResolutionIssue Issue(
        ArchiveUnitResolutionIssueCode code,
        string message,
        SourceId sourceId) => new(code, message, sourceId);

    private static bool IsHardFailure(ArchiveUnitResolutionIssue issue) =>
        issue.Code is not ArchiveUnitResolutionIssueCode.IncompleteObservation;

    private sealed record DiscoveredUnit(
        SourceId SourceId,
        LogicalPath Path,
        BackupIgnoreParseResult Parsed,
        string Fingerprint);

    private sealed record ProvisionalUnit(
        ArchiveUnitId ArchiveUnitId,
        SourceId SourceId,
        LogicalPath Path,
        RuleSource RuleSource,
        RuleSet LocalRuleSet,
        EffectiveRuleSet EffectiveRuleSet,
        EffectiveArchiveSpec ArchiveSpec,
        EffectiveHistoryPolicy History,
        string? Fingerprint);
}
