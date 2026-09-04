using System.Collections.Immutable;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Application.StorageMaintenance;

public sealed record StorageRelocationInventoryRequest(PlanId PlanId, ResolvedPhysicalPath? NewCurrentRoot, ResolvedPhysicalPath? NewHistoryRoot);
public sealed record StorageRelocationRootPaths(StorageRootKind Kind, ResolvedPhysicalPath OldRoot, ResolvedPhysicalPath NewRoot);
public sealed record StorageRelocationPlacement(ArchiveUnitId UnitId, StorageRootKind RootKind, StorageTransferArtifact Artifact, RelativeStoragePath RelativePath);

/// <summary>只读一致 metadata snapshot；不包含物理 proof，不独立授权 Begin 或文件操作。</summary>
public sealed record StorageRelocationInventory(PlanId PlanId, DeviceId DeviceId,
    ImmutableArray<StorageRelocationRootPaths> Roots, ImmutableArray<StorageRelocationPlacement> Entries);

public interface IStorageRelocationInventoryStore
{
    Task<StorageRelocationInventory> ReadRelocationInventoryAsync(StorageRelocationInventoryRequest request,
        StorageRelocationConfigurationObservation configuration, CancellationToken cancellationToken);
}
