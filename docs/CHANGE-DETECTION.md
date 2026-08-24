# Change Detection & Baseline Commit v1

本文是 StowCrate v1 变更检测、Committed Baseline 和提交时序的规范真相源。它定义纯领域行为，不定义 SQLite 表结构。

## 1. 十条核心规则

1. Change Detection 以 Archive Unit 为最小比较和提交单位。
2. Observed State、Candidate State 与 Committed Baseline 必须严格分离。
3. 只有成功验证、发布为 Current 并持久提交的 ArchiveVersion 才能成为 baseline。
4. Committed Baseline 随 Current ArchiveVersion 持久化在 `config.db`；`cache.db` 只用于加速。
5. Baseline 至少包含 EntrySet、Selection、ArchiveSpec 三类确定性 fingerprint。
6. 数据、选择语义或归档规格变化可要求 rebuild；调度、History retention 等设置不得触发重压缩。
7. Baseline 只允许在单元成功发布后前进，失败、取消和发布前状态不得推进。
8. 异常时宁可保守地额外 rebuild，不得在不确定时判定 Unchanged。
9. Incomplete Observation 默认禁止覆盖 Current 或提交 baseline。
10. 删除 `cache.db` 不得影响配置、Current、History 或 Committed Baseline 的正确性。

## 2. 三种状态

### Observed State

`SourceSnapshot + ScanIssue[]` 是 Scanner 本轮看到的临时物理事实。它不代表最终选择集合、执行成功或 baseline。

### Candidate State

Planning Kernel 根据 SourceSnapshot、BackupPlan、Archive Unit、Rules、LinkPolicy、Boundary 与 External Sources 产生每个 `PlannedArchive`，再派生用于比较的 Candidate Unit State。Candidate 表示“按本轮语义执行时应生成什么”，仍不是成功状态。

### Committed Baseline

Committed Baseline 是最近一个已验证、成功发布为 Current，并完成 durable metadata commit 的 ArchiveVersion 所对应的输入状态。Baseline identity 是稳定的 `PlanId + ArchiveUnitId`，不能用一次运行或整个 Plan 的成功状态代替。

若 B、D、F 中只有 B 和 F 成功，则只推进 B、F 的 baseline，D 保留旧 baseline；Plan 结果为 Partial Success。

## 3. Candidate 与 Baseline 指纹

### EntrySetFingerprint

表示最终进入单元归档的数据状态，按逻辑路径规范排序，至少包含：

- path、entry kind、size、UTC mtime；
- 按归档 metadata policy 保留的 metadata identity；
- LinkKind、raw target、target scope、dangling 与 directory-target 状态；
- 当前 Change Detection 模式要求的内容 hash。

### SelectionFingerprint

表示条目为何被选择，至少包含：

- rule/scanner/fingerprint semantics version；
- `SourceId` 与 Archive Unit 的 Source-relative logical path；
- authoritative、concrete 的 pinned Global Rules Snapshot，以及 Plan Rules、Local Rules、mode、case policy 与 resolved case sensitivity；
- LinkPolicy、Archive Boundary Tree；
- External Source 的逻辑 mapping、archive destination 与相关选择语义。

`PlanId`、`ArchiveUnitId`、`ExternalSourceId`、`DeviceId`、数据库 ID、authority、registration path，以及 Global Rule Library 的 ID、名称、revision/provenance 都不进入 SelectionFingerprint。它们是 identity、运行命名空间或 authoring metadata，不是归档内容选择语义。`PlanId + ArchiveUnitId` 仍是 baseline key；更换该 key 会因为没有对应 baseline 得到 `FirstBackup`，不需要把 identity 再编码进 SelectionFingerprint。

Archive Unit logical path 仍然进入 SelectionFingerprint：即使 identity 保持不变，路径变化也会改变来源与 manifest/Current 的逻辑结构。External Source 同理，进入 fingerprint 的是逻辑映射与归档目标，而不是 `ExternalSourceId` 自身。FILE_MANAGED 的 `@id` 文本虽然不作为独立 identity 字段进入 SelectionFingerprint，但 `.backupignore` 是本单元的保留归档内容；修改其 bytes 会自然改变 EntrySetFingerprint。

### ArchiveSpecFingerprint

表示相同输入应如何生成归档，至少包含：

- format、compression algorithm/level、solid mode、volume size；
- metadata preservation 与 protection mode；
- Secure protection 使用的 portable `SecretSlotId` 与当前设备 local `SecretRevision`；
- Privacy protection semantics version；
- manifest schema 与 archive semantics version。

`SecretValue`、secret-derived verifier/hash、OS `SecretReference`/locator、Secret Store provider/implementation、`DeviceId`、Privacy 每次执行随机生成的 key/nonce/recovery material bytes 都不进入 ArchiveSpecFingerprint。`SecretRevision` 由 StowCrate 在 Set/Replace/Rebind 有效 secret 时保守递增；即使新值与旧值相同也允许额外 rebuild，不得通过持久化 secret hash 判断相等。

调度、History retention、UI 状态、日志级别、CurrentRoot 和 HistoryRoot 不进入 ArchiveSpecFingerprint。输出根变化产生 Storage Relocation，而不是 Archive Rebuild。应用版本本身不进入 fingerprint；只有行为 semantics/schema version 变化才使 baseline 失效。

PlanAuthority、File-backed registration path 与 Managed/File-backed 转换也不进入 fingerprint。两种 authority 解析出的 Plan Snapshot 语义相同时不得 rebuild；File-backed 文档或已解析的外部规则源在运行中变化则触发 PlanChangedDuringRun。

`InputFingerprint = SHA256(EntrySetFingerprint + SelectionFingerprint)`。如需要聚合 rebuild identity，则由 InputFingerprint 与 ArchiveSpecFingerprint 组合。领域 API 必须使用强类型 fingerprint，不能以可互换的裸 `string` 表达。

所有 fingerprint 使用 SHA-256 和显式版本化的 Canonical Fingerprint Encoding。encoding 固定字段 ID、顺序、长度和 UTF-8 值；不得使用 `GetHashCode()`，也不得直接依赖 JSON serializer 输出。未知 fingerprint/semantics version 视为 BaselineInvalid，并保守 rebuild。

## 4. Standard 与 Strict

v1 只有两个模式：

- `Standard`（默认）：比较 path、kind、size、mtime、metadata 和 Link raw target；可按版本化策略计算或复用快速内容 hash。它可能漏掉 bytes 已变化但 size、mtime、metadata 完全相同的文件，产品必须明确披露。
- `Strict`：每轮为候选普通文件重新读取内容并计算 hash；没有可信 Change Journal 时不得仅凭 metadata 复用旧 hash。

USN Journal、FSEvents、inotify 等只提供可信变化提示和性能优化，必须有全量扫描回退。具体快速内容 hash 算法仍是未决实现选择，并必须版本化。

File Change Hash 与 Archive Integrity Hash 是不同类型：前者服务于文件内容变化判断，可选择高速算法；后者验证最终归档完整性，使用 SHA-256。

## 5. Change Decision

API 不得只返回 `bool HasChanged`。结果至少包含：

```text
ChangeStatus
  FirstBackup
  Unchanged
  RebuildRequired
  BlockedByIncompleteSource

ChangeReason
  NoBaseline
  EntrySetChanged
  RulesChanged
  BoundaryChanged
  LinkPolicyChanged
  ExternalSourceChanged
  ArchiveFormatChanged
  CompressionChanged
  EncryptionChanged
  ManifestSchemaChanged
  SemanticsVersionChanged
  Forced
  BaselineInvalid
  PlanChangedDuringRun
```

比较顺序为：检查 baseline → semantics version → Selection → EntrySet → ArchiveSpec。任一必要 identity 缺失、损坏或版本不支持时不得判定 Unchanged。

Unchanged 只表示无需重建归档，不表示无需扫描。没有可信 journal 时仍执行 SourceScanner。

## 6. Scan Completeness 与发布资格

ScanIssue 除 severity 外还必须映射到发布所需的 completeness impact：

- `None`：不影响候选集合完整性；
- `IntentionalSkip`：由已知 v1 safety/policy 明确排除，例如 LinkPolicy Skip、文件系统边界或已识别但不支持的 Special；
- `IncompleteObservation`：AccessDenied、扫描中消失、目录枚举失败、metadata 无法读取等，无法证明候选集合完整。

IntentionalSkip 可以发布并提交 baseline，但必须在预览、结果和 manifest 中明确报告。IncompleteObservation 默认 `AllowIncompletePublish = false`：允许生成 Preview/ArchivePlan，但不得覆盖 Current 或提交 baseline。首次备份同样显示 BlockedByIncompleteSource，不能宣称完整成功。

允许不完整发布若未来成为高级选项，必须是显式、可审计的单次或方案策略；v1 默认行为不因无头运行而放宽。

## 7. Baseline Commit 协议

```text
Scan / Candidate / Change Decision
  → Write .partial
  → Archive Test + Integrity Verification
  → Persist and verify previous Current as History（启用时）
  → Revalidate ExecutionSemanticSnapshot
  → Atomic Publish Current
  → Durable ArchiveVersion / CurrentVersion commit in config.db
  → Baseline committed
  → Refresh disposable cache.db
```

概念状态为 `Prepared → Verified → Published → Superseded`，失败可进入 `Failed`；只有 Published 可以作为 baseline。这些是领域/事务语义，不预先规定 SQLite schema。

运行开始时捕获 `ExecutionSemanticSnapshot`，发布前再次检查。它至少包含 Managed Plan 的 PlanRevision（适用时）、PlanSemanticFingerprint、所有已解析外部规则源的 fingerprint，以及 Secure protection 实际解析的 `SecretSlotId + SecretRevision`。FILE_MANAGED 的 `.backupignore` 或 secret revision 从规则解析/规划到发布前只要发生变化，即按 PlanChangedDuringRun 处理，默认不发布、不提交 baseline。这里的 reason 覆盖整个已解析执行配置，不只覆盖 `*.backupplan`。

外部规则源 fingerprint 必须基于运行实际读取的文件 bytes 和版本化解析语义确定，不能只依赖 mtime。即使一次变化解析后得到相同规则，也不得让本轮以过期的规则源观察结果发布。执行层还必须按 `FILESYSTEM.md` 重新验证关键 path/kind/metadata，防止 TOCTOU 类型替换。

`cache.db` 永远不能领先 Current 和 `config.db` durable state。cache 丢失或落后只导致重新扫描/hash；不能导致 baseline、Current 或 History 丢失。

## 8. Crash Matrix

| 崩溃点 | Current | Baseline | 恢复语义 |
|---|---|---|---|
| Scan/Candidate 后 | 旧 | 旧 | 丢弃临时状态 |
| `.partial` 写入或验证中 | 旧 | 旧 | 隔离/清理 partial，保留诊断 |
| 验证后、Publish 前 | 旧 | 旧 | 不提交 baseline |
| Publish 后、durable commit 前 | 新 | 旧 | 启动 Reconciliation，依据 Current manifest 与 pending version 恢复；不得假装已提交 |
| durable commit 后、cache 更新前 | 新 | 新 | 安全；重建 cache |

最安全的失败后果是额外 rebuild，而不是错误 Unchanged。

## 9. 持久化边界

`config.db` 保存 BackupPlan durable configuration、ArchiveVersion、CurrentVersion 引用、三类 baseline fingerprint、semantics version、PublishedAt 和必要审计。它不需要永久保存数百万条 baseline entry。

`cache.db` 可保存 FileHashCache、MetadataCache、ScanCache、PlatformCursor 和 journal state。删除 cache 后必须仅表现为性能下降。Standard 可在 metadata identity 未变时按策略复用 hash；Strict 在没有可信 journal 证明时不得复用。

Change Detector 位于 Core 或 Application 的纯逻辑边界，只接收 Candidate 与 Baseline。Application 负责从端口加载 CurrentVersion、协调比较与提交；Infrastructure 后续实现持久化端口。Change Detector 不执行 SQL，也不决定 History retention。

## 10. 规范测试矩阵

至少覆盖：无 baseline、完全一致、增删文件、size/mtime/link target、Rules/Boundary/LinkPolicy/External Source、格式/压缩/ProtectionMode/SecretSlotId/SecretRevision/Privacy semantics/manifest schema、secret reference/provider 变化不触发、Privacy 随机材料不触发、非归档设置不触发、输入顺序稳定、semantics version、invalid baseline、Standard/Strict、cache 丢失、partial unit success、失败/取消/发布前不提交、stale plan、运行中 `.backupignore` 或 SecretRevision 变化、identity-only 变化不改变 SelectionFingerprint、logical path 变化会改变 SelectionFingerprint、Incomplete Observation 阻止与 IntentionalSkip 允许。

## 11. 与现有仓库的差异和迁移约束

本建议稿与现有规范没有不可兼容的产品冲突，但存在以下实现或阶段差异；本文不得被理解为这些能力已经完成：

1. `ARCHITECTURE.md` 第 13 节旧的阶段建议把 SQLite/归档适配放在 Change Detection 实现前。当前只调整**设计顺序**为 Change Detection → Backup Plan → Persistence；不提前实现数据库，也不宣称执行链已完成。
2. 当前 Core 的 Archive Unit 主要以逻辑 root 表达，尚未实现正式、稳定、可持久化的 `ArchiveUnitId`。Backup Plan v1 已确定该 identity；实现时不能临时用数据库行号或物理绝对路径代替。
3. 当前 `ArchivePlan.Fingerprint` 是一个聚合字符串，尚未拆成强类型 EntrySet/Selection/ArchiveSpec/Input fingerprint。未来实现需提供显式迁移与 fingerprint format version，不能把现有值误当 v1 durable baseline。
4. `FILESYSTEM.md` 已允许 Warning 后继续规划；本文进一步区分 IntentionalSkip 与 IncompleteObservation，用于决定能否发布。这是发布层收紧，不改变 Scanner 的 no-follow 或 issue severity 事实。
5. 当前仓库尚未实现 External Source、ArchiveSpec、Secret revision、ArchiveVersion、Current 发布或 Reconciliation。本文只定义这些边界，不得为满足文档而引入临时 SQLite schema。
