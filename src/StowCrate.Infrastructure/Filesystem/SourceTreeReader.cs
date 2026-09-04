using StowCrate.Application.LocalState;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Planning;

namespace StowCrate.Infrastructure.Filesystem;

public sealed class SourceTreeReader : ISourceTreeReader
{
    public async Task<SourceScanResult> ReadAsync(SourceId sourceId, string savedRoot, CancellationToken cancellationToken)
    {
        var paths = new LocalPhysicalPathResolver();
        var before = await paths.ResolveAsync(savedRoot, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(before.CanonicalPath, savedRoot, StringComparison.Ordinal))
            throw new IOException("已保存的源目录出现路径别名变化，请重新绑定。");
        // 浏览不读取文件内容，也不解析规则；链接、特殊对象和文件系统边界沿用 Scanner。
        var result = new SourceScanner().Scan(new BackupSource(sourceId.Value.ToString("D"), "source"), savedRoot,
            new SourceScanOptions(ObserveBackupIgnoreRuleSource: false, ComputeFullContentHashes: false), cancellationToken);
        var after = await paths.ResolveAsync(savedRoot, cancellationToken).ConfigureAwait(false);
        if (before != after) throw new IOException("浏览期间源目录路径发生变化，请重新读取。");
        return result;
    }
}
