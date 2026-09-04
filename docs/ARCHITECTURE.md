# StowCrate 技术架构

产品交付核对：底层阶段完成不代表 GUI/CLI 备份流程已完成。App 的默认产品入口应装配方案管理、源目录树配置、智能建议、预览与备份运行；当前存储维护窗口仅为阶段性工作台。业务逻辑继续留在 Application/Core，UI 与 CLI 应复用相同备份用例，不通过直接更新 SQLite 或绕过 readiness/publish 协议补齐界面。需求验收以 PRODUCT.md §5 为准，现状证据见 [原始需求核对](reviews/2026-09-04-ORIGINAL-REQUIREMENTS-AUDIT.md)。

## 1. 架构目标

StowCrate 采用 C#、.NET 10、Avalonia 和 MVVM，核心为平台无关的模块化架构。首版同时交付 Windows、macOS 和 Linux，不得把任一平台的路径、API 或调度模型写入领域层。

系统优先保证：长期可恢复、规划可解释、写入原子性、失败不破坏上次有效备份、秘密不泄漏，以及无平台优化时仍能正确工作。

## 2. 解决方案结构

```text
StowCrate.slnx
├─ src/
│  ├─ StowCrate.Core
│  ├─ StowCrate.Application
│  ├─ StowCrate.Infrastructure
│  ├─ StowCrate.Archiving
│  ├─ StowCrate.App
│  └─ StowCrate.Cli                 (计划中的无头入口)
└─ tests/
   ├─ StowCrate.Core.Tests
   ├─ StowCrate.Application.Tests
   ├─ StowCrate.Infrastructure.Tests
   ├─ StowCrate.Archiving.Tests
   └─ StowCrate.Architecture.Tests
```

### StowCrate.Core

纯领域模型与确定性规则：

- `BackupPlan`、`BackupSource`、`ArchiveUnit`、`ArchiveBoundary`；
- `SourceSnapshot` 与独立强类型 `ExternalSourceSnapshot`；二者可复用纯 entry value types，但 External snapshot 不冒充 BackupSource snapshot；
- `RuleSet`、`BackupRule`、`RuleMode`、`RuleSource`；
- `ArchivePlan`、`ArchiveEntry`、`ArchiveVersion`、`RetentionPolicy`；
- portable `ArchiveSpecDefault`、逐组件 `ArchiveSpecOverride`、resolved `EffectiveArchiveSpec`，以及 Format/CompressionPreset/ArchiveSemanticsVersion；
- `ProtectionConfiguration`、强类型 `SecretSlotId` 与 resolved `SecretRevision`，但不包含 OS SecretReference 或 SecretValue；
- portable `ScheduleIntent`、Daily/Weekly/OnStartup trigger 与 MissedRunPolicy，但不包含任何 native scheduler identity/configuration；
- portable `SourceOutputPath`、History default/unit override、`KeepAll` / `KeepLastVersions` 与 output/history result state；
- 逻辑路径、归档内路径、结果与错误类型。

不得依赖 Avalonia、SQLite、7-Zip、进程调用或 OS API。

### StowCrate.Application

用例、工作流和端口：

- 扫描源与识别 Archive Unit；
- 合并规则、构建并预览 `ArchivePlan`；
- 检测变化、执行备份、发布 Current、管理 History；
- 导入/导出 `.backupignore` 和 Backup Plan 文件（`*.backupplan`）；
- Import Managed Plan、Register File-backed Plan，并把两种 authority 解析为统一的 immutable Plan Snapshot；
- 生成 semantic diff，协调 same-PlanId whole-document Update、递归 identity Clone、registration relocation 与显式 authority conversion；
- 管理 Secret Slot 的显式 Bind/Set/Replace/Unbind，用 local SecretRevision 维护变更语义并验证交互式/无头 readiness；
- 保存 ScheduleIntent、协调本机 scheduler installation/status，并由所有触发方式调用同一 Run Plan 用例；
- 验证 OutputLayout、协调 Current/History publish、retention maintenance 与 CurrentRoot/HistoryRoot 的受控 relocation；
- 按 declared Archive Unit 解析 ArchiveSpec default/override，为每单元生成 EffectiveArchiveSpec 后再执行 capability/readiness validation；
- 解析 required External Source declaration/binding，协调 no-follow observation、private staging、entry ownership/boundary collision 与 TOCTOU revalidation；
- 智能项目识别与建议确认；
- 恢复、验证、配置快照和任务结果查询。

接口示例：`IFileSystem`、`IArchiveWriter`、`IPlanRepository`、`IFileStateStore`、`ISecretStore`、`IJobScheduler`、`IClock`、`IPlatformMetadata`。

### StowCrate.Infrastructure

实现应用端口：

- SQLite `config.db`、`cache.db` 与迁移；数据库只保存 secret binding metadata、revision、provider 与 opaque reference，不保存 SecretValue 或 secret-derived verifier；
- 物理文件系统、路径映射、staging 与原子替换；
- no-follow `SourceScanner`、平台对象分类和文件系统边界探测；
- `.backupignore`/`*.backupplan` 序列化；
- Managed Plan repository、File-backed registration、Plan document loader 与本机 binding；
- destination-safe output path encoding、storage slot resolution、Current/History staging/publish/relocation 与跨 Plan root overlap 检查；
- Windows Task Scheduler、launchd、systemd timer/cron 的 `ISchedulerAdapter` 实现与本机 installation metadata；Linux 优先 systemd user timer，cron 只作能力回退；
- Credential Manager/DPAPI、Keychain、Secret Service 适配器；
- 可选 USN Journal、FSEvents、inotify 加速器。

### StowCrate.Archiving

实现 `IArchiveWriter` 等端口：

- 7-Zip/7zz 进程适配；
- ZIP；
- TAR.ZST 与平台元数据策略；
- single-volume 归档、加密、测试、哈希和 `SupportsSecureEncryption` / `SupportsPrivacyProtection` 等 capability 探测；v1 不实现 split volume。

外部可执行文件的许可、版本、路径和能力必须显式管理。应用层不得拼接命令行。

首版发行包随平台携带固定兼容版本的 7-Zip/7zz；Archiving 通过能力探测验证随包二进制，打包流程必须附带对应许可证和第三方归属信息。

### Backup Plan resolution

```text
Managed configuration in config.db ──┐
                                     ├─→ Resolved immutable Plan Snapshot → Core/Application use cases
Registered *.backupplan ─────────────┘
```

Plan authority、registration path、local binding 与 scheduler installation state 是 Application/Infrastructure 管理信息，不进入 Core `BackupPlan`。同一 Plan 只能有一个 authoritative configuration source。不存在自动双向同步；Import 是复制为 Managed，Register 是链接到 File-backed document。

Core 中的 portable authored contract 位于独立 `StowCrate.Core.BackupPlans` namespace，使用强类型 UUID v4 identity，并完整保留 ArchiveSpec/History default、override 与 inherit intent。它不复用 M1 `StowCrate.Core.Planning.BackupPlan`；后者继续作为早期单 Source Planning Kernel input，现有 API 与测试保持稳定。Infrastructure 的 versioned Document DTO 只能显式映射到 portable authored aggregate，不得泄漏到 Application 或充当 persistence Entity。

File-backed loader 必须先按 UTF-8 与严格 JSON 读取并检测 duplicate property，再以必填正整数 `schemaVersion` 分派到 version-specific closed-schema reader、semantic validator 和 in-memory migrator。未知 property/enum/variant 或未来 schemaVersion 安全失败；Infrastructure 不得用 case-insensitive property binding、extension bag 或最新 DTO 猜测旧/新文档。Application 只接收迁移后的 current semantic model，并继续执行 authority、local binding、capability 与 readiness resolution。

反向写入同样停留在 Infrastructure adapter boundary：Core portable authored aggregate 经显式 versioned projector 转为 frozen DTO，再按 canonical ordering/formatting 序列化；返回 bytes 前必须由同版本 strict reader 与 Schema 重新验证。Core 不引用 JSON/Schema 类型，writer 不负责 path-level atomic replacement。

```text
Raw bytes → strict parse → versioned schema/semantic validation → in-memory migration
          → authority resolution → local binding → ResolvedPlanSnapshot
          → capability/readiness → execution
```

读取、预览、Register 或运行不得自动升级 File-backed 文件。显式 upgrade/save 通过 Application 用例协调 preview；writer 投影合法 document 后执行 schema validation、临时写入、round-trip read/validate 和原子替换。Managed Import 可在不修改来源文件的前提下迁移到当前内部模型。

Import/Update 的单位是完整 Plan aggregate，v1 没有 automatic、field、partial 或 three-way merge。Application 只按稳定 ID 匹配对象；Update 在确认 semantic diff 后原子替换 portable configuration，并把相同 identity 的 local/runtime state 保留给 Change Detector 重新验证。新增 identity 没有 binding/baseline；removed identity 的 state/artifacts 转为 inactive recovery state，任何 purge 都是独立破坏性用例。scheduler、binding、output/history relocation/maintenance 等副作用只在 config commit 后独立协调。

同一 DeviceId/PlanId 只能有一个 authority/registration。Managed ↔ File-backed conversion、第二个 File-backed path 的 registration relocation 都必须显式确认；File-backed 文档原地改变 PlanId 时安全进入 `RegisteredDocumentIdentityChanged`，不能让 registration 偷换 identity。Clone 递归重写全部 portable IDs 和引用，不复制任何 local/runtime state。

Portable identity 使用强类型 UUID v4 `PlanId`、`SourceId`、`ArchiveUnitId`、`ExternalSourceId`、`SecretSlotId`。Application 将 portable declaration 与当前 DeviceId 下的 Local Binding 合成为 `ResolvedPlanSnapshot`；Core 不读取 DeviceId、hostname、环境变量、registration path、OS SecretReference 或数据库键。

`ResolvedPlanSnapshot` 是 frozen PortableBackupPlan 之后、Source/External physical observation 与 FILE_MANAGED discovery 之前的 device-resolved immutable execution configuration snapshot。它携带 pinned Global/Plan Rules、already-resolved Source/Current/External paths、prepared declared units、DefaultUnitPolicy 及可选 History/Secret revision facts，但不声称最终 execution-ready。PlanAuthority、registration path、Document DTO、SQLite identity、ScheduleIntent、description 与 Global Rule provenance 不进入 snapshot；publish-time revision/re-read guard 留给后续 `ExecutionSemanticSnapshot`。

Observation 使用独立 immutable typed contract：`SourceObservationSnapshot` 关联 `SourceId`，只记录 filesystem facts、resolved case、entries、issues/completeness，不应用 rules 或决定 ArchiveUnit identity；`ExternalSourceSnapshot` 关联 `ExternalSourceId`，按 declared File/Directory kind 执行 no-follow observation，External 内 `.backupignore` 仅作为 payload。Infrastructure 可以适配现有 M2 scanner，但新的 Application resolver 不依赖旧 string identity。

Application 将 observation、prepared declarations、`.backupignore` parse result 与 local registration facts合成为 `ResolvedArchiveUnitSet`。FILE_MANAGED identity 的 `@id`、declaration、registration 与 generated UUID 必须相容，任何 relocation/conflict 都显式失败；generated identity 只产生 pending registration fact。无法确定 identity/boundary 的问题仍不返回 set；仅 observation incomplete 时可返回带 issue 的 preview-capable known state。该 set 已冻结 effective rules/ArchiveSpec/History、FILE_MANAGED rule-source fingerprint 和 typed parent/child boundaries，但尚不是 Candidate ArchiveSet 或 execution-ready state。

Candidate composition 与 Execution Readiness 是独立 pure stages。`ResolvedPlanSnapshot` 将 portable Rules/Archive/OutputPathEncoding semantics pins带入两层：Candidate 按 child Boundary、Safety/reserved namespace、LinkPolicy 与 EffectiveRuleSet 选择 normal entries，将 External observation 作为 rules-bypass explicit inclusion，并预留 generated manifest descriptor；统一 owner-aware archive-path validation禁止 actual owner same-path 和 non-directory ancestor collision。Application 同时固定 per-unit v1 logical OutputRelativePath，Archiver 不再决定 Current layout。

Readiness 只检查当前 Device 执行 Candidate 的条件：effective History Enabled 对 HistoryRoot 的条件需求、Secure `SecretSlotId + SecretRevision`、Archiving capability facts，以及 generated identity registration 已 durable commit。它不接触 SecretValue/locator/provider，也不降级 ArchiveSpec。Incomplete observation 可以保留 diagnostic Candidate，但只能得到 blockers，不能构造 `ExecutionReadyArchiveSet`。

Candidate fingerprints 使用 Core `ChangeDetection` strong types与 Canonical Fingerprint Encoding v1；durable encoding 显式写入 fingerprint kind、field ID、长度和 canonical value，不复用 M1/M2 newline string fingerprint。EntrySet/Selection/ArchiveSpec/OutputLayout/ExecutionSemantic/ExecutionBinding 均为 unit-scoped；component fingerprints 只提供 ChangeReason 诊断，top-level digest 才是 equality authority。Standard v1 使用 metadata-based observed content identity，Strict v1 要求每个 regular candidate file 的 full SHA-256；FILE_MANAGED control file始终另带 raw-byte SHA-256。

`ExecutionSemanticSnapshot` 将完整 authored Plan fingerprint 与 per-unit effective execution、binding、FILE_MANAGED raw rule source、Secure revision和 History maintenance facts分层保存。publish revalidation以 unit effective facts为阻止依据，而不是整 Plan revision；retention-only drift只标记 cleanup out-of-sync。ArchiveVersion 不拥有 placement；CurrentVersion 与 HistoryVersionPlacement 分别是两类位置的唯一真相，content baseline 显式关联 Current 的 ArchiveVersionId，committed OutputLayout state 只保存 fingerprint。只有 Published Current 的 atomic metadata transaction成功后才能把 BaselineCandidate提升为 Committed Baseline。

跨 filesystem/database 不假设原子性。每个 unit 使用 config.db durable PublishIntent记录 Prepared、verified History capture、CurrentPublished与 MetadataCommitted，并保存 new ArchiveVersion metadata、Current path、完整 BaselineCandidate、OutputLayout fingerprint、old Current facts 与 History proof；old Current必须 copy→SHA-256 verify→publish History后才可替换，不能先 move/delete。进程重启后 recovery 只依赖 filesystem + config.db，并只在 observed Current匹配 old或expected-new integrity时采取确定动作，否则进入 AmbiguousPublishRecovery。Retention和旧路径 cleanup位于 metadata commit之后，失败不得回滚已发布 Current。

Raw binding expression（包括 `${HOME}`）的展开、绝对化和 physical canonicalization 属于 Infrastructure binding resolver。Application pure resolver 只接收 canonical `ResolvedPhysicalPath` facts 与 platform-aware comparison key，不调用 `Path.GetFullPath`、`Environment` 或 filesystem API。Source、CurrentRoot、External bindings 是 pre-observation required；HistoryRoot 与 SecretBinding 可以携带但不在此阶段条件阻塞。扫描后，Application 再把 prepared declarations、物理 discovery 与 `.backupignore` metadata/rules 合成为 resolved units，并在 Execution Readiness 阶段检查 effective History、Secret、capability 与最终 output collision。

External Source 只指向 declared Archive Unit。它由独立 no-follow observation 作为 explicit inclusion 加入目标 Candidate，不经过普通 Rules，也不在 external directory 内做 `.backupignore` rule parsing 或 Archive Unit discovery。Application/Infrastructure 使用只读 private staging，并确保最终 EntrySet 状态对应真正 materialized payload；normal/external/generated entries 在统一 path trie 中验证 owner collision、reserved namespace 和 child Boundary。

M4 的单元构建入口正式命名为 `ArchiveBuildRequest`，其中复用现有 `ExecutionReadyArchive`，不引入易与既有 API 混淆的 `ExecutionReadyArchiveUnit`。Application 通过 `IArchiveInputMaterializer`、`IArchiveFormatWriter`、`IArchiveArtifactVerifier` 与 manifest codec port 编排：materializer 完成 physical no-follow/TOCTOU 验证并切断 writer 对原 Source/External root 的访问；writer 只读 private staging并只写唯一 runtime `.partial`；verifier 完成 format、entry set、archived manifest与最终 bytes integrity 四层验证。

`VerifiedArchiveArtifact` 只关联 lifecycle 为 Verified 的 Core `ArchiveVersion` 和仍属 runtime 的 `.partial` handle。它不是 durable placement，不创建 `CurrentVersion`/`HistoryVersionPlacement`，不进入 PublishIntent，也不推进 Committed Baseline。Manifest v1 是独立 closed-world deterministic UTF-8 contract，描述 normal/external payload但不自列，且禁止 physical binding、Device/storage root、Secret、staging和process数据。

Archive capability validation分为portable EffectiveArchiveSpec与Candidate-derived `ArchiveCapabilityRequirements`。后者显式表达payload是否含symbolic link、是否需要UTC mtime，以及`SourceMetadata`中ReadOnly/Hidden/Executable的required flag mask；adapter必须满足required flags ⊆ preserved flags才能进入Readiness/Build，不得用含混的“POSIX”名称宣称未观察的mode、uid/gid、xattr或ACL。SevenZip/ZIP当前只声明fixture已证明的mtime；TarZstd由managed PAX + Zstd backend按RID声明mtime与实际flag/link矩阵。

bundled executable由RID descriptor定位，并同时校验official package与executable SHA-256、26.02 version和必要format；禁止搜索或fallback到system PATH。进程调用只在Archiving内使用structured ArgumentList，取消时终止整个process tree并等待退出。Secure material若未来被支持，writer/verifier必须共享同一build-scoped lease；当前26.02 redirected-stdin spike未能证明verification可靠性，因此没有任何Secure capability或不安全fallback。

`ExternalSourceSnapshot` 是不可变、平台无关的 observation boundary，关联 ExternalSourceId、root kind、relative entries 与原始业务 metadata/ScanIssue，不包含 physical binding、staging path、FileInfo、Stream 或 Handle。Infrastructure 可以复用 SourceScanner 的底层 no-follow enumeration primitive；Application 负责 ArchiveDestination mapping，Planning Kernel 只接收规范化后的 Candidate entry facts。

```text
Portable Configuration              Device Local State
PlanId / SourceId                    DeviceId / Registration
ArchiveUnitId / ExternalSourceId  +  Source/External physical bindings
SecretSlotId / Protection intent     Secret binding / local SecretRevision
ScheduleIntent                       Scheduler provider / native task identity / installed fingerprint
SourceOutputPath / HistoryPolicy     CurrentRoot / conditional HistoryRoot / relocation state
Other logical paths / policies       Other local runtime state
                                   → validated ResolvedPlanSnapshot
```

v1 path expression 只允许受控 `${HOME}` anchor，不把任意 process environment 作为隐式输入。解析后必须绝对化、physical canonicalize 并验证单 Plan Source/Current/bound History root safety。跨 active Plan overlap 由 Application 接收的纯 `ActivePlanRootFacts` 检查；未来 Infrastructure 可从 persistence 提供这些 facts，但该 contract 不依赖 SQLite。缺少 Source/Current/External binding 时不得进入 observation；MissingHistoryRootBinding 与 MissingSecretBinding 延后到 resolved unit set 的 Execution Readiness。

### StowCrate.App / StowCrate.Cli

二者是组合根：注册依赖、解析用户输入并调用相同应用用例。GUI 负责 Avalonia Views、ViewModels、导航、进度和交互；CLI 负责非交互命令、退出码和调度器集成。两者不实现备份规则。

模板阶段允许直接创建无依赖的 `MainViewModel`。一旦出现 Scanner、Repository、ArchiveWriter 等业务服务，App/CLI 使用 `Microsoft.Extensions.DependencyInjection` 作为轻量组合容器；ViewModel 不得直接构造 Infrastructure 或 Archiving 实现。只有在后台生命周期、配置和日志需求确实需要时才引入完整 Generic Host。

### Schedule installation

```text
Portable ScheduleIntent
  → Application reconcile use case
  → ISchedulerAdapter Install / Update / Remove / GetStatus
  → Device-local ScheduleInstallation
  → native scheduler wakes StowCrate.Cli
  → common Application Run Plan use case
```

ScheduleInstallation 按 `PlanId + DeviceId`/local registration 定位，不以 File-backed 文档路径作为长期 identity。native task 只携带解析当前 registration 所需的最小稳定本机 identity；不得复制 portable configuration 或业务参数。修改 ScheduleIntent 与安装/更新 native task 是不同事务：Plan 保存成功后 reconcile 失败只产生 ScheduleOutOfSync/installation error，不能回滚 Plan。

File-backed 文档变化可在 GUI/configuration workflow 中提示 reconcile；普通 headless backup run 不得顺手修改系统 scheduler。Scheduler adapter 只负责尽量实现 portable wall-clock、DST 和 missed-run 语义，不参与扫描、规划、Secret 获取、Change Detection、归档或 History。

## 3. 依赖方向

```text
StowCrate.App ─────────┐
StowCrate.Cli ─────────┤
                       ▼
             StowCrate.Application ──▶ StowCrate.Core
                       ▲                        ▲
                       │                        │
StowCrate.Infrastructure ──────────────────────┘
StowCrate.Archiving ───────────────────────────┘
```

Infrastructure 和 Archiving 只实现向内层定义的端口。组合根选择具体实现。禁止 Core 反向引用外层项目。

## 4. 规划算法

执行前必须先生成不可变、可审阅的 `ArchivePlan`，不得边扫描边直接改写 Current。

```text
BackupPlan
  → SourceSnapshot
  → Archive Unit Discovery / Tree
  → Rule Resolution
  → ArchivePlan
  → Dry-run / Preview
```

`SourceSnapshot` 是 Scanner 输出给纯规划内核的不可变边界。同一份 Source Snapshot、BackupPlan 与规则必须生成内容、顺序和 fingerprint 均相同的 ArchivePlan。Milestone 1 先用可构造快照验证规划内核；真实文件系统 Scanner 在首版符号链接策略确定后实现。

Milestone 2 的 Scanner 按 [`FILESYSTEM.md`](FILESYSTEM.md) 将物理对象转换为纯数据快照。Scanner 永不跟随链接，也不根据 `LinkPolicy` 改变枚举；`Preserve` 或 `Skip` 由 Planning Kernel 决定。扫描问题与快照并列返回，任何跳过必须可见。

1. 解析设备路径映射并按目标平台规则规范化路径；验证 `SourceRoot`、`CurrentRoot`、`HistoryRoot` 两两不相等且不存在任何祖先/子孙关系，并验证 staging 不会形成递归输入。
2. 独立发现真实枚举树中的全部 `.backupignore` FILE_MANAGED Archive Unit，并读取 authoritative metadata/rules；discovery 不依赖 declaration 或过滤规则。
3. 在 Application resolution 阶段合并 declaration、discovery 与本机 registration；identity/source/path/rule-source 冲突、缺少 FILE_MANAGED 文件或未确认 relocation 时以 PlanNotReady/Fatal 停止。
4. 构建 Archive Unit 树；把所有直接子单元注册为父单元的停止边界。
5. 分别合成每个单元的 pinned global、方案和局部规则。
6. 遍历单元内容；先检查边界，再匹配规则。边界优先于普通 include/exclude。
7. 解析 required External Source declaration 与 physical binding；验证 File/Directory root、no-follow observation、非空 destination、declared target、reserved/control namespace 和 child Boundary，并 materialize 到不递归的 private staging。
8. 合并 normal selected、External explicit 与 generated entries，在统一 owner-aware path trie 中检测所有 file/directory/metadata collision；staged payload 与最终 Candidate metadata/content 不一致时按 IncompleteObservation 停止该单元。
9. 由 SourceOutputPath、Archive Unit logical path、format extension 与版本化 OutputPathEncoding 生成 destination-safe Current relative path；按目标文件系统真实 case semantics 检测所有 output collision。
10. 生成规范排序的 entries、警告、预估与变更/输出布局指纹。
11. 只有经过用户确认或无头策略允许的计划才进入执行阶段。

路径匹配器必须独立测试以下情况：不同分隔符、根路径、`!`、`*`、`**`、尾随斜杠、Unicode、大小写敏感文件系统和嵌套边界。Scanner 实现时还必须独立测试符号链接循环及逃逸 Source 的链接。

## 5. 执行与原子发布

单个 Archive Unit 的执行状态机：

```text
Planned
  → Staging (仅外部源/清单等需要时)
  → Writing runtime artifact .partial in private M4 workspace
  → Copying to destination-local M5 publish .partial on Current filesystem
  → Archive Test
  → SHA-256 / manifest verification
  → Persist previous Current as a History Version while Current remains valid
  → Verify durable History Version
  → Atomic replace Current on Current filesystem
  → Durable ArchiveVersion / Baseline commit in config.db
  → Retention maintenance（失败只产生 warning/out-of-sync）
  → Refresh disposable cache
  → Cleanup
```

关键约束：

- M4 build `.partial` 是 private runtime artifact，可以位于与 CurrentRoot 不同的文件系统；M4 writer 不解析或依赖 CurrentRoot；
- M5 在 format/integrity verification 后把 artifact 流式复制到最终 Current target 的 sibling private publish `.partial`，重算 SHA-256/length 并 durable flush；只有该 destination-local temp 可作为同文件系统原子发布源；
- Current/History rename后必须调用平台 metadata durability barrier：Windows尝试directory handle `FlushFileBuffers`，Linux/macOS使用directory `fsync`。proof明确记录barrier是否完成；atomic namespace操作本身不等同于突然断电后directory metadata必然durable。
- 保存历史版本时不得先移走或删除旧 Current。History 与 Current 在同一文件系统时可采用经过验证的链接、克隆或复制策略；跨文件系统时采用“复制到 History 临时文件 → 完整性验证 → History 内原子发布”。这些是持久化策略，不是 Current 的事务切换；
- 启用历史时，历史版本未持久化并验证成功就不得替换 Current；历史持久化失败时任务失败，旧 Current 保持原位；
- 最终切换只在 Current 所在文件系统内执行 atomic replace。替换前崩溃时旧 Current 有效，替换后崩溃时新 Current 有效，不能出现先移走旧 Current 导致 Current 路径缺失的窗口；
- 取消、断电或进程失败后，`.partial` 不得被识别为有效备份；
- 启动时可识别并安全清理陈旧 staging/partial，但必须保留诊断信息；
- 手动与 scheduled run 对同一 `PlanId + DeviceId` 使用同一运行锁；v1 固定 SkipIfRunning，竞争者记录 AlreadyRunning/Skipped，不排队。输出路径仍需冲突保护；不同 Plan 是否并发由后续资源策略决定；
- 日志和结果必须记录被跳过、不可读、变化中或锁定的文件；Incomplete Observation 默认阻止发布。
- 只有 Changed + old Current exists + effective History Enabled 才捕获 History。History capture/verification 是替换 Current 的前置条件，失败必须保留 old Current 并终止；Retention cleanup 必须在 Current/baseline durable commit 后执行，失败不得回滚新 Current；

无历史或首次发布时，同样只允许把已验证的目标文件系统临时文件原子发布为 Current。元数据提交必须可从 Current 与 History 的实际文件状态重建，不能成为判断归档有效性的唯一依据。

## 6. 变更检测

变更检测的完整行为以 [`CHANGE-DETECTION.md`](CHANGE-DETECTION.md) 为准。纯 Change Detector 接收 Candidate Unit State 与 Committed Baseline，不直接访问数据库或物理文件系统。

每个 Archive Unit 独立比较强类型的 `EntrySetFingerprint`、`SelectionFingerprint` 与 `ArchiveSpecFingerprint`。fingerprint 使用版本化 canonical encoding 和 SHA-256；不得使用运行时 `GetHashCode()` 或依赖通用 JSON serializer 的输出稳定性。Standard/Strict 的文件内容 hash 与最终 Archive Integrity SHA-256 是不同概念和类型。

Committed Baseline 是 Current ArchiveVersion 的持久事实，位于 `config.db`；逐文件 metadata/hash、扫描缓存和 journal cursor 位于可删除的 `cache.db`：

```text
config.db  — 计划、规则、调度、历史策略、归档版本与必要审计（重要）
cache.db   — 文件状态、哈希、扫描缓存和平台游标（可重建）
```

只有成功发布 Current 后，才能在 `config.db` 事务中把对应 ArchiveVersion 标记为 Published 并更新该单元的 CurrentVersion 引用。随后才能刷新 `cache.db`；cache 永远不能领先 durable state。失败、取消、stale Plan revision、发布前状态或 Incomplete Observation 均不得推进 baseline。USN/FSEvents/inotify 只减少扫描与 hash 范围，检测结果仍能回退到便携算法。

Plan authority、`PlanId`、`ArchiveUnitId`、`ExternalSourceId`、`DeviceId`、Global Rule Library provenance 和 `*.backupplan` 的物理存放路径不属于 SelectionFingerprint 或 ArchiveSpecFingerprint；`SourceId`、Archive Unit logical path 与实际选择/映射语义仍属于 SelectionFingerprint。Managed 与 File-backed 解析出相同语义 Plan Snapshot 时，切换 authority 或移动注册文件不得触发 rebuild。

PlanSemanticFingerprint 覆盖完整 desired configuration，包括 ScheduleIntent、SourceOutputPath、History Enabled 与 RetentionPolicy。ExecutionSemanticFingerprint 只覆盖影响本轮扫描、选择、归档或发布正确性的 portable 配置：包含 OutputLayoutFingerprint 与 effective History Enabled，不包含 ScheduleIntent 或 RetentionPolicy。

每次运行还捕获 ExecutionBindingFingerprint，使用 physical-canonical SourceRoot、CurrentRoot、effective HistoryRoot、required External Source bindings、目标 case/capability identity 和 storage semantics version。Publish 前发现 Plan 或 binding 变化时重新解析；ExecutionSemanticFingerprint、ExecutionBindingFingerprint 或其他执行关键输入变化才按 PlanChangedDuringRun 阻止。schedule-only 变化继续发布；retention-only 变化继续发布但跳过旧策略 cleanup 并标记 maintenance out-of-sync。

OutputLayoutFingerprint 与 ExecutionBindingFingerprint 都不进入 EntrySet/Selection/ArchiveSpec fingerprint 或 Committed Baseline。SourceOutputPath 变化产生 OutputReorganization，CurrentRoot/HistoryRoot 变化产生 StorageRelocation；已验证 artifact 保持原 ArchiveVersion identity 和 baseline，不重新压缩。

## 7. SQLite 与配置恢复

- 使用 schema migration 和事务；数据库 DTO 不直接充当领域对象。
- `ConfigDbOpenCoordinator` 对 existing database 必须先以 read-only low-level SQLite probe 验证独立 `DatabaseMetadata.SchemaVersion`，通过后才创建 writable EF context 或运行 migration；`__EFMigrationsHistory` 不替代业务 schema version。
- 每个 config.db connection 启用 foreign keys、WAL、`synchronous=FULL` 与 busy timeout；future/invalid/corrupt schema fail closed。
- EF Entity、configuration、mapper、migration 与 repository implementation 只位于 Infrastructure；Core/Application 不引用 EF、SQLite、`DbContext`、Entity 或 `IQueryable`。
- repository 按 Application aggregate ports 实现。Archive Unit metadata commit 使用单一 transaction；publish stage transition 使用 expected-stage CAS；数据库异常转换为 StowCrate repository/concurrency/corruption error。
- Application startup coordinator 先打开并冻结 `DatabaseMetadata` 的 database/device identity，再通过高层 query ports发现 active Plan 与 incomplete PublishIntent。每个 unit 独立执行 filesystem integrity probe与 recovery decision：expected-new 自动重建并提交 durable metadata；observed-old 与 ambiguous 均保留 journal，ambiguous 只隔离该 unit。数据库 corruption/unsupported version仍是整个 local state 的 fatal error。
- `DatabaseMetadata.DeviceId` 是本机唯一 identity 来源；binding repository 不接受调用方指定 DeviceId。Local Binding 保存先由 Infrastructure 生成 canonical physical path/comparison key，再由 Application 共享的 `DeviceBindingSafetyValidator` 检查同 Plan root overlap 与跨 active Plan writable collision；安全但缺少 required root 的 aggregate 可以保存，readiness 后续报告 PlanNotReady。
- 普通 Local Binding 保存不能重定向已有 Current/History placement 依赖的 root（含停用、省略及 path/comparison key 改变）；repository 必须在同一 SQLite 事务中检查并以 `StorageRelocationRequiredException` 拒绝整份保存。任何尚存 PublishIntent、RetentionDeletionIntent（包括待 compact 的 COMPLETED）或未完成的旧路径清理/relocation/reorganization maintenance 均保守阻止两个输出根改变。该入口不执行复制或迁移，也不从 unknown 文件推导归属；迁移工作流必须使用独立 journal-shaped commit 端口，不能复用普通保存绕过检查。
- Managed/File-backed authority 由统一 Application workflow 编排。Infrastructure document-source port封装 strict reader、schema、semantic mapper与 deterministic writer；Application 只接收 portable domain。File-backed 每次从注册文件读取且绝不 fallback，authority conversion必须显式进行，activation 不改写 document revision。
- path/storage bindings 与 SecretBinding metadata 使用独立 aggregate/port，禁止保存路径时携带 stale SecretRevision/locator。执行解析前才将 active SecretRevision组合为 local facts；material availability另由 Secret Store probe判断。
- Secret Set/Replace/Rebind 使用 copy-on-write：先在平台 Secret Store创建新 locator，再以 expected-revision CAS切换 config.db metadata并递增 revision，commit后 best-effort删除旧 locator。CAS失败时旧 binding保持有效，新 locator仅是可清理 orphan。Unbind先 durable deactivate，再删除 material；禁止原地覆盖 active locator。
- Secret material只经 disposable/zeroizable transient lease流动，不进入 portable model、长期 Application snapshot、fingerprint、SQLite、日志或异常。无头执行只能读取 durable active binding，不得以临时 prompt绕过。
- `config.db` 的灾难恢复副本通过 SQLite Online Backup API 写入 temporary database，验证 DatabaseMetadata/schema与 `integrity_check` 后 atomic replace为 `config.snapshot.db`；禁止复制 live DB/WAL/SHM。
- 损坏启动只报告 validated snapshot candidate，不自动覆盖。显式恢复保留原损坏 database及 sidecars，以 Online Backup重建目标，并重新通过正常 open coordinator。
- maintenance只允许 durability-critical snapshot/diagnostics与 completed PublishIntent cleanup；incomplete journal、artifact runtime state及 baseline不得被清理。
- `cache.db` 不备份，缺失时自动重建。
- portable configuration 只保存 SecretSlot declaration；local state 只保存 binding metadata、SecretRevision、provider 与 opaque SecretReference；SecretValue 位于平台 Secret Store。
- `*.backupplan` 和归档 manifest 使用显式 `schemaVersion`，读取器对未知新版本安全失败并给出可操作提示。

## 8. 归档清单

每个归档内保留 `__stowcrate__/manifest.json`，建议字段：

- schema、StowCrate 版本、archive/plan/unit ID；
- 逻辑源、归档路径和创建时间（UTC）；
- 格式、压缩预设、保护模式与 ArchiveSemanticsVersion 的非秘密描述；
- 文件数、原始/归档大小、规则摘要、排除的子 Archive Unit；
- 内容或卷的 SHA-256、上一个版本 ID；
- 跨平台元数据能力与未保留项警告。

manifest 不保存真实密码、密钥、token 或不必要的主机隐私信息。
External Source 只记录 logical destination、kind 与必要的非秘密 provenance，不得记录 device-local physical binding path。

ArchiveVersion 的 durable identity 与 placement 分离：只记录 VersionId、ArchiveUnitId、format/spec、SHA-256、Size、lifecycle 与 PublishedAt。CurrentVersion 独占 Current relative path，HistoryVersionPlacement 独占 History relative path；实际位置由对应 StorageRoot binding + relative path 解析。relocation 只改变 binding，Output Reorganization 只改变 CurrentVersion + OutputLayout state，均不生成 version 或推进 baseline。config.db v1 详见 [`plan/CONFIG-DB-v1-SCHEMA-DESIGN.md`](plan/CONFIG-DB-v1-SCHEMA-DESIGN.md)。

## 9. 平台抽象

| 能力 | Windows | macOS | Linux | 便携回退 |
|---|---|---|---|---|
| Secret Store | Credential Manager/DPAPI | Keychain | Secret Service/libsecret | 无明文回退；缺少可用 Secret Store 时阻止 Secure |
| Scheduler | Task Scheduler | launchd | systemd timer/cron | 手动/外部调用 CLI |
| Change hint | USN Journal | FSEvents | inotify | 全量元数据扫描 |
| Metadata | NTFS ACL/ADS | APFS xattr/ACL | POSIX/xattr/ACL | 明确警告并保存可支持子集 |
| Consistent files | VSS（后续） | 平台快照（后续） | LVM/文件系统能力（后续） | 普通读取并报告不一致风险 |

任何专有能力都不得成为 Core 的必要条件。

## 10. 安全与可靠性

- SecretValue、Privacy recovery material 和 secret-derived verifier 不得进入普通 manifest、`*.backupplan`、SQLite、cache、日志、异常、telemetry、crash annotations、命令行、进程环境或进程列表。Privacy 的未来专用 recovery carrier 与 Secure 的显式 Recovery Package 是独立受控 artifact，不改变普通 manifest 的非秘密职责。
- SecretValue 只允许存在于平台 Secret Store 和执行所需的最短生命周期内存中。Archiver 只能在执行边界获得临时 Secret Material，不能获得 OS locator；若 CLI 工具无法通过受控 stdin/library/IPC 等方式满足，应更换接口，不能使用参数或环境变量传递。
- Planning Kernel 不读取 SecretValue；Application 只向规划提供 ProtectionMode、SecretSlotId 与 SecretRevision，并负责 binding existence/availability validation。MissingSecretBinding、SecretUnavailable、SecretStoreError 必须安全阻止；headless 不得弹窗等待或降低为 None/Privacy。
- `.backupignore`、`*.backupplan` 和源路径均视为不可信输入，防止路径穿越和归档条目逃逸。
- 首版不跟随任何链接；默认保留链接对象及 raw target，可配置为 Skip。未知 Reparse Point 与 Unix 特殊文件不遍历且必须报告。
- 解压/恢复前检测目标覆盖、路径穿越、磁盘空间和大小写冲突。
- 第三方归档工具在发布包中固定兼容版本并附带许可说明。

## 11. 测试策略

- **Core 单元测试**：规则语义、层级边界、路径规范化、ArchivePlan 稳定性，以及 ArchiveSpec 逐组件 inherit/override、explicit-same-value 与 per-unit effective fingerprint。
- **Application 测试**：变化原因、独立 baseline commit、History capture/retention 顺序、取消、失败补偿、预览与结果，schema validity/semantic validity/local readiness/capability 的错误分层，旧文档内存迁移与显式 upgrade，Import 幂等/IdentityConflict、whole-document Update 的 stable-ID diff 与原子性、state preserved/added/removed/modified、Clone 递归重写、authority conversion/registration relocation、registered PlanId drift，ArchiveSpec default 只影响继承单元、format 同时改变 archive/output fingerprint、compression/protection 只改变 archive fingerprint、effective capability/secret readiness，External required binding/kind/declared target/destination/boundary/collision、rules bypass、per-unit completeness 与 fingerprint 分类，MissingSecretBinding/SecretUnavailable/SecretStoreError、SecretRevision drift、headless 不降级，schedule reconcile/out-of-sync、schedule-only stale change 不阻止发布、SkipIfRunning，以及 History Enabled/Retention/OutputLayout/ExecutionBinding drift 的不同发布结果。
- **Infrastructure 集成测试**：UTF-8/BOM、严格 JSON、property 大小写/重复检测、各层 unknown property/enum/variant、schemaVersion dispatch、writer round-trip/原子替换，SQLite migration/backup、文件锁、Current/History relocation、跨文件系统 copy/verify、output collision/case semantics、跨 Plan root overlap、scheduler install/update/remove/status、DST/missed-run 映射与 native identity lifecycle。
- **Archiving 契约测试**：每种格式与 CompressionPreset 的 single-volume 创建、测试、恢复、加密、Unicode 和大文件；验证 versioned preset/metadata/protection capabilities、split/raw option 不可表示，且 SecretValue 不出现在参数、环境、日志或诊断输出。
- **跨平台测试**：Windows/macOS/Linux CI，大小写、权限、链接和长路径 fixture；Secret Store prototype 还必须覆盖 GUI 用户与 Task Scheduler/launchd/systemd timer/cron 的实际执行身份。
- **故障注入测试**：写入中断、空间不足、损坏归档、进程退出、External File→Link/Directory→Junction drift、staging copy/enumeration 不完整或 metadata mismatch、Current/History 移动失败。
- **架构测试**：验证项目依赖方向、Core 不引用 Avalonia/SQLite、Application 不引用外层项目，以及 ViewModel 不直接引用 SQLite。

初始完成标准是 `dotnet build` 和所有自动化测试通过；涉及布局或交互的变更还需 Avalonia UI 验证。

## 12. 工程与依赖治理

- `global.json` 以仓库创建时实际使用的 `10.0.400` 为基线，并在 .NET 10 内按 `latestFeature` 前滚；
- `Directory.Build.props` 统一目标框架、Nullable、ImplicitUsings、分析级别、警告策略和确定性构建；
- `Directory.Packages.props` 开启 NuGet Central Package Management，所有项目文件只声明包名，不重复声明版本；
- `.editorconfig` 统一 UTF-8、LF、缩进、namespace、using、`var` 和命名规则；
- GitHub Actions 在 Windows、Linux、macOS 上执行 restore、Release build 和 test；
- `StowCrate.Architecture.Tests` 持续验证项目依赖方向以及 UI、数据库依赖没有泄漏进内层。

## 13. 分阶段实现建议

1. 领域模型、路径与规则引擎、Archive Unit 树及计划预览；
2. 本地文件系统、SQLite、ZIP/7z 适配、原子 Current 发布；
3. 变更检测、独立 History、恢复与完整性验证；
4. Avalonia 配置树、智能项目识别、导入导出；
5. CLI 与系统调度器；
6. TAR.ZST、平台元数据与高级文件系统优化。

阶段顺序可调整，但不得用 GUI 直接调用压缩进程绕过 Application 层，也不得以云端集成替代本地可靠性。

# M5.2 retention durability boundary

迁移 Stage 在创建 archive temp 前检查实际目标父目录的 durability barrier，再重验 namespace/旧对象并响应取消；已知能力不足时不先复制。该检查不代替写入和 rename 后的持久化，不签发可缓存能力或 ownership proof。

只读迁移检查可通过 `InspectTargetsAsync` 绑定拟用 transaction ID，调用独立 `IStorageRelocationTargetNamespaceProbe` 检查 final/事务 temp 的字面冲突与物理占用，再重验配置和 metadata。Stage 在写入前检查全部 Pending 目标和 temp，避免已知后续冲突造成前项复制；不会清除未知文件，也不会把 Staged 所有权误判为空路径。该检查不替代真实 case/encoding capability 或授权 Begin。

迁移初次检查要求新目标根已存在，不自动创建；明确缺失时以 `StorageRelocationTargetRootMissingException` 和 `RELOCATION_TARGET_ROOT_MISSING` 提示用户创建，异常不携带设备路径。文件占位、链接或访问失败不能归类为缺失。已冻结 identity 的根在恢复中丢失仍是 drift，不重建；根内父目录沿用 Stage 的创建与 durability 规则。

M5.3 配置 stale check 使用独立的 `StorageRelocationConfigurationFingerprint` identity/layout 投影，不复用 backup execution fingerprint。名称、定时、过滤规则、压缩级别变化不使迁移失效；authority、registration、identity/layout drift 则拒绝继续，root/binding safety 与 journal CAS 仍在事务边界独立重验。schema v5 已持久化独立 configuration checkpoint，并在原子根切换事务内重验配置、物理目标和 expected metadata；不重解释旧 manifest。

M5.3 Plan-scoped Output Reorganization / Storage Relocation 协议见 [`plan/STORAGE-MAINTENANCE-v1.md`](plan/STORAGE-MAINTENANCE-v1.md)。全量目标 durable 后才可原子切换 metadata，旧副本清理在 commit 后；当前按协议分层实现，不能把 progress kernel 或 metadata-only port 当作已交付物理迁移。

完整显式恢复由 `StorageRelocationRecoveryWorkflow.ResumeAsync` 编排，要求 PlanId + 既有 TransactionId；依次恢复 Pending/Staged、seal、commit，再进入已提交 cleanup，不自动 compaction。失败后仅重读日志分类永久成功点，不在同一次调用内重放；无法确认状态返回 OutcomeUnknown。原启动入口仍不自动执行 pre-commit。

只读 inventory 在一致数据库事务中提取全部选定根的 retained Current/History 与 immutable integrity/length，不依赖当前 active/declared unit 集合，不读取原始 Source/External，也不创建 journal 或物理 proof。它与 Begin/恢复/commit/compaction 复用 namespace 占用检查，包含 External local binding；External 绑定保存和 Plan 激活/发布也必须尊重现有 relocation reservation。inventory 不等于 readiness，完整物理 preview 仍待接入。

`StorageRelocationInspectionWorkflow` 已把配置读取、metadata inventory、物理观察和配置/metadata 重验串为只读检查。物理端口捕获旧/新根和旧归档 native identity，逐项验证 SHA-256/length、根内 no-follow ancestors、目标不存在与目标目录容量，并在返回前重验全体 namespace/identity。缺少目标父目录只记录为尚不存在，不创建目录；已有目标即使 bytes 相同也拒绝。结果是 `StorageRelocationPhysicalInventory` 瞬时观察，不是 durable proof 或 Begin authority；目标 filesystem case/encoding collision、写入/barrier capability 与完整启动门槛仍需独立完成。

Application 的 StorageRelocationCapacityGuard 通过独立 probe 观察指定现存目录所在卷的当前调用用户可用 bytes，同卷需求合并、观察值取最小值，未知/失败/不足一律阻止且没有 override。Infrastructure 使用 native volume/device identity 与实际目录容量查询，前后重验根 identity；Stage 在创建任何目标目录/temp 前检查全部 Pending 复制需求。已 staged 的 rename 和旧副本 cleanup 不重复要求剩余容量。只读 inventory 可单独调用同一 guard；容量是瞬时下界检查，不是空间预留或完整 Preview。

Application 启动协调器枚举并完整校验所有 relocation 日志（含 inactive Plan），未提交状态只报告待显式恢复，已提交状态可通过注入物理清理端口逐项恢复；完成后仍保留 reservation。缺少适配器或可恢复错误报告 CleanupPending，损坏继续向上传播。存在 reservation 的 Plan 跳过旧 publish/retention recovery 与 History inventory，避免恢复入口绕过互锁。尚未装配 App/CLI 用户入口，也不自动释放路径。

独立 `CompactRelocationAsync` 仅接受 Completed + revision CAS，事务内重验新 binding/placement/reservation/互锁及只读物理 completion probe 后，原子移除本事务日志与 reservation。物理核验要求所有根、目标 identity/integrity、旧路径与 temp absence、目录 barrier；任何漂移或失败保留保护。该接口不删除文件、不回滚新 binding，不在启动恢复自动调用。

`ResumeRelocationEntryAsync` 为 pre-commit 单条目恢复提供事务形状入口：数据库写锁覆盖当前日志/配置/根/placement/reservation 校验、Stage 或 PublishTarget 与 proof 持久化。复制/发布前 CAS 拒绝竞争调用；成功 proof 后不可取消地落日志。复制后写库失败仍留下无 ownership 的 ambiguous temp，rename 后失败可根据已持久化 staged identity 补记 target。该安全边界会在单个大归档 I/O 期间占用 SQLite 写锁，是当前保守实现的吞吐限制；整条 Application 显式恢复编排已接入 ResumeAsync，用户入口尚未接入。

Root relocation 的前置检查必须与 backup execution readiness 分离：通过 authoritative document/registration 与 durable storage facts 验证迁移，不以 `ExecutionReadyArchive`、源扫描、FILE_MANAGED discovery、Secret Store 或归档解密能力为前提。原始 Source/External 离线不阻止已有归档搬迁；持久 local root safety facts 仍需重验，未知/冲突安全事实不得放行。旧/新 archive roots 与字节完整性必须现场验证。该许可不放宽正常备份的 source/secret readiness，也不允许 File-backed 文档缺失时回退缓存。

config.db v4 持久化 root relocation 的 immutable canonical manifest、版本化 progress 与 old/new root reservations；v5 在 Begin、staged proof、target proof 和 seal 之外增加带 durable configuration checkpoint 的原子 root commit，没有任意 progress overwrite 或提前 metadata switch。v6 增加逐项 OldCopyAbsent 与 COMPLETED 持久化，repository 在事务内重验新 binding/placement/reservation、调用物理清理并验证关联 proof；删除后写库失败可通过重新证明 absence 恢复。COMPLETED 仍保留全部 reservation 和互锁，独立 compaction 尚未开放。Begin 与所有冲突 mutation 在 SQLite 事务内检查互斥；inactive Plan 尚存 publish/retention/cleanup 恢复工作时，其根也不能被新迁移占用。journal/root projection 损坏必须阻止访问与冲突保存，不得解释成没有 reservation。

History retention 是独立于 publish commit 的 destructive maintenance。每个自动删除必须先有 artifact-level durable intent；filesystem deletion 与 SQLite placement removal 不能假装是一个跨系统事务，而由 `PREPARED` journal、物理 absence/integrity observation、directory metadata durability barrier 和原子 metadata completion 实现可恢复性。粗粒度 `MaintenanceState` 只汇总健康状态，不能授权具体路径删除。

未知 HistoryRoot 内容以及只有 identity/hash、没有活动 workflow journal 的 artifact 不具备删除或 metadata 修复授权。未来 History relocation 与同单元 active PREPARED retention intent 必须互斥。

M5.2 orphan reconciliation 是只读诊断面：对全部 active Plan 的 tracked placements 与 `history-v1` managed namespace 做 no-follow inventory，报告 missing、corrupt/replaced、known-unplaced 与 unknown/ambiguous，不从 inventory 产生 mutation。Retention destructive path 必须验证全部 ancestor，并在可用平台读取 native object identity、在最终 namespace deletion 前重验；race-resistance 保证限于检测正常替换漂移。

迁移目标比较能力：无法可靠识别目标文件系统的大小写或 Unicode 比较规则时，必须阻止迁移并返回 RELOCATION_TARGET_COMPARISON_UNAVAILABLE；不提供强制继续，不创建探测文件。检查必须覆盖全部 final/temp 及父目录的实际规则和待创建目录的继承语义，不以操作系统默认值或规范化 comparison key 代替。Preview 保持只读。

目标比较适配器增量：StorageRelocationTargetComparisonProbe 在 Linux x64/arm64 上，仅接受目录句柄 fstatfs 返回 ext2/3/4 magic 且 FS_IOC_GETFLAGS 未启用 casefold/fscrypt 的目录。通过同句柄 statx identity 与路径 no-follow identity 交叉验证；按真实目录逐级查询，含空根和嵌套挂载，不以根卷规则代替子目录。缺失子目录只按已验证的 ext 继承语义推导，不创建；两次完整观察发现目录新增/替换或能力变化即拒绝。全部 final/temp 使用严格 UTF-8 和 255-byte component 限制，复用完整 namespace 冲突校验，跨路径目录 identity alias 保守拒绝。其他平台、文件系统、casefold/fscrypt 或原生接口不可用仍返回 RELOCATION_TARGET_COMPARISON_UNAVAILABLE。此结果仅是本次只读比较检查，不是 durable proof；完整 Begin 接入仍须在启动前重验；Stage 的接入与重验时点见下文，不承诺跨文件瞬时快照。

比较能力已接入物理执行默认路径：Stage 在任何父目录/temp 创建前验证完整 manifest 布局，并在父目录创建及 pre-copy barrier 后再次验证；PublishTarget 在恢复入口、rename 前及 rename/barrier 后重验；VerifyForCommit 在全量 I/O 前后重验。VerifyLayoutAsync 只检查冻结布局及实际目录比较规则，不要求 final/temp 全部为空；ownership、no-overwrite 和 unknown-file 拒绝仍由原物理协议独立负责。rename 后的重验忽略 caller cancellation，失败保留原 staged ownership/journal，允许后续按同对象恢复。显式 Resume 对能力不可用返回 RELOCATION_TARGET_COMPARISON_UNAVAILABLE，并保留旧 binding/baseline/reservation；已提交 cleanup/compaction 不因新一次比较能力查询而获得或丧失删除授权，继续按既有 exact-object 协议执行。构造物理 store 未注入比较端口时默认使用原生适配器，因此不支持的平台不能绕过 Preview 直接复制。

显式 compaction 用例 StorageRelocationCompactionWorkflow.CompactAsync 要求 PlanId、transaction UUID 和调用方选定的正 revision；选择已变化时拒绝，不自动改用新日志/revision。仅 COMPLETED 可调用仓储清理事务，未完成或缺少 completion adapter 仅报告状态，不推进恢复。成功响应后直接返回 Compacted，不因后续 caller cancellation 改报失败；失败只用不可取消读取重新观察，精确原 COMPLETED 日志仍在则返回 Retained，缺失/替换/revision 变化/读取不可用则 OutcomeUnknown，不自动重试，也不从 absence 推断本次提交成功。初次读取无日志返回 NotFound，不等于 Compacted。损坏日志继续显式抛出，不降级为普通待处理状态。该用例不在 startup 或 Resume 后自动调用，不删除归档、不清理未知文件，App/CLI 用户入口仍待装配。

Manifest 编码 v2 契约：StorageRelocationIntent.ProtocolVersion 仍为 transfer 状态协议 1，manifest payload 的 Version 独立取 1 或 2（与已独立版本化的 progress/configuration 编码一致），不改变 config.db schema v6。v1 保留原始 ExecutionDigest 字段和 canonical bytes；只能作为历史事实保存，不能重新解释为配置指纹。v2 使用独立 closed-world DTO，完全不包含 ExecutionDigest，禁止 null/占位字段；新建 v2 必须在同一 Begin 事务冻结 configuration checkpoint，缺少 checkpoint 的 v2 即为损坏状态，不能补签。reader 先验证 payload hash 和版本，再分派严格 reader 并检查 canonical 重编码；拒绝未知/重复/错版字段和未来版本，不自动升级或改写 v1。旧程序不能识别 v2 时须保持其既有 fail-closed 行为；不得向旧程序承诺 v2 日志可恢复。

Application 新增目标目录持久化预检端口，Infrastructure 的 StorageRelocationPhysicalStore 实现现存目标根/父链 barrier 与前后 identity、namespace 重验，不创建文件或目录。屏障不可用明确拒绝；预检不是未来写入成功或 durable publication 的证明，不替代执行阶段的屏障。完整 Begin 编排仍待装配。

Application 已提供 StorageRelocationBeginWorkflow：内部完成启动预检及最终配置/metadata 重验，以 manifest v2 + checkpoint 调用一次原子 Begin，只返回 PREPARED。预检结果不公开为可缓存的启动凭证。调用后失败报告携带 transaction ID 的 OutcomeUnknown，不自动重试或复制；成功返回后的取消不覆盖成功。App/CLI 组合根仍待装配。

桌面存储维护预览已接入：App 组合根通过 Microsoft.Extensions.DependencyInjection 装配 MainViewModel 与 RelocationWorkspace。组合适配器调用既有配置库 opener、authoritative reader、path resolver 和 InspectTargets；ViewModel 只管理显示、选择、异步命令与取消，不构造基础设施或裁定迁移规则。检查在后台执行，结果回到界面线程更新。当前 UI 不调用 Begin/Resume/Compact，也不在打开库时自动执行归档恢复。

桌面恢复增量通过 RelocationWorkspace 装配已有 StorageRelocationRecoveryWorkflow.ResumeAsync，ViewModel 只处理事务选择、明确确认、状态显示与取消。只读 LoadJournal 与有副作用 Resume 分离；调用后清除可重用 UI 选择，不自动重放。应用用例继续拥有冻结日志校验、revision CAS、物理验证、提交及清理规则。打开配置库包含 inactive retained journal 的方案；不自动调用 startup recovery 或 compaction。
