# StowCrate（归匣）

> A developer-friendly structured archive backup tool.  
> 面向开发者和个人重要资料的结构化归档备份工具。

StowCrate 将目录整理成一组可理解、可独立恢复的标准归档文件，而不是只有原软件才能读取的专有备份仓库。

项目正在 **Milestone 5 — 发布、历史与存储维护** 阶段，采用 [Apache License 2.0](LICENSE)。当前开发版尚未完成普通用户从配置到首次备份的完整流程。

## 设计原则

- 普通 `.7z`、`.zip`、`.tar.zst` 就是最终备份数据；
- 使用层级归档箱避免一个巨大压缩包，也避免重复打包；
- 识别开发项目并推荐排除可重建内容；
- 同时提供可视化规则、`.backupignore` 和 Backup Plan 文件（`*.backupplan`）；
- Current Backup 与 History Store 分离，方便交给任意同步工具。

## 文档

- [产品设计](docs/PRODUCT.md)
- [技术架构](docs/ARCHITECTURE.md)
- [原始需求与实现核对（2026-09-04）](docs/reviews/2026-09-04-ORIGINAL-REQUIREMENTS-AUDIT.md)
- [`.backupignore` v1 规范](docs/BACKUPIGNORE.md)
- [Milestone 1 — Planning Kernel](docs/milestones/MILESTONE-1-PLANNING-KERNEL.md)
- [Filesystem Semantics v1](docs/FILESYSTEM.md)
- [Change Detection & Baseline Commit v1](docs/CHANGE-DETECTION.md)
- [Backup Plan Document v1](docs/BACKUPPLAN.md)
- [Milestone 2 — Source Scanner & Filesystem Semantics](docs/milestones/MILESTONE-2-SOURCE-SCANNER.md)
- [Milestone 3 — Backup Plan Document Runtime](docs/milestones/MILESTONE-3-BACKUP-PLAN-DOCUMENT.md)
- [Milestone 5 — 发布、历史与存储维护](docs/milestones/MILESTONE-5-PUBLISH-HISTORY.md)
- [仓库开发约束](AGENTS.md)

## 当前状态

规划内核与 no-follow 真实文件系统 Scanner 已具备跨平台逻辑路径、规则合成、Archive Unit 边界、不可变 `ArchivePlan`、确定性 fingerprint 和可见扫描问题。

Backup Plan v1、SQLite 持久化、归档构建、Current/History 发布及多项恢复用例已有实现。桌面已提供个人配置入口、新建 Managed 方案、目录绑定和源目录树只读浏览，以及存储维护预览与已有迁移事务恢复；归档箱与规则编辑、智能向导、完整手动备份及 CLI/原生调度入口仍待接入。目录浏览不等于备份计划或执行成功。

产品能力与当前适配器支持分开：例如当前 bundled 7-Zip 适配器拒绝尚未验证的 Privacy/Secure，迁移比较能力也只覆盖部分 Linux ext 场景。三平台 CI 通过不等于全部产品功能已在三平台交付。用户主流程验收见产品设计 §5；本次需求核对未改变 frozen 文档契约或恢复安全边界。
