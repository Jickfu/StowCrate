# Milestone 1 — Planning Kernel

## 目标

实现 StowCrate 最有差异化、完全平台无关的确定性规划内核：

```text
BackupPlan
  → SourceSnapshot
  → Archive Unit Discovery / Tree
  → Rule Resolution
  → ArchivePlan
  → Dry-run / Preview
```

同一份 Source Snapshot、BackupPlan 与规则必须得到内容、顺序和 fingerprint 均一致的不可变 ArchivePlan。

## 本阶段实现

- `LogicalPath` / `RelativePath`；
- 最小 `BackupSource` / `BackupPlan`；
- `ArchiveUnit` / `ArchiveBoundary` / `ArchiveUnitTree`；
- `.backupignore v1` parser；
- `RuleAction` / `RuleMode` / `RuleSource` / `CaseSensitivity`；
- `BackupRule` / `RuleSet` / `EffectiveRuleSet` / Rule Engine；
- 不可变 `ArchivePlan` / `PlannedArchive` / `ArchiveEntry`；
- `RetentionPolicy` 概念占位，不实现清理算法；
- 可构造 SourceSnapshot 的端到端 dry-run 测试。

## 明确不做

- Avalonia UI 或新 ViewModel；
- SQLite Entity、Repository 或 migration；
- 7-Zip/ZIP/TAR.ZST Adapter；
- `*.backupplan` JSON Schema；
- History retention 算法；
- config snapshot；
- 密码与隐私保护原型；
- 真实文件系统 Scanner。

真实 Scanner 延后到首版符号链接策略确定之后。本阶段以不可变 `SourceSnapshot` 作为扫描结果边界，从而先稳定纯规划行为而不隐式选择平台文件遍历语义。

## 完成标准

- 文档中的 B/D/F 嵌套场景产生三个稳定归档计划；
- D 永不包含 F，Boundary 不能被 INCLUDE 穿透；
- `.backupignore v1` 的 parser、glob、mode、case、三层覆盖和错误行为具备回归测试；
- own `.backupignore` 保留、reserved namespace 冲突和 UI/FILE source 冲突有测试；
- ArchivePlan 对输入顺序不敏感，输出稳定排序，fingerprint 可复现；
- Windows、Linux、macOS CI 全部通过。
