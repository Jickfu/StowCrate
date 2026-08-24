# Backup Plan v1 JSON Schema Review

## Result

**PASS**

Reviewed artifacts:

- `schemas/backupplan-v1.schema.json`
- `schemas/fixtures/backupplan-v1/valid/*.json`
- `schemas/fixtures/backupplan-v1/invalid/*.json`
- `tests/StowCrate.Infrastructure.Tests/Configuration/BackupPlanSchemaTests.cs`

结论：实际 Schema 是 frozen domain 与已通过 Schema Design 的 Draft 2020-12 closed-world projection。Schema、fixtures 与自动测试 Review 无 blocker，Backup Plan v1 标记为 **Document Contract Frozen**。

本结论不表示 DTO、strict reader/writer、duplicate-property detector、semantic mapper、local binding、Archiver、SQLite 或 EF 已实现。

## Draft 与发布 URI

- Meta-schema：`https://json-schema.org/draft/2020-12/schema`。
- canonical `$id`：当前省略。仓库尚未确认长期稳定的公开 Schema URI，不能虚构正式域名。
- document `$schema` property：optional、non-authoritative URI metadata；fixtures 证明不存在也有效，使用合法 URN 也只做结构验证。
- 确认长期托管域名、路径、缓存和版本保留策略后，才可通过发布配置增加 canonical `$id` 和 writer 默认 `$schema` URI；不得因此改变 document semantics。

## Closed-world 与 union review

所有实际 object definition 均显式 `additionalProperties: false`。顶层以及以下 union 已验证为 closed discriminated shape：

| Union | Discriminator | Variants |
|---|---|---|
| ArchiveUnitDeclaration | `ruleSource` | uiManaged requires localRules；fileManaged forbids localRules |
| ProtectionConfiguration | `mode` | none、privacy、secure(secretSlotId required) |
| HistoryPolicy | `mode` | disabled、enabled(retention required) |
| HistoryOverride | `mode` | inherit、disabled、enabled(retention required) |
| RetentionPolicy | `kind` | keepAll、keepLastVersions(count >= 1) |
| ScheduleIntent | `enabled` | false manual-only；true automatic |
| ScheduleTrigger | `type` | daily、weekly、onStartup |

ArchiveSpecOverride 与 Global Rule provenance 使用 `minProperties: 1`，拒绝无意义空 object。Weekly days 使用 `uniqueItems: true`；跨 trigger semantic duplicate 继续留给 semantic validator。

## Version boundaries

- `schemaVersion` 是 `const: 1`。
- `semantics.rules/archive/outputPathEncoding` 是 positive integer，不使用 `const: 1`。Schema 只验证 shape；future strict reader/semantic validator 判断是否支持，v1 writer 只输出 1。
- Unsupported semantics pin 不得报告为 UnsupportedSchemaVersion。
- Fingerprint、scanner、External mapping、Schedule/DST、Privacy sub-semantics、manifest 与 storage binding version 未被错误加入 Plan Schema。

## Positive fixtures

| Fixture | Coverage |
|---|---|
| `minimal-ui-managed.json` | 最小完整 document、UI_MANAGED、None、manual、disabled History |
| `file-managed-overrides.json` | optional `$schema`、description、provenance、FILE_MANAGED、ArchiveSpec/History override、positive non-1 semantics pins |
| `secure-schedule-external.json` | Secure、SecretSlot、TarZstd/Extreme、inherit、enabled History、all trigger variants、External Source |

全部必须通过 Schema validation。

## Negative fixtures

每个 fixture 只依赖明确 structural/schema reason 失败：

| Fixture | Expected reason |
|---|---|
| `unknown-top-property.json` | top-level `additionalProperties: false` |
| `schema-version-not-one.json` | `schemaVersion const 1` |
| `uuid-not-canonical-v4.json` | lowercase canonical UUID v4 pattern |
| `semantics-pin-zero.json` | positive integer minimum 1 |
| `ui-managed-missing-local-rules.json` | UI discriminator requires localRules |
| `file-managed-with-local-rules.json` | FILE closed variant forbids localRules |
| `secure-missing-secret-slot-id.json` | Secure discriminator requires secretSlotId |
| `history-disabled-with-retention.json` | disabled closed variant forbids retention |
| `retention-count-zero.json` | KeepLastVersions count minimum 1 |
| `manual-schedule-with-trigger.json` | manual-only closed variant forbids triggers |
| `weekly-duplicate-day.json` | weekly days `uniqueItems` |
| `external-unknown-property.json` | nested External object rejects unknown `optional` |

全部必须失败；测试不会把失败 fixture 当作 semantic validator coverage。

## Deliberately excluded validation

Schema 不实现 typed ID collection uniqueness/reference graph、rule grammar、NFC/full LogicalPath semantics、FILE_MANAGED discovery/`@id` conflict、Archive Boundary、External ownership collision、output filesystem case collision、local binding/readiness 或 capability validation。

Duplicate JSON property 也不属于 Schema fixture：JSON DOM 在 Schema evaluation 前可能已采用 parser-specific first/last behavior，future strict reader 必须在构造 DOM/DTO 前主动拒绝。

## Automated test result

`BackupPlanSchemaTests`：

- 加载实际 Schema；
- 检查 Draft 2020-12 declaration 与未配置 canonical `$id`；
- 枚举全部 valid fixtures 并要求通过；
- 枚举全部 invalid fixtures 并要求失败。

Review 时 targeted suite 为 16/16 passed。最终提交前仍需完整 `dotnet build` 与 `dotnet test`。

## Document Contract Freeze Decision

Backup Plan v1 **Document Contract Frozen**。后续：

1. DTO/strict reader/writer 必须机械匹配 Schema，不得增加 tolerant/extension fields；
2. semantic mapper/validator 独立实现 frozen semantic checks；
3. persistence model 与 SQLite schema 不得直接复用 Document DTO；
4. 发布后任何新增字段、enum/variant、default change 或可接受 shape 扩展都必须遵守 schemaVersion evolution 规则。
