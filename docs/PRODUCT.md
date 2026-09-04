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

每个 Source 除稳定 SourceId 和 display Name 外，还必须有独立的 portable `SourceOutputPath`，决定其归档在 CurrentRoot 下的逻辑位置。修改显示名称不移动输出；修改 SourceOutputPath 是 Output Reorganization，不要求重新压缩。

Plan、Source、Archive Unit、External Source 与 Secret Slot 都具有与名称和路径分离的稳定 UUID v4 identity。rename、move、Export、Import、Save As 和 Managed/File-backed 转换保持 identity；只有明确 Clone 为新 Plan 时递归生成全部新 identity，且不复制本机 Secret Binding。

`SourceRoot`、`CurrentRoot` 和 `HistoryRoot` 经过绝对化、分隔符与大小写等平台规则规范化后必须两两不重叠：任意两个路径不得相等，任意一个也不得是另一个的祖先或子孙目录。该规则同时禁止 Source/输出递归、History 位于 Current 内，以及 Current 或 History 位于 Source 内。检测到冲突时必须阻止保存或执行方案并明确指出冲突路径，不能只跳过部分目录后继续。

已有 Current/History 后改变 CurrentRoot 或 HistoryRoot 必须走受控 relocation：复制或 staging 到新 root、验证 SHA-256、发布完整目标后才提交 binding；失败继续以旧 root 为事实源。relocation 不重压缩、不创建新 ArchiveVersion、不推进 baseline。同一设备跨 Plan 还必须禁止任何 writable Current/History root 与其他 Plan 的 Source/Current/History root 重叠。

迁移目标根目录必须由用户事先创建。目标根不存在时阻止迁移并提示“迁移目标根目录不存在，请先创建目录后重试”，程序不自动创建目标根。该限制不禁止迁移开始后按已验证清单创建根内所需的归档父目录；已开始迁移的根丢失或被替换仍按恢复异常处理，不自动重建。

迁移已有归档不要求原始 Backup Source 或 External Source 在线，也不要求取得归档解密密钥。它只搬迁已经生成的归档字节，不重新扫描原始文件、不重新归档或解密；这不代表可以在源离线时生成新备份。迁移仍须读取有效的 authoritative Plan 配置，验证持久化记录、旧/新归档完整性及路径安全；配置不可读或无法证明路径安全时仍阻止。旧归档所在磁盘与新目标必须可访问，不能把旧归档离线误当成原始源离线。

普通路径设置不能通过省略、停用或改写输出根绕过上述迁移流程；已有备份位置或尚未收敛的恢复/清理工作依赖该根时，保存必须整体拒绝并报告需要受控迁移，保留原绑定。初次绑定及无此类持久状态的输出根仍可正常编辑。

已有归档搬迁期间，外部修改方案名称、定时任务、过滤规则或压缩级别不影响本次搬迁；这些设置只作用于未来备份。若方案或归档单元身份、输出布局/格式、根路径发生变化，或配置失效，则暂停提交并保留旧归档与迁移日志。该规则不表示允许绕过应用内部迁移锁同时修改数据库配置。

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

Current output 固定镜像 `SourceOutputPath + ArchiveUnit logical path`，只把 unit 最后一个 segment 替换为带归档扩展名的文件；v1 不支持 filename/path template。所有输出在写 `.partial` 前按目标文件系统真实 case/path semantics 检测碰撞，冲突必须 Fatal，不能覆盖已有或其他单元输出。

在文件管理模式中，目录内存在 `.backupignore` 即表示该目录是 Archive Unit；文件同时定义本单元的局部过滤规则。空 `.backupignore` 表示“独立打包但无局部排除”。

FILE_MANAGED Archive Unit 可以用 `.backupignore @id <uuid-v4>` 选择性携带跨 rename/move 的稳定 identity。没有 `@id` 仍合法；StowCrate 不得自动修改用户文件，且未标识单元的路径变化默认不自动猜测为同一单元。

FILE_MANAGED Archive Unit 的存在由磁盘发现决定，不要求 Backup Plan 预先声明。Plan declaration 只负责 portable identity association 与非规则的 per-unit portable settings；FILE_MANAGED 的 RuleMode、CasePolicy 与 Local Rules 始终只来自 `.backupignore`。声明缺文件、与 `@id` 冲突、同 identity 路径迁移或 UI/FILE 规则来源冲突时，方案必须进入 PlanNotReady/Fatal 并等待用户确认，不能自动改写文件、计划或 SQLite。

如果配置的备份根目录内没有任何 Archive Unit，首版应提示配置问题并不备份该目录。位于 Archive Unit 之外的普通文件也不隐式复制或打包。

### 4.3 三层规则

有效规则来自：

```text
全局规则 → Backup Plan 规则 → 局部规则
```

v1 执行使用 Plan 内保存的 pinned Global Rules Snapshot。Global Rule Library 只用于跨 Plan 编写、复用与显式更新模板；Library 变化不会自动影响既有 Plan。用户执行 Apply/Update 后才替换 snapshot 并产生可审阅的规则语义变化。可选的 library name/revision provenance 只用于提示，不是执行真相源。

规则支持两种模式：

- 排除模式：默认备份全部内容，再排除匹配项；
- 仅包含模式：默认不备份，只包含指定内容。

高级规则尽量采用开发者熟悉的 `.gitignore` 风格能力，包括注释、否定规则、根路径、通配符和递归通配符。UI 必须把普通操作表达为选择文件、选择文件夹和文件类型，不要求普通用户编写 pattern。

`.backupignore v1` 的完整、规范性定义见 [`BACKUPIGNORE.md`](BACKUPIGNORE.md)。该文件是规则解析器、Rule Engine 和相关测试的行为真相源。

真实文件系统对象、链接、挂载边界和扫描问题的规范性定义见 [`FILESYSTEM.md`](FILESYSTEM.md)。

每个 Archive Unit 的局部规则来源只能是：

- `UI_MANAGED`：authoritative Managed configuration 或 File-backed Plan declaration 保存 `RuleSource`、`RuleMode` 和 `Rules`，是局部规则的唯一事实来源；或
- `FILE_MANAGED`：authoritative Plan configuration 只声明 `RuleSource = FILE_MANAGED` 及 identity association/non-rule settings；`RuleMode`、CasePolicy 与全部 `Rules` 都从 `.backupignore` 读取，`.backupignore` 是完整的局部规则唯一事实来源。SQLite 可保存本机 registration 与可重建索引，但不得保存独立规则副本。

产品应支持 UI 规则与 `.backupignore` 的导入/导出，但两者不得同时控制同一个 Archive Unit。

### 4.4 Backup Plan 文件（`*.backupplan`）

`*.backupplan` 是文件扩展名约定而不是固定文件名，例如 `MyCode.backupplan`、`Documents.backupplan`。它是稳定、可移植、声明式的 Backup Plan Document，描述用户希望 StowCrate 如何备份数据；不是 `config.db` 的序列化、EF Entity、运行状态、baseline、cache 或 History 快照。完整规范见 [`BACKUPPLAN.md`](BACKUPPLAN.md)。

StowCrate 支持两种互斥的 Plan authority：

- `MANAGED`：`config.db` 是计划配置唯一真相源；Import 将文档复制成与原文件脱离的 Managed Plan，Export 产生当时的可移植声明快照；
- `FILE_BACKED`：已注册的 `*.backupplan` 是计划配置唯一真相源；`config.db` 只保存 registration、本机路径绑定、secret binding、ArchiveVersion、Committed Baseline 和运行状态。

Import 与 Register 是不同操作。Register 保持文件 authoritative，后续运行重新解析文件；不得在 SQLite 与文件之间进行隐式双向同步。Managed 与 File-backed 可以显式转换，但 authority 或文档物理位置变化本身不得触发重新归档。

`*.backupplan` 必须带正整数 `schemaVersion` 并使用严格 UTF-8 JSON；optional `$schema` 只用于 IDE/editor discovery，不 authoritative。已知版本采用 closed-world contract：任何层级的未知字段、未知枚举值、未知联合类型或重复字段都必须拒绝，不能忽略后继续备份；未来版本必须安全提示需要新版 StowCrate，不能降级猜测。读取旧版文档只做内存迁移，不自动改写 File-backed 文件；升级和保存为新 schemaVersion 必须是用户明确操作。文档有效性、本机 binding/readiness 与归档平台 capability 是不同状态，必须分别报告。

Backup Plan v1 领域模型已经 Domain Freeze Review 确认，无 schema-shaping blocker；后续 JSON Schema 必须投影既有领域契约，不能反向把 Document DTO 或数据库结构当作产品模型。

Draft 2020-12 Schema、fixtures 与自动验证 Review 已通过，Backup Plan v1 Document Contract Frozen。正式公开 Schema URI 尚未配置；在确认长期稳定托管地址前不得虚构 `$id` 或 writer 默认发布 URI。

v1 不提供自动、字段级、部分或 three-way merge。Update Existing 只对相同 PlanId 做整份 portable desired configuration 替换，并在确认 semantic diff 后按稳定 ID 保留适用的本机/运行状态；新增对象需要重新绑定/首次备份，移除对象的 Current、History 与 baseline 进入 inactive recovery state，不自动删除。Clone 才递归生成全部 portable IDs，且不继承 binding、Current、History、baseline 或调度状态。Managed/File-backed authority 转换和 File-backed registration relocation 即使语义相同也必须显式确认。

Portable document 只保存逻辑 Source、Archive Unit 相对路径和 External Source declaration，不保存设备绝对 SourceRoot、CurrentRoot、HistoryRoot 或 External Source 路径。这些物理位置属于按 DeviceId 隔离的 Local Binding；同一个 Plan 可在不同设备重新绑定。

Local Binding v1 可以使用 StowCrate 定义的 `${HOME}` 根变量，但不支持任意环境变量、shell expansion 或命令替换。binding 展开后必须得到绝对路径并完成 physical canonicalization 与三根两两不重叠验证。

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

Schedule Intent 属于完整 Plan desired configuration，因此改变调度会更新 Plan/schedule semantic identity 并要求独立协调本机 scheduler；但它不是本轮归档执行关键语义。备份运行期间仅修改 schedule 不得废弃已经验证的归档结果。

- 未变化：保留 Current，不创建重复历史版本；
- 已变化：验证新归档成功后，在旧 Current 仍位于原路径且有效的前提下，将其持久化为独立 History Version；历史版本验证完成后，再在 `CurrentRoot` 所在文件系统内原子替换 Current；
- 失败：保留最后一个有效 Current，记录错误和遗漏。

只有成功验证、原子发布并持久提交为 Current 的 ArchiveVersion 才能成为 baseline。单元部分成功时只推进成功单元；Incomplete Observation 默认阻止覆盖 Current。

历史用于恢复误删和误改，不默认混入第三方同步的 Current 目录。用户可以按 Archive Unit 配置是否保留历史。

Current 是每个 Archive Unit 最多一个、位于稳定确定性路径的最新有效标准归档；History 只保存被新 Current 替代的旧 Current，不是第二套 Current 或同步镜像。CurrentRoot 必须保持干净，不混入旧版本、数据库或日志。

History 默认 Disabled。Portable Plan 保存 Plan default 和 declared Archive Unit override；未声明的 FILE_MANAGED unit 使用 Plan default。启用 History 时必须显式选择 `KeepAll` 或 `KeepLastVersions(N)`（`N >= 1`），并在当前设备绑定 HistoryRoot。Disabled 只停止生成新历史，绝不删除已有版本；Purge History 是独立的破坏性操作，必须明确确认。

只有 Archive Unit Changed、存在 old Current 且 effective History Enabled 时才捕获 History。捕获并验证 old Current 失败必须保留旧 Current、阻止发布；Retention cleanup 只在新 Current 和 baseline durable commit 后运行，失败产生 SuccessWithWarnings/HistoryMaintenanceOutOfSync，不回滚有效 Current。

### 4.7 归档格式与保护

支持方向：

- `7z`：Windows 和普通资料的默认高压缩格式；
- `ZIP`：兼容性优先；
- `TAR.ZST`：Linux/macOS 需要保留 POSIX 权限、符号链接、ACL 或扩展属性时使用。

单个归档允许很大；v1 固定 single-volume，不支持分卷。产品提供可配置大小告警、预计大小、文件数量和最大文件提示；未来分卷必须先设计 Archive Artifact Set、逐卷完整性和原子发布语义。

每个 Plan 必须显式保存完整 ArchiveSpec 默认值，新 Plan 产品默认是 SevenZip + Standard + None。v1 portable ArchiveSpec 只提供 SevenZip/Zip/TarZstd、Store/Fast/Standard/Extreme 压缩预设和 Protection Configuration；declared Archive Unit 可逐组件 override，未声明的 FILE_MANAGED unit 使用 Plan 默认。算法、solid、线程、分卷和 metadata toggle 等底层参数不作为 portable 配置。

首版随应用分发各目标平台对应的 7-Zip/7zz 可执行文件，不要求用户预先安装；发行物必须固定兼容版本并包含第三方许可与归属说明。

归档保护在 UI 中明确分为：

1. **无保护（None）**：不加密，不使用 Secret；
2. **隐私保护（Privacy）**：内容经过加密或遮蔽，恢复所需材料随备份 artifact 保存，只用于阻止预览、索引、误打开或低成本扫描，明确声明不提供机密性保证；
3. **安全加密（Secure）**：使用外部 Secret 真正加密，恢复秘密默认不随归档保存；没有独立保存的 Secret 或未来显式导出的 Recovery Package 就无法恢复。

Portable Plan 只声明 Protection Configuration 和 plan-scoped portable Secret Slot，不保存密码、secret-derived hash、平台 Secret Store locator、加密 blob 或恢复材料。Secure 所需 Secret 通过当前设备的显式 Local Binding 关联系统 Secret Store；Import、Register 或 Clone 不得按名称自动绑定或复制 Secret。

None 与 Privacy 禁止引用用户 Secret Slot；Secure 必须引用 Secret Slot，缺少 binding 时 PlanNotReady。无头运行无法读取 Secret 时必须阻止并清晰报告，不能等待 GUI 输入，也不能降级保护模式。

“隐私保护”的跨格式恢复材料承载方式仍需原型验证，不能先把 archive comment、普通 manifest、sidecar 或 extra field 固定为协议。Secure 的 Recovery Export 是未来独立、显式的安全 artifact，不属于 `*.backupplan`，也不得默认复制到 Current。

### 4.8 外部源

External Source 是 BackupSource 之外的显式附加输入，不是第二套 Source、规则源或 Archive Unit discovery。portable declaration 使用稳定 ExternalSourceId、显示名、File/Directory Kind、显式 declared TargetArchiveUnitId 和非空 ArchiveDestination；FILE_MANAGED unit 可作为目标但必须先 declaration。一个 External Source 只绑定一个本机真实 regular file 或 ordinary directory root，全部 required；缺少/离线/不可读/kind mismatch 时 PlanNotReady 或阻止目标单元执行，不得静默跳过或解释成删除。

External Source 绕过 Global/Plan/Local include/exclude Rules，但仍遵守 no-follow、filesystem/child Archive Boundary、LinkPolicy、reserved/control namespace、collision、IncompleteObservation、TOCTOU 和 archive capability。External Directory 内的 `.backupignore` 是普通 payload，不声明 Crate 或局部规则。File destination 是完整归档路径；Directory destination 是映射根且不追加原 basename。Normal、External 与 generated entries 的任何不同-owner path collision 都 Fatal，v1 不支持 overlay。

运行时通过只读 observation 和 run-scoped private staging 把 External payload 映射进目标 Archive Unit；绝不写入或污染真实路径。staging 不属于配置、baseline 或备份状态，不得形成递归输入，且 fingerprint/归档必须对应真正 staged 的 payload。manifest 不记录本机 physical binding。v1 不支持 optional、glob/multi-root、external rules、follow links、generated/remote/cloud source 或 pre-backup hook。

### 4.9 执行方式

同时支持：

- GUI 手动运行、预览计划、查看警告和结果；
- CLI/headless 按计划运行；
- 操作系统原生调度器调用 CLI。

`*.backupplan` 只保存跨平台 Schedule Intent，不保存 Task Scheduler XML/GUID、launchd plist/label、systemd unit、cron expression/line 或其他 native scheduler 配置。v1 默认 Manual-only；用户显式启用后可组合 Daily、Weekly 与 OnStartup triggers。Daily/Weekly 使用执行设备的 local wall-clock，MissedRunPolicy 支持 Skip 和默认的 RunOnceWhenAvailable，不累计补跑。

Scheduler installation 是 `PlanId + DeviceId` 下的本机状态，与 Plan 配置分开。Schedule 已配置但未安装、安装失败或 out-of-sync 不使 Plan 文档无效，也不回滚已保存配置；UI 必须分别显示 desired schedule 与 installation 状态。系统 scheduler 只唤醒统一 CLI/Application Run Plan 用例，不承载扫描、规则、Secret、归档或 History 逻辑。

同一设备上的同一 Plan 不允许手动与定时任务并行；v1 固定 SkipIfRunning，重复触发记录 AlreadyRunning/Skipped，不无限排队。定时运行始终非交互：PlanNotReady、SecretUnavailable 等状态必须记录并以失败退出，不能弹出 GUI 等待。

## 5. 用户体验

主配置界面以目录树显示状态：普通目录与“📦 归档箱”视觉区分。选择归档箱后配置模式、规则、格式、保护、历史和大小警告。规则编辑提供“选择文件”“选择文件夹”“文件类型”“高级规则”四个层级。

执行前提供可审阅计划：将创建、跳过、更新哪些归档，预计大小，被排除内容和配置风险。执行后提供成功、警告、失败、耗时、原始/压缩大小与完整性结果。

## 6. 数据与恢复承诺

- 每个已发布归档必须可由对应标准工具独立解压。
- 每个归档包含版本化的 `__stowcrate__/manifest.json`，记录非秘密的来源逻辑标识、创建时间、应用版本、规则摘要、文件数量、大小、子边界和完整性信息。
- `config.db` 是重要配置，通过一致快照备份；Current ArchiveVersion 及其 Committed Baseline 属于 `config.db` 的持久事实。`cache.db` 只保存可重建的逐文件 hash、扫描状态和平台游标，不属于灾难恢复必需数据。
- 配置导出、清单和日志不包含明文密码、云令牌或恢复密钥。
- 对 Secure Plan，`*.backupplan`、Current、History 和 `config.db` 快照都不保证具备解密能力；用户还必须独立保有 Secret 或未来显式导出的 Recovery Package，产品必须避免暗示“导出 Plan 等于备份密码”。

- Storage Relocation 容量规则已确认：无法可靠查询目标可用空间时必须阻止启动，不提供强制继续；不得把“未知”视为“足够”。同卷多个目标的复制需求合并检查，不扣除迁移完成前不能释放的旧副本。容量检查不是空间预留保证，实际 I/O 失败仍遵循保留旧 authority 的协议。

## 7. 首版范围

首版聚焦端到端可靠的本地结构化备份：方案与路径映射、目录树配置、`.backupignore`、三层规则、嵌套 Archive Unit 规划、项目识别建议、变更检测、标准归档、Current/History、手动与定时执行、配置导出/恢复和错误报告。

明确不做：

- 自研云盘或首版直连云服务；
- 块级去重和专有对象仓库；
- 磁盘镜像、系统裸机恢复和 NAS Server；
- 实时双向同步；
- 依赖 StowCrate 才能恢复的格式。

## 8. 尚未决策或需验证

- `*.backupplan` 的 v1 JSON Schema；
- 隐私保护恢复信息在 7z、ZIP、TAR.ZST 中的可靠承载方式；
- 7-Zip 密码能否在不出现在命令行或进程列表的前提下可靠地自动传入；
- `config.snapshot.db` 在 Current Backup 中的最终逻辑路径与版本结构；
- VSS/文件系统瞬时快照、ACL、xattr 和锁定文件的后续支持边界；
- 各格式压缩预设的最终 adapter capability 验证与大小警告默认值。
- Standard 模式采用的快速文件内容 hash 算法及版本迁移策略。

本项目采用 Apache License 2.0，详见仓库根目录 `LICENSE`。

迁移目标比较能力：无法可靠识别目标文件系统的大小写或 Unicode 比较规则时，必须阻止迁移并返回 RELOCATION_TARGET_COMPARISON_UNAVAILABLE；不提供强制继续，不创建探测文件。检查必须覆盖全部 final/temp 及父目录的实际规则和待创建目录的继承语义，不以操作系统默认值或规范化 comparison key 代替。Preview 保持只读。

### 存储维护界面首个增量

桌面主窗口提供已有配置库的打开入口、启用方案选择、当前 Current/History 根展示，以及新目标根的只读检查。配置库不存在时不新建；打开已有库沿用现有 schema 升级规则，并在入口提示。选择项展示方案名称与稳定 ID，以区分同名方案。

检查调用真实 InspectTargets 用例，可取消；运行期间锁定配置库、方案和目标输入。改变方案、配置库或目标会清除旧检查结果。目标根缺失提示先创建，容量和比较能力不足明确阻止。此增量只提供预览，不提供执行、恢复或 compaction 按钮，不把检查通过显示为已迁移。

### 已有迁移事务的桌面恢复

存储维护界面允许显式读取所选方案的完整迁移日志，展示 transaction UUID、revision、阶段、归档数量与冻结旧/新根。仅有名称相同不是同一事务。停用但仍保留迁移日志的方案也列入选择，不能因停用而隐藏 reservation。

继续操作前必须勾选对当前显示事务的确认，说明复制/提交后会按清单清理旧副本，取消不回滚已提交迁移。操作始终使用原 transaction 和冻结路径；新目标输入不参与恢复。读取、打开配置库、切换选择不自动复制或清理。执行后清除恢复选择与确认，要求重新读取才可继续；结果不明不重试。界面分别显示待恢复、已提交待清理、已完成但保护仍保留。根绑定可能变化后显示刷新提示，不继续呈现旧值为当前事实。新事务启动与独立释放保护入口仍未开放。
