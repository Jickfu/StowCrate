# StowCrate（归匣）

> A developer-friendly structured archive backup tool.  
> 面向开发者和个人重要资料的结构化归档备份工具。

StowCrate 将目录整理成一组可理解、可独立恢复的标准归档文件，而不是只有原软件才能读取的专有备份仓库。

项目已进入 **Milestone 1 — Planning Kernel**，采用 [Apache License 2.0](LICENSE)。

## 设计原则

- 普通 `.7z`、`.zip`、`.tar.zst` 就是最终备份数据；
- 使用层级归档箱避免一个巨大压缩包，也避免重复打包；
- 识别开发项目并推荐排除可重建内容；
- 同时提供可视化规则、`.backupignore` 和 Backup Plan 文件（`*.backupplan`）；
- Current Backup 与 History Store 分离，方便交给任意同步工具。

## 文档

- [产品设计](docs/PRODUCT.md)
- [技术架构](docs/ARCHITECTURE.md)
- [`.backupignore` v1 规范](docs/BACKUPIGNORE.md)
- [Milestone 1 — Planning Kernel](docs/milestones/MILESTONE-1-PLANNING-KERNEL.md)
- [Filesystem Semantics v1](docs/FILESYSTEM.md)
- [Change Detection & Baseline Commit v1](docs/CHANGE-DETECTION.md)
- [Backup Plan Document v1](docs/BACKUPPLAN.md)
- [Milestone 2 — Source Scanner & Filesystem Semantics](docs/milestones/MILESTONE-2-SOURCE-SCANNER.md)
- [仓库开发约束](AGENTS.md)

## 当前状态

规划内核已经具备跨平台逻辑路径、`.backupignore` v1 解析与规则合成、Archive Unit 边界树、不可变 `ArchivePlan` 以及确定性 fingerprint。当前正在接入 no-follow 的真实文件系统 Scanner。

当前进入 Change Detection、Backup Plan 和 Persistence 的契约设计阶段，暂不实现 Avalonia UI、SQLite 或归档执行器。尚未决策事项统一记录在产品设计文档中。
