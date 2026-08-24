# StowCrate 技术架构

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

File-backed loader 必须先按 UTF-8 与严格 JSON 读取并检测 duplicate property，再以必填正整数 `schemaVersion` 分派到 version-specific closed-schema reader、semantic validator 和 in-memory migrator。未知 property/enum/variant 或未来 schemaVersion 安全失败；Infrastructure 不得用 case-insensitive property binding、extension bag 或最新 DTO 猜测旧/新文档。Application 只接收迁移后的 current semantic model，并继续执行 authority、local binding、capability 与 readiness resolution。

```text
Raw bytes → strict parse → versioned schema/semantic validation → in-memory migration
          → authority resolution → local binding → ResolvedPlanSnapshot
          → capability/readiness → execution
```

读取、预览、Register 或运行不得自动升级 File-backed 文件。显式 upgrade/save 通过 Application 用例协调 preview；writer 投影合法 document 后执行 schema validation、临时写入、round-trip read/validate 和原子替换。Managed Import 可在不修改来源文件的前提下迁移到当前内部模型。

Import/Update 的单位是完整 Plan aggregate，v1 没有 automatic、field、partial 或 three-way merge。Application 只按稳定 ID 匹配对象；Update 在确认 semantic diff 后原子替换 portable configuration，并把相同 identity 的 local/runtime state 保留给 Change Detector 重新验证。新增 identity 没有 binding/baseline；removed identity 的 state/artifacts 转为 inactive recovery state，任何 purge 都是独立破坏性用例。scheduler、binding、output/history relocation/maintenance 等副作用只在 config commit 后独立协调。

同一 DeviceId/PlanId 只能有一个 authority/registration。Managed ↔ File-backed conversion、第二个 File-backed path 的 registration relocation 都必须显式确认；File-backed 文档原地改变 PlanId 时安全进入 `RegisteredDocumentIdentityChanged`，不能让 registration 偷换 identity。Clone 递归重写全部 portable IDs 和引用，不复制任何 local/runtime state。

Portable identity 使用强类型 UUID v4 `PlanId`、`SourceId`、`ArchiveUnitId`、`ExternalSourceId`、`SecretSlotId`。Application 将 portable declaration 与当前 DeviceId 下的 Local Binding 合成为 `ResolvedPlanSnapshot`；Core 不读取 DeviceId、hostname、环境变量、registration path、OS SecretReference 或数据库键。

`ResolvedPlanSnapshot` 携带 authoritative 的 pinned Global Rules Snapshot，并为 Scanner 提供已验证的 local binding。Global Rule Library 属于 Application/Infrastructure 的 authoring facility，运行时不得 live-reference 本机 library。扫描后，Application 再把 Archive Unit declarations、物理 discovery、`.backupignore` metadata/rules 与本机 registration 合成为 resolved units。Declared/Discovered origin、Plan authority 和规则文件物理路径不进入 Planning Kernel。

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

v1 path expression 只允许受控 `${HOME}` anchor，不把任意 process environment 作为隐式输入。解析后必须绝对化、physical canonicalize 并验证 Source/Current/History 两两不重叠。缺少 required binding 时不得进入 Scanner。

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
7. 解析外部源映射和归档内逻辑路径，检测路径冲突。
8. 由 SourceOutputPath、Archive Unit logical path、format extension 与版本化 OutputPathEncoding 生成 destination-safe Current relative path；按目标文件系统真实 case semantics 检测所有 output collision。
9. 生成规范排序的 entries、警告、预估与变更/输出布局指纹。
10. 只有经过用户确认或无头策略允许的计划才进入执行阶段。

路径匹配器必须独立测试以下情况：不同分隔符、根路径、`!`、`*`、`**`、尾随斜杠、Unicode、大小写敏感文件系统和嵌套边界。Scanner 实现时还必须独立测试符号链接循环及逃逸 Source 的链接。

## 5. 执行与原子发布

单个 Archive Unit 的执行状态机：

```text
Planned
  → Staging (仅外部源/清单等需要时)
  → Writing <name>.<format>.partial on Current filesystem
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

- 新归档的 `.partial` 必须创建在 `CurrentRoot` 所在文件系统，确保最终发布可使用同文件系统原子替换；
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
- `config.db` 的灾难恢复副本通过 SQLite Online Backup API 生成一致的 `config.snapshot.db`。
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

ArchiveVersion 的 durable identity 与物理绝对路径分离：概念上记录 VersionId、ArchiveUnitId、StorageSlot（Current/History）、RelativeStoragePath、SHA-256、Size、PublishedAt 等；实际位置由当前 StorageRoot binding + relative path 解析。relocation 只改变 binding/location，不生成新 version 或推进 baseline。本段不预先规定 SQLite schema。

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
- **Application 测试**：变化原因、独立 baseline commit、History capture/retention 顺序、取消、失败补偿、预览与结果，schema validity/semantic validity/local readiness/capability 的错误分层，旧文档内存迁移与显式 upgrade，Import 幂等/IdentityConflict、whole-document Update 的 stable-ID diff 与原子性、state preserved/added/removed/modified、Clone 递归重写、authority conversion/registration relocation、registered PlanId drift，ArchiveSpec default 只影响继承单元、format 同时改变 archive/output fingerprint、compression/protection 只改变 archive fingerprint、effective capability/secret readiness，MissingSecretBinding/SecretUnavailable/SecretStoreError、SecretRevision drift、headless 不降级，schedule reconcile/out-of-sync、schedule-only stale change 不阻止发布、SkipIfRunning，以及 History Enabled/Retention/OutputLayout/ExecutionBinding drift 的不同发布结果。
- **Infrastructure 集成测试**：UTF-8/BOM、严格 JSON、property 大小写/重复检测、各层 unknown property/enum/variant、schemaVersion dispatch、writer round-trip/原子替换，SQLite migration/backup、文件锁、Current/History relocation、跨文件系统 copy/verify、output collision/case semantics、跨 Plan root overlap、scheduler install/update/remove/status、DST/missed-run 映射与 native identity lifecycle。
- **Archiving 契约测试**：每种格式与 CompressionPreset 的 single-volume 创建、测试、恢复、加密、Unicode 和大文件；验证 versioned preset/metadata/protection capabilities、split/raw option 不可表示，且 SecretValue 不出现在参数、环境、日志或诊断输出。
- **跨平台测试**：Windows/macOS/Linux CI，大小写、权限、链接和长路径 fixture；Secret Store prototype 还必须覆盖 GUI 用户与 Task Scheduler/launchd/systemd timer/cron 的实际执行身份。
- **故障注入测试**：写入中断、空间不足、损坏归档、进程退出、Current/History 移动失败。
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
