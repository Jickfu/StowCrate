using System.Collections.Immutable;
using StowCrate.Application.BackupPlans.Documents;
using StowCrate.Core.BackupPlans;

namespace StowCrate.Application.LocalState;

public interface IExistingBindingDirectoryProbe
{
    Task RequireDirectoryAsync(string path, CancellationToken cancellationToken);
}

public sealed record DirectoryBindingSnapshot(AuthoritativePlanSnapshot Configuration, DevicePlanLocalBindings? Bindings)
{
    public bool HistoryRequired => Configuration.Plan.ArchiveUnits.Any(unit =>
        HistoryPolicy.Resolve(Configuration.Plan.HistoryDefault, unit.HistoryOverride) is EffectiveHistoryEnabled);
}

public sealed record DirectoryBindingEdit(DirectoryBindingSnapshot Original, ImmutableArray<SourceBindingInput> Sources,
    string CurrentRoot, string? HistoryRoot);

/// <summary>目录表单只更新展示的绑定；隐藏的 History/External 状态不能因局部编辑被清空。</summary>
public sealed class DirectoryBindingEditorWorkflow(AuthoritativePlanWorkflow authority, IDevicePlanBindingStore store,
    LocalBindingWorkflow bindings, IExistingBindingDirectoryProbe directories)
{
    public async Task<DirectoryBindingSnapshot> LoadAsync(PlanId id, CancellationToken cancellationToken)
        => new(await authority.LoadAsync(id, cancellationToken).ConfigureAwait(false),
            await store.LoadAsync(id, cancellationToken).ConfigureAwait(false));

    public async Task<DirectoryBindingSnapshot> SaveAsync(DirectoryBindingEdit edit, CancellationToken cancellationToken)
    {
        var id = edit.Original.Configuration.Plan.Id;
        var current = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        // 检查编辑期间已经可见的变更；仓库事务仍负责最终 reservation/placement 安全校验。
        if (current.Configuration.ManagedRevision != edit.Original.Configuration.ManagedRevision
            || current.Configuration.Authority != edit.Original.Configuration.Authority
            || !SameBindings(current.Bindings, edit.Original.Bindings))
            throw new LocalStateConcurrencyException("目录配置已变化，请重新读取后再编辑。");
        var plan = current.Configuration.Plan;
        if (edit.Sources.Length != plan.Sources.Length || edit.Sources.Select(x => x.SourceId).Distinct().Count() != plan.Sources.Length
            || plan.Sources.Any(x => !edit.Sources.Any(s => s.SourceId == x.Id && s.IsActive && !string.IsNullOrWhiteSpace(s.Path))))
            throw new ArgumentException("请为方案中的每个备份源填写目录。");
        if (string.IsNullOrWhiteSpace(edit.CurrentRoot)) throw new ArgumentException("请填写 Current 输出根目录。");
        var history = current.HistoryRequired ? edit.HistoryRoot : current.Bindings?.HistoryRoot?.CanonicalPath;
        if (current.HistoryRequired && string.IsNullOrWhiteSpace(history)) throw new ArgumentException("当前方案需要 History 根目录。");
        foreach (var source in edit.Sources) await directories.RequireDirectoryAsync(source.Path, cancellationToken).ConfigureAwait(false);
        await directories.RequireDirectoryAsync(edit.CurrentRoot, cancellationToken).ConfigureAwait(false);
        if (current.HistoryRequired) await directories.RequireDirectoryAsync(history!, cancellationToken).ConfigureAwait(false);
        var externalIds = plan.ExternalSources.Select(x => x.Id).ToHashSet();
        var external = current.Bindings?.ExternalSources.Where(x => externalIds.Contains(x.ExternalSourceId))
            .Select(x => new ExternalBindingInput(x.ExternalSourceId, x.CanonicalPath, x.IsActive)).ToImmutableArray() ?? [];
        await bindings.SaveAsync(plan, new(id, edit.Sources, edit.CurrentRoot, history, external), cancellationToken).ConfigureAwait(false);
        return await LoadAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private static bool SameBindings(DevicePlanLocalBindings? a, DevicePlanLocalBindings? b)
        => a is null ? b is null : b is not null && a.DeviceId == b.DeviceId
            && a.CurrentRoot == b.CurrentRoot && a.HistoryRoot == b.HistoryRoot
            && a.Sources.OrderBy(x => x.SourceId.Value).SequenceEqual(b.Sources.OrderBy(x => x.SourceId.Value))
            && a.ExternalSources.OrderBy(x => x.ExternalSourceId.Value).SequenceEqual(b.ExternalSources.OrderBy(x => x.ExternalSourceId.Value));
}
