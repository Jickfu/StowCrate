# Backup Plan v1 Domain Freeze Review

## Result

**PASS WITH REQUIRED FIXES APPLIED**

Review 发现 4 个需要在冻结前收口的问题：External observation 缺少独立纯数据边界、portable reference graph 缺少集中验证规则、History inherit/explicit 的 effective fingerprint 规则不完整、TarZstd enum 仍被 capability prototype 条件化。以上修复已经同步进入规范；当前没有 schema-shaping blocker。

结论：**Backup Plan v1 Domain Frozen / Ready for JSON Schema Design**。本结论冻结领域语义，不表示 JSON Schema、Document DTO、reader/writer、Archiver capability 或 SQLite persistence 已实现。

## Normative Documents Reviewed

- `docs/BACKUPPLAN.md`
- `docs/BACKUPIGNORE.md`
- `docs/CHANGE-DETECTION.md`
- `docs/FILESYSTEM.md`
- `docs/ARCHITECTURE.md`
- `docs/PRODUCT.md`
- `AGENTS.md`

## Domain Aggregate Inventory

| Aggregate/value | Portable role | Resolution/runtime role |
|---|---|---|
| BackupPlan | 完整 desired configuration aggregate | 解析为 immutable ResolvedPlanSnapshot |
| BackupSource | SourceId、display metadata、SourceOutputPath | SourceId → SourceRoot binding → SourceSnapshot |
| ArchiveUnitDeclaration | ArchiveUnitId、SourceId、logical path、RuleSource、ArchiveSpecOverride、HistoryOverride | 与 discovery/registration 合成为 resolved ArchiveUnit |
| SecretSlot | Plan-scoped logical secret requirement | SecretSlotId → device-local SecretBinding/SecretRevision |
| ExternalSourceDeclaration | ExternalSourceId、Name、Kind、TargetArchiveUnitId、ArchiveDestination | binding → ExternalSourceSnapshot → mapped Candidate entries |
| ArchiveSpecDefault/Override | authored portable inheritance | 每单元 EffectiveArchiveSpec |
| History default/override | authored portable inheritance | 每单元 effective Enabled/RetentionPolicy |
| ScheduleIntent | portable desired schedule | device-local ScheduleInstallation reconciliation |

`SourceSnapshot` 与 `ExternalSourceSnapshot` 是不同强类型 observation boundaries。它们可复用相同纯 entry value types 和 filesystem enumeration primitive，但 External snapshot 不伪装为 BackupSource、没有 rules/discovery 语义，也不携带 physical/staging/OS objects。

## Identity / Reference Matrix

| Identity | Defined by | References | Update/Clone lifecycle |
|---|---|---|---|
| PlanId | BackupPlan | local registration/runtime namespace | Update 保持；Clone 新建 |
| SourceId | BackupSource | ArchiveUnitDeclaration | same-ID state 保留；Clone 全部重写 |
| ArchiveUnitId | declaration、FILE_MANAGED `@id` 或 local discovery registration | External target、baseline/current/history | Update 按 ID 对应；Clone 重写引用 |
| ExternalSourceId | ExternalSourceDeclaration | local external binding/provenance | identity-only migration不 rebuild；Clone 新建且不复制 binding |
| SecretSlotId | SecretSlot | Secure default/override、local SecretBinding | Clone 新建且不复制 binding |

Semantic validation 在 local binding 前拒绝 duplicate identity、dangling ArchiveUnit.SourceId、dangling ExternalSource.TargetArchiveUnitId、dangling Secure.SecretSlotId 和强类型错误。Target External unit 必须是 declared unit。PlanId/child ID 不由名称、路径、数组位置或数据库键推导。

## Portable vs Local State Matrix

| Portable Configuration | Device Local / Runtime State（不得进入文档） |
|---|---|
| IDs、names、logical paths、SourceOutputPath | DeviceId、registration path |
| declarations、pinned rules、ArchiveSpec、History policy | SourceRoot、CurrentRoot、conditional HistoryRoot |
| External Kind/target/destination | External physical binding、staging path |
| SecretSlot/Protection intent | SecretReference、SecretRevision、SecretValue |
| ScheduleIntent | native scheduler identity/config/status |
| ChangeDetection intent、semantics versions | ArchiveVersion、CurrentVersion、History、Baseline、cache/run state |

跨设备只需 portable document 加显式 local bindings 即可达到 PlanReady；不存在未归属的第三类配置。缺少 binding 是 PlanNotReady，不使 document invalid。

## Fingerprint Classification Matrix

| Field/semantic | PlanSemantic | ExecutionSemantic | ExecutionBinding | Selection | EntrySet | ArchiveSpec | OutputLayout | Schedule |
|---|---|---|---|---|---|---|---|---|
| SourceId + unit logical path | 是 | 是 | 否 | 是 | 间接 | 否 | 间接 | 否 |
| PlanId/ArchiveUnitId identity | 是 | identity only | 否 | 否 | 否 | 否 | 否 | 否 |
| ExternalSourceId/Name | 是 | identity/display only | 否 | 否 | 否 | 否 | 否 | 否 |
| External Kind/destination/mapping | 是 | 是 | 否 | 是 | path/kind | 否 | 否 | 否 |
| External physical binding | 否 | 否 | 是 | 否 | observed data另算 | 否 | 否 | 否 |
| Rules/Boundary/LinkPolicy | 是 | 是 | 否 | 是 | selected entries另算 | 否 | 否 | 否 |
| SourceOutputPath/encoding | 是 | 是 | destination capability | 否 | 否 | 否 | 是 | 否 |
| Effective Format | 是 | 是 | capability identity | 否 | 否 | 是 | extension | 否 |
| Compression/Protection/SecretRevision | portable intent（revision 除外） | 是 | Secret binding locator 否 | 否 | 否 | 是 | 否 | 否 |
| History authored inherit/override | 是 | effective Enabled only | effective HistoryRoot | 否 | 否 | 否 | 否 | 否 |
| RetentionPolicy | 是 | 否 | 否 | 否 | 否 | 否 | 否 | 否 |
| ScheduleIntent | 是 | 否 | 否 | 否 | 否 | 否 | 否 | 是 |
| authority/document path/formatting/schemaVersion | 否（若 resolved semantics 相同） | 否 | registration check only | 否 | 否 | 否 | 否 | 否 |

Plan semantic 表示完整 authored desired configuration；execution/archive fingerprints 使用 defaults-expanded effective semantics。运行中 PlanSemantic 变化后重新解析，只有相应 per-unit ExecutionSemantic/Binding drift 才阻止该单元发布。

## Inheritance / Effective Semantics Matrix

| Change | PlanSemantic | Effective execution | Required action |
|---|---|---|---|
| ArchiveSpec explicit X → inherit，default仍 X | 变 | 不变 | 不 rebuild、不取消相同单元 |
| ArchiveSpec default X → Y，unit explicit X | 变 | 不变 | 该单元不 rebuild |
| ArchiveSpec default X → Y，unit inherit | 变 | 变 | ArchiveSpec rebuild；Format 还改变 OutputLayout |
| History explicit policy → inherit，default相同 | 变 | 不变 | 不阻止发布，不运行无意义维护 |
| effective Retention 改变、Enabled不变 | 变 | execution 不变 | commit 后 maintenance/reconcile |
| effective History Enabled 改变 | 变 | 变 | 运行中 drift 阻止发布 |

FILE_MANAGED 的 `.backupignore` 只负责 unit existence、optional `@id`、RuleMode、CasePolicy 与 Local Rules。Plan declaration 只负责 identity/source/path association、ArchiveSpecOverride、HistoryOverride 与 External targetability；SQLite 不保存第二份 authoritative rules。

## Validation / Readiness Matrix

| Layer | Examples | Effect |
|---|---|---|
| Encoding/parse | invalid UTF-8、MalformedJson、duplicate property | InvalidDocument，零状态修改 |
| Closed schema | missing/unknown property、enum/variant、schemaVersion | InvalidDocument / UnsupportedSchemaVersion |
| Semantic/reference | duplicate IDs、dangling refs、invalid paths、rule-source conflict | SemanticValidationFailed，不能 binding 补救 |
| Identity/authority | IdentityConflict、AuthorityConflict、RegistrationConflict | 需要显式 Update/Clone/Convert/Relocate/Cancel |
| Local readiness | Missing Source/Current/History/External/Secret binding | valid document + PlanNotReady |
| Capability | unsupported effective format/protection/metadata | UnsupportedArchiveCapability |
| Observation | access/kind/TOCTOU/staging mismatch | IncompleteObservation，阻止对应 unit publish |
| Runtime drift/failure | PlanChangedDuringRun、writer/test/hash/publish failure | 不发布、不推进 baseline |
| Maintenance | retention cleanup/scheduler reconcile failure | valid Current/config + warning/out-of-sync |

## Current / History / Version Consistency

- Format change：ArchiveSpec rebuild + extension/OutputLayout change；新 artifact 验证、可选 old Current history capture、Current publish、durable version/baseline commit 后才清理旧 path。
- SourceOutputPath/encoding-only change：OutputReorganization，不重压缩、不生成新 ArchiveVersion、不推进 baseline。
- CurrentRoot/HistoryRoot change：StorageRelocation，copy/stage + SHA-256 verify 后提交 binding；不生成 version、不推进 baseline。
- History capture failure 阻止 Current replace；retention maintenance failure 只产生 warning/out-of-sync。
- removed unit state 保留为 inactive recovery state，purge 是显式 destructive operation。

## Cross-document Conflicts Found and Fixes Applied

1. **External observation boundary 缺口**：此前只写 SourceSnapshot + External Sources，容易把 external tree 冒充 BackupSource。已引入独立强类型 ExternalSourceSnapshot，并规定与 Candidate 的合流边界。
2. **Reference graph 分散**：ID 生命周期存在，但 dangling/type validation 未集中。已增加完整 portable reference graph semantic validation。
3. **History inheritance fingerprint 不完整**：ArchiveSpec 已区分 authored/effective，History 未明确。已统一 inherit/explicit-same-policy 与 effective Enabled/Retention 行为。
4. **TarZstd enum 条件化**：此前称 Schema freeze 前可能移除，使 schema shape 未冻结。现固定为 portable Format intent；实现不可用属于 capability failure。
5. **Product 未决项陈旧**：移除已经确定的 History retention/default 未决表述，保留真正的实现/capability/Schema 后续项。

未发现 FILE_MANAGED 双真相源、Boundary 穿透、Current/History publish、relocation/reorganization 或 portable/local ownership 的剩余冲突。

## Explicitly Deferred v1 Features

以下不属于 Backup Plan v1 Schema，不阻塞冻结：split volume/Artifact Set、optional/glob/multi-root/generated/remote External Source、external rules/nested units/overlay、arbitrary cron、configurable metadata/backend raw archive parameters、Privacy carrier、Secure Recovery Package、device binding export、ephemeral execution、VSS/APFS/LVM snapshot、云存储与 hooks。

具体 OutputPathEncoding、fast hash、compression preset backend mapping、Secret transport、native scheduler representation、History physical naming、manifest fields 和 SQLite schema 是版本化实现/后续 artifact 设计；它们已有明确领域输入输出与安全失败边界，不改变 v1 document aggregate shape。

## Schema Readiness Decision

可以开始 JSON Schema structure design，但必须遵守：

1. schema 是 frozen domain 的 closed-world projection，不是 EF/SQLite DTO；
2. `schemaVersion` 必填，所有对象禁止未知字段，enum/variant closed；
3. defaults、inherit/explicit、reference validation 与 semantic mapping 不能只依赖结构校验；
4. local/runtime state 不得因实现方便进入 document；
5. 本次 review 不授权创建 JSON Schema 或实现 reader/writer/persistence。
