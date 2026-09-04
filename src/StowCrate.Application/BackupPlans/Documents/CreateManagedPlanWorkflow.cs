using StowCrate.Core.BackupPlans;
using StowCrate.Core.Paths;

namespace StowCrate.Application.BackupPlans.Documents;

public sealed record NewManagedPlanRequest(string Name, string SourceName, string SourceOutputPath);

/// <summary>建立可继续配置的方案，不隐式选择物理目录或将整个源设为归档箱。</summary>
public sealed class CreateManagedPlanWorkflow(AuthoritativePlanWorkflow authority)
{
    public Task<AuthoritativePlanSnapshot> CreateAsync(NewManagedPlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceName);
        if (string.IsNullOrWhiteSpace(request.SourceOutputPath))
            throw new ArgumentException("请填写非空的输出相对目录，例如 projects。", nameof(request));
        var outputPath = new LogicalPath(request.SourceOutputPath);
        // 源输出目录是 frozen v1 的非空声明，不能用逻辑根代替，也不从显示名称自动推导。
        if (outputPath.IsRoot) throw new ArgumentException("输出相对目录不能是根目录，请填写目录名称。", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        var plan = new PortableBackupPlan(new(Guid.NewGuid()), request.Name.Trim(), null, new(1, 1, 1),
            [new(new(Guid.NewGuid()), request.SourceName.Trim(), outputPath)],
            new([], null), [], new(PortableArchiveFormat.SevenZip, PortableCompressionPreset.Standard, new NoProtection()),
            [], [], PortableLinkPolicy.Preserve, PortableChangeDetectionMode.Standard,
            new HistoryDisabled(), new ManualOnlySchedule(), []);
        return authority.CreateManagedAsync(plan, cancellationToken);
    }
}
