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

## 下一项：Source/External Observation + Archive Unit Resolution Contract

```text
ResolvedPlanSnapshot
+ SourceSnapshot[]
+ ExternalSourceSnapshot[]
+ .backupignore discovery
→ ResolvedArchiveUnitSet
→ Execution Readiness
```

下一阶段才处理 MissingHistoryRootBinding、MissingSecretBinding、SecretRevision、archive capability 与最终 output collision；仍不提前实现 SQLite、Archiver 或 backup execution。
