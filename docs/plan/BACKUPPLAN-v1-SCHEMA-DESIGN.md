# Backup Plan v1 JSON Schema 设计

> [!IMPORTANT]
> 本文定义 `*.backupplan v1` 的 JSON 结构设计输入，但不是实际 JSON Schema。本轮不得创建 `backupplan-v1.schema.json`、Document DTO、reader/writer、semantic mapper 或 persistence model。领域语义仍以 `BACKUPPLAN.md` 等正式规范和 Domain Freeze Review 为准。

## 1. 设计结论

Schema Design Review 结论为 **PASS**，详见 `../reviews/BACKUPPLAN-v1-SCHEMA-DESIGN-REVIEW.md`。没有发现必须改变 frozen domain 才能确定 shape 的 `Schema Design Blocker`。

v1 使用 closed-world、camelCase、case-sensitive property name 和字符串 enum。所有 object 在实际 Schema 中都必须等价于 `additionalProperties: false`。文档使用严格 UTF-8 JSON；array 是否有顺序语义由本文明确，不由 JSON property/array 的偶然排列推断。

`$schema` 纳入 v1，作为 optional IDE/editor discovery metadata：

- property 名固定为 `$schema`，值为 URI string；
- writer 在已知正式 URI 后应输出，但 reader 不要求存在；
- 它不 authoritative，不参与 version dispatch、semantic fingerprint 或执行；
- `schemaVersion` 才是唯一权威 Document Contract Version。

## 2. 顶层 Document Structure

```text
BackupPlanDocumentV1
  $schema?                   string URI, optional metadata
  schemaVersion              integer const 1
  planId                     UuidV4
  name                       NonEmptyDisplayName
  description?               string, display metadata
  semantics                  PortableSemanticsPinsV1
  sources                    BackupSourceV1[]
  globalRules                GlobalRulesSnapshotV1
  planRules                  RuleV1[]
  archiveSpecDefault         ArchiveSpecV1
  archiveUnits               ArchiveUnitDeclarationV1[]
  secretSlots                SecretSlotV1[]
  linkPolicy                 LinkPolicyV1
  changeDetection            ChangeDetectionV1
  historyDefault             HistoryPolicyV1
  schedule                   ScheduleIntentV1
  externalSources            ExternalSourceDeclarationV1[]
```

所有顶层 property 都 required，只有 `$schema` 和 `description` optional。集合即使为空也必须显式输出，避免 reader 依赖应用当前默认。`sources` 至少一项；`archiveUnits` 可以为空，因为 FILE_MANAGED unit 可纯 discovery；`globalRules.rules`、`planRules`、`secretSlots`、`externalSources` 可以为空。

`description` 是 v1 已确定的 optional、非执行显示 metadata；它进入完整 authored Plan semantic，但不进入 Execution/Archive fingerprints。

## 3. `$defs` 完整清单

实际 Schema 应至少定义以下 `$defs`，名称是 schema-local contract name：

```text
UuidV4
NonEmptyDisplayName
LogicalPath
ArchiveDestination
LocalTime

PortableSemanticsPinsV1
BackupSourceV1
GlobalRulesSnapshotV1
GlobalRuleProvenanceV1
RuleV1
RuleActionV1
RuleModeV1
CasePolicyV1

ArchiveUnitDeclarationV1
UiManagedArchiveUnitV1
FileManagedArchiveUnitV1
UiManagedLocalRulesV1

ArchiveSpecV1
ArchiveSpecOverrideV1
ArchiveFormatV1
CompressionPresetV1
ProtectionConfigurationV1
NoProtectionV1
PrivacyProtectionV1
SecureProtectionV1
SecretSlotV1

LinkPolicyV1
ChangeDetectionV1

HistoryPolicyV1
HistoryDisabledV1
HistoryEnabledV1
HistoryOverrideV1
HistoryInheritV1
RetentionPolicyV1
KeepAllRetentionV1
KeepLastVersionsRetentionV1

ScheduleIntentV1
ManualOnlyScheduleV1
AutomaticScheduleV1
ScheduleTriggerV1
DailyTriggerV1
WeeklyTriggerV1
OnStartupTriggerV1
DayOfWeekV1
MissedRunPolicyV1

ExternalSourceDeclarationV1
ExternalSourceKindV1
```

## 4. Scalar 与 enum encoding

### 4.1 UuidV4

JSON type `string`，pattern 必须约束 canonical lowercase `8-4-4-4-12`，第 13 位为 `4`，variant 为 `8|9|a|b`。JSON Schema 负责 lexical shape；semantic validator 仍应以 UUID parser 验证。

### 4.2 LogicalPath / ArchiveDestination

JSON type `string`。Schema 可以用 pattern 排除空字符串、leading `/`、反斜杠和明显 `..` segment；完整 NFC、segment、reserved namespace、root path、child Boundary 和目标 filesystem collision 必须由 semantic validator 检查。

`SourceOutputPath` 与 External `archiveDestination` 必须非空。ArchiveUnit `path` 允许空字符串表示 Source root unit；否则使用 Source-relative LogicalPath。

### 4.3 Enum strings

| Domain enum | JSON strings |
|---|---|
| RuleAction | `include`, `exclude` |
| RuleMode | `exclude`, `includeOnly` |
| CasePolicy | `auto`, `sensitive`, `insensitive` |
| RuleSource | `uiManaged`, `fileManaged` |
| ArchiveFormat | `sevenZip`, `zip`, `tarZstd` |
| CompressionPreset | `store`, `fast`, `standard`, `extreme` |
| Protection mode | `none`, `privacy`, `secure` |
| LinkPolicy | `preserve`, `skip` |
| ChangeDetection mode | `standard`, `strict` |
| History mode | `disabled`, `enabled` |
| History override mode | `inherit`, `disabled`, `enabled` |
| Retention kind | `keepAll`, `keepLastVersions` |
| Schedule trigger type | `daily`, `weekly`, `onStartup` |
| DayOfWeek | `monday`, `tuesday`, `wednesday`, `thursday`, `friday`, `saturday`, `sunday` |
| MissedRunPolicy | `skip`, `runOnceWhenAvailable` |
| Secret purpose | `archiveEncryption` |
| External kind | `file`, `directory` |

未知值必须由 closed enum 拒绝，不能映射到 default。

## 5. Rules 与 Global Snapshot

### RuleV1

```json
{
  "action": "exclude",
  "pattern": "**/bin/"
}
```

`action`、`pattern` required；无 optional property。`pattern` 是 non-empty string。JSON Schema 不尝试实现 `.backupignore v1` parser；escape、character class、root anchor、directory suffix、NFC 和 invalid range 由 semantic validator 按 `rulesSemanticsVersion` 验证。

Global/Plan rules 只有有序 action overlay，不携带 mode/case。UI_MANAGED local rules完整携带：

```text
UiManagedLocalRulesV1
  mode      required: exclude | includeOnly
  case      required: auto | sensitive | insensitive
  rules     required: RuleV1[]
```

### GlobalRulesSnapshotV1

```text
GlobalRulesSnapshotV1
  rules         required: RuleV1[]
  provenance?   optional: GlobalRuleProvenanceV1

GlobalRuleProvenanceV1
  id?           optional string
  name?         optional string
  revision?     optional string
```

`provenance` 必须至少有一个 property；其所有字段都是 display/authoring metadata，不 authoritative、不影响 SelectionFingerprint。`id` 故意不使用 portable aggregate UuidV4：frozen domain 没有把 Global Rule Library identity 纳入 Backup Plan 强类型 UUID 集合。writer 不应输出空 provenance。

规则顺序是 Global snapshot rules → Plan rules → Local rules，且每个 rules array 内顺序有语义。

## 6. Sources 与 Archive Units

### BackupSourceV1

```text
sourceId          required UuidV4
name              required NonEmptyDisplayName
sourceOutputPath  required non-empty LogicalPath
```

physical SourceRoot 不存在于 document。

### ArchiveUnitDeclarationV1

使用 property `ruleSource` 作为 discriminator：

```text
common required:
  archiveUnitId
  sourceId
  path
  ruleSource

common optional:
  archiveSpecOverride
  historyOverride
```

`ruleSource = uiManaged` 时 `localRules` required；`ruleSource = fileManaged` 时 `localRules` forbidden。FILE_MANAGED 的 RuleMode、CasePolicy、Rules 和 `.backupignore @id` 不得复制进 Plan declaration。

```json
{
  "archiveUnitId": "6c3ad16a-ae76-4d21-9738-c70e6264c209",
  "sourceId": "8b89c49e-f3ab-4cc7-a94e-3be782b36561",
  "path": "projects/app",
  "ruleSource": "uiManaged",
  "localRules": {
    "mode": "exclude",
    "case": "auto",
    "rules": []
  }
}
```

## 7. ArchiveSpec、Protection 与 SecretSlot

### ArchiveSpecV1

完整 default 的三个 property 全部 required：

```text
format               ArchiveFormatV1
compressionPreset    CompressionPresetV1
protection            ProtectionConfigurationV1
```

### ArchiveSpecOverrideV1

逐组件 override，三个 property 都 optional，但 object 至少包含一个：

```text
format?
compressionPreset?
protection?
```

property 缺失编码 `inherit`；不增加额外 `inherit` enum。显式写出与 default 相同的值仍表示 explicit override，Plan semantic 与缺失 property 不同。

### ProtectionConfigurationV1

使用 `mode` discriminator：

- `{ "mode": "none" }`
- `{ "mode": "privacy" }`
- `{ "mode": "secure", "secretSlotId": "..." }`

`secure` 必须且只能携带 `secretSlotId`；none/privacy 禁止该字段。SecretValue、SecretReference、SecretRevision 和 provider 均不存在于 document。

### SecretSlotV1

```text
secretSlotId  required UuidV4
name          required NonEmptyDisplayName
purpose       required const "archiveEncryption"
```

## 8. Link 与 Change Detection

`linkPolicy` 直接编码为 enum string `preserve|skip`。

```text
ChangeDetectionV1
  mode required: standard | strict
```

不提供 per-External 或 per-unit ChangeDetection override。

## 9. History inheritance

### HistoryPolicyV1

使用 `mode` discriminator：

- disabled：`{ "mode": "disabled" }`
- enabled：`{ "mode": "enabled", "retention": RetentionPolicyV1 }`

### HistoryOverrideV1

- inherit：`{ "mode": "inherit" }`
- disabled：`{ "mode": "disabled" }`
- enabled：`{ "mode": "enabled", "retention": RetentionPolicyV1 }`

### RetentionPolicyV1

使用 `kind` discriminator：

- keepAll：`{ "kind": "keepAll" }`
- keepLastVersions：`{ "kind": "keepLastVersions", "count": N }`，Schema 约束 integer、minimum 1。

HistoryRoot、History versions 和 maintenance state 不进入 document。

## 10. Schedule encoding

### ScheduleIntentV1

使用 boolean `enabled` discriminator：

- Manual-only：`{ "enabled": false }`，禁止 triggers/missedRunPolicy；
- Automatic：`enabled=true`，`triggers` 和 `missedRunPolicy` required，triggers 至少一项。

### Trigger union

使用 `type` discriminator：

```text
DailyTriggerV1
  type       const daily
  localTime  required HH:mm

WeeklyTriggerV1
  type        const weekly
  daysOfWeek  required DayOfWeekV1[], minItems 1, uniqueItems true
  localTime   required HH:mm

OnStartupTriggerV1
  type        const onStartup
```

Schema 可检查 localTime pattern `^(?:[01][0-9]|2[0-3]):[0-5][0-9]$` 和 weekly day uniqueness；跨 trigger 语义重复与 canonical set equality 由 semantic validator检查。

## 11. External Source encoding

```text
ExternalSourceDeclarationV1
  externalSourceId     required UuidV4
  name                 required NonEmptyDisplayName
  kind                 required file | directory
  targetArchiveUnitId  required UuidV4
  archiveDestination   required non-empty ArchiveDestination
```

physical path、optional flag、glob、rules、staging 和 runtime status 均不存在于 document。File/Directory destination 的不同解释由 `kind` 与 semantic mapper决定，不需要第二个 union shape。

## 12. Array ordering contract

| Array | 顺序有语义 | Writer canonical ordering |
|---|---|---|
| `globalRules.rules` | 是，Last Match Wins | 保留原顺序 |
| `planRules` | 是，Last Match Wins | 保留原顺序 |
| `localRules.rules` | 是，Last Match Wins | 保留原顺序 |
| `sources` | 否 | `sourceId` ordinal |
| `archiveUnits` | 否 | `sourceId`, normalized `path`, `archiveUnitId` ordinal |
| `secretSlots` | 否 | `secretSlotId` ordinal |
| `externalSources` | 否 | `targetArchiveUnitId`, normalized `archiveDestination`, `kind`, `externalSourceId` ordinal |
| `schedule.triggers` | 否，语义集合 | `type`, `localTime`, canonical days ordinal |
| `weekly.daysOfWeek` | 否，语义集合 | Monday → Sunday 固定顺序 |

Canonical ordering 只规定 future writer 的 deterministic output；reader 接受任意 array order。集合重复不是“排序后去重”：必须 validation error。JSON object property 顺序始终无语义；writer 后续另行固定 property emission order。

## 13. Semantics-version pins 审计

### 13.1 必须进入 portable document

顶层 required object：

```json
{
  "semantics": {
    "rules": 1,
    "archive": 1,
    "outputPathEncoding": 1
  }
}
```

对应 `$defs/PortableSemanticsPinsV1`，三个字段均 positive integer，v1 writer 输出 const 1；reader 对不支持值返回 UnsupportedDocumentSemantics。

| Pin | portable 理由 | frozen domain 来源 |
|---|---|---|
| `rules` | Global/Plan/UI local rule pattern/action 在相同 schema shape 下必须保持旧解释 | RulesSemanticsVersion / Selection fingerprint |
| `archive` | Format + CompressionPreset + protection/metadata/backend mapping 必须跨应用升级稳定 | ArchiveSemanticsVersion / EffectiveArchiveSpec |
| `outputPathEncoding` | 同一 SourceOutputPath/unit mapping 必须稳定解析到 Current relative layout | OutputPathEncodingVersion / OutputLayoutFingerprint |

这三个 pin 是 frozen domain 已明确需要独立固定、且用户文档必须跨版本保留旧含义的版本；不是为 Schema 方便新造的字段。`.backupignore` 自己继续用 `@version 1`，不从 Plan 的 `semantics.rules` 覆盖文件 parser version；Plan pin控制 Plan 内规则表示。

### 13.2 不进入 portable document

| Version | 所属边界 | 不 portable 的理由 |
|---|---|---|
| `schemaVersion` 之外的 Document reader/migrator implementation version | Document infrastructure | schemaVersion 已负责 contract dispatch |
| FingerprintFormatVersion/canonical encoding | baseline/runtime persistence | 用户不选择 hash encoding；未知旧 baseline保守 rebuild |
| scanner/file observation semantics version | execution/baseline | 由当前 implementation 与 baseline version协调，不是 desired config |
| External mapping semantics version | document v1 contract | v1 的 Kind/Destination mapping 已由 schemaVersion 固定；fingerprint内部记录实现语义版本 |
| Schedule local-time/DST semantics version | document v1 contract + scheduler fingerprint | v1 行为已固定，用户没有多版本选择 |
| PrivacyProtectionSemanticsVersion | ArchiveSemanticsVersion 子语义 | 由 archive pin覆盖，不独立暴露 |
| manifest schemaVersion | archive 内独立 artifact | 由 archive writer/manifest reader控制 |
| storage binding semantics version | ExecutionBindingFingerprint | local runtime resolution，不是 portable desired config |
| History/output mapping implementation fingerprint version | runtime fingerprint | 算法版本不是用户选择；output encoding pin已覆盖 portable layout contract |

如果未来任何内部版本必须允许一份 document 显式选择旧/新行为，应通过新 schemaVersion 或冻结领域变更流程决定；不能在实际 Schema 编写时临时加字段。

## 14. JSON Schema vs semantic validator

### JSON Schema 负责

- object/array/string/integer/boolean 基本类型；
- required/optional、closed properties、const `schemaVersion=1`；
- enum 与 discriminator union shape；
- UUID lexical pattern、LocalTime pattern；
- non-empty/minimum/minItems、weekly `uniqueItems`；
- UI_MANAGED requires localRules、FILE_MANAGED forbids localRules；
- Secure requires secretSlotId、None/Privacy forbids it；
- enabled History requires retention；
- enabled Schedule requires triggers/missedRunPolicy；
- ArchiveSpecOverride/provenance 至少一个 property。

### BackupPlanDocumentV1 semantic validator 负责

- UUID parser/version/variant 和各 typed-ID collection uniqueness；
- ArchiveUnit.SourceId、External target、Secure SecretSlot reference graph；
- normalized LogicalPath/NFC、root unit empty-path special case、reserved namespace；
- SourceOutputPath、ArchiveUnit path、External destination collision与 child Boundary；
- rule pattern grammar、escape、range、directory suffix 和 semantics pin支持；
- duplicate semantic schedule trigger、weekly canonical set；
- FILE_MANAGED declaration/discovery/`.backupignore @id` identity 与 RuleSource conflict；
- Global/Plan/Local rule authority；
- effective ArchiveSpec/History inheritance resolution；
- unsupported semantics pin、adapter capability 和 missing binding 的分层（后两者不是 document invalid）；
- External File/Directory root、no-follow、observation、staging 与 completeness（运行阶段）；
- target filesystem case/output collision、cross-plan root overlap（local readiness/planning）。

Schema validation 成功只表示 document shape 合法；semantic validation、local binding/readiness、capability 和 execution 仍是不同阶段。

## 15. 完整示例 A：最小 UI_MANAGED

```json
{
  "$schema": "https://schemas.stowcrate.dev/backupplan/v1.json",
  "schemaVersion": 1,
  "planId": "0c79c2c4-53bc-4a63-b4f0-a67bed58f8d8",
  "name": "Code",
  "semantics": {
    "rules": 1,
    "archive": 1,
    "outputPathEncoding": 1
  },
  "sources": [
    {
      "sourceId": "8b89c49e-f3ab-4cc7-a94e-3be782b36561",
      "name": "Code",
      "sourceOutputPath": "code"
    }
  ],
  "globalRules": { "rules": [] },
  "planRules": [],
  "archiveSpecDefault": {
    "format": "sevenZip",
    "compressionPreset": "standard",
    "protection": { "mode": "none" }
  },
  "archiveUnits": [
    {
      "archiveUnitId": "6c3ad16a-ae76-4d21-9738-c70e6264c209",
      "sourceId": "8b89c49e-f3ab-4cc7-a94e-3be782b36561",
      "path": "projects/app",
      "ruleSource": "uiManaged",
      "localRules": {
        "mode": "exclude",
        "case": "auto",
        "rules": [
          { "action": "exclude", "pattern": "**/bin/" },
          { "action": "exclude", "pattern": "**/obj/" }
        ]
      }
    }
  ],
  "secretSlots": [],
  "linkPolicy": "preserve",
  "changeDetection": { "mode": "standard" },
  "historyDefault": { "mode": "disabled" },
  "schedule": { "enabled": false },
  "externalSources": []
}
```

## 16. 完整示例 B：FILE_MANAGED + override

```json
{
  "schemaVersion": 1,
  "planId": "cf9c7217-d67f-4376-9454-74925aeacf7d",
  "name": "Projects",
  "description": "规则由各项目的 .backupignore 管理",
  "semantics": {
    "rules": 1,
    "archive": 1,
    "outputPathEncoding": 1
  },
  "sources": [
    {
      "sourceId": "287742a8-b4d8-4ca1-9219-b4a642cecc52",
      "name": "Projects",
      "sourceOutputPath": "projects"
    }
  ],
  "globalRules": {
    "rules": [
      { "action": "exclude", "pattern": "**/.DS_Store" }
    ],
    "provenance": {
      "name": "Cross-platform defaults",
      "revision": "2026-08-24"
    }
  },
  "planRules": [],
  "archiveSpecDefault": {
    "format": "sevenZip",
    "compressionPreset": "standard",
    "protection": { "mode": "none" }
  },
  "archiveUnits": [
    {
      "archiveUnitId": "d4f27ac8-e59a-4b0c-b6bb-057c65e5bb1d",
      "sourceId": "287742a8-b4d8-4ca1-9219-b4a642cecc52",
      "path": "large-repo",
      "ruleSource": "fileManaged",
      "archiveSpecOverride": {
        "compressionPreset": "fast"
      },
      "historyOverride": {
        "mode": "enabled",
        "retention": {
          "kind": "keepLastVersions",
          "count": 3
        }
      }
    }
  ],
  "secretSlots": [],
  "linkPolicy": "preserve",
  "changeDetection": { "mode": "strict" },
  "historyDefault": { "mode": "disabled" },
  "schedule": { "enabled": false },
  "externalSources": []
}
```

## 17. 完整示例 C：Secure + Schedule + External Source

```json
{
  "$schema": "https://schemas.stowcrate.dev/backupplan/v1.json",
  "schemaVersion": 1,
  "planId": "ae8d85e7-2f28-4f79-a2a8-83f319a41ea5",
  "name": "Secure workstation",
  "semantics": {
    "rules": 1,
    "archive": 1,
    "outputPathEncoding": 1
  },
  "sources": [
    {
      "sourceId": "1d1effaf-68d6-4eab-ae6d-b817b376ed74",
      "name": "Documents",
      "sourceOutputPath": "documents"
    }
  ],
  "globalRules": { "rules": [] },
  "planRules": [
    { "action": "exclude", "pattern": "**/*.tmp" }
  ],
  "archiveSpecDefault": {
    "format": "sevenZip",
    "compressionPreset": "standard",
    "protection": {
      "mode": "secure",
      "secretSlotId": "554f1abe-043d-4fbf-86be-f3289467a4c2"
    }
  },
  "archiveUnits": [
    {
      "archiveUnitId": "0ac1268d-4a7c-4567-9fe0-54c689a2ce47",
      "sourceId": "1d1effaf-68d6-4eab-ae6d-b817b376ed74",
      "path": "work",
      "ruleSource": "uiManaged",
      "localRules": {
        "mode": "exclude",
        "case": "auto",
        "rules": []
      }
    }
  ],
  "secretSlots": [
    {
      "secretSlotId": "554f1abe-043d-4fbf-86be-f3289467a4c2",
      "name": "Work archive password",
      "purpose": "archiveEncryption"
    }
  ],
  "linkPolicy": "skip",
  "changeDetection": { "mode": "strict" },
  "historyDefault": {
    "mode": "enabled",
    "retention": { "kind": "keepAll" }
  },
  "schedule": {
    "enabled": true,
    "triggers": [
      { "type": "daily", "localTime": "02:00" },
      {
        "type": "weekly",
        "daysOfWeek": ["monday", "friday"],
        "localTime": "20:30"
      },
      { "type": "onStartup" }
    ],
    "missedRunPolicy": "runOnceWhenAvailable"
  },
  "externalSources": [
    {
      "externalSourceId": "11b34cab-d089-4374-8616-65c69879cab2",
      "name": "SSH config",
      "kind": "file",
      "targetArchiveUnitId": "0ac1268d-4a7c-4567-9fe0-54c689a2ce47",
      "archiveDestination": "machine/ssh/config"
    }
  ]
}
```

## 18. Schema Design Blockers

**None.**

以下是实现/actual-schema review items，不是 frozen-domain blocker：

- 正式 `$id` / `$schema` 发布 URI 和托管位置；示例 URI 仅为 proposed canonical URI；
- JSON Schema draft 版本选择；推荐 2020-12，但实际文件创建时确认工具链；
- regex 对 Unicode/NFC/LogicalPath 的覆盖程度；不能用复杂 regex 替代 semantic validator；
- description/display string 的最大长度与 UI policy；不影响领域 shape；
- canonical pretty-print 空格/换行/property emission order；只影响 writer presentation。

这些事项不得引入新领域字段或改变 enum/default/inheritance。

## 19. 下一步

只有 Schema Design Review 保持 PASS 后，才可：

1. 选择 JSON Schema draft 与正式 `$id` URI；
2. 创建 `backupplan-v1.schema.json`；
3. 用本文三个示例和反例验证实际 Schema；
4. 再设计 `BackupPlanDocumentV1` DTO、strict reader/writer 和 semantic mapper；
5. 最后进入 Persistence model，不得让 DTO 直接充当 EF/SQLite Entity。
