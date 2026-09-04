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
        cancellationToken.ThrowIfCancellationRequested();
        var plan = new PortableBackupPlan(new(Guid.NewGuid()), request.Name.Trim(), null, new(1, 1, 1),
            [new(new(Guid.NewGuid()), request.SourceName.Trim(), new LogicalPath(request.SourceOutputPath))],
            new([], null), [], new(PortableArchiveFormat.SevenZip, PortableCompressionPreset.Standard, new NoProtection()),
            [], [], PortableLinkPolicy.Preserve, PortableChangeDetectionMode.Standard,
            new HistoryDisabled(), new ManualOnlySchedule(), []);
        return authority.CreateManagedAsync(plan, cancellationToken);
    }
}
