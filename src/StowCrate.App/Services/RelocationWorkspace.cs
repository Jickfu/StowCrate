using System.Collections.Immutable;
using StowCrate.Application.BackupPlans.Documents;
using StowCrate.Application.StorageMaintenance;
using StowCrate.Core.BackupPlans;
using StowCrate.Infrastructure.Configuration.BackupPlans;
using StowCrate.Infrastructure.Filesystem;
using StowCrate.Infrastructure.Persistence.ConfigDb;

namespace StowCrate.App.Services;

public sealed record RelocationPlanChoice(PlanId Id, string Name, string CurrentRoot, string HistoryRoot);
public interface IRelocationWorkspace
{
    Task<ImmutableArray<RelocationPlanChoice>> OpenAsync(string databasePath, CancellationToken cancellationToken);
    Task<StorageRelocationTargetInspection> InspectAsync(PlanId planId, string? currentRoot, string? historyRoot, CancellationToken cancellationToken);
}

/// <summary>桌面组合适配器只装配已有用例，不在界面重写迁移安全规则。</summary>
public sealed class RelocationWorkspace : IRelocationWorkspace
{
    private StorageRelocationInspectionWorkflow? inspection;
    private readonly LocalPhysicalPathResolver paths = new();
    public async Task<ImmutableArray<RelocationPlanChoice>> OpenAsync(string databasePath, CancellationToken cancellationToken)
    {
        inspection = null;
        // 本入口只打开已有配置库，拼错路径不能意外创建一个空库。
        if (!File.Exists(databasePath)) throw new FileNotFoundException("配置库不存在，请选择已有的 config.db。");
        var repository = await ConfigDbOpenCoordinator.OpenAsync(databasePath, null, null, cancellationToken).ConfigureAwait(false);
        var configuration = new StorageRelocationConfigurationReader(new AuthoritativePlanWorkflow(repository, new BackupPlanDocumentSource()));
        var choices = ImmutableArray.CreateBuilder<RelocationPlanChoice>();
        foreach (var registration in await repository.ListRegisteredAsync(true, cancellationToken).ConfigureAwait(false))
        {
            var snapshot = await configuration.ReadAsync(registration.PlanId, cancellationToken).ConfigureAwait(false);
            var binding = await repository.LoadAsync(registration.PlanId, cancellationToken).ConfigureAwait(false);
            choices.Add(new(registration.PlanId, snapshot.Snapshot.Plan.Name,
                binding?.CurrentRoot?.CanonicalPath ?? "未绑定", binding?.HistoryRoot?.CanonicalPath ?? "未绑定"));
        }
        var physical = new StorageRelocationPhysicalStore();
        inspection = new(configuration, repository, physical, physical, new StorageRelocationTargetComparisonProbe(), physical);
        return choices.ToImmutable();
    }
    public async Task<StorageRelocationTargetInspection> InspectAsync(PlanId planId, string? currentRoot, string? historyRoot, CancellationToken cancellationToken)
    {
        var workflow = inspection ?? throw new InvalidOperationException("请先打开配置库。");
        var current = string.IsNullOrWhiteSpace(currentRoot) ? null : await paths.ResolveAsync(currentRoot, cancellationToken).ConfigureAwait(false);
        var history = string.IsNullOrWhiteSpace(historyRoot) ? null : await paths.ResolveAsync(historyRoot, cancellationToken).ConfigureAwait(false);
        return await workflow.InspectTargetsAsync(new(planId, current, history), Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
    }
}
