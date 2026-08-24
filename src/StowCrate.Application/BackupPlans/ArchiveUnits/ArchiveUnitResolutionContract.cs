using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Core.BackupPlans;

namespace StowCrate.Application.BackupPlans.ArchiveUnits;

public interface IArchiveUnitResolver
{
    ArchiveUnitResolutionResult Resolve(
        ResolvedPlanSnapshot plan,
        IReadOnlyCollection<SourceObservationSnapshot> sourceObservations,
        IReadOnlyCollection<ExternalSourceSnapshot> externalObservations,
        IReadOnlyCollection<LocalArchiveUnitIdentityRegistration> registrations);
}
