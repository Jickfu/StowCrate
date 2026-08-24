# StowCrate（归匣）产品设计

## 1. 产品定位

StowCrate 是面向开发者和个人重要资料的**结构化归档备份工具**。它解决大量小文件、可重建项目产物、可读备份结构和长期可恢复性之间的矛盾。

它不是另一个通用云备份仓库，也不以块级增量或去重为核心。它把筛选后的源数据组织成一组有意义的标准压缩包；即使 StowCrate 停止维护、数据库损坏或用户换了电脑，仍能使用通用工具恢复数据。

首版同时交付 Windows、macOS 和 Linux；产品、核心模型、测试和发布流程从第一天起按三平台设计。

## 2. 核心价值

1. **Transparent Backup（透明备份）**：输出就是普通 `.7z`、`.zip` 或 `.tar.zst` 文件，不依赖专有仓库格式。
2. **Structured Archive Units（结构化归档单元）**：把一个大目录拆成多个有语义的归档箱，并保留可理解的相对目录结构。
3. **Developer-aware（项目感知）**：识别开发项目和可重建产物，帮助用户显著减少备份体积和小文件数量。
4. **Visual Rules + Config as Code**：普通用户用目录树和向导，高级用户用 `.backupignore` 与 Backup Plan 文件（`*.backupplan`）。
5. **Current 与 History 分离**：当前备份目录保持干净，可直接交给网盘、NAS 或同步软件；历史库独立保存旧版本。

## 3. 产品语言

| 英文/代码概念 | 中文产品用语 | 含义 |
|---|---|---|
| StowCrate | 归匣 | 软件品牌 |
| Backup Plan | 备份方案 | 一套完整的源、输出、规则、归档和调度配置 |
| Backup Source | 备份源 | 被扫描的根目录或逻辑源 |
| Crate | 归档箱 | UI 中的独立归档单元 |
| Archive Unit | 归档单元 | 领域模型和开发文档中的 Crate |
| Archive Boundary | 归档边界 | 父归档单元遇到子归档单元时停止进入 |
| Current Backup | 当前备份 | 每个归档单元最新的有效版本 |
| History Store | 历史库 | 与当前备份分离的旧版本存储位置 |
| Global Rules | 全局规则 | 跨方案复用的第一层规则 |
| Plan Rules | 方案规则 | 一个备份方案的第二层规则 |
| Local Rules | 局部规则 | 一个归档单元的第三层规则 |
| Exclude Mode | 排除模式 | 默认包含，排除匹配项 |
| Include-only Mode | 仅包含模式 | 默认排除，只包含匹配项 |
| Smart Setup | 智能配置 | 识别项目并推荐归档边界和排除项 |
| External Source | 外部源 | 映射进归档但不位于源树内的文件或目录 |
| Retention Policy | 保留策略 | 历史版本的保留与清理规则 |

产品文案使用“归档箱”；代码优先使用 `ArchiveUnit`，避免将 UI 品牌语言泄漏成含糊的技术概念。

## 4. 核心模型与行为

### 4.1 备份方案

一个 Backup Plan 至少包含：

- 一个逻辑备份源及当前设备上的路径映射；
- Current Backup 输出根目录；
- 可独立配置的 History Store 根目录；
- Archive Unit 集合及规则来源；
- 压缩、归档保护、卷大小、历史保留和调度设置；
- 可选外部源映射。

`SourceRoot`、`CurrentRoot` 和 `HistoryRoot` 经过绝对化、分隔符与大小写等平台规则规范化后必须两两不重叠：任意两个路径不得相等，任意一个也不得是另一个的祖先或子孙目录。该规则同时禁止 Source/输出递归、History 位于 Current 内，以及 Current 或 History 位于 Source 内。检测到冲突时必须阻止保存或执行方案并明确指出冲突路径，不能只跳过部分目录后继续。

### 4.2 归档箱与层级边界

假设源树中 `B`、`D`、`F` 是 Archive Unit：

```text
A
├─ B
└─ C
   └─ D
      ├─ E
      └─ F
```

输出保持结构：

```text
Current/A
├─ B.7z
└─ C
   ├─ D.7z
   └─ D
      └─ F.7z
```

构建 `D.7z` 时扫描到子单元 `F` 必须停止，`F` 只进入 `F.7z`。父级规则不穿透子 Archive Unit；子单元按自己的有效规则独立规划。

在文件管理模式中，目录内存在 `.backupignore` 即表示该目录是 Archive Unit；文件同时定义本单元的局部过滤规则。空 `.backupignore` 表示“独立打包但无局部排除”。

如果配置的备份根目录内没有任何 Archive Unit，首版应提示配置问题并不备份该目录。位于 Archive Unit 之外的普通文件也不隐式复制或打包。

### 4.3 三层规则

有效规则来自：

```text
全局规则 → Backup Plan 规则 → 局部规则
```

规则支持两种模式：

- 排除模式：默认备份全部内容，再排除匹配项；
- 仅包含模式：默认不备份，只包含指定内容。

高级规则尽量采用开发者熟悉的 `.gitignore` 风格能力，包括注释、否定规则、根路径、通配符和递归通配符。UI 必须把普通操作表达为选择文件、选择文件夹和文件类型，不要求普通用户编写 pattern。

`.backupignore v1` 的完整、规范性定义见 [`BACKUPIGNORE.md`](BACKUPIGNORE.md)。该文件是规则解析器、Rule Engine 和相关测试的行为真相源。

真实文件系统对象、链接、挂载边界和扫描问题的规范性定义见 [`FILESYSTEM.md`](FILESYSTEM.md)。

每个 Archive Unit 的局部规则来源只能是：

- `UI_MANAGED`：SQLite 保存 `RuleSource`、`RuleMode` 和 `Rules`，是局部规则的唯一事实来源；或
- `FILE_MANAGED`：SQLite 只保存 `RuleSource = FILE_MANAGED`、文件位置和可重建的索引状态；`RuleMode` 与全部 `Rules` 都从 `.backupignore` 读取，`.backupignore` 是完整的局部规则唯一事实来源。

产品应支持 UI 规则与 `.backupignore` 的导入/导出，但两者不得同时控制同一个 Archive Unit。

### 4.4 Backup Plan 文件（`*.backupplan`）

`*.backupplan` 是文件扩展名约定而不是固定文件名，例如 `MyCode.backupplan`、`Documents.backupplan`。它是稳定、可移植、声明式的 Backup Plan Document，描述用户希望 StowCrate 如何备份数据；不是 `config.db` 的序列化、EF Entity、运行状态、baseline、cache 或 History 快照。完整规范见 [`BACKUPPLAN.md`](BACKUPPLAN.md)。

StowCrate 支持两种互斥的 Plan authority：

- `MANAGED`：`config.db` 是计划配置唯一真相源；Import 将文档复制成与原文件脱离的 Managed Plan，Export 产生当时的可移植声明快照；
- `FILE_BACKED`：已注册的 `*.backupplan` 是计划配置唯一真相源；`config.db` 只保存 registration、本机路径绑定、secret binding、ArchiveVersion、Committed Baseline 和运行状态。

Import 与 Register 是不同操作。Register 保持文件 authoritative，后续运行重新解析文件；不得在 SQLite 与文件之间进行隐式双向同步。Managed 与 File-backed 可以显式转换，但 authority 或文档物理位置变化本身不得触发重新归档。

路径采用逻辑源和分平台映射，允许 `${HOME}` 等可移植表达，不把 Windows 盘符写成唯一身份。同一个计划可在不同设备重新绑定实际路径。

### 4.5 智能配置

添加源后，向导扫描项目标记并推荐归档箱及排除项。首批目标包括：

- Git、.NET、Maven/Gradle、Node.js、Python、Rust 和 Go 项目；
- `bin`、`obj`、`target`、`node_modules`、`.venv`、`__pycache__`、IDE 缓存等可重建内容；
- 建议项的文件数量、占用空间和预计可节省空间。

建议必须由用户确认。`.git`、构建输出和发布产物是否排除取决于用户需求，不能无提示删除。

### 4.6 变更检测与历史

变更检测以 Archive Unit 为最小单位，严格区分 Scanner 的 Observed State、Planner 的 Candidate State 和最近成功发布 Current 所对应的 Committed Baseline。完整规范见 [`CHANGE-DETECTION.md`](CHANGE-DETECTION.md)。

首版提供两种清晰模式：Standard 默认依据规范路径、类型、大小、`mtime`、metadata 和 Link raw target；Strict 每轮读取候选普通文件并计算内容 hash。Standard 无法保证发现“内容变化但 size/mtime/metadata 完全不变”的修改，产品必须明确提示这一限制。平台日志只作为未来加速器，不能成为正确性的唯一来源。

文件集合、选择语义和归档规格分别具有确定性 fingerprint。调度、History retention、窗口状态等不改变归档字节的设置不得触发重压缩；输出根迁移也不得伪装成内容变化。

- 未变化：保留 Current，不创建重复历史版本；
- 已变化：验证新归档成功后，在旧 Current 仍位于原路径且有效的前提下，将其持久化为独立 History Version；历史版本验证完成后，再在 `CurrentRoot` 所在文件系统内原子替换 Current；
- 失败：保留最后一个有效 Current，记录错误和遗漏。

只有成功验证、原子发布并持久提交为 Current 的 ArchiveVersion 才能成为 baseline。单元部分成功时只推进成功单元；Incomplete Observation 默认阻止覆盖 Current。

历史用于恢复误删和误改，不默认混入第三方同步的 Current 目录。用户可以按 Archive Unit 配置是否保留历史。

### 4.7 归档格式与保护

支持方向：

- `7z`：Windows 和普通资料的默认高压缩格式；
- `ZIP`：兼容性优先；
- `TAR.ZST`：Linux/macOS 需要保留 POSIX 权限、符号链接、ACL 或扩展属性时使用。

单个归档允许很大，并应支持分卷、可配置大小告警、预计大小、文件数量和最大文件提示。

首版随应用分发各目标平台对应的 7-Zip/7zz 可执行文件，不要求用户预先安装；发行物必须固定兼容版本并包含第三方许可与归属说明。

归档保护在 UI 中明确分为：

1. **无保护**：内容可直接读取；
2. **隐私保护**：内容加密，但恢复信息随归档保存，只用于阻止预览、索引或简单扫描，明确声明不提供安全保密；
3. **安全加密**：密码不写入归档，没有外部保存的秘密无法恢复。

“隐私保护”的跨格式承载方式仍需原型验证，不能先把 archive comment 当作可靠协议。安全加密的秘密交给系统 Secret Store，并支持用户单独导出恢复密钥。

### 4.8 外部源

外部文件或目录通过“实际路径 → 归档内逻辑路径”映射进入指定 Archive Unit。运行时使用独立 staging 区，绝不临时写入或污染真实备份源。任务结束或恢复清理阶段删除 staging 数据。

### 4.9 执行方式

同时支持：

- GUI 手动运行、预览计划、查看警告和结果；
- CLI/headless 按计划运行；
- 操作系统原生调度器调用 CLI。

## 5. 用户体验

主配置界面以目录树显示状态：普通目录与“📦 归档箱”视觉区分。选择归档箱后配置模式、规则、格式、保护、历史和大小警告。规则编辑提供“选择文件”“选择文件夹”“文件类型”“高级规则”四个层级。

执行前提供可审阅计划：将创建、跳过、更新哪些归档，预计大小，被排除内容和配置风险。执行后提供成功、警告、失败、耗时、原始/压缩大小与完整性结果。

## 6. 数据与恢复承诺

- 每个已发布归档必须可由对应标准工具独立解压。
- 每个归档包含版本化的 `__stowcrate__/manifest.json`，记录非秘密的来源逻辑标识、创建时间、应用版本、规则摘要、文件数量、大小、子边界和完整性信息。
- `config.db` 是重要配置，通过一致快照备份；Current ArchiveVersion 及其 Committed Baseline 属于 `config.db` 的持久事实。`cache.db` 只保存可重建的逐文件 hash、扫描状态和平台游标，不属于灾难恢复必需数据。
- 配置导出、清单和日志不包含明文密码、云令牌或恢复密钥。

## 7. 首版范围

首版聚焦端到端可靠的本地结构化备份：方案与路径映射、目录树配置、`.backupignore`、三层规则、嵌套 Archive Unit 规划、项目识别建议、变更检测、标准归档、Current/History、手动与定时执行、配置导出/恢复和错误报告。

明确不做：

- 自研云盘或首版直连云服务；
- 块级去重和专有对象仓库；
- 磁盘镜像、系统裸机恢复和 NAS Server；
- 实时双向同步；
- 依赖 StowCrate 才能恢复的格式。

## 8. 尚未决策或需验证

- 首版精确的历史保留预设和清理算法；
- `*.backupplan` 的 v1 JSON Schema；
- 隐私保护恢复信息在 7z、ZIP、TAR.ZST 中的可靠承载方式；
- 7-Zip 密码能否在不出现在命令行或进程列表的前提下可靠地自动传入；
- `config.snapshot.db` 在 Current Backup 中的最终逻辑路径与版本结构；
- VSS/文件系统瞬时快照、ACL、xattr 和锁定文件的后续支持边界；
- 默认压缩级别、分卷阈值、大小警告和历史默认值。
- Standard 模式采用的快速文件内容 hash 算法及版本迁移策略。

本项目采用 Apache License 2.0，详见仓库根目录 `LICENSE`。
