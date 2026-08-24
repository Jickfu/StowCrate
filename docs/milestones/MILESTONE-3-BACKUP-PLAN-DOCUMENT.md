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

## 下一项：Document Writer + deterministic round-trip

portable authored aggregate 已足以形成稳定的 document round-trip 边界。下一项先实现 v1 writer、deterministic canonical ordering/property emission，以及 DTO/domain/document round-trip 测试；writer 只输出当前支持的 semantics pins `1`，且不写入任何 local/runtime state。

Application `ResolvedPlanSnapshot` resolution contract 在 writer 契约稳定后推进。本阶段仍不实现 Import/Update、Local Binding、SQLite、EF 或归档执行。
