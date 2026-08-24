> [!NOTE]
> 本文是设计讨论的初始建议稿，不是规范真相源。已经确认并可用于实现的正式语义以 [`docs/FILESYSTEM.md`](../FILESYSTEM.md) 为准；两者冲突时不得依据本文修改行为。

我建议把这一块正式定义为 **StowCrate Filesystem Semantics v1**，而不只叫“Symlink 规则”。因为一旦开始真实 Scanner，就同时会遇到 Windows Junction/Reparse Point、Unix 特殊文件、挂载点、broken symlink、跨文件系统等问题。

核心方向我建议定死：

> **StowCrate v1 永远不隐式跟随任何文件系统链接。默认保存链接本身，而不是保存它指向的数据。**([learn.microsoft.com](https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-points?utm_source=chatgpt.com))B 数据装进归档”的设计。

------

# 1. v1 的 LinkPolicy

我建议 v1 **只正式支持两个值**：

```csharp
public enum LinkPolicy
{
    Preserve,
    Skip
}
```

默认：

```text
Preserve
```

暂时**不实现 `Follow` / `FollowInternal`**。

原因很重要。

假设：

```text
E:\Code\Project
└─ shared -> D:\Shared
```

如果默认 Follow：

```text
Project.7z
└─ shared/
   └─ D:\Shared 下所有数据
```

那么一个看似只有几 MB 的项目，可能突然备份几十 GB。

还可能：

```text
A/foo -> A
```

造成循环。

或者：

```text
A/data -> D:/Photos
```

把完全不属于 Backup Source 的数据带进来。

所以 v1：

| Policy     | 行为                            |
| ---------- | ------------------------------- |
| `Preserve` | 保存链接对象本身，不扫描 target |
| `Skip`     | 不保存链接，也不扫描 target     |

明确禁止：

```text
Preserve ≠ Follow
Preserve ≠ CopyTarget
```

这是第一条基本原则。

------

# 2. 为什么暂时不做 `FollowInternal`

从功能上看：

```text
FollowInternal
```

似乎很合理：

> target 在 SourceRoot 内就跟随，外部就不跟。

但它会瞬间引入：

```text
循环检测
文件身份识别
重复 inode/file-id
Alias path
Archive Boundary 穿透
同一文件通过两个逻辑路径进入归档
链接指向另一个 Crate
相对链接解析
Scanner 与 Plan 的耦合
```

这些都会破坏刚做好的纯 `SourceSnapshot → ArchivePlan` 模型。

所以我的建议是：

> **v1 宁可少一个高级功能，也不要让 Link traversal 污染 Planning Kernel。**

以后真有需求，再增加独立的：

```text
LinkExpansion
```

阶段，而不是偷偷塞进 Scanner。

------

# 3. SourceScanner 不应用 LinkPolicy

这个架构决定很重要。

Scanner 应该回答：

> **磁盘上有什么？**

而不是：

> 用户想备份什么？

所以即便：

```text
LinkPolicy = Skip
```

Scanner 仍然应该产生：

```text
SourceEntry
{
    Path = "shared",
    Type = SymbolicLink,
    ...
}
```

然后：

```text
Physical FS
    ↓
SourceScanner
    ↓
SourceSnapshot
    ↓
BackupPlan + LinkPolicy
    ↓
Planning Kernel
    ↓
ArchivePlan
```

这样同一个 `SourceSnapshot`：

```text
Plan A = Preserve
Plan B = Skip
```

仍然可以得到不同但确定性的 ArchivePlan。

不要让 Scanner 根据 Plan 改变事实快照。

------

# 4. Core 中的 Entry 类型

我建议不要设计成：

```text
File
Directory
SymlinkToFile
SymlinkToDirectory
Junction
...
```

一路无限扩展。

Core 第一层只需要：

```csharp
public enum FileSystemEntryKind
{
    File,
    Directory,
    Link,
    Special
}
```

然后 Link 自己有详细类型：

```csharp
public enum LinkKind
{
    SymbolicLink,
    Junction,
    MountPoint,
    Other
}
```

这样 Core 世界非常稳定。

------

# 5. `SourceEntry` 建议模型

大致：

```csharp
public sealed record SourceEntry
{
    public required RelativePath Path { get; init; }

    public required FileSystemEntryKind Kind { get; init; }

    public long? Size { get; init; }

    public DateTimeOffset? LastWriteTimeUtc { get; init; }

    public LinkInfo? Link { get; init; }
}
```

Link：

```csharp
public sealed record LinkInfo
{
    public required LinkKind Kind { get; init; }

    public required string Target { get; init; }

    public LinkTargetScope TargetScope { get; init; }

    public bool IsDangling { get; init; }
}
```

TargetScope：

```csharp
public enum LinkTargetScope
{
    WithinArchiveUnit,
    WithinSource,
    OutsideSource,
    Unresolved
}
```

这几个 scope 以后非常有用。

------

# 6. 为什么要保存 TargetScope

例如：

```text
A/
├─ B/
│  └─ file.txt
└─ link -> B
```

：

```text
WithinArchiveUnit
```

而：

```text
A/
├─ B   ← Archive Unit
└─ C
   └─ link -> ../B
```

如果 link 属于 C：

```text
WithinSource
```

但 target 不属于当前 Archive Unit。

再比如：

```text
link -> D:\Photos
```

：

```text
OutsideSource
```

Broken：

```text
link -> ./not-exists
```

：

```text
Unresolved
```

注意：

> **TargetScope 只是诊断信息。**

即使：

```text
WithinArchiveUnit
```

StowCrate v1 也：

```text
不跟随
```

------

# 7. Preserve 保存的是原始 Target

非常重要。

例如 Unix：

```bash
shared -> ../../shared
```

应该保存：

```text
../../shared
```

而不是把它解析成：

```text
/home/user/code/shared
```

再存进去。

原因是：

> 相对 symlink 是有语义的。

如果还原目录结构：

```text
../../shared
```

依旧可能正确。

所以：

```text
LinkInfo.Target
```

应该是**链接自身记录的 target 表达式**。

解析后的真实目标只用于：

```text
TargetScope
安全检测
诊断
```

不能替代 raw target。

.NET 10 已提供 `FileSystemInfo.LinkTarget` 获取链接 target，并且 `ResolveLinkTarget()` 可以解析 symlink 和 Windows junction；解析最终 target 时 .NET 本身也有链深度限制——Unix 40、Windows 63。([微软学习](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesysteminfo.resolvelinktarget?view=net-10.0&utm_source=chatgpt.com))

------

# 8. Broken Symlink 是合法数据

例如：

```text
current -> releases/2025
```

但：

```text
releases/2025
```

已经不存在。

这不代表：

> symlink 不存在。

它本身仍然是一个文件系统对象。

Windows 的 symbolic link 也允许创建时 target 根本不存在。([微软学习](https://learn.microsoft.com/en-us/windows/win32/fileio/symbolic-link-programming-considerations?utm_source=chatgpt.com))

所以：

```text
LinkPolicy = Preserve
```

情况下：

```text
Broken Symlink
→ 正常进入 ArchivePlan
```

同时：

```text
IsDangling = true
TargetScope = Unresolved
```

可以产生：

```text
Info / Warning
```

但**不能因为 dangling 就丢掉它**。

------

# 9. Windows：绝不能只看 `ReparsePoint`

这是实现层最容易踩坑的地方。

.NET：

```text
FileAttributes.ReparsePoint
```

只说明：

> 此文件/目录包含 reparse point。

它不等于：

> 这是 symlink。

因为 Windows Reparse Point 还被用于：

```text
Junction
Volume Mount Point
Remote storage
Filesystem filter
其他扩展机制
```

微软官方也明确说明 reparse point 是一个通用文件系统扩展机制。([微软学习](https://learn.microsoft.com/en-us/dotnet/api/system.io.fileattributes?view=net-10.0&utm_source=chatgpt.com))

因此不能：

```csharp
if (attributes.HasFlag(FileAttributes.ReparsePoint))
{
    return SymbolicLink;
}
```

这是错误实现。

------

# 10. Windows v1 分类

建议 Infrastructure 做：

| Windows 对象         | Core 分类             |
| -------------------- | --------------------- |
| File Symlink         | `Link / SymbolicLink` |
| Directory Symlink    | `Link / SymbolicLink` |
| Junction             | `Link / Junction`     |
| Volume Mount Point   | `Link / MountPoint`   |
| 已知其他路径重定向点 | 相应 Link             |
| 未知 Reparse Point   | `Special`             |

Windows Junction 本质上是目录到另一个目录的别名，并基于 Reparse Point 实现，而且可以指向另一块本地卷。([微软学习](https://learn.microsoft.com/en-us/sysinternals/downloads/junction?utm_source=chatgpt.com))

所以：

```text
Junction
```

和：

```text
普通 Directory
```

绝对不能使用相同递归逻辑。

------

# 11. Unknown Reparse Point

例如发现：

```text
FILE_ATTRIBUTE_REPARSE_POINT
```

但 StowCrate 无法确定：

```text
Symlink?
Junction?
Cloud placeholder?
Filesystem filter?
其他类型?
```

不要猜。

我建议：

```text
EntryKind = Special
```

并：

```text
不遍历
不静默忽略
产生 ScanWarning
```

例如：

```text
SCFS1004
Unsupported reparse point at:
foo/bar
```

这比：

> “大概是目录，进去看看”

安全很多。

因为微软明确说明，不同 Reparse Tag 可以由不同文件系统 filter 解释，甚至不存在相应 filter 时打开操作会失败。([微软学习](https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-points?utm_source=chatgpt.com))

------

# 12. 但要注意 OneDrive 这类场景

这是为什么我不建议：

```text
所有 ReparsePoint = Skip
```

因为 Windows 一些云文件、过滤器功能也可能利用 reparse point。

所以以后可以增加：

```text
KnownReparseHandler
```

识别：

```text
Cloud placeholder
WOF
OneDrive
...
```

但 v1 Scanner：

> **Unknown = Special + 明确警告。**

不要错误分类。

------

# 13. Unix symlink

Linux/macOS 的 symlink 语义简单得多：

```text
Link
→ target path string
```

Scanner 使用类似：

```text
lstat
```

的语义观察链接对象本身，而不是：

```text
stat
```

后自动跟 target。

Linux 明确把 `lstat`、`readlink` 等作为操作 symlink 本身的接口。([man7.org](https://www.man7.org/linux/man-pages/man7/symlink.7.html?utm_source=chatgpt.com))

因此：

```text
dir/
└─ link -> ../foo
```

Scanner：

```text
link
Kind = Link
Target = ../foo
```

不会枚举：

```text
foo/**
```

------

# 14. Directory Symlink 不递归

例如：

```text
A/
├─ normal/
└─ shared -> /mnt/data/shared/
```

Scanner：

```text
A
├─ normal
│  └─ ...
└─ shared [Link]
```

结束。

不会：

```text
shared/
├─ file1
├─ file2
...
```

这意味着循环天然消失：

```text
A/loop -> A
```

只是：

```text
loop [Link]
```

没有递归。

所以 v1 根本不需要复杂：

```text
Visited inode
Visited FileId
Max link depth
Cycle detector
```

这是“不 Follow”的巨大架构收益。

------

# 15. `.backupignore` 发现也绝不穿 Link

例如：

```text
A
└─ shared -> ../ProjectB

ProjectB
└─ .backupignore
```

通过：

```text
A/shared
```

不能发现：

```text
ProjectB/.backupignore
```

也就是说：

> Archive Unit Discovery 遵守物理 Source Tree，而不是 symlink 展开的虚拟树。

如果：

```text
ProjectB
```

本身也位于 Source 的真实目录结构里，那么正常从它真正的位置发现即可。

------

# 16. `.backupignore` 本身必须是 Regular File

这是我非常建议增加的一条安全规则。

禁止：

```text
Project/
└─ .backupignore -> ../../rules/shared.txt
```

它不应该声明 Archive Unit。

`.backupignore` 必须：

```text
EntryKind == File
```

不是：

```text
Link
Special
Directory
```

如果扫描发现：

```text
.backupignore 是 symlink
```

应该：

> **Fatal Configuration Error**

而不是跟进去读取。

否则有人可以：

```text
.backupignore -> /some/external/file
```

使规则来源逃出 Source。

------

# 17. SourceRoot 本身也不允许是 Link

例如用户配置：

```text
SourceRoot =
E:\CodeLink
```

而：

```text
E:\CodeLink -> D:\Code
```

v1 建议直接拒绝。

提示：

> 请选择实际目标目录 `D:\Code` 作为 Backup Source。

这样：

```text
BackupSource identity
Root overlap validation
Archive Unit relative path
```

都不会产生歧义。

同样，Windows：

```text
Junction
Volume Mount Point
```

也不建议作为 SourceRoot 的最终 entry。

------

# 18. Source / Current / History 的 overlap 检查也必须考虑链接

这是一个比普通路径规范化更深的问题。

我们现在已经有：

```text
SourceRoot
CurrentRoot
HistoryRoot
```

两两不重叠规则。

假设：

```text
SourceRoot  = D:\Data
CurrentRoot = E:\Backup
```

字符串看没有关系。

但：

```text
E:\Backup
```

实际上是 Junction：

```text
E:\Backup -> D:\Data\Backup
```

那么实际上：

```text
Current ⊂ Source
```

如果只做：

```csharp
Path.GetFullPath()
```

检测不出来。

所以 Milestone 2 建议增加：

```text
Physical Root Validation
```

至少对所有存在的 root：

```text
Lexical Normalization
+
Link/Junction Resolution
+
Physical Canonicalization
```

然后再检查 overlap。

这是安全要求，不是性能优化。

------

# 19. Rule Engine 如何匹配 Link

规则永远匹配：

> **链接的逻辑路径，不匹配 target。**

例如：

```text
A/shared -> ../../shared
```

规则：

```text
shared
```

可以匹配。

规则：

```text
../../shared
```

绝不能通过 target 去匹配它。

也就是说：

```text
Rule Engine
↓
RelativePath = shared
```

完全不关心：

```text
LinkTarget = ../../shared
```

------

# 20. 目录 pattern 与 directory symlink

这里需要明确。

我建议：

```text
foo/
```

可以匹配：

```text
真实目录 foo
```

以及：

```text
已知目标类型为目录的 Directory Symlink/Junction
```

但：

> 匹配只是对 link entry 本身做 INCLUDE / EXCLUDE。

绝不意味着：

```text
foo/
→ 跟进去
```

例如：

```text
shared/
```

匹配：

```text
shared -> ../Shared
```

结果：

```text
Exclude shared link
```

而不是排除 target 内容。

------

# 21. Archive Boundary 仍然比 Link 高

例如：

```text
A
├─ D                 Archive Unit
│  ├─ shortcut -> F
│  └─ F              Archive Unit
```

D 的 archive：

```text
shortcut
```

如果 Preserve：

> 可以包含这个 symlink entry。

但：

```text
F/**
```

仍然不能进入 D。

也就是说：

```text
link points to child Archive Unit
```

不会让 Archive Boundary 失效。

------

# 22. Link fingerprint

对于 Preserve link：

ArchivePlan fingerprint 必须考虑：

```text
RelativePath
LinkKind
RawTarget
```

例如：

原来：

```text
current -> releases/v1
```

改成：

```text
current -> releases/v2
```

即使：

```text
size = 一样
mtime 甚至一样
```

也必须认为：

> ArchivePlan 发生变化。

所以 Link Target 本身属于内容身份。

------

# 23. Hard Link 怎么处理

Hard Link 不应该纳入 `LinkPolicy`。

因为它不是 symlink，也不是 reparse point。

Windows hard link 的多个路径实际上引用同一个文件数据；微软也明确指出 hard link 只能针对文件，并要求位于同一卷。([微软学习](https://learn.microsoft.com/en-us/windows/win32/fileio/hard-links-and-junctions?utm_source=chatgpt.com))

v1 我建议：

```text
Hard Link
→ 当作普通 File
```

所以：

```text
A.txt
B.txt
```

如果是同一个文件的两个 hard links：

```text
Archive
├─ A.txt
└─ B.txt
```

两份逻辑文件都进入归档。

暂时不要：

```text
hard-link deduplication
```

以后如果值得优化再做。

------

# 24. Unix FIFO / Socket / Device File

真实 Scanner 开始后一定可能遇到：

```text
FIFO
Unix Domain Socket
Block Device
Character Device
```

这些统一：

```text
EntryKind = Special
```

v1：

```text
不备份
不打开
不读取内容
产生 Warning
```

不要试图：

```text
读取 /dev/xxx
```

这甚至可能产生安全或阻塞问题。

------

# 25. Unix/macOS Mount Point

这不是 Symlink，所以不能靠 LinkPolicy 解决。

例如：

```text
/home/user/data/
└─ nas/     ← NFS mount
```

从普通目录 API 看：

```text
nas
```

可能就是 Directory。

但进入它其实跨了文件系统。

我建议 v1 再定义一个独立策略：

```csharp
public enum FileSystemBoundaryPolicy
{
    StayOnSourceFileSystem,
    CrossFileSystems
}
```

默认：

```text
StayOnSourceFileSystem
```

这样：

```text
SourceRoot
  ↓
只扫描 SourceRoot 所在 filesystem/device
```

遇到 mount boundary：

```text
停止
+
Warning
```

如果用户真的想备份 NAS：

> 把 NAS mount 本身配置成独立 Backup Source。

这个原则与：

> 不跟随外部 symlink

完全一致。

------

# 26. Windows Volume Mount Point

例如：

```text
C:\Data\Disk2
```

实际挂载：

```text
另一卷
```

因为 Windows mount point 本身是 Reparse Point 机制，v1 会识别为：

```text
Link / MountPoint
```

默认 Preserve：

```text
保存 mount-point 本身的元数据
不进入 Disk2
```

微软文档也明确指出 Reparse Point 被用于 volume mount point。([微软学习](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/fsutil-reparsepoint?utm_source=chatgpt.com))

------

# 27. Archive 中如何保存 Link

这里需要把：

```text
规划语义
```

和：

```text
具体归档格式能力
```

分开。

Planning Kernel 只输出：

```text
ArchiveEntry
{
    Kind = Link,
    LinkKind = SymbolicLink,
    LinkTarget = "../foo"
}
```

然后 Archiving Adapter 声明：

```text
ArchiveCapabilities
```

例如：

```text
SupportsSymbolicLinks
SupportsJunctions
SupportsUnixPermissions
SupportsHardLinks
...
```

------

# 28. 格式不支持 Preserve 时禁止降级成 target copy

假设以后发现：

```text
ZIP Adapter
```

无法可靠保存某种 link。

绝不能：

```text
“那我就把 target 内容打进去吧”
```

因为：

```text
link
```

和：

```text
target directory contents
```

是完全不同的数据。

正确行为：

```text
Planning / Execution Validation Error

Selected archive format cannot preserve:
foo -> ../bar
```

用户可以：

```text
改成 7z / tar.zst
```

或者：

```text
LinkPolicy = Skip
```

但 StowCrate 不能偷偷改变语义。

------

# 29. 7-Zip 这里暂时不要写死实现

当前官方 7-Zip 已提供 Windows、Linux、macOS console binaries，而且 2025 年以后还专门调整过 symbolic-link extraction 的安全处理，这说明链接恢复本身就是一个需要谨慎处理的安全边界。([7-Zip](https://www.7-zip.org/download.html?utm_source=chatgpt.com))

所以现在文档应该只规定：

> **StowCrate 要 Preserve。**

至于：

```text
7zz 什么参数
7z 格式能保存哪些 LinkKind
ZIP 能保存哪些
tar.zst 能保存哪些
```

留到 Archiving Milestone 做 capability prototype。

不要让 Core 绑定某个 7-Zip CLI 参数。

------

# 30. Manifest 应记录 Link 信息

例如：

```json
{
  "path": "shared",
  "type": "link",
  "linkKind": "symbolicLink",
  "target": "../shared",
  "targetScope": "withinSource"
}
```

尤其 Windows Junction：

```json
{
  "path": "data",
  "type": "link",
  "linkKind": "junction",
  "target": "D:\\Data",
  "targetScope": "outsideSource"
}
```

这样即使某个通用归档工具不能完美恢复原平台 link：

> 数据结构和原始语义仍然存在于 manifest 中。

------

# 31. Restore 的安全规则

这一点也最好现在定义，不然以后很容易出安全洞。

比如归档中：

```text
evil -> ../../Windows/System32
```

Restore 不能：

```text
创建链接
↓
然后继续往链接下面解压文件
```

否则就是典型 symlink path traversal。

所以恢复顺序必须：

```text
普通文件/目录
↓
验证所有目标路径
↓
最后创建 Link
```

并且：

```text
永远不通过刚恢复出来的 symlink 写后续文件
```

7-Zip 近期版本也强化了 symbolic-link extraction 的安全性，更说明这类问题值得把安全语义放在 StowCrate 自己的恢复层，而不是完全委托给外部工具。([7-Zip](https://www.7-zip.org/sdk.html?utm_source=chatgpt.com))

------

# 32. External Link 恢复

例如：

```text
shared -> D:\Shared
```

在另一台电脑恢复：

```text
D:\Shared
```

可能根本不存在。

所以建议：

```text
WithinArchiveUnit
→ 可以自动恢复

WithinSource
→ 可恢复，但提示可能依赖其他 Crate

OutsideSource
→ 默认要求用户确认

Unresolved
→ 可以恢复 raw link，但显示 dangling warning
```

尤其：

```text
absolute external link
```

不要无提示创建。

------

# 33. Junction 跨平台恢复

Windows：

```text
Junction
```

在 Linux/macOS 没有一模一样的概念。

所以 StowCrate Restore 可以：

```text
Windows → 优先恢复 Junction

Linux/macOS
→ 可选择恢复为 symbolic link
```

但这是：

> **语义转换。**

必须显式告诉用户：

```text
Junction converted to symbolic link
```

不能假装完全等价。

------

# 34. Scan Error Model

我建议 Scanner 正式产出：

```text
SourceScanResult
├─ SourceSnapshot
└─ Issues
```

Issue：

```csharp
public sealed record ScanIssue(
    ScanIssueSeverity Severity,
    string Code,
    RelativePath? Path,
    string Message);
```

Severity：

```text
Info
Warning
Fatal
```

典型定义：

| 场景                       | 级别           |
| -------------------------- | -------------- |
| Broken symlink preserved   | Info / Warning |
| Link skipped by policy     | Info           |
| Unknown Reparse Point      | Warning        |
| Unix socket                | Warning        |
| 单个文件扫描中消失         | Warning        |
| 单个文件 AccessDenied      | Warning        |
| `.backupignore` 是 symlink | Fatal          |
| SourceRoot 本身不可访问    | Fatal          |
| SourceRoot 是 link         | Fatal          |
| Root physical overlap      | Fatal          |

最重要的是：

> **任何 Skip 都必须可见。**

------

# 35. 执行结果状态也建议有三级

不要只有：

```text
Success
Failed
```

建议：

```text
Success
SuccessWithWarnings
Failed
```

例如：

```text
备份完成
但跳过 2 个 unsupported reparse points
```

不能显示绿色：

> 成功，所有内容已备份。

应该是：

> **完成，但有 2 项未备份。**

这符合备份软件的可靠性原则。

------

# 36. Scanner 与 Archive 时的 TOCTOU

Scanner 看到：

```text
foo
=
普通文件
```

但压缩时它可能已经被替换成：

```text
foo
=
symlink
```

这是典型：

> Time-of-check to time-of-use

问题。

所以以后执行阶段不能完全相信 SourceSnapshot。

打开文件准备归档时应该再次验证关键 identity：

```text
EntryKind
Path
必要 metadata
```

如果：

```text
File → Link
Directory → ReparsePoint
```

发生类型变化：

> 不继续读取 target。

产生：

```text
ChangedDuringBackup
```

错误/警告。

这个安全规则很重要。

------

# 37. v1 的完整决策顺序

我建议 Scanner 对一个目录 entry 的处理正式按：

```text
读取 entry，不 follow
        ↓
识别基础类型
        ↓
是否是已知 Link？
   YES ─────────────→ 生成 Link Entry
        │             不递归
        │
        NO
        ↓
是否是不支持的 Special/Reparse？
   YES ─────────────→ 生成 Special + Warning
        │             不递归
        │
        NO
        ↓
普通 File？
        ↓
生成 File Entry

普通 Directory？
        ↓
检查 filesystem boundary
        ↓
允许？
        ↓
递归
```

而：

```text
.backupignore
Archive Unit Discovery
Rule Engine
```

都工作在产生出来的逻辑树之上。

------

# 38. 我建议正式定下来的 v1 决策

这组内容可以直接写进 `docs/FILESYSTEM.md`：

1. **默认 `LinkPolicy = Preserve`。**
2. v1 只支持 `Preserve / Skip`，不支持任何 Follow。
3. Preserve 只保存链接对象及 raw target，绝不备份 target 内容。
4. Scanner 永远不根据 LinkPolicy 改变枚举；LinkPolicy 属于 Planning。
5. Windows 只把已识别的 symlink/junction/mount point 当 Link，不能把所有 Reparse Point 当 symlink。
6. 未知 Reparse Point 和 Unix 特殊文件不遍历，明确报告。
7. `.backupignore` 必须是真实 regular file，不能是 link。
8. SourceRoot 本身不能是 symlink/junction/mount-point alias。
9. Archive Unit Discovery 永不穿越 link。
10. Rule Engine 匹配 link 的逻辑路径，不匹配 target。
11. Hard Link v1 当普通文件处理。
12. POSIX mount / filesystem boundary 默认不跨越。
13. Preserve 无法由当前归档格式忠实表达时必须失败或要求用户改策略，绝不能自动 dereference。
14. Link target 属于 ArchivePlan fingerprint。
15. Restore 最后创建 links，禁止通过恢复出的 link 写文件，防止路径逃逸。

这一套我认为比简单做一个：

```text
if ReparsePoint then skip
```

成熟很多，同时又不会让 Milestone 2 变得失控。
