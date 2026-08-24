using System.Collections.Immutable;
using StowCrate.Core.Paths;

namespace StowCrate.Core.BackupPlans;

public static class PortableBackupPlanValidator
{
    public static ImmutableArray<BackupPlanSemanticError> Validate(PortableBackupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var semanticsErrors = PortableSemanticsSupport.Validate(plan.Semantics);
        if (!semanticsErrors.IsEmpty)
        {
            return semanticsErrors;
        }

        var errors = ImmutableArray.CreateBuilder<BackupPlanSemanticError>();
        ValidateUniqueIds(plan, errors);
        ValidateReferences(plan, errors);
        ValidateUnitDeclarations(plan, errors);
        ValidateSchedule(plan.Schedule, errors);
        ValidateExternalOwnership(plan, errors);
        ValidateDeclaredChildBoundaries(plan, errors);
        return errors.ToImmutable();
    }

    private static void ValidateUniqueIds(
        PortableBackupPlan plan,
        ImmutableArray<BackupPlanSemanticError>.Builder errors)
    {
        AddDuplicates(plan.Sources.Select(x => x.Id), BackupPlanSemanticErrorCode.DuplicateSourceId, "/sources", errors);
        AddDuplicates(plan.ArchiveUnits.Select(x => x.Id), BackupPlanSemanticErrorCode.DuplicateArchiveUnitId, "/archiveUnits", errors);
        AddDuplicates(plan.ExternalSources.Select(x => x.Id), BackupPlanSemanticErrorCode.DuplicateExternalSourceId, "/externalSources", errors);
        AddDuplicates(plan.SecretSlots.Select(x => x.Id), BackupPlanSemanticErrorCode.DuplicateSecretSlotId, "/secretSlots", errors);
    }

    private static void ValidateReferences(
        PortableBackupPlan plan,
        ImmutableArray<BackupPlanSemanticError>.Builder errors)
    {
        var sources = plan.Sources.Select(x => x.Id).ToHashSet();
        var units = plan.ArchiveUnits.Select(x => x.Id).ToHashSet();
        var slots = plan.SecretSlots.Select(x => x.Id).ToHashSet();

        foreach (var (unit, index) in plan.ArchiveUnits.Select((value, index) => (value, index)))
        {
            if (!sources.Contains(unit.SourceId))
            {
                errors.Add(Error(BackupPlanSemanticErrorCode.UnknownSourceReference, "Archive Unit references an unknown SourceId.", $"/archiveUnits/{index}/sourceId"));
            }
        }

        foreach (var (external, index) in plan.ExternalSources.Select((value, index) => (value, index)))
        {
            if (!units.Contains(external.TargetArchiveUnitId))
            {
                errors.Add(Error(BackupPlanSemanticErrorCode.UnknownArchiveUnitReference, "External Source references an unknown declared ArchiveUnitId.", $"/externalSources/{index}/targetArchiveUnitId"));
            }
        }

        ValidateProtection(plan.ArchiveSpecDefault.Protection, slots, "/archiveSpecDefault/protection", errors);
        foreach (var (unit, index) in plan.ArchiveUnits.Select((value, index) => (value, index)))
        {
            if (unit.ArchiveSpecOverride?.Protection is { } protection)
            {
                ValidateProtection(protection, slots, $"/archiveUnits/{index}/archiveSpecOverride/protection", errors);
            }
        }
    }

    private static void ValidateProtection(
        AuthoredProtection protection,
        HashSet<SecretSlotId> slots,
        string location,
        ImmutableArray<BackupPlanSemanticError>.Builder errors)
    {
        if (protection is SecureProtection secure && !slots.Contains(secure.SecretSlotId))
        {
            errors.Add(Error(BackupPlanSemanticErrorCode.UnknownSecretSlotReference, "Secure protection references an unknown SecretSlotId.", $"{location}/secretSlotId"));
        }
    }

    private static void ValidateUnitDeclarations(
        PortableBackupPlan plan,
        ImmutableArray<BackupPlanSemanticError>.Builder errors)
    {
        var seen = new HashSet<(SourceId SourceId, LogicalPath Path)>();
        foreach (var (unit, index) in plan.ArchiveUnits.Select((value, index) => (value, index)))
        {
            if (!seen.Add((unit.SourceId, unit.Path)))
            {
                errors.Add(Error(BackupPlanSemanticErrorCode.DuplicateArchiveUnitDeclaration, "SourceId and normalized Archive Unit path must be unique.", $"/archiveUnits/{index}/path"));
            }
        }
    }

    private static void ValidateSchedule(
        PortableScheduleIntent schedule,
        ImmutableArray<BackupPlanSemanticError>.Builder errors)
    {
        if (schedule is not AutomaticSchedule automatic)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (trigger, index) in automatic.Triggers.Select((value, index) => (value, index)))
        {
            var key = trigger switch
            {
                DailyScheduleTrigger daily => $"daily:{daily.LocalTime:HH\\:mm}",
                WeeklyScheduleTrigger weekly => $"weekly:{weekly.LocalTime:HH\\:mm}:{string.Join(',', weekly.DaysOfWeek.Order())}",
                OnStartupScheduleTrigger => "onStartup",
                _ => throw new InvalidOperationException($"Unknown schedule trigger {trigger.GetType().Name}.")
            };

            if (!seen.Add(key))
            {
                errors.Add(Error(BackupPlanSemanticErrorCode.DuplicateScheduleTrigger, "Schedule contains a duplicate semantic trigger.", $"/schedule/triggers/{index}"));
            }
        }
    }

    private static void ValidateExternalOwnership(
        PortableBackupPlan plan,
        ImmutableArray<BackupPlanSemanticError>.Builder errors)
    {
        for (var leftIndex = 0; leftIndex < plan.ExternalSources.Length; leftIndex++)
        {
            var left = plan.ExternalSources[leftIndex];
            for (var rightIndex = leftIndex + 1; rightIndex < plan.ExternalSources.Length; rightIndex++)
            {
                var right = plan.ExternalSources[rightIndex];
                if (left.TargetArchiveUnitId == right.TargetArchiveUnitId
                    && PathsOverlap(left.ArchiveDestination, right.ArchiveDestination))
                {
                    errors.Add(Error(BackupPlanSemanticErrorCode.ExternalOwnershipCollision, "External Source destinations have overlapping ownership in the same Archive Unit.", $"/externalSources/{rightIndex}/archiveDestination"));
                }
            }
        }
    }

    private static void ValidateDeclaredChildBoundaries(
        PortableBackupPlan plan,
        ImmutableArray<BackupPlanSemanticError>.Builder errors)
    {
        var unitsById = plan.ArchiveUnits.GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First());
        foreach (var (external, index) in plan.ExternalSources.Select((value, index) => (value, index)))
        {
            if (!unitsById.TryGetValue(external.TargetArchiveUnitId, out var target))
            {
                continue;
            }

            foreach (var child in plan.ArchiveUnits)
            {
                if (child.SourceId != target.SourceId || !child.Path.IsDescendantOf(target.Path))
                {
                    continue;
                }

                var boundary = new LogicalPath(child.Path.RelativeTo(target.Path).Value);
                if (external.ArchiveDestination.IsSameOrDescendantOf(boundary))
                {
                    errors.Add(Error(BackupPlanSemanticErrorCode.ExternalCrossesDeclaredChildBoundary, "External destination equals or crosses a declared child Archive Boundary.", $"/externalSources/{index}/archiveDestination"));
                    break;
                }
            }
        }
    }

    private static bool PathsOverlap(LogicalPath left, LogicalPath right) =>
        left.IsSameOrDescendantOf(right) || right.IsSameOrDescendantOf(left);

    private static void AddDuplicates<T>(
        IEnumerable<T> values,
        BackupPlanSemanticErrorCode code,
        string location,
        ImmutableArray<BackupPlanSemanticError>.Builder errors)
        where T : notnull
    {
        var seen = new HashSet<T>();
        foreach (var value in values)
        {
            if (!seen.Add(value))
            {
                errors.Add(Error(code, $"Duplicate typed identity '{value}'.", location));
            }
        }
    }

    private static BackupPlanSemanticError Error(BackupPlanSemanticErrorCode code, string message, string location) =>
        new(code, message, location);
}
