# Milestone 2 — Source Scanner & Filesystem Semantics

## 目标

将真实物理文件系统以 no-follow 语义转换为纯 `SourceSnapshot`，并保持 Milestone 1 Planning Kernel 的确定性边界：

```text
Physical File System
  → SourceScanner
  → SourceSnapshot + ScanIssue[]
  → Planning Kernel
  → ArchivePlan
```

## 本阶段实现

- `FileSystemEntryKind`、`LinkInfo`、`LinkPolicy` 与 metadata；
- Windows Symbolic Link/Junction/Mount Point/unknown Reparse Point 分类；
- Unix Symbolic Link 与特殊文件分类；
- no-follow 目录枚举与 `.backupignore` 安全读取；
- SourceRoot、跨文件系统边界和取消处理；
- best-effort 扫描及 Info/Warning/Fatal 问题模型；
- Link Preserve/Skip 的确定性 ArchivePlan 行为；
- Windows、Linux、macOS 的真实文件系统集成测试。

## 明确不做

- UI、SQLite、Backup Plan Schema；
- cache.db 或 Change Detection；
- VSS/LVM/APFS snapshot；
- Follow/FollowInternal；
- 7-Zip/ZIP/TAR.ZST Adapter；
- Current/History 发布与恢复实现。

## 完成标准

- 普通目录可稳定扫描为规范排序的 SourceSnapshot；
- directory symlink、junction 和循环链接从不被递归；
- Link raw target 进入 snapshot 与 ArchivePlan fingerprint；
- `.backupignore` Link 导致 Fatal；
- 单文件消失、不可读子目录和 Special 产生可见 issue，而不是 crash 或静默遗漏；
- 默认不跨 filesystem boundary；
- Scanner 支持取消；
- 三平台 CI 全部通过。
