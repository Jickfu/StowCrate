# StowCrate 仓库开发约束

## 文档真相源

本仓库实现 **StowCrate（归匣）**：一款跨平台、开发者友好的结构化归档备份工具。

修改产品行为或技术架构前，必须先阅读：

- `docs/PRODUCT.md`：产品行为、术语、范围和未决产品问题；
- `docs/ARCHITECTURE.md`：模块边界、依赖规则、持久化与执行设计。
- `docs/BACKUPIGNORE.md`：`.backupignore v1` 的正式语法与规则语义。
- `docs/FILESYSTEM.md`：真实文件系统扫描、链接、特殊对象和扫描问题的 v1 语义。
- `docs/CHANGE-DETECTION.md`：候选状态、变更判定、Committed Baseline 与提交时序。
- `docs/BACKUPPLAN.md`：`*.backupplan` 声明文档、Plan Authority、Import/Register 与配置真相源。

仓库文档是项目设计的唯一真相源：`PRODUCT.md` 负责产品行为，`ARCHITECTURE.md` 负责技术架构，`AGENTS.md` 负责开发约束。新的产品或架构决定必须同步更新相应文档，不能仅存在于聊天、Issue 或 Pull Request 讨论中。

如果仓库文档之间发生冲突，不得自行猜测或依据仓库外对话裁决；应由维护者明确决策并更新文档。不得擅自把文档中的“未决项”固化成永久设计。

## 不可擅自改变的产品决定

- 备份输出必须是普通、工具无关的标准归档文件（`.7z`、`.zip` 或 `.tar.zst`），不得采用专有分块仓库作为最终数据。
- **归档箱（Crate）**是产品用语，**归档单元（Archive Unit）**是领域模型和代码用语。
- Archive Unit 是层级边界。父单元扫描到子单元时必须停止进入，避免内容重复。
- 在文件管理模式中，`.backupignore` 的存在既表示该目录是 Archive Unit，也提供该单元的局部过滤规则；空文件有效。
- 规则分为全局规则、Backup Plan 规则和局部规则（`.backupignore` 或等价的 UI 配置）三层；子 Archive Unit 独立规划。
- UI 配置并保存到 SQLite 是默认方式；`.backupignore` 是高级用户的“配置即代码”方式。同一个 Archive Unit 不得同时由两种规则来源控制。
- `SourceRoot`、`CurrentRoot`、`HistoryRoot` 规范化后必须两两不重叠：任意一个都不能等于、包含或位于另一个之下。严禁把归档输出写回源目录，也不得把 History 放进 Current。同一设备跨 active Plan 的任一 writable CurrentRoot/HistoryRoot 也不得与其他 Plan 的 SourceRoot、CurrentRoot 或 HistoryRoot 重叠；不同 Plan 的只读 SourceRoot 可以重叠。
- 只有 Archive Unit 发生变化时才生成历史版本。Current 输出保持干净，可直接交给第三方工具同步。
- 首版不负责上传云端。StowCrate 只生成本地归档，用户自行选择同步方式。
- 同时支持手动与定时执行。定时任务必须通过 CLI/无头入口调用应用用例，不得自动操作 GUI。
- 核心行为必须跨平台。平台专用服务只能作为适配器或可选优化。
- 密码、令牌和恢复密钥不得写入 `config.db`、`*.backupplan`、归档清单、日志或版本控制文件。

## 架构约束

- 使用 C#、.NET 10、Avalonia 和 MVVM。
- `StowCrate.Core` 放置领域概念，不得依赖 UI、SQLite、归档程序或操作系统 API。
- `StowCrate.Application` 放置用例与端口，只能依赖 Core。
- `StowCrate.Infrastructure` 实现持久化、文件系统、配置、调度和平台端口。
- `StowCrate.Archiving` 实现归档适配器，依赖抽象而不是 UI。
- `StowCrate.App` 与未来的 `StowCrate.Cli` 是组合根。业务规则不得放进 View、ViewModel 或命令行处理器。
- 当前模板可以直接创建 `MainViewModel`；一旦引入 Scanner、Repository、ArchiveWriter 等业务服务，应使用 `Microsoft.Extensions.DependencyInjection` 在 App/CLI 组合根完成装配，不得在 ViewModel 中手工构造基础设施对象。除非生命周期需求证明必要，不必引入完整 Generic Host。
- USN Journal、FSEvents、inotify 等平台加速器必须具备便携回退方案。
- 当前设计阶段按 `CHANGE-DETECTION.md` → Backup Plan v1 → Persistence 的顺序收敛契约；Persistence 规范完成前不得实现 SQLite schema、Entity、Repository 或 migration。
- Change Detection 以 Archive Unit 为提交粒度；Observed、Candidate、Committed Baseline 不得混用，失败、取消或发布前状态不得推进 baseline。
- `*.backupplan` 是可移植声明文档，不是数据库或运行状态备份。Managed 与 File-backed 只能选择一个配置真相源，禁止与 SQLite 隐式双向同步；Core 不得感知 authority 或文档物理路径。
- `*.backupplan` 必须按必填正整数 `schemaVersion` 使用 version-specific closed-world reader；未知字段、枚举或联合类型一律拒绝，禁止 case-insensitive property、任意 extension bag 或降级猜测未来版本。旧文档只允许内存迁移，未经用户明确升级不得改写 File-backed 文件。
- `$schema` 是 v1 optional、non-authoritative discovery metadata。portable semantics pin 只允许 Rules、Archive 与 OutputPathEncoding 三项；Fingerprint、scanner、External mapping、Schedule/DST、manifest 与 storage binding 等内部版本不得临时暴露为 Plan 字段。
- `schemas/backupplan-v1.schema.json` 是 frozen Draft 2020-12 closed-world document contract；长期稳定公开 URI 未配置前不得虚构 `$id`。Schema validation 不替代 strict duplicate-property parsing 或 semantic/reference/readiness/capability validation，Document DTO 也不得充当 persistence Entity。
- Import/Update 以完整 Plan aggregate 为单位，禁止 automatic/field/partial/three-way merge；Update 只按稳定 ID 对应并原子替换 portable configuration，不得清空 baseline 或自动删除 removed identity 的 Current/History。Clone 必须递归生成全部 portable IDs 且不复制任何 local/runtime state；authority conversion 与 registration relocation 必须显式确认。
- Plan、Source、Archive Unit、External Source 使用稳定 UUID v4 identity；名称、逻辑/物理路径、文件位置、数组下标和数据库键都不是 identity。Portable Configuration 不保存设备绝对路径，物理 Source/Current/History/External 路径属于 Device Local Binding。
- `SourceOutputPath`、History Enabled/Retention 属于 Portable Configuration；CurrentRoot 永远是 required local binding，HistoryRoot 仅在至少一个单元 effective History Enabled 时 required。Retention cleanup 必须在 Current/baseline durable commit 后执行，失败不得回滚有效 Current。
- OutputLayout 与 effective History Enabled 属于 ExecutionSemanticFingerprint；RetentionPolicy 不属于。物理 Source/Current/effective History/External binding 进入仅用于单次运行 stale check 的 ExecutionBindingFingerprint，不进入 archive fingerprint 或 Committed Baseline。
- Backup Plan 必须显式保存完整 ArchiveSpecDefault；declared unit 只能逐组件 override Format、CompressionPreset、Protection。Archiver 只接收 resolved EffectiveArchiveSpec。v1 固定 single-volume，禁止把 algorithm、solid、thread、volume size、raw CLI 参数或 metadata toggle 暴露为 portable 配置。
- External Source v1 是指向 declared Archive Unit 的 required explicit inclusion：一个稳定 ID 对应一个 File/Directory local binding 与非空 ArchiveDestination。它绕过普通 Rules，但必须 no-follow、禁止 external unit discovery/overlay，并通过 private staging、owner collision、Boundary、Completeness 与 TOCTOU 验证；不得记录 physical path 或把异常解释成删除。
- External observation 使用独立强类型 `ExternalSourceSnapshot`（可复用纯 entry value types），不得冒充 BackupSource `SourceSnapshot` 或携带 physical/staging/OS object。portable reference graph 必须在 local binding 前完整验证，dangling/duplicate/type-mismatched reference 属于 document semantic invalid。
- `.backupignore @id <uuid-v4>` 是 FILE_MANAGED Archive Unit 的可选稳定 identity；空文件仍合法，任何流程都不得未经用户明确确认自动写入 `@id`。
- 归档先写入 `.partial` 临时文件，完成测试和完整性计算后再原子发布。不得用未验证结果覆盖有效 Current。
- 一致的 SQLite 配置快照必须通过 SQLite Online Backup API 创建，不得直接复制正在使用的数据库文件。

## 工作规范

- Git 提交信息遵循 Conventional Commits：`feat`、`fix`、`docs`、`refactor`、`test`、`build`、`ci`、`chore` 等 type（及可选 scope）保持英文，冒号后的提交描述使用中文并简洁说明变更目的，例如 `docs: 明确备份方案规则语义`。
- 关键代码、特殊处理和不直观的业务约束必须给出简体中文注释。
- 变更应保持小而明确，并遵守文档中的 MVP 范围；未经产品决定，不得加入云存储、块级去重、磁盘镜像或双向同步。
- 在领域边界使用逻辑路径或可移植路径，不得假设盘符、大小写不敏感或 Windows 路径分隔符。
- 扫描、哈希、压缩等长时间操作必须支持取消。
- 对可恢复的单文件错误进行明确报告，不得静默漏掉数据；成功结果必须准确列出遗漏和警告。
- 行为变更必须补充测试，重点覆盖规则优先级、路径规范化、嵌套 Archive Unit、变更检测和原子发布。
- 通用构建设置只在 `Directory.Build.props` 管理，NuGet 版本只在 `Directory.Packages.props` 管理；项目文件不得重复声明这些值。
- 完成实现工作前必须运行 `dotnet build` 和相关测试。
