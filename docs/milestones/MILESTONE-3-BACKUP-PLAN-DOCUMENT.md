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

## 下一项：BackupPlanDocumentV1 Semantic Validator + DTO→Frozen Domain Mapper

下一项只实现 portable document semantic validation 与到 frozen domain 的显式映射，包括 reference graph、ID uniqueness/type matching、rule grammar、Archive Boundary、External collision 和当前 reader 支持的 semantics pins。

该阶段仍不实现 Import/Update、Local Binding、writer、SQLite、EF 或归档执行。
