using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StowCrate.App.Services;
using StowCrate.Application.LocalState;
using StowCrate.Core.BackupPlans;

namespace StowCrate.App.ViewModels;

public partial class SourceDirectoryRow(SourceId id, string name, string path) : ObservableObject
{
    public SourceId Id { get; } = id;
    public string Name { get; } = name;
    [ObservableProperty] public partial string Path { get; set; } = path;
}

public partial class DirectoryBindingsViewModel(IRelocationWorkspace workspace) : ObservableObject
{
    private PlanId? selected;
    private DirectoryBindingSnapshot? snapshot;
    private CancellationTokenSource? operation;
    public Task PendingLoad { get; private set; } = Task.CompletedTask;
    public ObservableCollection<SourceDirectoryRow> Sources { get; } = [];
    [ObservableProperty] public partial string CurrentRoot { get; set; } = "";
    [ObservableProperty] public partial string HistoryRoot { get; set; } = "";
    [ObservableProperty] public partial bool HistoryRequired { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool HostBusy { get; set; }
    public DirectoryBindingSnapshot? Loaded => snapshot;
    [ObservableProperty] public partial string Status { get; set; } = "选择方案后读取本机目录。";
    public bool CanSave => !IsBusy && !HostBusy && snapshot is not null;
    public bool CanReload => !IsBusy && !HostBusy && selected is not null;
    public bool CanEdit => CanSave;
    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnHostBusyChanged(bool value) => NotifyCommands();
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanSave)); OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(Loaded));
        SaveCommand.NotifyCanExecuteChanged(); ReloadCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }
    public void SelectPlan(PlanId? id)
    {
        operation?.Cancel(); selected = id; snapshot = null;
        Sources.Clear(); CurrentRoot = ""; HistoryRoot = ""; HistoryRequired = false;
        NotifyCommands();
        PendingLoad = LoadSelectionAsync(id);
    }
    private async Task LoadSelectionAsync(PlanId? id)
    {
        using var pending = new CancellationTokenSource(); operation = pending;
        IsBusy = id is not null;
        Status = id is null ? "选择方案后读取本机目录。" : "正在读取本机目录…";
        try
        {
            if (id is null) return;
            var loaded = await Task.Run(() => workspace.LoadBindingsAsync(id.Value, pending.Token), pending.Token);
            if (operation != pending || pending.IsCancellationRequested) return;
            Apply(loaded); Status = "目录已读取；保存配置不会执行备份。";
        }
        catch (Exception exception)
        {
            if (operation == pending) { snapshot = null; Status = $"目录读取未完成：{exception.Message}"; }
        }
        finally { if (operation == pending) { operation = null; IsBusy = false; NotifyCommands(); } }
    }
    private void Apply(DirectoryBindingSnapshot loaded)
    {
        snapshot = loaded; Sources.Clear();
        foreach (var source in loaded.Configuration.Plan.Sources)
            Sources.Add(new(source.Id, source.Name, loaded.Bindings?.Sources.FirstOrDefault(x => x.SourceId == source.Id && x.IsActive)?.CanonicalPath ?? ""));
        CurrentRoot = loaded.Bindings?.CurrentRoot?.CanonicalPath ?? "";
        HistoryRoot = loaded.Bindings?.HistoryRoot?.CanonicalPath ?? "";
        HistoryRequired = loaded.HistoryRequired;
        NotifyCommands();
    }
    [RelayCommand(CanExecute = nameof(CanReload))]
    private Task ReloadAsync() => PendingLoad = LoadSelectionAsync(selected);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave) return;
        var original = snapshot!;
        var edit = new DirectoryBindingEdit(original, [.. Sources.Select(x => new SourceBindingInput(x.Id, x.Path))], CurrentRoot, HistoryRoot);
        using var pending = new CancellationTokenSource(); operation = pending; IsBusy = true;
        Status = "正在验证并保存目录…";
        try
        {
            var saved = await Task.Run(() => workspace.SaveBindingsAsync(edit, pending.Token), pending.Token);
            if (operation == pending) { Apply(saved); Status = "目录已保存并重新读取；归档箱配置尚未完成，未执行备份。"; }
        }
        catch (Exception exception)
        {
            if (operation != pending) return;
            snapshot = null;
            var diagnostic = exception switch
            {
                StorageRelocationRequiredException => "输出根已有归档或恢复依赖，请使用受控迁移。",
                LocalBindingValidationException => "目录存在重叠或方案间冲突，请选择互不重叠的源与输出位置。",
                OperationCanceledException => "保存已取消，不能据此判断是否已提交。",
                _ => exception.Message
            };
            // 写入结果未知时只读核对，使用独立且有时限的 token；绝不自动重试写入。
            using var verification = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                var actual = await Task.Run(() => workspace.LoadBindingsAsync(original.Configuration.Plan.Id, verification.Token), verification.Token);
                if (operation == pending) { Apply(actual); Status = $"{diagnostic} 已读回实际持久状态，请核对后重新编辑。"; }
            }
            catch (Exception readError)
            {
                if (operation == pending) Status = $"{diagnostic} 读回失败：{readError.Message}。请先重新读取，当前禁止保存。";
            }
        }
        finally { if (operation == pending) { operation = null; IsBusy = false; NotifyCommands(); } }
    }
    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => operation?.Cancel();
}
