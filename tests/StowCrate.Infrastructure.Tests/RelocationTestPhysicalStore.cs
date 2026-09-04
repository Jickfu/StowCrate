using StowCrate.Application.Publishing;
using StowCrate.Application.StorageMaintenance;
using StowCrate.Infrastructure.Filesystem;

namespace StowCrate.Infrastructure.Tests;

internal static class RelocationTestPhysicalStore
{
    // 既有 transfer/恢复测试验证协议，不假定宿主目录支持产品比较模型。
    // 仅替换原生能力查询；仍执行真实目录链、identity、长度与漂移检查。原生能力另有强制 CI 用例。
    internal static StorageRelocationPhysicalStore Create(IArchivePublishMetadataDurabilityBarrier? durability = null,
        IStorageRelocationCapacityProbe? capacity = null)
        => new(durability, capacity, new StorageRelocationTargetComparisonProbe(path => StorageRelocationPhysicalStore.InspectIdentity(path, true)));
}
