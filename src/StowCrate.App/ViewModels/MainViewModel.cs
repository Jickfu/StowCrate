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
    [ObservableProperty] public partial string Status { get; set; } = "选择已有配置库，查看方案并检查迁移目标。";
    [ObservableProperty] public partial string Details { get; set; } = "此页面提供迁移预览，不会复制归档或启动迁移。";
    public bool CanEdit => !IsBusy;
    public bool CanPreview => !IsBusy && SelectedPlan is not null;
    public string CurrentRootDisplay => SelectedPlan?.CurrentRoot ?? "尚未选择方案";
    public string HistoryRootDisplay => SelectedPlan?.HistoryRoot ?? "尚未选择方案";
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanPreview));
        OpenCommand.NotifyCanExecuteChanged(); PreviewCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedPlanChanged(RelocationPlanChoice? value)
    {
        NewCurrentRoot = ""; NewHistoryRoot = ""; InvalidatePreview();
        OnPropertyChanged(nameof(CanPreview)); PreviewCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CurrentRootDisplay)); OnPropertyChanged(nameof(HistoryRootDisplay));
    }
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
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        IsBusy = true; Status = "正在检查，请稍候…"; Details = "可取消当前操作。";
        using var cancellation = new CancellationTokenSource(); operation = cancellation;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Status = "已取消"; Details = "未启动迁移。"; }
        catch (Exception exception)
        {
            Status = "检查未通过";
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
