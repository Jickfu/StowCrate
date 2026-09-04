using StowCrate.Application.BackupPlans.Candidates;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.Filesystem;

namespace StowCrate.Application.LocalState;

public interface ISourceTreeReader
{
    Task<SourceScanResult> ReadAsync(SourceId sourceId, string savedRoot, CancellationToken cancellationToken);
}

public sealed record SourceTreeObservation(SourceId SourceId, string Name, string Root, SourceScanResult Scan);

/// <summary>浏览只使用持久化绑定，不接受表单草稿，也不产生候选归档或执行凭证。</summary>
public sealed class SourceTreeWorkflow(DirectoryBindingEditorWorkflow bindings, ISourceTreeReader reader)
{
    public async Task<SourceTreeObservation> ReadAsync(PlanId planId, SourceId sourceId, CancellationToken cancellationToken)
    {
        var before = await bindings.LoadAsync(planId, cancellationToken).ConfigureAwait(false);
        if (!before.Configuration.IsActive) throw new InvalidOperationException("方案未启用，不能浏览备份源。");
        var source = before.Configuration.Plan.Sources.SingleOrDefault(x => x.Id == sourceId)
            ?? throw new InvalidOperationException("备份源已不属于当前方案，请重新读取目录。");
        var binding = before.Bindings?.Sources.SingleOrDefault(x => x.SourceId == sourceId && x.IsActive)
            ?? throw new InvalidOperationException("请先保存此备份源的目录绑定。");
        var scan = await reader.ReadAsync(sourceId, binding.CanonicalPath, cancellationToken).ConfigureAwait(false);
        var after = await bindings.LoadAsync(planId, cancellationToken).ConfigureAwait(false);
        // File-backed 没有 Managed revision，仍须比较实际配置；过时观察不能贴到新方案上。
        if (!after.Configuration.IsActive || before.Configuration.Authority != after.Configuration.Authority
            || before.Configuration.FileDocumentPath != after.Configuration.FileDocumentPath
            || CandidateFingerprintCalculator.ComputePlanSemantic(before.Configuration.Plan) != CandidateFingerprintCalculator.ComputePlanSemantic(after.Configuration.Plan)
            || before.Bindings!.DeviceId != after.Bindings?.DeviceId
            || binding != after.Bindings?.Sources.SingleOrDefault(x => x.SourceId == sourceId && x.IsActive))
            throw new LocalStateConcurrencyException("浏览期间方案或源目录绑定已变化，请重新读取。");
        cancellationToken.ThrowIfCancellationRequested();
        return new(sourceId, source.Name, binding.CanonicalPath, scan);
    }
}
