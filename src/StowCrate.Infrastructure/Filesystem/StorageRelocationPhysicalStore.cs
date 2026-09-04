using System.Globalization;
using System.Security.Cryptography;
using StowCrate.Application.Publishing;
using StowCrate.Application.StorageMaintenance;
using StowCrate.Core.ChangeDetection;
using StowCrate.Core.Filesystem;

namespace StowCrate.Infrastructure.Filesystem;

/// <summary>物理复制/发布及提交后的 exact old-copy cleanup；不切换 binding，也不清除未知临时文件或目录。</summary>
public sealed partial class StorageRelocationPhysicalStore(IArchivePublishMetadataDurabilityBarrier? durabilityBarrier = null,
    IStorageRelocationCapacityProbe? capacityProbe = null,
    IStorageRelocationTargetComparisonProbe? comparisonProbe = null) : IStorageRelocationPhysicalStore, IStorageRelocationOldCopyStore, IStorageRelocationCompletionProbe
{
    private readonly IArchivePublishMetadataDurabilityBarrier durability = durabilityBarrier ?? new PlatformArchivePublishMetadataDurabilityBarrier();
    private readonly StorageRelocationCapacityGuard capacity = new(capacityProbe ?? new StorageRelocationCapacityProbe());
    private readonly IStorageRelocationTargetComparisonProbe comparison = comparisonProbe ?? new StorageRelocationTargetComparisonProbe();

    public static StorageObjectIdentity InspectIdentity(string path, bool directory)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
            || ((attributes & FileAttributes.Directory) != 0) != directory
            || NativeFileType.GetEntryKind(path, directory) != (directory ? FileSystemEntryKind.Directory : FileSystemEntryKind.File))
            throw new IOException("Relocation requires an ordinary no-follow filesystem object.");
        var identity = NativeFileType.GetIdentityNoFollow(path);
        var provider = OperatingSystem.IsWindows() ? "windows-volume-file-id" : OperatingSystem.IsMacOS() ? "macos-device-inode" : OperatingSystem.IsLinux() ? "linux-device-inode" : throw new PlatformNotSupportedException();
        return new(provider, 1, string.Create(CultureInfo.InvariantCulture, $"{identity.DeviceOrVolume:x16}:{identity.FileId:x16}"));
    }

    public async Task<StorageTransferProof> StageAsync(StorageRelocationJournal journal, ArchiveVersionId versionId, CancellationToken cancellationToken)
    {
        var (entry, root, progress) = ValidateJournal(journal, versionId);
        if (progress.Stage is not StorageTransferArtifactStage.Pending) throw new InvalidOperationException("Artifact is not pending staging.");
        await comparison.VerifyLayoutAsync(journal.Manifest, cancellationToken).ConfigureAwait(false);
        // 先检查全部尚未复制条目的 target/temp，避免已知的后续冲突留下本条目的无用副本。
        var pending = journal.Progress.Artifacts.Where(x => x.Stage == StorageTransferArtifactStage.Pending)
            .Select(x => x.Artifact.VersionId).ToHashSet();
        var pendingEntries = journal.Manifest.Entries.Where(x => pending.Contains(x.Artifact.VersionId))
            .Select(x => new StorageRelocationPlacement(x.UnitId, x.RootKind, x.Artifact, x.RelativePath)).ToArray();
        await CheckDestinationCapacityAsync(journal.Manifest.Roots.Where(x => pendingEntries.Any(e => e.RootKind == x.Kind)).ToArray(),
            pendingEntries, cancellationToken).ConfigureAwait(false);
        VerifyUnoccupiedTargets(journal.Manifest.Roots, journal.Manifest.Entries.Where(x => pending.Contains(x.Artifact.VersionId)), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var source = Namespace(root.OldRoot.CanonicalPath, root.OldIdentity, entry.RelativePath);
        RequireIdentity(source, false, entry.OldIdentity);
        await EnsureParentsAsync(root, entry.RelativePath, cancellationToken).ConfigureAwait(false);
        Namespace(root.OldRoot.CanonicalPath, root.OldIdentity, entry.RelativePath);
        RequireIdentity(source, false, entry.OldIdentity);
        var target = Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.RelativePath);
        var temp = Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.TempRelativePath);
        if (Exists(target) || Exists(temp)) throw new IOException("Relocation target or unowned temporary entry already exists.");

        // 先排除目标父目录已知不支持持久化的情况，避免复制完成后才留下无 ownership 的 temp。
        // 该探测不替代复制/rename 后的真实 barrier，也不声明目录写入权限已经被预留。
        await BarrierAsync(Path.GetDirectoryName(temp)!, cancellationToken).ConfigureAwait(false);
        // 新建父目录及 barrier I/O 后重新读取实际比较规则；不复用 Preview 或创建前推导。
        await comparison.VerifyLayoutAsync(journal.Manifest, cancellationToken).ConfigureAwait(false);
        Namespace(root.OldRoot.CanonicalPath, root.OldIdentity, entry.RelativePath);
        RequireIdentity(source, false, entry.OldIdentity);
        Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.RelativePath);
        if (Exists(target) || Exists(temp)) throw new IOException("Relocation destination changed during durability check.");
        cancellationToken.ThrowIfCancellationRequested();

        StorageObjectIdentity staged;
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            // 创建后立即捕获对象 identity；不能关闭后才把同路径、同 hash 的替换对象认领为自己的 temp。
            staged = InspectIdentity(temp, false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[131072]; long length = 0;
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                length = checked(length + count);
                if (length > entry.Artifact.Length) throw new IOException("Relocation source length changed.");
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            if (length != entry.Artifact.Length || new Sha256Digest(Convert.ToHexStringLower(hash.GetHashAndReset())) != entry.Artifact.Integrity)
                throw new IOException("Relocation source integrity changed.");
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        Namespace(root.OldRoot.CanonicalPath, root.OldIdentity, entry.RelativePath);
        await VerifyAsync(source, entry.OldIdentity, entry.Artifact, cancellationToken).ConfigureAwait(false);
        Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.TempRelativePath);
        await VerifyAsync(temp, staged, entry.Artifact, cancellationToken).ConfigureAwait(false);
        await BarrierAsync(Path.GetDirectoryName(temp)!, cancellationToken).ConfigureAwait(false);
        Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.TempRelativePath);
        RequireIdentity(temp, false, staged);
        if (Exists(target)) throw new IOException("Relocation target appeared during staging.");
        return Proof(journal, entry, staged);
    }

    public async Task<StorageTransferProof> PublishTargetAsync(StorageRelocationJournal journal, ArchiveVersionId versionId, CancellationToken cancellationToken)
    {
        var (entry, root, progress) = ValidateJournal(journal, versionId);
        if (progress.Stage is not StorageTransferArtifactStage.Staged || progress.StagedIdentity is null)
            throw new InvalidOperationException("Durably recorded staged identity is required before publish.");
        await comparison.VerifyLayoutAsync(journal.Manifest, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var source = Namespace(root.OldRoot.CanonicalPath, root.OldIdentity, entry.RelativePath);
        await VerifyAsync(source, entry.OldIdentity, entry.Artifact, cancellationToken).ConfigureAwait(false);
        Namespace(root.OldRoot.CanonicalPath, root.OldIdentity, entry.RelativePath);
        var target = Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.RelativePath);
        var temp = Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.TempRelativePath);
        if (Exists(target))
        {
            // rename 后 journal 尚未来得及推进：必须是已记录的同一对象；hash 相同的外来目标不予采用。
            if (Exists(temp)) throw new IOException("Both relocation temp and target exist; recovery is ambiguous.");
            await VerifyAsync(target, progress.StagedIdentity, entry.Artifact, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await VerifyAsync(temp, progress.StagedIdentity, entry.Artifact, cancellationToken).ConfigureAwait(false);
            await comparison.VerifyLayoutAsync(journal.Manifest, cancellationToken).ConfigureAwait(false);
            Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.TempRelativePath);
            RequireIdentity(temp, false, progress.StagedIdentity);
            Namespace(root.OldRoot.CanonicalPath, root.OldIdentity, entry.RelativePath);
            RequireIdentity(source, false, entry.OldIdentity);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, target, overwrite: false);
        }
        // namespace mutation 后必须收敛为可恢复的 durable proof，不再接受本次 caller cancellation。
        await BarrierAsync(Path.GetDirectoryName(target)!, CancellationToken.None).ConfigureAwait(false);
        await comparison.VerifyLayoutAsync(journal.Manifest, CancellationToken.None).ConfigureAwait(false);
        Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.RelativePath);
        await VerifyAsync(target, progress.StagedIdentity, entry.Artifact, CancellationToken.None).ConfigureAwait(false);
        Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.RelativePath);
        Namespace(root.OldRoot.CanonicalPath, root.OldIdentity, entry.RelativePath);
        RequireIdentity(source, false, entry.OldIdentity);
        return Proof(journal, entry, progress.StagedIdentity);
    }

    public async Task VerifyForCommitAsync(StorageRelocationJournal journal, CancellationToken cancellationToken)
    {
        ValidateJournalSet(journal, StorageTransferStage.TargetsDurable);
        cancellationToken.ThrowIfCancellationRequested();
        VerifyRoots(journal);
        await comparison.VerifyLayoutAsync(journal.Manifest, cancellationToken).ConfigureAwait(false);
        foreach (var root in journal.Manifest.Roots)
            await BarrierAsync(root.NewRoot.CanonicalPath, cancellationToken).ConfigureAwait(false);
        foreach (var entry in journal.Manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = journal.Manifest.Roots.Single(x => x.Kind == entry.RootKind);
            var identity = journal.Progress.Artifacts.Single(x => x.Artifact.VersionId == entry.Artifact.VersionId).StagedIdentity!;
            var source = Namespace(root.OldRoot.CanonicalPath, root.OldIdentity, entry.RelativePath);
            var target = Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.RelativePath);
            var temp = Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.TempRelativePath);
            if (Exists(temp)) throw new IOException("Relocation temporary entry reappeared before commit.");
            await VerifyAsync(source, entry.OldIdentity, entry.Artifact, cancellationToken).ConfigureAwait(false);
            await VerifyAsync(target, identity, entry.Artifact, cancellationToken).ConfigureAwait(false);
            // 重验阶段不创建缺失目录；由内向外刷新已有 namespace，覆盖此前重启后的目录持久性。
            var parents = new List<string> { root.NewRoot.CanonicalPath };
            foreach (var segment in entry.RelativePath.Value.Split('/').SkipLast(1))
                parents.Add(Path.Combine(parents[^1], segment));
            for (var i = parents.Count - 1; i >= 0; i--)
                await BarrierAsync(parents[i], cancellationToken).ConfigureAwait(false);
            await VerifyAsync(target, identity, entry.Artifact, cancellationToken).ConfigureAwait(false);
        }
        // 后续条目的 I/O 期间，先前条目也可能发生正常替换；返回前再检查整个 namespace 和 native identity。
        await comparison.VerifyLayoutAsync(journal.Manifest, cancellationToken).ConfigureAwait(false);
        VerifyRoots(journal);
        foreach (var entry in journal.Manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = journal.Manifest.Roots.Single(x => x.Kind == entry.RootKind);
            RequireIdentity(Namespace(root.OldRoot.CanonicalPath, root.OldIdentity, entry.RelativePath), false, entry.OldIdentity);
            RequireIdentity(Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.RelativePath), false,
                journal.Progress.Artifacts.Single(x => x.Artifact.VersionId == entry.Artifact.VersionId).StagedIdentity!);
            if (Exists(Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.TempRelativePath)))
                throw new IOException("Relocation temporary entry reappeared before commit.");
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<StorageRelocationOldCopyAbsenceProof> RemoveOldCopyAsync(StorageRelocationJournal journal,
        ArchiveVersionId versionId, CancellationToken cancellationToken)
    {
        ValidateJournalSet(journal, StorageTransferStage.MetadataCommitted);
        cancellationToken.ThrowIfCancellationRequested();
        var entry = journal.Manifest.Entries.Single(x => x.Artifact.VersionId == versionId);
        var progress = journal.Progress.Artifacts.Single(x => x.Artifact.VersionId == versionId);
        var root = journal.Manifest.Roots.Single(x => x.Kind == entry.RootKind);
        var targetIdentity = progress.StagedIdentity ?? throw new InvalidOperationException("Committed target identity is missing.");
        var old = Namespace(root.OldRoot.CanonicalPath, root.OldIdentity, entry.RelativePath);
        var target = Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.RelativePath);
        await VerifyAsync(target, targetIdentity, entry.Artifact, cancellationToken).ConfigureAwait(false);
        var existed = Exists(old);
        if (existed)
        {
            // 已记录清理完成后再次出现的对象没有新的删除授权，不能凭旧 journal 重复认领。
            if (progress.Stage == StorageTransferArtifactStage.OldCopyAbsent)
                throw new IOException("Old relocation entry reappeared after recorded cleanup.");
            await VerifyAsync(old, entry.OldIdentity, entry.Artifact, cancellationToken).ConfigureAwait(false);
        }
        var parent = Path.GetDirectoryName(old)!;
        // 先验证该目录支持 barrier；能力不可用时尚未删除任何旧文件。
        await BarrierAsync(parent, cancellationToken).ConfigureAwait(false);
        Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.RelativePath);
        await VerifyAsync(target, targetIdentity, entry.Artifact, cancellationToken).ConfigureAwait(false);
        Namespace(root.OldRoot.CanonicalPath, root.OldIdentity, entry.RelativePath);
        if (existed)
        {
            await VerifyAsync(old, entry.OldIdentity, entry.Artifact, cancellationToken).ConfigureAwait(false);
            Namespace(root.OldRoot.CanonicalPath, root.OldIdentity, entry.RelativePath);
            RequireIdentity(old, false, entry.OldIdentity);
            Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.RelativePath);
            RequireIdentity(target, false, targetIdentity);
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(old);
        }
        else if (Exists(old)) throw new IOException("Old relocation entry appeared during absence reconciliation.");

        // 删除后不响应 caller cancellation，直到 directory barrier 和 absence re-proof 收敛；失败仍保留 journal。
        await BarrierAsync(parent, CancellationToken.None).ConfigureAwait(false);
        Namespace(root.OldRoot.CanonicalPath, root.OldIdentity, entry.RelativePath);
        if (Exists(old)) throw new IOException("Old relocation entry is not absent after cleanup.");
        Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.RelativePath);
        RequireIdentity(target, false, targetIdentity);
        return new(journal.Manifest.TransactionId, journal.Manifest.PlanId, journal.Revision, entry.Artifact,
            root.OldIdentity, entry.OldIdentity, targetIdentity);
    }

    public async Task VerifyCompletedAsync(StorageRelocationJournal journal, CancellationToken cancellationToken)
    {
        ValidateJournalSet(journal, StorageTransferStage.Completed);
        cancellationToken.ThrowIfCancellationRequested();
        VerifyRoots(journal);
        foreach (var root in journal.Manifest.Roots)
        {
            await BarrierAsync(root.OldRoot.CanonicalPath, cancellationToken).ConfigureAwait(false);
            await BarrierAsync(root.NewRoot.CanonicalPath, cancellationToken).ConfigureAwait(false);
        }
        foreach (var entry in journal.Manifest.Entries)
        {
            var root = journal.Manifest.Roots.Single(x => x.Kind == entry.RootKind);
            var identity = journal.Progress.Artifacts.Single(x => x.Artifact.VersionId == entry.Artifact.VersionId).StagedIdentity!;
            var old = Namespace(root.OldRoot.CanonicalPath, root.OldIdentity, entry.RelativePath);
            var target = Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.RelativePath);
            if (Exists(old) || Exists(Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.TempRelativePath)))
                throw new IOException("Completed relocation has a reappeared old or temporary entry.");
            await VerifyAsync(target, identity, entry.Artifact, cancellationToken).ConfigureAwait(false);
            await BarrierAsync(Path.GetDirectoryName(old)!, cancellationToken).ConfigureAwait(false);
            await BarrierAsync(Path.GetDirectoryName(target)!, cancellationToken).ConfigureAwait(false);
            await VerifyAsync(target, identity, entry.Artifact, cancellationToken).ConfigureAwait(false);
        }
        // 只观察，不用旧 journal 重新认领任何重现文件；不重建缺失祖先。
        VerifyRoots(journal);
        foreach (var entry in journal.Manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = journal.Manifest.Roots.Single(x => x.Kind == entry.RootKind);
            if (Exists(Namespace(root.OldRoot.CanonicalPath, root.OldIdentity, entry.RelativePath))
                || Exists(Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.TempRelativePath)))
                throw new IOException("Completed relocation absence changed during reconciliation.");
            RequireIdentity(Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, entry.RelativePath), false,
                journal.Progress.Artifacts.Single(x => x.Artifact.VersionId == entry.Artifact.VersionId).StagedIdentity!);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void VerifyRoots(StorageRelocationJournal journal)
    {
        // 即使某个迁移根没有 placement，也不能因空循环而漏掉根替换检查。
        foreach (var root in journal.Manifest.Roots)
        {
            RequireIdentity(root.OldRoot.CanonicalPath, true, root.OldIdentity);
            RequireIdentity(root.NewRoot.CanonicalPath, true, root.NewIdentity);
        }
    }

    private static void ValidateJournalSet(StorageRelocationJournal journal, StorageTransferStage expectedStage)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.Revision < 1 || journal.Manifest.TransactionId != journal.Progress.TransactionId || journal.Manifest.PlanId != journal.Progress.PlanId
            || journal.Progress.Stage != expectedStage) throw new InvalidOperationException("Journal is not in the required relocation stage.");
        if (journal.Manifest.Entries.Length != journal.Progress.Artifacts.Length
            || journal.Manifest.Entries.Any(entry => !journal.Progress.Artifacts.Any(x => x.Artifact == entry.Artifact)))
            throw new InvalidOperationException("Journal manifest and progress sets disagree.");
    }

    private static (StorageRelocationEntry Entry, StorageRelocationRoot Root, StorageTransferArtifactProgress Progress) ValidateJournal(StorageRelocationJournal journal, ArchiveVersionId versionId)
    {
        ValidateJournalSet(journal, StorageTransferStage.Prepared);
        var entry = journal.Manifest.Entries.Single(x => x.Artifact.VersionId == versionId);
        var progress = journal.Progress.Artifacts.Single(x => x.Artifact.VersionId == versionId);
        if (entry.Artifact != progress.Artifact) throw new InvalidOperationException("Journal manifest and progress disagree.");
        return (entry, journal.Manifest.Roots.Single(x => x.Kind == entry.RootKind), progress);
    }

    private async Task EnsureParentsAsync(StorageRelocationRoot root, RelativeStoragePath relative, CancellationToken token)
    {
        RequireIdentity(root.NewRoot.CanonicalPath, true, root.NewIdentity);
        var parent = root.NewRoot.CanonicalPath;
        var segments = relative.Value.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            token.ThrowIfCancellationRequested();
            Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, new(string.Join('/', segments.Take(i + 1))));
            var child = Path.Combine(parent, segments[i]);
            if (!Exists(child))
            {
                Directory.CreateDirectory(child);
            }
            _ = InspectIdentity(child, true);
            // 先前尝试可能创建目录后在 barrier 处中断；已有目录也必须重新证明父 namespace durable。
            await BarrierAsync(parent, token).ConfigureAwait(false);
            parent = child;
        }
        Namespace(root.NewRoot.CanonicalPath, root.NewIdentity, relative);
    }

    private async Task BarrierAsync(string path, CancellationToken token)
    {
        var proof = await durability.FlushDirectoryMetadataAsync(path, token).ConfigureAwait(false);
        if (!proof.BarrierCompleted) throw new IOException("Relocation directory durability is unavailable; no durable proof can be issued.");
    }

    private static string Namespace(string root, StorageObjectIdentity identity, RelativeStoragePath relative)
    {
        RequireIdentity(root, true, identity);
        var parts = relative.Value.Split('/'); var path = root;
        for (var i = 0; i < parts.Length; i++)
        {
            path = Path.Combine(path, parts[i]);
            if (i < parts.Length - 1) _ = InspectIdentity(path, true);
        }
        return path;
    }
    private static void RequireIdentity(string path, bool directory, StorageObjectIdentity expected)
    {
        if (InspectIdentity(path, directory) != expected) throw new IOException("Relocation filesystem object identity changed.");
    }
    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static async Task VerifyAsync(string path, StorageObjectIdentity identity, StorageTransferArtifact artifact, CancellationToken token)
    {
        RequireIdentity(path, false, identity);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != artifact.Length || new Sha256Digest(Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false))) != artifact.Integrity)
            throw new IOException("Relocation artifact integrity mismatch.");
        RequireIdentity(path, false, identity);
    }
    private static StorageTransferProof Proof(StorageRelocationJournal journal, StorageRelocationEntry entry, StorageObjectIdentity identity)
        => new(journal.Manifest.TransactionId, journal.Manifest.PlanId, entry.Artifact.VersionId, entry.Artifact.Integrity, entry.Artifact.Length, identity, true, true);
}
