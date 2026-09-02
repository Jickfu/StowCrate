using System.Security.Cryptography;
using StowCrate.Application.LocalState;
using StowCrate.Application.Publishing;
using StowCrate.Core.ChangeDetection;

namespace StowCrate.Infrastructure.Filesystem;

public sealed class ArchivePhysicalPublisher(IArchivePublishMetadataDurabilityBarrier? durabilityBarrier = null) : IArchivePhysicalPublisher, IHistoryArtifactDeletionStore
{
    private readonly IArchivePublishMetadataDurabilityBarrier durability = durabilityBarrier ?? new PlatformArchivePublishMetadataDurabilityBarrier();
    public async Task<PhysicalArchiveObservation?> ObserveAsync(OutputRootLocalBinding root, RelativeStoragePath path, CancellationToken cancellationToken)
    {
        var physical = Resolve(root, path);
        if (!File.Exists(physical)) return null;
        var (hash, length) = await HashAsync(physical, cancellationToken).ConfigureAwait(false);
        return new(path, hash, length);
    }

    public async Task<CurrentPublishStagingProof> StageCurrentAsync(ArchivePublishRequest request, CancellationToken cancellationToken)
    {
        var version = request.Artifact.ArchiveVersion;
        var final = Resolve(request.CurrentRoot, request.CurrentRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        var tempRelative = CurrentPublishTempLayoutV1.Create(request.CurrentRelativePath, version.Id);
        var temp = Resolve(request.CurrentRoot, tempRelative);
        var observation = await CopyVerifiedAsync(request.Artifact.PartialArtifactPath, temp, version.Integrity!.Value,
            version.Length!.Value, cancellationToken).ConfigureAwait(false);
        return new(version.PlanId, version.ArchiveUnitId, version.Id, tempRelative,
            version.Integrity.Value, observation.Sha256, observation.Length);
    }

    public async Task<HistoryCapturePhysicalProof> CaptureHistoryAsync(OldCurrentFacts oldCurrent, OutputRootLocalBinding currentRoot,
        OutputRootLocalBinding historyRoot, RelativeStoragePath historyPath, CancellationToken cancellationToken)
    {
        var source = Resolve(currentRoot, oldCurrent.Placement.RelativePath);
        var final = Resolve(historyRoot, historyPath);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        if (File.Exists(final)) throw new IOException("History target already exists.");
        var temp = Path.Combine(Path.GetDirectoryName(final)!, $".{Path.GetFileName(final)}.stowcrate-history-{oldCurrent.ArchiveVersion.Id.Value:N}.partial");
        var observation = await CopyVerifiedAsync(source, temp, oldCurrent.ArchiveVersion.Integrity!.Value,
            oldCurrent.ArchiveVersion.Length!.Value, cancellationToken).ConfigureAwait(false);
        File.Move(temp, final, overwrite: false);
        _ = await durability.FlushDirectoryMetadataAsync(Path.GetDirectoryName(final)!, cancellationToken).ConfigureAwait(false);
        observation = await ObserveRequiredAsync(historyRoot, historyPath, cancellationToken).ConfigureAwait(false);
        Ensure(observation, oldCurrent.ArchiveVersion.Integrity.Value, oldCurrent.ArchiveVersion.Length.Value);
        return new(oldCurrent.ArchiveVersion.PlanId, oldCurrent.ArchiveVersion.ArchiveUnitId, oldCurrent.ArchiveVersion.Id,
            historyPath, oldCurrent.ArchiveVersion.Integrity.Value, observation.Sha256, observation.Length);
    }

    public async Task<CurrentPublishReceipt> PublishCurrentAsync(ArchivePublishRequest request, CurrentPublishStagingProof staging,
        OldCurrentFacts? oldCurrent, CancellationToken cancellationToken)
    {
        var version = request.Artifact.ArchiveVersion;
        if (staging.PlanId != version.PlanId || staging.ArchiveUnitId != version.ArchiveUnitId || staging.ArchiveVersionId != version.Id)
            throw new InvalidOperationException("Current staging proof identity mismatch.");
        var temp = Resolve(request.CurrentRoot, staging.RelativeStoragePath);
        var before = await HashAsync(temp, cancellationToken).ConfigureAwait(false);
        Ensure(new(staging.RelativeStoragePath, before.Hash, before.Length), version.Integrity!.Value, version.Length!.Value);
        var final = Resolve(request.CurrentRoot, request.CurrentRelativePath);
        var samePath = oldCurrent?.Placement.RelativePath == request.CurrentRelativePath;
        if (!samePath && File.Exists(final)) throw new IOException("Unexpected Current target exists.");
        // File.Move 提供同文件系统 namespace 原子操作；断电后的目录项 durability 由独立 barrier 证明，不能由 rename 本身推断。
        File.Move(temp, final, overwrite: samePath);
        var metadataDurability = await durability.FlushDirectoryMetadataAsync(Path.GetDirectoryName(final)!, cancellationToken).ConfigureAwait(false);
        var observed = await ObserveRequiredAsync(request.CurrentRoot, request.CurrentRelativePath, cancellationToken).ConfigureAwait(false);
        Ensure(observed, version.Integrity.Value, version.Length.Value);
        return new(version.PlanId, version.ArchiveUnitId, version.Id, request.CurrentRelativePath,
            version.Integrity.Value, observed.Sha256, observed.Length, DateTimeOffset.UtcNow, metadataDurability);
    }

    public async Task<bool> DeleteIfMatchesAsync(OutputRootLocalBinding root, RelativeStoragePath path, Sha256Digest expected, long length, CancellationToken cancellationToken)
    {
        var observed = await ObserveAsync(root, path, cancellationToken).ConfigureAwait(false);
        if (observed is null) return true;
        if (observed.Sha256 != expected || observed.Length != length) return false;
        try { File.Delete(Resolve(root, path)); return true; } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
    }

    public Task CleanupRuntimeArtifactAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task<HistoryDeletionPhysicalResult> DeleteDurablyIfMatchesAsync(OutputRootLocalBinding historyRoot,
        RetentionDeletionIntent intent, CancellationToken cancellationToken)
    {
        var path = Resolve(historyRoot, intent.HistoryRelativePath); var parent = Path.GetDirectoryName(path)!;
        if (!PathEntryExists(path))
        {
            try { _ = await durability.FlushDirectoryMetadataAsync(parent, cancellationToken).ConfigureAwait(false); return new(HistoryDeletionPhysicalStatus.AlreadyAbsentDurably); }
            catch (Exception ex) { return new(HistoryDeletionPhysicalStatus.Failed, ex.Message); }
        }
        FileInfo info;
        try { info = new(path); info.Refresh(); if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0 || info.LinkTarget is not null) return new(HistoryDeletionPhysicalStatus.UnsupportedObject, "Expected History path is a link, directory, or special object."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(HistoryDeletionPhysicalStatus.Failed, ex.Message); }
        var first = await HashAsync(path, cancellationToken).ConfigureAwait(false);
        if (first.Hash != intent.ExpectedIntegrity || first.Length != intent.ExpectedLength) return new(HistoryDeletionPhysicalStatus.Mismatch, "History artifact integrity mismatch.");
        info.Refresh();
        if (!info.Exists || info.Length != first.Length || (info.Attributes & FileAttributes.ReparsePoint) != 0) return new(HistoryDeletionPhysicalStatus.Mismatch, "History artifact changed during destructive verification.");
        var second = await HashAsync(path, cancellationToken).ConfigureAwait(false);
        if (second != first) return new(HistoryDeletionPhysicalStatus.Mismatch, "History artifact changed during destructive verification.");
        try { File.Delete(path); _ = await durability.FlushDirectoryMetadataAsync(parent, cancellationToken).ConfigureAwait(false); return new(HistoryDeletionPhysicalStatus.DeletedDurably); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(HistoryDeletionPhysicalStatus.Failed, ex.Message); }
    }

    public async Task<bool> ConfirmAbsentDurablyAsync(OutputRootLocalBinding historyRoot, RetentionDeletionIntent intent, CancellationToken cancellationToken)
    {
        var path = Resolve(historyRoot, intent.HistoryRelativePath); if (PathEntryExists(path)) return false;
        var proof = await durability.FlushDirectoryMetadataAsync(Path.GetDirectoryName(path)!, cancellationToken).ConfigureAwait(false); return proof.BarrierCompleted;
    }

    private static bool PathEntryExists(string path)
    {
        try
        {
            // File.Exists 会把断链符号链接报告为不存在；保留目录项本身，避免把未知对象当作已完成删除。
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static async Task<PhysicalArchiveObservation> CopyVerifiedAsync(string source, string destination, Sha256Digest expected, long length, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 128]; long copied = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false); hash.AppendData(buffer, 0, read); copied += read;
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false); output.Flush(flushToDisk: true);
        var observation = new PhysicalArchiveObservation(new(Path.GetFileName(destination)), new(Convert.ToHexStringLower(hash.GetHashAndReset())), copied);
        Ensure(observation, expected, length); return observation;
    }

    private static async Task<(Sha256Digest Hash, long Length)> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        return (new(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))), length);
    }

    private async Task<PhysicalArchiveObservation> ObserveRequiredAsync(OutputRootLocalBinding root, RelativeStoragePath path, CancellationToken cancellationToken) =>
        await ObserveAsync(root, path, cancellationToken).ConfigureAwait(false) ?? throw new IOException("Published artifact is missing.");

    private static void Ensure(PhysicalArchiveObservation value, Sha256Digest expected, long length)
    { if (value.Sha256 != expected || value.Length != length) throw new InvalidDataException("Published artifact integrity mismatch."); }

    private static string Resolve(OutputRootLocalBinding root, RelativeStoragePath path)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.CanonicalPath));
        var result = Path.GetFullPath(Path.Combine(canonicalRoot, path.Value.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(canonicalRoot, result);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Storage path escapes its configured root.");
        return result;
    }

}
