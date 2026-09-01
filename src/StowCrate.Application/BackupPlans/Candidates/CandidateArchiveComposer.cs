using StowCrate.Application.BackupPlans.ArchiveUnits;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;

namespace StowCrate.Application.BackupPlans.Candidates;

public interface ICandidateArchiveComposer
{
    CandidateArchiveSet Compose(
        ResolvedPlanSnapshot plan,
        ResolvedArchiveUnitSet resolvedUnits,
        IReadOnlyCollection<PendingArchiveUnitRegistration> pendingRegistrations);
}

public sealed class CandidateArchiveComposer : ICandidateArchiveComposer
{
    private const string ReservedNamespace = "__stowcrate__";
    private static readonly RelativePath ManifestPath = new("__stowcrate__/manifest.json");
    private static readonly RelativePath RecoveryPath = new("__stowcrate__/recovery.json");

    public CandidateArchiveSet Compose(
        ResolvedPlanSnapshot plan,
        ResolvedArchiveUnitSet resolvedUnits,
        IReadOnlyCollection<PendingArchiveUnitRegistration> pendingRegistrations)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(resolvedUnits);
        ArgumentNullException.ThrowIfNull(pendingRegistrations);

        var issues = new List<CandidateCompositionIssue>();
        var sources = resolvedUnits.SourceObservations.ToDictionary(item => item.SourceId);
        var externals = resolvedUnits.ExternalObservations.ToDictionary(item => item.ExternalSourceId);
        foreach (var observation in resolvedUnits.SourceObservations.Cast<object>().Concat(resolvedUnits.ExternalObservations))
        {
            var incomplete = observation switch
            {
                SourceObservationSnapshot source => source.Completeness is ObservationCompleteness.Incomplete,
                ExternalSourceSnapshot external => external.Completeness is ObservationCompleteness.Incomplete,
                _ => false
            };
            if (incomplete)
            {
                issues.Add(new CandidateCompositionIssue(CandidateCompositionIssueCode.IncompleteObservation, "Candidate is preview-only because an input observation is incomplete."));
            }
        }

        var archives = new List<CandidateArchive>();
        foreach (var unit in resolvedUnits.Units)
        {
            if (!sources.TryGetValue(unit.SourceId, out var source))
            {
                issues.Add(new CandidateCompositionIssue(CandidateCompositionIssueCode.MissingObservation, "Resolved unit has no Source observation.", unit.ArchiveUnitId));
                continue;
            }

            var entries = SelectNormal(plan, unit, source, resolvedUnits.Units, issues);
            foreach (var external in plan.ExternalSources.Where(item => item.TargetArchiveUnitId == unit.ArchiveUnitId))
            {
                if (!externals.TryGetValue(external.ExternalSourceId, out var observation))
                {
                    issues.Add(new CandidateCompositionIssue(CandidateCompositionIssueCode.MissingObservation, "External Source observation is missing.", unit.ArchiveUnitId));
                    continue;
                }
                entries.AddRange(ProjectExternal(plan.LinkPolicy, unit, external, observation, issues));
            }

            entries.Add(new CandidateArchiveEntry(
                ManifestPath,
                FileSystemEntryKind.File,
                CandidateEntryOwnerKind.Generated,
                null,
                null,
                null,
                0,
                null,
                ObservedContentIdentity.MetadataV1,
                null,
                null,
                SourceMetadata.None));
            if (unit.ArchiveSpec.Protection is PrivacyProtection)
            {
                entries.Add(new CandidateArchiveEntry(
                    RecoveryPath, FileSystemEntryKind.File, CandidateEntryOwnerKind.Generated,
                    null, null, null, 0, null, ObservedContentIdentity.MetadataV1,
                    null, null, SourceMetadata.None));
            }
            ValidateOwnership(unit.ArchiveUnitId, entries, issues);
            var sourceBinding = plan.Sources.Single(item => item.SourceId == unit.SourceId);
            archives.Add(new CandidateArchive(
                unit,
                OutputPath(sourceBinding.SourceOutputPath, unit.Root, unit.ArchiveSpec.Format, plan.Semantics.OutputPathEncoding),
                entries,
                new GeneratedMetadataPlan(ManifestPath, plan.Semantics.Archive, CandidateRuntimeSemantics.ManifestSchemaVersion,
                    unit.ArchiveSpec.Protection is PrivacyProtection ? RecoveryPath : null,
                    unit.ArchiveSpec.Protection is PrivacyProtection ? 1 : null,
                    unit.ArchiveSpec.Protection is PrivacyProtection ? 1 : null),
                resolvedUnits.Units.Where(candidate => candidate.ParentArchiveUnitId == unit.ArchiveUnitId).Select(candidate => candidate.Root)));
        }

        foreach (var duplicate in archives.GroupBy(archive => archive.OutputRelativePath).Where(group => group.Count() > 1))
        {
            issues.Add(new CandidateCompositionIssue(CandidateCompositionIssueCode.OutputPathCollision, $"Multiple Archive Units map to output '{duplicate.Key.Value}'."));
        }

        return new CandidateArchiveSet(plan.Semantics, archives, issues, pendingRegistrations);
    }

    private static List<CandidateArchiveEntry> SelectNormal(
        ResolvedPlanSnapshot plan,
        ResolvedArchiveUnit unit,
        SourceObservationSnapshot source,
        IReadOnlyCollection<ResolvedArchiveUnit> allUnits,
        List<CandidateCompositionIssue> issues)
    {
        var childRoots = allUnits.Where(candidate => candidate.ParentArchiveUnitId == unit.ArchiveUnitId).Select(candidate => candidate.Root).ToArray();
        var result = new List<CandidateArchiveEntry>();
        foreach (var entry in source.Entries.Where(entry => entry.Path.IsSameOrDescendantOf(unit.Root)))
        {
            if (childRoots.Any(root => entry.Path.IsSameOrDescendantOf(root))) continue;
            var relative = entry.Path.RelativeTo(unit.Root);
            if (entry.Kind is FileSystemEntryKind.Special)
            {
                continue;
            }
            if (IsReserved(relative))
            {
                issues.Add(new CandidateCompositionIssue(CandidateCompositionIssueCode.ReservedNamespaceCollision, "Normal entry collides with reserved metadata namespace.", unit.ArchiveUnitId, relative));
                continue;
            }
            if (entry.Kind is FileSystemEntryKind.Link && plan.LinkPolicy is PortableLinkPolicy.Skip) continue;
            // FILE_MANAGED 的控制文件是可复现规则语义的一部分，即使规则本身排除它也必须入档。
            var ownControl = unit.RuleSource is RuleSource.FileManaged && relative.Value == ".backupignore";
            if (!ownControl && unit.EffectiveRuleSet.Decide(relative, entry.Kind, entry.MetadataFlags.HasFlag(SourceMetadata.DirectoryTarget)) is RuleAction.Exclude) continue;
            result.Add(Map(entry, relative, CandidateEntryOwnerKind.Normal, unit.SourceId, null));
        }
        return result;
    }

    private static IEnumerable<CandidateArchiveEntry> ProjectExternal(
        PortableLinkPolicy linkPolicy,
        ResolvedArchiveUnit unit,
        ResolvedExternalSource external,
        ExternalSourceSnapshot observation,
        List<CandidateCompositionIssue> issues)
    {
        var expected = external.Kind is PortableExternalSourceKind.File ? ExternalObservedRootKind.File : ExternalObservedRootKind.Directory;
        if (observation.RootKind != expected)
        {
            issues.Add(new CandidateCompositionIssue(CandidateCompositionIssueCode.ExternalKindMismatch, "External observation kind differs from declaration.", unit.ArchiveUnitId));
            yield break;
        }
        foreach (var entry in observation.Entries)
        {
            // External 是显式 inclusion，只应用安全与 LinkPolicy，不经过任何普通规则层。
            if (entry.Kind is FileSystemEntryKind.Special) continue;
            if (entry.Kind is FileSystemEntryKind.Link && linkPolicy is PortableLinkPolicy.Skip) continue;
            var mapped = observation.RootKind is ExternalObservedRootKind.File
                ? new RelativePath(external.ArchiveDestination.Value)
                : new RelativePath(external.ArchiveDestination.Combine(new RelativePath(entry.Path.Value)).Value);
            if (IsReserved(mapped))
            {
                issues.Add(new CandidateCompositionIssue(CandidateCompositionIssueCode.ReservedNamespaceCollision, "External entry collides with reserved metadata namespace.", unit.ArchiveUnitId, mapped));
                continue;
            }
            yield return Map(entry, mapped, CandidateEntryOwnerKind.External, null, external.ExternalSourceId);
        }
    }

    private static CandidateArchiveEntry Map(ObservedFileSystemEntry entry, RelativePath path, CandidateEntryOwnerKind owner, SourceId? sourceId, ExternalSourceId? externalId) =>
        new(path, entry.Kind, owner, sourceId, externalId, entry.Path, entry.Length, entry.LastWriteTimeUtc, entry.ContentIdentity, entry.RawFileSha256, entry.Link, entry.MetadataFlags);

    private static void ValidateOwnership(ArchiveUnitId unitId, IReadOnlyCollection<CandidateArchiveEntry> entries, List<CandidateCompositionIssue> issues)
    {
        // 只比较实际 owner；为路径投影隐式产生的父目录容器不占有 archive path。
        var ordered = entries.OrderBy(entry => entry.ArchivePath.Value, StringComparer.Ordinal).ToArray();
        foreach (var group in ordered.GroupBy(entry => entry.ArchivePath))
        {
            if (group.Count() > 1) issues.Add(new CandidateCompositionIssue(CandidateCompositionIssueCode.EntryOwnershipCollision, "Multiple actual owners claim the same archive path.", unitId, group.Key));
        }
        foreach (var owner in ordered.Where(entry => entry.Kind is not FileSystemEntryKind.Directory))
        {
            if (ordered.Any(other => other.ArchivePath != owner.ArchivePath && IsDescendant(other.ArchivePath, owner.ArchivePath)))
                issues.Add(new CandidateCompositionIssue(CandidateCompositionIssueCode.EntryOwnershipCollision, "A non-directory owner blocks a descendant archive path.", unitId, owner.ArchivePath));
        }
    }

    private static bool IsReserved(RelativePath path) => path.Value == ReservedNamespace || path.Value.StartsWith(ReservedNamespace + "/", StringComparison.Ordinal);
    private static bool IsDescendant(RelativePath path, RelativePath ancestor) => path.Value.StartsWith(ancestor.Value + "/", StringComparison.Ordinal);

    private static LogicalPath OutputPath(LogicalPath sourceOutputPath, LogicalPath unitRoot, PortableArchiveFormat format, int outputPathEncodingVersion)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(outputPathEncodingVersion, 1);
        var logical = sourceOutputPath.Combine(new RelativePath(unitRoot.Value));
        var extension = format switch
        {
            PortableArchiveFormat.SevenZip => ".7z",
            PortableArchiveFormat.Zip => ".zip",
            PortableArchiveFormat.TarZstd => ".tar.zst",
            _ => throw new InvalidOperationException($"Unknown archive format {format}.")
        };
        return new LogicalPath(logical.Value + extension);
    }
}

public static class CandidateRuntimeSemantics
{
    public const int ManifestSchemaVersion = 1;
    public const int PrivacyProtectionVersion = 1;
}
