using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using StowCrate.App.Services;
using StowCrate.Application.StorageMaintenance;

namespace StowCrate.App.ViewModels;

public partial class MainViewModel(IRelocationWorkspace workspace) : ViewModelBase
{
    private CancellationTokenSource? operation;
    public ObservableCollection<RelocationPlanChoice> Plans { get; } = [];
    [ObservableProperty] public partial string DatabasePath { get; set; } = "";
    [ObservableProperty] public partial RelocationPlanChoice? SelectedPlan { get; set; }
    [ObservableProperty] public partial string NewCurrentRoot { get; set; } = "";
    [ObservableProperty] public partial string NewHistoryRoot { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial StorageRelocationJournal? Journal { get; set; }
    [ObservableProperty] public partial bool ConfirmResume { get; set; }
    [ObservableProperty] public partial string JournalDetails { get; set; } = "尚未读取迁移事务。";
    [ObservableProperty] public partial bool RootsMayBeStale { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "选择已有配置库，查看方案并检查迁移目标。";
    [ObservableProperty] public partial string Details { get; set; } = "可检查新目标，或读取已有迁移事务后明确选择继续。";
    public bool CanEdit => !IsBusy;
    public bool CanPreview => !IsBusy && SelectedPlan is not null;
    public bool CanResume => !IsBusy && ConfirmResume && Journal is { Progress.Stage: not StorageTransferStage.Completed }
        && SelectedPlan?.Id == Journal.Manifest.PlanId;
    public string CurrentRootDisplay => RootsMayBeStale ? "绑定可能已更新，请重新打开配置库刷新。" : SelectedPlan?.CurrentRoot ?? "尚未选择方案";
    public string HistoryRootDisplay => RootsMayBeStale ? "绑定可能已更新，请重新打开配置库刷新。" : SelectedPlan?.HistoryRoot ?? "尚未选择方案";
    partial void OnRootsMayBeStaleChanged(bool value) { OnPropertyChanged(nameof(CurrentRootDisplay)); OnPropertyChanged(nameof(HistoryRootDisplay)); }
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanPreview));
        OpenCommand.NotifyCanExecuteChanged(); PreviewCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        ReadJournalCommand.NotifyCanExecuteChanged(); ResumeCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedPlanChanged(RelocationPlanChoice? value)
    {
        NewCurrentRoot = ""; NewHistoryRoot = ""; InvalidatePreview();
        RootsMayBeStale = false;
        Journal = null; JournalDetails = "尚未读取迁移事务。";
        OnPropertyChanged(nameof(CanPreview)); PreviewCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CurrentRootDisplay)); OnPropertyChanged(nameof(HistoryRootDisplay));
        ReadJournalCommand.NotifyCanExecuteChanged(); ResumeCommand.NotifyCanExecuteChanged();
    }
    partial void OnConfirmResumeChanged(bool value) => ResumeCommand.NotifyCanExecuteChanged();
    partial void OnJournalChanged(StorageRelocationJournal? value) { ConfirmResume = false; ResumeCommand.NotifyCanExecuteChanged(); }
    partial void OnNewCurrentRootChanged(string value) => InvalidatePreview();
    partial void OnNewHistoryRootChanged(string value) => InvalidatePreview();
    partial void OnDatabasePathChanged(string value) { Plans.Clear(); SelectedPlan = null; InvalidatePreview(); }
    private void InvalidatePreview()
    {
        Status = "尚未检查";
        Details = "选择要迁移的目标根；留空表示该根不迁移。目标目录必须已存在。";
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task OpenAsync()
    {
        var path = DatabasePath; Plans.Clear(); SelectedPlan = null;
        await RunAsync(async token =>
        {
            var plans = await Task.Run(() => workspace.OpenAsync(path, token), token);
            token.ThrowIfCancellationRequested();
            foreach (var plan in plans) Plans.Add(plan);
            SelectedPlan = Plans.FirstOrDefault();
            Status = plans.Length == 0 ? "配置库中没有启用的方案" : $"已加载 {plans.Length} 个方案";
            Details = "填写迁移目标后检查。此页面不会自动复制、恢复或清理归档。";
        });
    }
    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync()
    {
        var plan = SelectedPlan!; var current = NewCurrentRoot; var history = NewHistoryRoot;
        if (string.IsNullOrWhiteSpace(current) && string.IsNullOrWhiteSpace(history)) { Status = "请填写至少一个迁移目标"; return; }
        await RunAsync(async token =>
        {
            var result = await Task.Run(() => workspace.InspectAsync(plan.Id, current, history, token), token);
            token.ThrowIfCancellationRequested();
            Status = $"目标检查通过 · {result.Observation.Entries.Length} 个归档";
            Details = "已检查旧归档完整性、容量及目标路径。本次观察不代表迁移已启动，启动时仍须重新检查。";
        });
    }
    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task ReadJournalAsync()
    {
        var plan = SelectedPlan!;
        Journal = null; JournalDetails = "正在读取事务…";
        await RunAsync(async token =>
        {
            var journal = await Task.Run(() => workspace.LoadJournalAsync(plan.Id, token), token);
            token.ThrowIfCancellationRequested();
            Journal = journal;
            JournalDetails = journal is null ? "此方案没有迁移事务。" :
                $"事务：{journal.Manifest.TransactionId:D}\n版本：{journal.Revision} · {StageLabel(journal.Progress.Stage)} · {journal.Manifest.Entries.Length} 个归档\n"
                + string.Join("\n", journal.Manifest.Roots.Select(x => $"{x.Kind}：{x.OldRoot.CanonicalPath} → {x.NewRoot.CanonicalPath}"));
            Status = "事务读取完成"; Details = "读取不会复制、删除归档或推进事务。";
        });
        if (Journal is null && JournalDetails == "正在读取事务…") JournalDetails = "读取未完成，请重试；当前不能恢复。";
    }

    private static string StageLabel(StorageTransferStage stage) => stage switch
    {
        StorageTransferStage.Prepared => "等待继续复制",
        StorageTransferStage.TargetsDurable => "目标已持久化，等待提交",
        StorageTransferStage.MetadataCommitted => "迁移已提交，等待清理旧副本",
        StorageTransferStage.Completed => "已完成，根目录保护仍保留",
        _ => "未知状态"
    };

    [RelayCommand(CanExecute = nameof(CanResume))]
    private async Task ResumeAsync()
    {
        if (!CanResume) return;
        var selected = Journal!;
        RootsMayBeStale = true;
        Journal = null;
        JournalDetails = $"本次操作事务：{selected.Manifest.TransactionId:D}。操作后须重新读取状态。";
        await RunAsync(async token =>
        {
            var result = await Task.Run(() => workspace.ResumeAsync(selected.Manifest.PlanId, selected.Manifest.TransactionId, token), token);
            // 已提交的结果不能被晚到的取消覆盖；清理取消由 Application 返回 CleanupPending。
            Status = result.Status switch
            {
                StorageRelocationRecoveryStatus.CompletedReservationsRetained => "迁移已完成，根目录保护仍保留",
                StorageRelocationRecoveryStatus.CleanupPending => "迁移已提交，旧副本清理待继续",
                StorageRelocationRecoveryStatus.ResumeRequired => "迁移已暂停，仍需恢复",
                StorageRelocationRecoveryStatus.NotFound => "所选事务不存在，请重新读取",
                _ => "操作结果未确认，请重新读取事务"
            };
            Details = $"事务：{selected.Manifest.TransactionId:D}。请重新打开配置库刷新根绑定，再读取事务。"
                + (result.Diagnostic is null ? "" : $"\n诊断：{result.Diagnostic}");
        }, mayWrite: true);
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool mayWrite = false)
    {
        if (IsBusy) return;
        IsBusy = true; Status = "正在检查，请稍候…"; Details = "可取消当前操作。";
        using var cancellation = new CancellationTokenSource(); operation = cancellation;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Status = "已取消"; Details = mayWrite ? "操作已停止，请重新读取原事务；取消不会回滚已提交的迁移。" : "未启动迁移。"; }
        catch (Exception exception)
        {
            Status = mayWrite ? "操作未完成，请重新读取原事务" : "检查未通过";
            Details = exception switch
            {
                StorageRelocationTargetRootMissingException => "迁移目标根目录不存在，请先创建目录后重试。",
                StorageRelocationComparisonUnavailableException => "当前目标文件系统的大小写或 Unicode 比较规则暂无法可靠识别，已阻止迁移。当前仅支持部分 Linux ext 文件系统。",
                StorageRelocationCapacityException => "目标可用空间不足或无法查询，已阻止迁移。",
                _ => exception.Message
            };
        }
        finally { operation = null; IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => operation?.Cancel();
}
