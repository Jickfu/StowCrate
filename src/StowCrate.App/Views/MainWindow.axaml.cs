using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using StowCrate.App.ViewModels;

namespace StowCrate.App.Views;

public partial class MainWindow : Window
{
    private async void ChooseBindingDirectory(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MainViewModel model || sender is not Button button || !model.Bindings.CanEdit) return;
        var originalPlan = model.SelectedPlan;
        var row = button.DataContext as SourceDirectoryRow;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "选择已存在的目录", AllowMultiple = false });
            // 选择器返回时方案可能已切换；取消或过时结果不能污染新方案编辑态。
            if (!model.Bindings.CanEdit || originalPlan != model.SelectedPlan || folders.Count != 1 || folders[0].TryGetLocalPath() is not { } path) return;
            if (Equals(button.Tag, "source") && row is not null && model.Bindings.Sources.Contains(row)) row.Path = path;
            else if (Equals(button.Tag, "current")) model.Bindings.CurrentRoot = path;
            else if (Equals(button.Tag, "history") && model.Bindings.HistoryRequired) model.Bindings.HistoryRoot = path;
        }
        catch (Exception exception) { model.Bindings.Status = $"无法选择目录：{exception.Message}"; }
    }
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void ChooseDatabase(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MainViewModel model || model.IsBusy) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new()
            {
                Title = "选择已有配置库", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("SQLite 配置库") { Patterns = ["*.db"] }]
            });
            if (!model.IsBusy && files.Count == 1 && files[0].TryGetLocalPath() is { } path) model.DatabasePath = path;
        }
        catch (Exception exception) { model.Status = "无法打开文件选择器"; model.Details = exception.Message; }
    }
}
