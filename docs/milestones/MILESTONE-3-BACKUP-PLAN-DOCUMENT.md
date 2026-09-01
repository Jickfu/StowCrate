# Milestone 3 — Backup Plan Document Runtime

## 目标

把已经冻结的 Backup Plan v1 portable document contract 落为安全、严格且与领域/持久化边界分离的运行时入口：

```text
UTF-8 bytes / Stream
  → strict JSON lexical validation
  → schemaVersion dispatch
  → Draft 2020-12 structural validation
  → BackupPlanDocumentV1 DTO
  → semantic validator + frozen domain mapper
```

## 已完成：Document Contract Runtime

- `BackupPlanDocumentV1` 及完整 nested DTO 位于 Infrastructure document adapter boundary；
- 仓库唯一正式 `schemas/backupplan-v1.schema.json` 作为 Infrastructure embedded resource；
- strict UTF-8 reader 支持 BOM，并拒绝 invalid UTF-8、comments、trailing comma、malformed JSON；
- 在 DOM/DTO 创建前检测任意 object 内 duplicate property；
- case-sensitive closed-world property matching；
- 在 v1 反序列化前完成 `schemaVersion` dispatch，未知未来版本返回 `UnsupportedSchemaVersion`；
- JsonSchema.Net 的验证结果转换为 StowCrate 自有结构化 document error；
- valid/invalid Schema fixtures 与 strict-reader 边界测试全部纳入自动测试；
- `schemaVersion = 1` 且未知 semantics pin 在结构层保持合法。

## 已完成：Semantic Validator + DTO→Frozen Portable Domain Mapper

- 新增独立 `StowCrate.Core.BackupPlans` portable authored aggregate，不复用或改变 M1 `Planning.BackupPlan`；
- 使用强类型 UUID v4 identity，并保留 ArchiveSpec/History default、override 与 inherit authored intent；
- Infrastructure DTO 显式映射到 Core，不泄漏到 Application，也不解析为 device/runtime snapshot；
- semantics pins 先于内容解释检查，当前只支持 rules/archive/outputPathEncoding `1`；
- Core semantic validator 检查 typed-ID uniqueness、reference graph、normalized unit declaration uniqueness；
- rule grammar 复用 Core `BackupRule`/`GlobPattern`；
- 检查 canonical Schedule trigger 重复、External portable ownership collision 与已声明 child Archive Boundary；
- semantic error/result 与 lexical/schema document error 保持独立；
- semantic-invalid fixtures 均先证明通过 Draft 2020-12 Schema，再证明被预期语义拒绝。

## 已完成：Document Writer + deterministic round-trip

- Domain→Document 使用显式 `BackupPlanDocumentV1Projector`，Core 不感知 JSON；
- writer 输入只能是通过现有 semantic validator 的 `PortableBackupPlan`，不改写 unsupported semantics pins；
- Rule arrays 保留 authored order，其余 aggregate arrays、Schedule triggers 和 weekdays 使用 frozen canonical ordering；
- lower-camel enum/discriminator string 显式配置，并由生成文档 Schema validation 覆盖；
- formatting 固定为 UTF-8 no BOM、2-space、LF、final newline、lowercase UUID、`HH:mm`、稳定 escaping/property order；
- 未配置 canonical public Schema URI 前不输出 optional `$schema`；
- 每份生成 bytes 返回前必须通过同一 strict reader 与 embedded Draft 2020-12 Schema；
- round-trip 测试覆盖 semantic preservation、canonical idempotence、collection permutation invariance、Rule order 与 authored override/inherit distinction；
- bytes、同步 Stream 与异步 Stream writer 已完成，不包含 path-level replacement。

## 已完成：Application ResolvedPlanSnapshot Resolution Contract

- `ResolvedPlanSnapshot` 固定为 device-resolved pre-observation immutable execution configuration，不表示最终 execution-ready；
- Application 新增 `DeviceId`、`ResolvedPhysicalPath`、immutable binding/root facts、resolution result/issues 与 pure resolver contract；
- raw expression/`${HOME}`/physical canonicalization 保持在未来 Infrastructure binding resolver；
- Source、CurrentRoot、External binding 是本阶段 required，HistoryRoot 与 Secret revision facts 不提前条件阻塞；
- Core 提供唯一 ArchiveSpec/History default+override/inherit resolution primitive；
- declared UI_MANAGED unit 携带已知 LocalRuleSet，FILE_MANAGED 只形成无伪造 LocalRuleSet 的 prepared declaration；
- snapshot 携带 effective `DefaultUnitPolicy` 供后续未声明 FILE_MANAGED discovery 使用；
- PlanAuthority、registration、Document DTO、SQLite identity、Schedule/description/provenance 不进入 snapshot；
- External 仅完成 physical input + target mapping，不产生 observation/staging state；
- 单 Plan root safety 与纯 `ActivePlanRootFacts` 跨 Plan writable overlap 检查已完成。

## 已完成：Source/External Observation + Archive Unit Resolution Contract

```text
ResolvedPlanSnapshot
+ typed SourceObservationSnapshot[]
+ ExternalSourceSnapshot[]
+ .backupignore discovery
+ local ArchiveUnit registration facts
→ ResolvedArchiveUnitSet
```

- `.backupignore` parser 以兼容旧 API 的完整 parse result 返回 optional canonical UUID-v4 `@id` 与 RuleSet，且绝不修改规则文件；
- Source/External observation 使用 typed portable identity 并与旧 M2 `SourceScanner`/`ArchivePlanner` API 隔离；Source observation 只表达 filesystem facts、case、issues/completeness，External 使用独立 no-follow snapshot；
- FILE_MANAGED identity 合成显式验证 `@id`、declaration、local registration 与 path 的全部矛盾；新 identity 通过 injectable UUID-v4 generator 生成并返回 pending durable registration；
- UI_MANAGED rules 只来自 prepared declaration，FILE_MANAGED rules 只来自实际 marker observation；resolved unit 已具有 effective rules、ArchiveSpec、History 与 rule-source observation fingerprint；
- 最终 discovered unit set 建立 typed parent/child boundaries，并重新检查 External destination 穿越新发现 child boundary；
- observation issues、ArchiveUnit resolution issues 与 document/semantic/binding errors 保持分层；incomplete observation 不产生 complete resolved set。

## 已完成：Candidate Archive Composition + Execution Readiness

- `ResolvedPlanSnapshot` 携带 authoritative Rules/Archive/OutputPathEncoding semantics pins；
- Candidate composition 与 Execution Readiness 是两个独立 pure stages；
- normal selection 按 Safety、reserved namespace、LinkPolicy、EffectiveRuleSet，并停止于 direct child boundary；FILE_MANAGED own control entry 强制保留；
- External File/Directory observation 映射为 explicit inclusion，不经过 Rules；
- normal/external/generated actual owners 在统一 archive-path ownership validation 中检查 same-path 与 non-directory ancestor collision；
- `__stowcrate__/manifest.json` 由无 runtime bytes 的 `GeneratedMetadataPlan` 预留；
- v1 source/unit/format mapping在 Application 决定 per-unit OutputRelativePath 并检查同 Plan logical output collision；
- Readiness 仅对实际 units 检查 conditional HistoryRoot、Secure SecretSlotId+SecretRevision、archive capability 与 pending identity durable registration；
- incomplete observation 可保留已知 resolved state并生成 diagnostic Candidate，但 `CanExecute=false`。

## 已完成：Candidate Fingerprints + Change Decision Integration

- Candidate entry 保留 UTC mtime 与 typed/versioned observed content identity；Standard v1 使用 metadata policy，Strict v1 对 regular file 强制 full SHA-256；
- `.backupignore` 的 rule-source observation identity 来自实际 raw bytes SHA-256，不再复用 legacy M2 fingerprint；
- Core 新增六类不可互换 strong fingerprint 与 validated SHA-256 digest；
- Canonical Fingerprint Encoding v1 使用显式 kind、field ID、length-delimited UTF-8/value encoding 与 deterministic ordering；
- unit-scoped EntrySet/Selection/ArchiveSpec/OutputLayout/ExecutionSemantic/ExecutionBinding fingerprint 已覆盖 frozen v1 输入边界；
- Rules/Boundary/LinkPolicy/External mapping 与 Format/Compression/Protection/Manifest component fingerprints 只用于诊断，top-level fingerprints 保持 equality authority；
- pure `CommittedArchiveUnitBaseline` 以 `PlanId + ArchiveUnitId` 为 identity，并拒绝 preview/incomplete candidate；
- Change Decision 独立表达 archive rebuild 与 output reorganization，unknown encoding/semantics 按 BaselineInvalid 保守处理。

## 已完成：ExecutionSemanticSnapshot + Baseline / ArchiveVersion Durable State Contract

- `ExecutionSemanticFingerprint` 已与 local SecretRevision/resolved capability state解耦，后两者继续属于 ArchiveSpec bytes semantics或 snapshot local stale facts；
- 新增完整 authored `PlanSemanticFingerprint`，保留 inherit/explicit intent并覆盖 Schedule/Retention，但排除 authority、binding、runtime 与 provenance；
- `ExecutionSemanticSnapshot` 按 unit 冻结 execution/binding/rule-source/secret revision/history maintenance facts；
- publish-time revalidator 允许 schedule/display/unrelated-unit drift，retention-only drift允许 publish但跳过 cleanup，其余 unit execution-critical drift安全阻止；
- Core 新增 `ArchiveVersionId`、无 placement 的 ArchiveVersion lifecycle、独立 CurrentVersion/HistoryVersionPlacement 与只含 fingerprint 的 committed OutputLayout state；
- Candidate 只能生成 `BaselineCandidate`；真正 baseline 只在 Published Current 的 metadata commit plan确认后产生；
- PendingPublishIntent 冻结 Prepared → HistoryCaptured → CurrentPublished → MetadataCommitted，并要求 old Current 的 History copy/hash/publish proof；
- crash recovery只根据 old/expected-new integrity作出 abort/resume、complete metadata或 ambiguous blocker决策；
- output reorganization保持同一 ArchiveVersionId，post-commit retention/old-path cleanup失败只形成 maintenance out-of-sync状态。

## 已完成：M3.9 Local Durable State / config.db Schema Design & Repository Contracts

- 收敛 ArchiveVersion/Current/History/layout placement 单一真相，baseline 显式关联 committed ArchiveVersionId；
- PublishIntent 保存完整 recoverable commit payload，可在重启后仅依赖 filesystem + config.db 重建 metadata commit；
- 冻结 config.db v1 tables、stable encoding、restrict lifecycle 与 atomic transaction boundaries；
- Application repository ports 按 aggregate consistency boundary 设计，不暴露 table CRUD；
- Schema Design Review PASS，并已作为 M3.10 implementation contract 使用。

## 已完成：M3.10 config.db EF Core SQLite Implementation + Repository Tests

- Initial migration 前修正 MaintenanceState nullable scope：Infrastructure surrogate row key + Plan/unit 两个 partial unique indexes，surrogate 不进入领域；
- Infrastructure 引入 EF Core SQLite 10，Entity/configuration/mapper/context/migration 均未泄漏到 Core/Application；
- frozen codecs覆盖 RFC/network-order UUID BLOB、digest、UTC unix-ms、boolean、stable token、NFC path 与 strict canonical UTF-8；
- `ConfigDbOpenCoordinator` 在 writable context/migration 前执行 low-level schema probe，并为每个 connection配置 FK/WAL/FULL/busy timeout；
- Managed authoritative payload执行 strict reader/schema/semantic/PlanId/canonical writer验证、repository SHA-256 与 expected-revision CAS；FILE_BACKED 不保留 fallback document；
- aggregate repositories实现 binding/registration/schedule/maintenance 与 atomic Archive Unit durable state；publish progress采用 expected-stage CAS；
- metadata commit在单一 transaction内完成 new version、old supersede、History、Current、Baseline、OutputLayout与 intent completion；
- 真实 file-backed SQLite 测试覆盖 restart recovery、六个 fault injection rollback点、migration/durability、corruption fail-closed、non-cascade lifecycle 与 Output Reorganization isolation。

## 已完成：M3.11 config.db Startup/Recovery Integration + Local Binding/Authority Application Workflows

- startup query ports 可发现 active registrations 与 incomplete PublishIntent，不暴露 table DTO、`DbSet` 或 `IQueryable`；
- `DatabaseMetadata.DeviceId` 成为本机唯一 identity 来源，binding repository 不再接受调用方伪造的 DeviceId；
- Application startup coordinator 打开数据库、冻结 identity、逐 unit 探测 Current 并分类恢复；expected-new 自动重建并原子提交 metadata，old/ambiguous 均保留 journal，歧义只隔离对应 unit；
- Infrastructure filesystem recovery probe 由 CurrentRoot 与 journal relative path 计算实际 artifact integrity，恢复不依赖 Candidate、scanner、cache 或进程内状态；
- Managed 与 File-backed 通过统一 authoritative Plan workflow 加载；strict document runtime 隐藏在 Infrastructure document-source port 后，普通保存不得静默切换 authority，File-backed 无数据库 fallback；
- activation/deactivation 与 portable revision 分离，unregister/authority conversion 不级联删除 runtime state；
- Local Binding workflow 复用共享 `DeviceBindingSafetyValidator`，先规范化物理路径，再验证同 Plan root safety 与跨 active Plan writable collision；安全但不完整的 binding 可以 durable 保存；
- 真实 file-backed SQLite 集成测试覆盖关闭/重开、自动恢复、歧义隔离、authority/no-fallback/non-cascade、single-device identity 与 binding safety。

## 已完成：M3.12 Secret Binding Workflow + config.db Snapshot/Recovery Maintenance

- path/storage binding 与 SecretBinding metadata 拆为独立 aggregate/port；普通 binding save 不再携带或重写 SecretRevision/locator，解析执行快照前才组合 active revision facts；
- `ISecretBindingMetadataStore` 以 expected-revision CAS 实现 Set=1、Replace/Rebind=revision+1 与 durable deactivate，最终 revision 只能由 repository 推进；
- platform-neutral Secret Store port 仅通过 disposable/zeroizable transient lease接触 material，并提供 availability probe与无头读取；SecretValue、hash、verifier或 derived digest均不持久化；
- Set/Replace/Rebind 使用 create new locator → DB CAS switch → best-effort delete old 的 copy-on-write顺序；Unbind先 durable deactivate，再删除 material；
- fault-injection测试覆盖 create后DB失败、DB commit后中断、old delete失败与 stale CAS，active metadata始终只指向已成功创建的 material；
- `config.db` snapshot 使用 SQLite Online Backup API写入同目录 temporary database，经 DatabaseMetadata/schema与 `PRAGMA integrity_check` 验证后 atomic replace；
- recovery只报告 validated snapshot candidate；显式 restore保留原损坏 database/WAL/SHM，再以 Online Backup恢复并重新经过正常 open coordinator；
- durability maintenance仅清理 `METADATA_COMMITTED` journal，保留 incomplete journal及全部 ArchiveVersion/Current/History/Baseline runtime state；
- config.db v1 schema、Initial migration与 frozen durable encoding均保持不变。

## Milestone 3 状态：COMPLETE

M3 Completion Review 结论为 **PASS**，见 [`../reviews/MILESTONE-3-COMPLETION-REVIEW.md`](../reviews/MILESTONE-3-COMPLETION-REVIEW.md)。下一阶段正式进入 [`MILESTONE-4-ARCHIVING.md`](MILESTONE-4-ARCHIVING.md) 的 Archive Writer / capability / manifest / `.partial` / archive verification；不先实现 Physical Current/History Publisher。
