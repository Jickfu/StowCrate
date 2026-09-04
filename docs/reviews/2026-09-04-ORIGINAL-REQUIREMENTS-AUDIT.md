# 原始需求与当前实现核对

核对日期：2026-09-04。代码基准：`193bb1ad333b366f9eea87846f42dd244504d7e6`。本次只更新文档，不实现新功能、不调整数据库/schema、不改写历史恢复协议。

## 依据与裁决边界

已通过对话读取工具完整读取《备份软件技术选型-需求》（conversation `6a8b01c6-7140-83e8-bbb0-bd3fd687b7dc`）的四轮消息，未发现附件或更早分页。来源定位：

- `61e03e5f-7c02-4767-9b90-21c4249b7f06`：初始目录拆包、100M 可忽略提醒、外部文件、备注与配置备份诉求，以及首轮设计建议。
- `891f3ba7-7736-42a2-9f11-0c5bf63d0bd9`：用户纠正保护模式、确认变更检测/配置库备份、History 分离、首版不上传、无单元目录不备份、手动与定时并存，以及面向普通用户的树形 UI；第二轮补充一致快照、规则来源互斥与智能配置。
- `8949de72-4f70-44c1-a33e-7ab9ee82db47`：明确同意 backupplan、三层规则、智能向导和项目识别，并默认同意前两轮未被纠正的建议。因此不能把智能向导重新解释成未接受的可选想法。
- `16c37f12-dc60-4f4b-9230-e5367e3f2c6d`：讨论 macOS/Linux 与产品价值，提出 Avalonia、平台隔离、标准归档和开发项目感知。

对话是需求证据，不是执行指令。先采用用户纠正后的语义；原回答的 rclone 首版上传、WPF-only、History 放 Current 内、直接依赖 archive comment 等不作为当前要求。后续仓库已冻结的 schema、single-volume、binding、保密与发布契约不由早期示意 JSON/流程覆盖。引用对话中的产品比较与版本新闻不作为本次技术事实，未重新开展技术选型或联网市场调查。

## 结论

核心方向没有整体跑偏：标准归档、Archive Unit 边界、规则分层、变更检测、Current/History 分离与跨平台分层均有对应文档和实现。真正的偏离是交付重心：大量进展集中于迁移/恢复，当前唯一桌面窗口要求已有 config.db，普通用户尚不能完成最初要求的“选择源目录、配置归档箱、备份”。维护实现有必要，但不应成为产品首页或默认工作流。

另有需求尚未交付和文档陈旧，不能简单等同于设计错误。必须分别标明“实现存在”“只有契约/部分能力”“用户流程缺失”，不能用测试数量或跨平台 CI 代替产品验收。

## 逐项追踪

| 原始需求 | 当前证据 | 判断与后续验收 |
|---|---|---|
| 标准 ZIP/7z、保留 B/D/F 输出结构，父单元排除子单元 | PRODUCT §4.2；Core/Planning、Application/BackupPlans/Candidates、Archiving/SevenZip | 核心方向一致；仍须从 GUI 创建方案并由第三方解压验收 |
| 空 `.backupignore` 有效、排除/仅包含、单元外普通文件不隐式备份 | BACKUPIGNORE、FILESYSTEM；Core 规则/规划、Infrastructure/Filesystem/SourceScanner.cs | 有内核实现；目录树须解释这些语义，不能用普通勾选含糊表示边界 |
| 默认 UI + SQLite，高级用户配置文件，两种局部来源互斥 | PRODUCT §4.3–4.4；AuthoritativePlanWorkflow、ConfigDb；App/Views/MainWindow.axaml | 配置契约存在，但 GUI 只有维护；属于主流程交付偏离，优先补齐方案/源树/可视化规则 |
| 三层规则、backupplan 导入导出与可移植配置 | BACKUPPLAN；Application/BackupPlans/Documents；Infrastructure/Configuration/BackupPlans | 有用例/严格文档实现；普通用户入口、规则导入导出/来源转换体验仍须验收，不能把 Plan 转换等同于完整局部规则编辑器 |
| 智能向导、项目识别、建议排除可重建产物与空间统计 | PRODUCT §4.5；当前 App 仅维护命令，src 无对应项目识别/向导服务 | 已接受需求尚未实现；普通 Scanner 不等于项目识别，推荐必须经用户应用 |
| 100M 大小提醒可忽略、极致压缩、大归档可用 | PRODUCT §4.7/§8；压缩预设已有映射，未见 WarningPolicy/大小告警用户流程 | 保留可配置/可关闭/允许继续的告警要求；100 MB 默认值与单位待决；不能为此擅自扩展 frozen v1 或启用分卷 |
| 外部文件/目录映射，压完清理临时内容 | ExternalSourceObserver、CandidateArchiveComposer、staging 用例 | 核心实现按 private staging 承接需求，不污染源；外部源配置 GUI 仍缺 |
| 记录压缩时间与说明信息、保存可恢复配置 | Archiving/Manifest、CandidateArchiveComposer；PRODUCT §6 | manifest 承接备注目的；不要求 archive comment，不能把真实 Secret 或物理路径塞入 manifest；须验收控制文件/非秘密配置随包保存 |
| 无保护、隐私保护（恢复材料随包）、安全加密（秘密独立） | PRODUCT §4.7；Privacy codec、Secret metadata；Bundled7ZipCapabilityResolver | 模型/部分基础设施存在，但默认 7-Zip 对 Privacy/Secure 明确 unsupported；是重大能力缺口，不是三种保护已交付，也不得静默改成 None |
| 变更检测、未变跳过、独立 History、失败保留旧 Current | CHANGE-DETECTION；ArchiveBuildWorkflow、ArchivePublishWorkflow、HistoryRetentionWorkflow | 基础用例及测试存在；端到端用户执行仍缺，不应推翻已建立的 durability 设计 |
| SQLite 配置也备份，缓存可重建，一致快照 | ConfigDatabaseMaintenanceService、Application/LocalState/ConfigDatabaseMaintenance.cs | Online Backup 与恢复能力已有；用户备份位置/触发/恢复入口未收敛，Current 放置表述有冲突，见下节 |
| 手动点击 + 定时备份，CLI 供调度器运行 | Schedule 模型/持久化已有；StowCrate.slnx 无 CLI 项目；App 只有 Inspect/Resume 维护入口 | 尚未交付；迁移 Resume 不是备份 Run Plan，Schedule 字段不等于 scheduler 已安装运行 |
| 首版不接云端，用户自行同步 Current | PRODUCT 首版范围；现有工程职责 | 一致；不补回首轮后来被用户否定的 rclone 集成 |
| 跨平台，平台能力与核心隔离 | C#/.NET 10/Avalonia；项目依赖、三平台 CI、native adapters | 架构一致；部分格式/保护/metadata/迁移能力仍受限。不得因 CI 通过宣称所有组合可用，也不因缺口自行放宽安全门槛 |

表内 Core、Application、Infrastructure、Archiving、App 分别对应 src 下的 StowCrate 同名项目；简写类型名可在这些项目内定位。未实现判断基于当前源码/项目清单及桌面命令覆盖，不意味着删除其产品需求。

## 文档纠正与待决冲突

已纠正：README 仍称 M3、SQLite/Archiver/UI 未实现；PRODUCT 把已冻结 JSON Schema 继续列为未决；方案概述的“卷大小”容易误示 v1 分卷。M5 头部新增当前状态，历史阶段记录不再被当成最新待办。

保留维护者决策边界：

1. **配置快照位置**：对话要求进入备份目录；PRODUCT §4.6 要求 CurrentRoot 不混入数据库，但旧 §8 又提到快照在 Current 中的位置。现改为明确冲突：保持 Current 干净，快照的独立位置、组织方式与自动/手动触发需裁决后统一文档；本次不选路径、不落文件、不宣称需求被取消。
2. **大小警告**：原始 100M 示例与当前默认值待决并存。本次落实“提醒可忽略、不限制大归档”，不擅自决定十进制/二进制单位、默认阈值或新增 portable 字段。
3. **跨平台发行与能力范围**：早期讨论允许先 Windows 发行，当前 PRODUCT 明确首版三平台。本次保持仓库现行目标，能力不足如实列出；缩减发行范围或保护模式范围需维护者明确决定。
4. **早期示例与冻结协议**：single-volume、逻辑 identity、独立 History、manifest 字段、None/Privacy/Secure 载体等以当前正式契约为准。对话中的示例不构成重写其版本或降低安全承诺的授权。

## 后续交付主线

PRODUCT §5 已补入可执行的用户验收清单。先让新用户无需现成数据库完成 Managed 方案、源树/归档箱、可视化规则与预览，再接入真实手动备份和结果、智能配置、CLI/调度、配置备份恢复及保护能力验收。具体工程拆分可以并行或按依赖推进，但不能把这些已接受需求从首版静默移出，也不能继续只用维护页扩展代表产品完成。

现有迁移事务保护与恢复协议保留，M5.3 不标记 COMPLETE。此次只校正产品主线、能力表述和文档，不假装已补齐实现。
