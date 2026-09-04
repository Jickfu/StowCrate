using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using StowCrate.App.ViewModels;

namespace StowCrate.App.Views;

public partial class MainWindow : Window
{
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
