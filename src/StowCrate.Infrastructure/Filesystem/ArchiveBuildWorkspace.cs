using StowCrate.Application.Archiving;

namespace StowCrate.Infrastructure.Filesystem;

/// <summary>workspace root 必须由组合根放在 Source/External/Current/History 之外的私有 runtime 区域。</summary>
public sealed class ArchiveBuildWorkspaceFactory(string privateRuntimeRoot) : IArchiveBuildWorkspaceFactory
{
    public Task<IArchiveBuildWorkspace> CreateAsync(ArchiveBuildRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(privateRuntimeRoot);
        var root = Path.Combine(privateRuntimeRoot, $"build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return Task.FromResult<IArchiveBuildWorkspace>(new ArchiveBuildWorkspace(root));
    }
}

internal sealed class ArchiveBuildWorkspace : IArchiveBuildWorkspace
{
    private readonly string root;
    public ArchiveBuildWorkspace(string root)
    {
        this.root = Path.GetFullPath(root);
        StagingRoot = Path.Combine(this.root, "staging");
        Directory.CreateDirectory(StagingRoot);
        PartialArtifactPath = Path.Combine(this.root, $"artifact-{Guid.NewGuid():N}.partial");
    }
    public string StagingRoot { get; }
    public string PartialArtifactPath { get; }
    public Task CleanupAsync(bool preservePartialArtifact, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(StagingRoot)) Directory.Delete(StagingRoot, true);
        if (!preservePartialArtifact && File.Exists(PartialArtifactPath)) File.Delete(PartialArtifactPath);
        if (!preservePartialArtifact && Directory.Exists(root)) Directory.Delete(root, false);
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
