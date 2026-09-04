using StowCrate.Application.LocalState;

namespace StowCrate.Infrastructure.Filesystem;

public sealed class ExistingBindingDirectoryProbe : IExistingBindingDirectoryProbe
{
    public Task RequireDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("请填写目录的完整绝对路径。");
        // 只观察，不通过创建目录来消除缺失错误；链接身份继续由路径解析器验证。
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"目录不存在或不可访问，请先创建目录并确认访问权限：{path}");
        return Task.CompletedTask;
    }
}
