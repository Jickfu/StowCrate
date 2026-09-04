using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using StowCrate.App.Services;
using StowCrate.App.ViewModels;
using StowCrate.App.Views;
using StowCrate.Application.StorageMaintenance;
using StowCrate.Core.BackupPlans;

[assembly: AvaloniaTestApplication(typeof(StowCrate.App.Tests.TestApplication))]

namespace StowCrate.App.Tests;

public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<StowCrate.App.App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class RelocationWindowTests
{
    [AvaloniaFact]
    public void WindowStartsWithPreviewDisabledAndRenders()
    {
        var model = new MainViewModel(new Workspace());
        var window = new MainWindow { DataContext = model };
        window.Show();
        try
        {
            Assert.False(model.PreviewCommand.CanExecute(null));
            var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(), x => Equals(x.Content, "检查迁移目标"));
            Assert.False(button.IsEffectivelyEnabled);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (Environment.GetEnvironmentVariable("STOWCRATE_UI_SCREENSHOT") is { Length: > 0 } output)
                frame.Save(output, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ChangingDatabaseClearsSelectionAndPreviewAuthority()
    {
        var model = new MainViewModel(new Workspace()) { DatabasePath = "first.db" };
        await model.OpenCommand.ExecuteAsync(null);
        Assert.True(model.CanPreview);
        model.NewCurrentRoot = "/target";
        model.DatabasePath = "second.db";
        Assert.Empty(model.Plans); Assert.Null(model.SelectedPlan);
        Assert.False(model.CanPreview); Assert.Empty(model.NewCurrentRoot);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewReportsRootAndCapabilityFailures(bool missingRoot)
    {
        var workspace = new Workspace { Failure = missingRoot ? new StorageRelocationTargetRootMissingException(StorageRootKind.Current) : new StorageRelocationComparisonUnavailableException() };
        var model = new MainViewModel(workspace);
        await model.OpenCommand.ExecuteAsync(null);
        model.NewCurrentRoot = "/target";
        await model.PreviewCommand.ExecuteAsync(null);
        Assert.Equal("检查未通过", model.Status);
        Assert.Contains(missingRoot ? "请先创建目录" : "比较规则", model.Details, StringComparison.Ordinal);
        Assert.False(model.IsBusy);
    }

    [AvaloniaFact]
    public async Task EditingTargetInvalidatesSuccessfulPreview()
    {
        var model = new MainViewModel(new Workspace { Succeed = true });
        await model.OpenCommand.ExecuteAsync(null);
        model.NewCurrentRoot = "/target";
        await model.PreviewCommand.ExecuteAsync(null);
        Assert.Contains("检查通过", model.Status, StringComparison.Ordinal);
        model.NewHistoryRoot = "/other";
        Assert.Equal("尚未检查", model.Status);
    }

    [AvaloniaFact]
    public async Task CancellationRestoresControlsAndDoesNotReportSuccess()
    {
        var workspace = new Workspace { WaitForCancellation = true };
        var model = new MainViewModel(workspace);
        await model.OpenCommand.ExecuteAsync(null);
        model.NewCurrentRoot = "/target";
        var running = model.PreviewCommand.ExecuteAsync(null);
        await workspace.Entered.Task;
        Assert.False(model.CanEdit); Assert.False(model.CanPreview);
        model.CancelCommand.Execute(null);
        await running;
        Assert.Equal("已取消", model.Status); Assert.True(model.CanEdit);
    }

    [Fact]
    public async Task MissingDatabaseIsNotCreated()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        await Assert.ThrowsAsync<FileNotFoundException>(() => new RelocationWorkspace().OpenAsync(path, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path));
    }

    private sealed class Workspace : IRelocationWorkspace
    {
        public Exception? Failure { get; init; }
        public bool WaitForCancellation { get; init; }
        public bool Succeed { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ImmutableArray<RelocationPlanChoice>> OpenAsync(string path, CancellationToken token)
            => Task.FromResult<ImmutableArray<RelocationPlanChoice>>([new(new(Guid.NewGuid()), "测试方案", "/current", "/history")]);
        public async Task<StorageRelocationTargetInspection> InspectAsync(PlanId planId, string? currentRoot, string? historyRoot, CancellationToken token)
        {
            Entered.SetResult();
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, token);
            if (Succeed) return new(Guid.NewGuid(), new(new(planId, new(Guid.NewGuid()), [], []), [], [], []));
            throw Failure ?? new InvalidOperationException("Unexpected preview.");
        }
    }
}
