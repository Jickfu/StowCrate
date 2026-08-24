# StowCrate Filesystem Semantics v1

本文是 StowCrate v1 对真实文件系统扫描行为的规范真相源。

## 1. 核心原则

- Scanner 记录“磁盘上有什么”，Planning 决定“备份什么”。
- v1 永不隐式跟随任何链接；默认保存链接对象，不保存 target 内容。
- `SourceSnapshot` 只包含不可变、平台无关的数据，不包含 `FileInfo`、`DirectoryInfo`、Stream、Handle 或 OS 对象。
- 扫描采用 best-effort consistent enumeration，不承诺 VSS/LVM/APFS snapshot 级的瞬时一致性。
- 任何跳过、遗漏或无法识别的对象必须产生结构化 issue，不得静默忽略。

```text
Physical File System
  → SourceScanner
  → SourceSnapshot + ScanIssue[]
  → Planning Kernel
  → ArchivePlan
```

## 2. 平台无关模型

`FileSystemEntryKind` 只有 `File`、`Directory`、`Link`、`Special`。Link 可细分为 `SymbolicLink`、`Junction`、`MountPoint`、`Other`，并保存 raw target、target scope、dangling 状态和是否指向目录等 metadata。

`SourceEntry` 至少携带逻辑相对路径、类型、文件大小、UTC 修改时间、链接信息、metadata flags 和稳定内容身份。Hard Link v1 按每个逻辑路径分别视为普通 File，不做 inode/file-id 去重。

规则只匹配链接的逻辑路径，绝不匹配 target。目录 pattern 可以匹配已知指向目录的 Link，但只影响链接 entry 本身，不产生遍历。

## 3. LinkPolicy

v1 只支持：

- `Preserve`（默认）：链接 entry 进入 ArchivePlan，target 内容不进入；
- `Skip`：链接 entry 不进入 ArchivePlan，target 内容仍不进入。

Scanner 不读取 `LinkPolicy`，因此相同物理事实总是产生相同 SourceSnapshot。v1 不支持 `Follow` 或 `FollowInternal`。

Preserve 必须保存链接自身记录的 raw target。解析后的绝对路径只用于 target scope 与诊断，不能替代 raw target。Broken link 是合法数据，应保留并报告；raw target、LinkKind 和 target scope 都参与 fingerprint。

归档格式不能忠实表达某种链接时必须拒绝执行或要求用户选择 Skip，绝不能自动 dereference 成 target 副本。

## 4. no-follow 枚举

Scanner 对每个对象按以下顺序处理：

1. 以 no-follow 语义读取 entry metadata；
2. 识别已知 Link，生成 Link entry 且不递归；
3. 未知 Reparse Point 或特殊文件生成 Special entry、Warning，且不递归或打开；
4. 普通 File 生成 File entry；
5. 普通 Directory 检查文件系统边界后才递归。

Archive Unit Discovery 只观察真实枚举树，绝不穿过 Link 发现 `.backupignore`。`.backupignore` 必须是真实 regular file；Link、Directory 或 Special 形式的 `.backupignore` 是 Fatal configuration error。SourceRoot 必须是真实可访问目录，不能是 Symbolic Link、Junction、Mount Point 或其他 alias。

Windows 不能把全部 `ReparsePoint` 猜成 Symbolic Link。已识别 symlink、junction、volume mount point 映射为 Link；未知 tag 映射为 Special 并报告。Unix FIFO、socket、block/character device 等映射为 Special，不打开、不备份。

## 5. 文件系统边界

默认 `FileSystemBoundaryPolicy = StayOnSourceFileSystem`。普通目录跨入另一 filesystem/device 时保留该目录 entry、停止递归并产生 Warning。需要备份 NAS 或其他挂载卷时，应将其配置为独立 Backup Source。Windows volume mount point 已作为 Link 处理，不递归。

`SourceRoot`、`CurrentRoot`、`HistoryRoot` 的两两不重叠验证必须同时考虑 lexical path 与解析 Link/Junction 后的 physical canonical path；仅用 `Path.GetFullPath` 不足以完成安全验证。

同一 DeviceId 下还必须执行跨 active Plan 的全局 overlap 检查：任一 writable CurrentRoot/HistoryRoot 都不得等于、包含或位于任何其他 active SourceRoot、CurrentRoot 或 HistoryRoot 之下。不同 Plan 的 SourceRoot 之间可以重叠，因为两者均为只读输入；但任何输出/历史根与另一个 Plan 的输入或 writable root 重叠都必须阻止配置或执行。共享父目录下互不包含的 sibling plan roots 合法。

## 6. 扫描问题与一致性

`SourceScanResult` 包含 `SourceSnapshot?` 与规范排序的 `ScanIssue[]`。Issue severity 为 `Info`、`Warning`、`Fatal`。

- SourceRoot 不存在、不可访问或不是普通目录：Fatal；
- `.backupignore` 不是 regular file、无法读取或无法解析：Fatal；
- 单个对象 metadata 失败、扫描中消失或目录无法枚举：Warning，跳过并继续；
- unknown Reparse Point、特殊文件、文件系统边界：Warning，停止处理该对象/子树；
- broken link 被保留：Info。

存在 Fatal 时不得产生可供 Planning 使用的快照。只有 Warning 时可继续规划，但最终结果必须明确显示遗漏数量，不能声称所有内容均已备份。

扫描期间新增的对象可能被纳入也可能留到下一次；被删除或 metadata 明显变化的对象应产生 Warning。真正归档时必须再次验证关键 path、entry kind 与 metadata，防止 File 在 TOCTOU 窗口被替换成 Link 后读取 target。

## 7. 恢复与后续阶段约束

Manifest 必须记录 LinkKind、raw target 和 target scope。恢复时先验证并写普通目录/文件，最后创建 Link，且永不通过刚恢复出的 Link 写后续内容。外部绝对链接默认要求用户确认；跨平台转换 Junction 等语义时必须明确提示。

7-Zip/ZIP/TAR.ZST 的具体链接能力、参数与恢复原型属于 Archiving Milestone，不得反向污染 Scanner 或 Planning Kernel。
