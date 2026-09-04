using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StowCrate.App.Services;
using StowCrate.Application.LocalState;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;

namespace StowCrate.App.ViewModels;

public sealed record SourceTreeChoice(SourceId Id, string Name);
public sealed record SourceTreeNode(string Name, string Kind, LogicalPath Path)
{
    public ObservableCollection<SourceTreeNode> Children { get; } = [];
}

public partial class SourceTreeViewModel(IRelocationWorkspace workspace) : ObservableObject
{
    private PlanId? planId;
    private CancellationTokenSource? operation;
    private long selectionVersion;
    public ObservableCollection<SourceTreeChoice> Sources { get; } = [];
    public ObservableCollection<SourceTreeNode> Roots { get; } = [];
    [ObservableProperty] public partial SourceTreeChoice? SelectedSource { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool HostBusy { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "保存源目录绑定后，可读取源目录树。";
    [ObservableProperty] public partial string Issues { get; set; } = "";
    [ObservableProperty] public partial string RootPath { get; set; } = "";
    public bool CanRead => !IsBusy && !HostBusy && planId is not null && SelectedSource is not null;
    public bool CanSelect => !IsBusy && !HostBusy;
    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnHostBusyChanged(bool value) => NotifyCommands();
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanSelect));
        ReadCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedSourceChanged(SourceTreeChoice? value) { Clear(); NotifyCommands(); }
    private void Clear()
    {
        selectionVersion++;
        operation?.Cancel(); Roots.Clear(); Issues = ""; RootPath = "";
        Status = "读取已保存的源目录；浏览不会执行备份。";
    }
    public void SetSnapshot(DirectoryBindingSnapshot? snapshot)
    {
        Clear(); planId = snapshot?.Configuration.Plan.Id;
        Sources.Clear(); SelectedSource = null;
        if (snapshot is not null)
            foreach (var source in snapshot.Configuration.Plan.Sources)
                if (snapshot.Bindings?.Sources.Any(x => x.SourceId == source.Id && x.IsActive) == true)
                    Sources.Add(new(source.Id, source.Name));
        SelectedSource = Sources.FirstOrDefault(); NotifyCommands();
    }
    [RelayCommand(CanExecute = nameof(CanRead))]
    private async Task ReadAsync()
    {
        if (!CanRead) return;
        var selected = SelectedSource!; var plan = planId!.Value;
        Clear();
        var version = selectionVersion;
        using var pending = new CancellationTokenSource(); operation = pending; IsBusy = true;
        Status = "正在读取源目录树…";
        try
        {
            // 扫描和树结构组装均在后台执行，界面只接收完成的展示模型。
            var result = await Task.Run(async () =>
            {
                var observed = await workspace.ReadSourceTreeAsync(plan, selected.Id, pending.Token);
                return (Observed: observed, Tree: BuildTree(observed, pending.Token));
            }, pending.Token);
            if (pending.IsCancellationRequested || selectionVersion != version) return;
            RootPath = result.Observed.Root;
            if (result.Tree is not null) Roots.Add(result.Tree);
            Issues = string.Join("\n", result.Observed.Scan.Issues.Select(x => $"{x.Severity} · {x.Code} · {x.Path?.Value ?? "源根"}：{x.Message}"));
            Status = result.Tree is null ? "源目录读取失败，请查看扫描问题。"
                : result.Observed.Scan.HasWarnings ? "源目录部分读取，有遗漏；请查看扫描问题。"
                : $"源目录已读取 · {result.Observed.Scan.Snapshot!.Entries.Count} 个条目（只读观察）";
        }
        catch (OperationCanceledException) { if (selectionVersion == version) Status = "源目录读取已取消。"; }
        catch (Exception exception) { if (selectionVersion == version) Status = $"源目录读取失败：{exception.Message}"; }
        finally
        {
            if (operation == pending)
            {
                if (pending.IsCancellationRequested && selectionVersion == version) Status = "源目录读取已取消。";
                operation = null; IsBusy = false;
            }
        }
    }
    private static SourceTreeNode? BuildTree(SourceTreeObservation observed, CancellationToken token)
    {
        if (observed.Scan.Snapshot is not { } snapshot) return null;
        var root = new SourceTreeNode(observed.Name, "备份源", LogicalPath.Root);
        var nodes = new Dictionary<LogicalPath, SourceTreeNode> { [LogicalPath.Root] = root };
        foreach (var entry in snapshot.Entries)
        {
            token.ThrowIfCancellationRequested();
            var kind = entry.Kind switch { FileSystemEntryKind.Directory => "目录", FileSystemEntryKind.File => "文件", FileSystemEntryKind.Link => "链接（不进入）", _ => "特殊对象" };
            var node = new SourceTreeNode(entry.Path.Name, kind, entry.Path);
            nodes.Add(entry.Path, node);
            nodes[entry.Path.Parent].Children.Add(node);
        }
        return root;
    }
    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => operation?.Cancel();
}
