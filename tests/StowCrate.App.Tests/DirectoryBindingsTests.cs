using System.Collections.Immutable;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using StowCrate.App.Views;
using StowCrate.App.Services;
using StowCrate.App.ViewModels;
using StowCrate.Application.BackupPlans.Documents;
using StowCrate.Application.LocalState;
using StowCrate.Application.StorageMaintenance;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.Paths;
using StowCrate.Core.Rules;
using StowCrate.Infrastructure.Configuration.BackupPlans;
using StowCrate.Infrastructure.Persistence.ConfigDb;

namespace StowCrate.App.Tests;

public sealed class DirectoryBindingsTests
{
    [AvaloniaFact]
    public async Task SourceTreeUsesSavedBindingAndKeepsNestedDirectoriesAndRawControlFile()
    {
        using var fixture = new Fixture();
        var model = new MainViewModel(fixture.Workspace);
        await model.StartCommand.ExecuteAsync(null);
        model.PlanName = "资料"; model.SourceName = "项目 / 文件"; model.SourceOutputPath = "projects";
        await model.CreatePlanCommand.ExecuteAsync(null);
        Assert.False(model.SourceTree.ReadCommand.CanExecute(null));
        var source = fixture.Directory("source");
        System.IO.Directory.CreateDirectory(Path.Combine(source, "B", "D"));
        await File.WriteAllTextAsync(Path.Combine(source, "B", "D", "中文.txt"), "payload", TestContext.Current.CancellationToken);
        var control = Path.Combine(source, "B", ".backupignore");
        await File.WriteAllBytesAsync(control, [0xff, 0xfe], TestContext.Current.CancellationToken);
        model.Bindings.Sources[0].Path = source;
        model.Bindings.CurrentRoot = fixture.Directory("current");
        await model.Bindings.SaveCommand.ExecuteAsync(null);
        Assert.True(model.SourceTree.ReadCommand.CanExecute(null));
        // 草稿路径与持久绑定分离；浏览不得读到未保存目录。
        model.Bindings.Sources[0].Path = fixture.Directory("unsaved");
        await model.SourceTree.ReadCommand.ExecuteAsync(null);
        Assert.Equal(source, model.SourceTree.RootPath);
        var root = Assert.Single(model.SourceTree.Roots);
        Assert.Equal("项目 / 文件", root.Name);
        var b = Assert.Single(root.Children);
        Assert.Contains(b.Children, x => x.Name == ".backupignore" && x.Kind == "文件");
        Assert.Equal("中文.txt", Assert.Single(b.Children.Single(x => x.Name == "D").Children).Name);
        Assert.Empty(model.SourceTree.Issues);
        Assert.Equal(new byte[] { 0xff, 0xfe }, await File.ReadAllBytesAsync(control, TestContext.Current.CancellationToken));
        Assert.Empty(System.IO.Directory.EnumerateFileSystemEntries(model.Bindings.CurrentRoot));
        var window = new MainWindow { DataContext = model };
        window.Show();
        try
        {
            var tree = window.GetVisualDescendants().OfType<TreeView>().Single();
            Assert.Single(tree.Items);
            if (tree.ContainerFromIndex(0) is TreeViewItem container) tree.ExpandSubTree(container);
            tree.BringIntoView();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (Environment.GetEnvironmentVariable("STOWCRATE_UI_SCREENSHOT") is { Length: > 0 } output)
                frame.Save(Path.ChangeExtension(output, "source-tree.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        finally { window.Close(); }
        model.DatabasePath = "other.db";
        Assert.Empty(model.SourceTree.Roots); Assert.Empty(model.SourceTree.Sources);
    }

    [AvaloniaFact]
    public async Task MissingSourceIsVisibleAndClearsPreviousTree()
    {
        using var fixture = new Fixture();
        var initial = await fixture.Create();
        var source = fixture.Directory("source");
        var saved = await fixture.Workspace.SaveBindingsAsync(new(initial,
            [new(initial.Configuration.Plan.Sources[0].Id, source)], fixture.Directory("current"), null), TestContext.Current.CancellationToken);
        var model = new SourceTreeViewModel(fixture.Workspace);
        model.SetSnapshot(saved);
        await model.ReadCommand.ExecuteAsync(null);
        Assert.Single(model.Roots);
        System.IO.Directory.Delete(source);
        await model.ReadCommand.ExecuteAsync(null);
        Assert.Empty(model.Roots);
        Assert.Contains("读取失败", model.Status, StringComparison.Ordinal);
        Assert.Contains("SCFS0001", model.Issues, StringComparison.Ordinal);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrReplacedSelectionDiscardsLateTree(bool replaceSelection)
    {
        using var fixture = new Fixture();
        var initial = await fixture.Create();
        var saved = await fixture.Workspace.SaveBindingsAsync(new(initial,
            [new(initial.Configuration.Plan.Sources[0].Id, fixture.Directory("source"))], fixture.Directory("current"), null), TestContext.Current.CancellationToken);
        var result = await fixture.Workspace.ReadSourceTreeAsync(saved.Configuration.Plan.Id, saved.Configuration.Plan.Sources[0].Id, TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SourceTreeObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workspace = new UncertainWorkspace(fixture.Workspace) { TreeRead = (_, _, _) => { entered.SetResult(); return release.Task; } };
        var model = new SourceTreeViewModel(workspace);
        model.SetSnapshot(saved);
        var read = model.ReadCommand.ExecuteAsync(null);
        await entered.Task;
        Assert.False(model.CanSelect); Assert.False(model.ReadCommand.CanExecute(null));
        if (replaceSelection) model.SetSnapshot(null); else model.CancelCommand.Execute(null);
        release.SetResult(result);
        await read;
        Assert.Empty(model.Roots); Assert.Empty(model.RootPath); Assert.False(model.IsBusy);
        if (!replaceSelection) Assert.Contains("已取消", model.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangedSavedBindingDiscardsObservation()
    {
        using var fixture = new Fixture();
        var initial = await fixture.Create();
        var id = initial.Configuration.Plan.Sources[0].Id;
        var saved = await fixture.Workspace.SaveBindingsAsync(new(initial,
            [new(id, fixture.Directory("source"))], fixture.Directory("current"), null), TestContext.Current.CancellationToken);
        var repository = await fixture.Repository();
        var identity = await repository.LoadAsync(TestContext.Current.CancellationToken);
        var authority = new AuthoritativePlanWorkflow(repository, new BackupPlanDocumentSource());
        var editor = new DirectoryBindingEditorWorkflow(authority, repository,
            new(identity!, repository, new StowCrate.Infrastructure.Filesystem.LocalPhysicalPathResolver()),
            new StowCrate.Infrastructure.Filesystem.ExistingBindingDirectoryProbe());
        var workflow = new SourceTreeWorkflow(editor, new CallbackTreeReader(async token =>
        {
            var result = await fixture.Workspace.ReadSourceTreeAsync(initial.Configuration.Plan.Id, id, token);
            await fixture.Workspace.SaveBindingsAsync(new(saved, [new(id, fixture.Directory("replacement"))], fixture.Directory("current"), null), token);
            return result.Scan;
        }));
        await Assert.ThrowsAsync<LocalStateConcurrencyException>(() => workflow.ReadAsync(initial.Configuration.Plan.Id, id, TestContext.Current.CancellationToken));
    }

    private sealed class CallbackTreeReader(Func<CancellationToken, Task<StowCrate.Core.Filesystem.SourceScanResult>> read) : ISourceTreeReader
    {
        public Task<StowCrate.Core.Filesystem.SourceScanResult> ReadAsync(SourceId sourceId, string savedRoot, CancellationToken token) => read(token);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("./")]
    public async Task EmptyOutputDirectoryIsRejectedBeforePersistingPlan(string output)
    {
        using var fixture = new Fixture();
        await fixture.Workspace.OpenDefaultAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Workspace.CreatePlanAsync(new("资料", "项目", output), TestContext.Current.CancellationToken));
        Assert.Empty((await fixture.Workspace.OpenDefaultAsync(TestContext.Current.CancellationToken)).Plans);
    }

    [AvaloniaFact]
    public async Task NewPlanCanBindAndReopenRealDirectories()
    {
        using var fixture = new Fixture();
        var model = new MainViewModel(fixture.Workspace);
        Assert.False(model.Bindings.SaveCommand.CanExecute(null));
        await model.StartCommand.ExecuteAsync(null);
        model.PlanName = "资料"; model.SourceName = "项目"; model.SourceOutputPath = "projects";
        await model.CreatePlanCommand.ExecuteAsync(null);
        Assert.True(model.SelectedPlan is not null, model.Status + " " + model.Details);
        var id = model.SelectedPlan!.Id;
        var row = Assert.Single(model.Bindings.Sources);
        Assert.Equal("项目", row.Name); Assert.Empty(row.Path);
        Assert.False(model.Bindings.HistoryRequired);
        row.Path = fixture.Directory("source");
        model.Bindings.CurrentRoot = fixture.Directory("current");
        await model.Bindings.SaveCommand.ExecuteAsync(null);
        Assert.Contains("已保存并重新读取", model.Bindings.Status, StringComparison.Ordinal);
        var reopened = new RelocationWorkspace(fixture.Root.FullName);
        await reopened.OpenDefaultAsync(TestContext.Current.CancellationToken);
        var saved = await reopened.LoadBindingsAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(row.Id, Assert.Single(saved.Bindings!.Sources).SourceId);
        Assert.Equal(model.Bindings.CurrentRoot, saved.Bindings.CurrentRoot!.CanonicalPath);
        Assert.Equal(saved.Bindings.CurrentRoot.CanonicalPath, model.CurrentRootDisplay);
        model.DatabasePath = "other.db";
        await model.Bindings.PendingLoad;
        Assert.Empty(model.Bindings.Sources);
        Assert.False(model.Bindings.CanSave);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("overlap")]
    [InlineData("empty")]
    [InlineData("relative")]
    public async Task InvalidTargetsDoNotReplaceExistingBindings(string kind)
    {
        using var fixture = new Fixture();
        var snapshot = await fixture.Create();
        var edit = new DirectoryBindingEdit(snapshot, [new(snapshot.Configuration.Plan.Sources[0].Id, fixture.Directory("source"))], fixture.Directory("current"), null);
        var saved = await fixture.Workspace.SaveBindingsAsync(edit, TestContext.Current.CancellationToken);
        var bad = kind switch { "missing" => Path.Combine(fixture.Root.FullName, "missing"), "overlap" => edit.Sources[0].Path, "empty" => "", _ => "relative" };
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Workspace.SaveBindingsAsync(edit with { Original = saved, CurrentRoot = bad }, TestContext.Current.CancellationToken));
        var actual = await fixture.Workspace.LoadBindingsAsync(snapshot.Configuration.Plan.Id, TestContext.Current.CancellationToken);
        Assert.Equal(saved.Bindings!.CurrentRoot, actual.Bindings!.CurrentRoot);
        Assert.False(System.IO.Directory.Exists(Path.Combine(fixture.Root.FullName, "missing")));
    }

    [Fact]
    public async Task MultiSourceHistoryAndHiddenExternalBindingsArePreserved()
    {
        using var fixture = new Fixture();
        var initial = await fixture.Create();
        var p = initial.Configuration.Plan;
        var second = new PortableBackupSource(new(Guid.NewGuid()), "第二源", new LogicalPath("second"));
        var unit = new UiManagedArchiveUnit(new(Guid.NewGuid()), p.Sources[0].Id, LogicalPath.Root, new RuleSet(), null, null);
        var external = new PortableExternalSource(new(Guid.NewGuid()), "附加目录", PortableExternalSourceKind.Directory, unit.Id, new("extra"));
        var plan = new PortableBackupPlan(p.Id, p.Name, p.Description, p.Semantics, [.. p.Sources, second], p.GlobalRules, p.PlanRules,
            p.ArchiveSpecDefault, [unit], [], p.LinkPolicy, p.ChangeDetection, new HistoryEnabled(new KeepAllRetention()), p.Schedule, [external]);
        var repo = await fixture.Repository();
        var authority = new AuthoritativePlanWorkflow(repo, new BackupPlanDocumentSource());
        await authority.UpdateManagedAsync(plan, initial.Configuration.ManagedRevision!.Value, TestContext.Current.CancellationToken);
        var loaded = await fixture.Workspace.LoadBindingsAsync(p.Id, TestContext.Current.CancellationToken);
        Assert.True(loaded.HistoryRequired);
        var edit = new DirectoryBindingEdit(loaded, [new(p.Sources[0].Id, fixture.Directory("source")), new(second.Id, fixture.Directory("second"))], fixture.Directory("current"), null);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Workspace.SaveBindingsAsync(edit, TestContext.Current.CancellationToken));
        var saved = await fixture.Workspace.SaveBindingsAsync(edit with { HistoryRoot = fixture.Directory("history") }, TestContext.Current.CancellationToken);
        Assert.Equal(2, saved.Bindings!.Sources.Length);
        var resolver = new StowCrate.Infrastructure.Filesystem.LocalPhysicalPathResolver();
        var extraPath = await resolver.ResolveAsync(fixture.Directory("external"), TestContext.Current.CancellationToken);
        await repo.SaveValidatedAggregateAsync(saved.Bindings with { ExternalSources = [new(external.Id, extraPath.CanonicalPath, extraPath.ComparisonKey, true)] }, TestContext.Current.CancellationToken);
        var disabled = new PortableBackupPlan(plan.Id, plan.Name, plan.Description, plan.Semantics, plan.Sources, plan.GlobalRules, plan.PlanRules,
            plan.ArchiveSpecDefault, [unit], [], plan.LinkPolicy, plan.ChangeDetection, new HistoryDisabled(), plan.Schedule, [external]);
        await authority.UpdateManagedAsync(disabled, (await authority.LoadAsync(p.Id, TestContext.Current.CancellationToken)).ManagedRevision!.Value, TestContext.Current.CancellationToken);
        var before = await fixture.Workspace.LoadBindingsAsync(p.Id, TestContext.Current.CancellationToken);
        Assert.False(before.HistoryRequired);
        var after = await fixture.Workspace.SaveBindingsAsync(edit with { Original = before, HistoryRoot = "" }, TestContext.Current.CancellationToken);
        Assert.Equal(before.Bindings!.HistoryRoot, after.Bindings!.HistoryRoot);
        Assert.Equal(Assert.Single(before.Bindings.ExternalSources), Assert.Single(after.Bindings.ExternalSources));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainWriteReadsActualStateWithoutReplay(bool readFails)
    {
        using var fixture = new Fixture();
        var initial = await fixture.Create();
        var wrapper = new UncertainWorkspace(fixture.Workspace) { FailReadAfterWrite = readFails };
        var model = new DirectoryBindingsViewModel(wrapper);
        model.SelectPlan(initial.Configuration.Plan.Id); await model.PendingLoad;
        model.Sources[0].Path = fixture.Directory("source"); model.CurrentRoot = fixture.Directory("current");
        await model.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, wrapper.Writes);
        Assert.Equal(!readFails, model.CanSave);
        Assert.Contains(readFails ? "当前禁止保存" : "已读回实际持久状态", model.Status, StringComparison.Ordinal);
        var actual = await fixture.Workspace.LoadBindingsAsync(initial.Configuration.Plan.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(actual.Bindings!.CurrentRoot);
    }

    private sealed class Fixture : IDisposable
    {
        public DirectoryInfo Root { get; } = System.IO.Directory.CreateTempSubdirectory("StowCrate-binding-ui-");
        public RelocationWorkspace Workspace { get; }
        public Fixture() => Workspace = new(Root.FullName);
        public string Directory(string name) => System.IO.Directory.CreateDirectory(Path.Combine(Root.FullName, name)).FullName;
        public async Task<DirectoryBindingSnapshot> Create()
        {
            await Workspace.OpenDefaultAsync(TestContext.Current.CancellationToken);
            var plan = await Workspace.CreatePlanAsync(new("资料", "源", "source"), TestContext.Current.CancellationToken);
            return await Workspace.LoadBindingsAsync(plan.Id, TestContext.Current.CancellationToken);
        }
        public Task<ConfigDbRepository> Repository() => ConfigDbOpenCoordinator.OpenAsync(Path.Combine(Root.FullName, "StowCrate", "config.db"), null, null, TestContext.Current.CancellationToken);
        public void Dispose() => Root.Delete(true);
    }

    private sealed class UncertainWorkspace(IRelocationWorkspace inner) : IRelocationWorkspace
    {
        public Func<PlanId, SourceId, CancellationToken, Task<SourceTreeObservation>>? TreeRead { get; init; }
        public Task<SourceTreeObservation> ReadSourceTreeAsync(PlanId planId, SourceId sourceId, CancellationToken token)
            => TreeRead is null ? inner.ReadSourceTreeAsync(planId, sourceId, token) : TreeRead(planId, sourceId, token);
        public int Writes { get; private set; }
        public bool FailReadAfterWrite { get; init; }
        public Task<DirectoryBindingSnapshot> LoadBindingsAsync(PlanId id, CancellationToken token)
            => Writes > 0 && FailReadAfterWrite ? Task.FromException<DirectoryBindingSnapshot>(new IOException("读回不可用")) : inner.LoadBindingsAsync(id, token);
        public async Task<DirectoryBindingSnapshot> SaveBindingsAsync(DirectoryBindingEdit edit, CancellationToken token)
        { Writes++; await inner.SaveBindingsAsync(edit, token); throw new OperationCanceledException("提交后取消"); }
        public Task<RelocationPlanChoice> CreatePlanAsync(NewManagedPlanRequest request, CancellationToken token) => inner.CreatePlanAsync(request, token);
        public Task<DefaultWorkspaceResult> OpenDefaultAsync(CancellationToken token) => inner.OpenDefaultAsync(token);
        public Task<ImmutableArray<RelocationPlanChoice>> OpenAsync(string path, CancellationToken token) => inner.OpenAsync(path, token);
        public Task<StorageRelocationTargetInspection> InspectAsync(PlanId id, string? current, string? history, CancellationToken token) => inner.InspectAsync(id, current, history, token);
        public Task<StorageRelocationJournal?> LoadJournalAsync(PlanId id, CancellationToken token) => inner.LoadJournalAsync(id, token);
        public Task<StorageRelocationRecoveryResult> ResumeAsync(PlanId id, Guid transaction, CancellationToken token) => inner.ResumeAsync(id, transaction, token);
    }
}
