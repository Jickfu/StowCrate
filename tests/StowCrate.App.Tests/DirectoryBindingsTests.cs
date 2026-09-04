using System.Collections.Immutable;
using Avalonia.Headless.XUnit;
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
