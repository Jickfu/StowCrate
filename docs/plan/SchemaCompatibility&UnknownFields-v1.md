下一项建议正式确定 **Schema Compatibility & Unknown Fields v1**。这一项的目标不是开始写 JSON Schema，而是先规定：

> 不同版本 StowCrate 遇到不同版本的 `*.backupplan` 时，到底允许读什么、拒绝什么、迁移什么，以及绝不能偷偷忽略什么。

对于备份软件，我建议采用**严格、封闭、保守兼容**策略。

------

# 1. `schemaVersion` 必须强制存在

与 `.backupignore` 不同，`*.backupplan` 不应该允许省略版本。

概念上：

```json
{
  "schemaVersion": 1
}
```

规则：

```text
缺失            → InvalidDocument
非整数          → InvalidSchemaVersion
<= 0            → InvalidSchemaVersion
已支持版本      → 按对应版本解析
未来未知版本    → UnsupportedSchemaVersion
```

不能：

```text
没有 schemaVersion
→ 猜它大概是 v1
```

因为 `.backupplan` 是长期可移植配置格式。

------

# 2. `schemaVersion` 使用整数，不用 SemVer

推荐：

```text
1
2
3
```

而不是：

```text
"1.0"
"1.2.3"
```

因为它表示：

> **Document Contract Version**

而不是软件版本。

StowCrate：

```text
1.3.5
2.0.0
```

都完全可能继续读：

```text
schemaVersion = 1
```

------

# 3. v1 采用 Closed World Schema

这是最关键的决定之一：

> **已知 schemaVersion 下出现未知字段，一律 validation failure。**

例如 v1 只认识：

```json
{
  "schemaVersion": 1,
  "name": "...",
  "sources": []
}
```

却出现：

```json
{
  "schemaVersion": 1,
  "name": "...",
  "sources": [],
  "magicBackupMode": true
}
```

不能：

```text
忽略 magicBackupMode
继续备份
```

必须：

```text
UnknownProperty
→ InvalidDocument / PlanNotReady
```

原因是这个字段可能真的影响：

- 文件选择；
- 加密；
- History；
- 输出；
- 删除；
- 恢复。

备份软件不能假设：

> “我不认识，应该不重要。”

------

# 4. Unknown Enum Value 同样 Fatal

例如：

```text
ProtectionMode:
None
Privacy
Secure
```

遇到未来：

```text
"protectionMode": "hardwareKey"
```

旧版本不能：

```text
hardwareKey
→ 当 None
```

也不能：

```text
hardwareKey
→ Secure 差不多吧
```

只能：

```text
UnknownEnumValue
→ UnsupportedDocumentSemantics
```

这一条适用于：

```text
ArchiveFormat
RuleSource
LinkPolicy
ScheduleTrigger
RetentionPolicy
ProtectionMode
ChangeDetectionMode
...
```

------

# 5. 未知 discriminator 类型也 Fatal

假设未来：

```text
ScheduleTrigger
Daily
Weekly
OnStartup
OnNetworkAvailable
```

旧版本看到：

```json
{
  "type": "onNetworkAvailable"
}
```

必须拒绝。

同理未来：

```text
RetentionPolicy
KeepAll
KeepLastVersions
GrandfatherFatherSon
```

旧 reader 不认识 GFS：

> 不能忽略这个 History policy。

------

# 6. 所有对象默认禁止未知字段

以后正式 JSON Schema 应遵循：

```text
additionalProperties = false
```

或等价 closed-schema 约束。

不仅顶层：

```text
BackupPlan
```

而是所有对象：

```text
Source
ArchiveUnit
Rule
Protection
SecretSlot
Schedule
History
ExternalSource
...
```

全部如此。

否则：

```text
顶层严格
嵌套对象随便塞字段
```

依然留下兼容漏洞。

------

# 7. v1 不支持 `x-*` 自定义扩展

我暂时不建议：

```json
"x-my-plugin": {}
```

也不建议：

```json
"extensions": {}
```

然后 StowCrate 无条件忽略。

因为我们还没有插件执行模型。

如果未来确实需要扩展机制，可以专门定义：

```text
Extension Semantics
Capability Declaration
Required / Optional
Namespace
Round-trip preservation
```

在这之前：

> **未知就是错误。**

不要现在为了“看起来可扩展”留下一个安全洞。

------

# 8. 同一个 schemaVersion 不能随意增加字段

Closed World 会带来一个自然结果：

假设：

```text
schemaVersion = 1
```

正式发布以后，我们又新增：

```json
"newFeature": ...
```

即使 `newFeature` 是 optional：

> 也不能继续声称还是相同的 v1 contract。

否则旧 v1 reader 会拒绝新 v1 文档。

所以建议：

> **任何会扩展已发布文档可接受字段集合的改变，原则上 bump schemaVersion。**

包括：

```text
新增字段
新增 enum value
新增 union variant
新增 trigger kind
新增 retention kind
```

------

# 9. 但不是所有程序语义变化都需要 bump schemaVersion

这里必须区分：

```text
DocumentSchemaVersion
```

和：

```text
RulesSemanticsVersion
ArchiveSemanticsVersion
OutputPathEncodingVersion
FingerprintFormatVersion
...
```

例如：

```text
JSON 结构完全没变
```

只是 fingerprint canonical encoding 算法升级：

```text
FingerprintFormatVersion 1 → 2
```

没必要：

```text
schemaVersion 1 → 2
```

反过来，如果：

> 相同 `schemaVersion=1` 文档在新版应用中会产生不同备份语义，

并且没有单独的显式 semantics version 可以固定旧行为，

那么必须：

```text
bump schemaVersion
```

或者给文档增加明确 version pin 并保持旧实现。

核心原则：

> **同一受支持文档不能因为升级 StowCrate 而悄悄改变含义。**

------

# 10. Default Value 也是 Schema Contract

例如：

```text
history.enabled
```

如果字段缺失，v1 定义：

```text
false
```

以后不能仍然 schemaVersion=1，却改成：

```text
missing → true
```

因为同一个文件含义变了。

因此：

> **每一个 omitted optional field 的 default 都属于版本化 document semantics。**

ResolvedPlanSnapshot 应始终展开默认值。

所以：

```text
字段省略
```

和：

```text
显式写默认值
```

最终：

```text
PlanSemanticFingerprint
ExecutionSemanticFingerprint
```

应该相同。

------

# 11. Fingerprint 不包含 JSON 表现形式

例如：

```json
{"name":"Code","schemaVersion":1}
```

和：

```json
{
  "schemaVersion": 1,
  "name": "Code"
}
```

只要语义相同：

```text
PlanSemanticFingerprint = identical
```

所以不能：

```text
SHA256(raw backupplan bytes)
```

当 semantic fingerprint。

应该：

```text
JSON
↓
Version-specific parser
↓
Validated document model
↓
Resolved semantic model
↓
Canonical semantic fingerprint
```

当前已经形成的 semantic fingerprint 分层继续保持。

------

# 12. JSON Property Name 大小写必须严格

建议：

```text
schemaVersion
```

合法。

```text
SchemaVersion
SCHEMAVERSION
```

非法。

不要开启：

```csharp
PropertyNameCaseInsensitive = true
```

用于 `.backupplan`。

因为 Config-as-Code 应具有唯一、稳定写法。

------

# 13. Duplicate JSON Property 必须拒绝

例如：

```json
{
  "schemaVersion": 1,
  "history": {
    "enabled": true
  },
  "history": {
    "enabled": false
  }
}
```

不同 JSON parser 可能：

```text
first wins
last wins
```

这对备份配置不可接受。

因此：

```text
DuplicateProperty
→ InvalidDocument
```

即使 JSON library 默认接受，也要主动检测。

------

# 14. v1 使用严格 JSON

建议不支持：

```text
// comment
/* comment */
trailing comma
NaN
Infinity
single quote
```

只接受标准 JSON。

原因：

```text
*.backupplan
```

目标是：

- Git；
- 跨语言；
- IDE Schema；
- 长期保存；
- 可被普通 JSON 工具读取。

用户说明信息用：

```text
name
description
```

等正式 metadata 字段。

不要做 JSONC。

------

# 15. Encoding

建议：

```text
UTF-8
```

作为唯一编码。

可接受：

```text
UTF-8 BOM
```

但 writer 默认输出：

```text
UTF-8 without BOM
```

这样和仓库现有 UTF-8/LF 策略一致。

不要支持：

```text
UTF-16
GBK
系统 ANSI
```

------

# 16. Unsupported Future Version：绝不能降级读取

假设用户用 StowCrate 2 创建：

```json
{
  "schemaVersion": 3
}
```

然后拿到只支持：

```text
1 / 2
```

的 StowCrate。

行为：

```text
UnsupportedSchemaVersion
Document requires newer StowCrate
```

禁止：

```text
“我试着把它当 v2 读一下”
```

更不能：

```text
忽略未知内容并执行
```

------

# 17. 旧版本文档由显式 Migrator 处理

未来：

```text
Current Schema = 4
```

读取：

```text
schemaVersion = 1
```

推荐架构：

```text
BackupPlanDocumentV1
        ↓
V1 Validator
        ↓
Document Migrator
        ↓
Current Semantic Model
        ↓
ResolvedPlanSnapshot
```

而不是让最新 DTO：

```csharp
BackupPlanDocument
```

带一百个 nullable 字段兼容所有年代。

应该：

```text
DocumentV1
DocumentV2
DocumentV3
```

版本边界明确。

------

# 18. Migrator 是内存迁移，不自动改文件

File-backed：

```text
Old.backupplan
schemaVersion = 1
```

新版 StowCrate 能读：

```text
v1
↓
migrate in memory
↓
execute
```

但不能：

```text
打开一次
↓
自动覆盖成 schemaVersion 4
```

尤其不能污染 Git working tree。

这和 `.backupignore @id` 不自动写入的原则一致。

------

# 19. Upgrade Document 必须是显式操作

未来可以有：

```text
Upgrade Backup Plan
```

用户明确执行：

```text
v1
↓
preview semantic changes
↓
write v4
```

才修改 File-backed document。

如果升级理论上 semantic-preserving：

也应该：

> 明确告诉用户文件将被修改。

------

# 20. Managed Import 可以透明迁移到当前内部模型

对于：

```text
Import v1 document
```

因为最终 authority 变成：

```text
MANAGED
```

可以：

```text
parse v1
↓
migrate semantic model
↓
存入当前 config.db model
```

原文件完全不修改。

以后 Export：

```text
使用当前 writer
→ 当前 schemaVersion
```

这是合理的。

------

# 21. File-backed Save 时不要假装还能写旧格式

未来如果应用：

```text
能读 v1
但 writer 只支持 v4
```

用户编辑 v1 File-backed Plan 后：

不能：

```text
偷偷改成 v4 保存
```

建议返回：

```text
DocumentUpgradeRequired
```

用户明确执行：

```text
Upgrade & Save
```

才允许。

这能避免大块 Git diff 突然出现。

------

# 22. Reader Compatibility ≠ Platform Capability

必须区分两个阶段。

例如：

```text
schemaVersion = 1
format = tar.zst
protection = secure
```

文档完全符合 v1 schema。

但当前 adapter 不支持某组合。

那么：

```text
Schema Validation ✅
Semantic Validation ✅
Capability Validation ❌
UnsupportedArchiveCapability
```

不能报告：

```text
InvalidSchema
```

同理：

```text
HistoryRoot missing
```

是：

```text
PlanNotReady
```

不是 schema invalid。

建议正式形成：

```text
Parse
↓
Document Schema Validation
↓
Semantic Validation
↓
Local Binding Resolution
↓
Capability Validation
↓
Readiness
↓
Execution
```

错误不要混在一起。

------

# 23. 文档本身有效 ≠ 当前设备可执行

比如 File-backed Plan：

```text
SecretSlot 正确
SourceId 正确
History 正确
```

但是新电脑没有：

```text
SourceBinding
CurrentRoot
SecretBinding
```

它依然是：

```text
Valid BackupPlanDocument
```

只是：

```text
PlanNotReady
```

这和当前 portable/local 分层完全一致。

------

# 24. 未知字段不能做 Round-trip Preservation

因为 v1 直接拒绝未知字段，所以不存在：

```text
“我虽然不懂，但替用户保存着”
```

这种复杂行为。

这是我推荐 closed-world 的另一个好处。

否则 File-backed GUI 保存时必须解决：

```text
未知字段该放哪？
位置？
顺序？
未知字段引用已经删除对象怎么办？
```

复杂度非常高。

------

# 25. Writer 必须只写合法状态

不要：

```text
domain object
→ serializer
→ 希望结果合法
```

Writer 输出前：

```text
Current semantic model
↓
Document projection
↓
Schema validation
↓
write temp
↓
read/validate roundtrip
↓
atomic replace
```

File-backed 的写操作必须尤其保守。

虽然具体实现以后做，但这条契约现在值得定下。

------

# 26. Schema Version 不进入 Archive Fingerprint

例如未来：

```text
v1 document
```

迁移后语义：

```text
X
```

以及：

```text
v2 document
```

解析后也是：

```text
X
```

那么：

```text
EntrySetFingerprint
SelectionFingerprint
ArchiveSpecFingerprint
ExecutionSemanticFingerprint
```

都应该一样。

因此：

```text
DocumentSchemaVersion
```

本身不进入 archive semantic fingerprints。

真正进入的是：

> resolved semantics。

------

# 27. PlanSemanticFingerprint 也应尽量是 semantic，而不是 schema identity

如果：

```text
v1 → v2
```

只是文档结构迁移，没有 desired configuration 改变：

```text
PlanSemanticFingerprint
```

也建议保持一致。

这样显式 Schema Upgrade 不会：

```text
错误触发 scheduler reconcile
output relocation
archive rebuild
```

除非迁移本身真的改变了配置语义。

------

# 28. Schema Evolution 分类建议

以后修改 `*.backupplan` 时按照下面判断：

### A. 文档完全不受影响

例如：

```text
修文档 typo
改善错误消息
优化实现
```

不 bump。

### B. 算法变化，但已有显式 semantics version

例如：

```text
FingerprintFormatVersion
1 → 2
```

更新对应 semantics version。

不一定 bump document schema。

### C. 新增/删除/改变字段结构

```text
schemaVersion++
```

### D. 新增 enum / union variant

```text
schemaVersion++
```

### E. 修改字段默认含义

如果没有其他显式 version pin：

```text
schemaVersion++
```

### F. 同一个旧文档会产生不同 desired configuration

必须：

```text
版本化
```

绝不能静默改变。

------

# 29. 已发布版本尽量永久保留 Reader

StowCrate 本身强调长期可恢复，我建议做一个较强的产品承诺：

> **已正式发布的 `\*.backupplan` schema version，未来版本应尽可能持续保留 read/import migration 支持。**

Writer 不需要永久支持所有版本。

可以：

```text
Read v1/v2/v3/v4
Write v4 only
```

这样维护成本合理。

只有：

- 严重安全问题；
- 格式根本无法安全解释；

才考虑停止支持，并必须给迁移说明。

------

# 30. Git 历史因此非常有价值

例如用户：

```bash
git checkout 2027-old-config
```

得到：

```text
schemaVersion = 1
```

几年后的 StowCrate：

```text
read v1
↓
migrate
↓
ResolvedPlanSnapshot
```

仍能理解。

这才真正实现 Config-as-Code 的长期价值。

------

# 31. Error Model 建议正式区分

至少：

```text
MalformedJson
InvalidEncoding
MissingSchemaVersion
InvalidSchemaVersion
UnsupportedSchemaVersion

DuplicateProperty
UnknownProperty
MissingRequiredProperty
InvalidPropertyValue
UnknownEnumValue
UnknownVariant

SchemaValidationFailed
DocumentMigrationFailed
UnsupportedDocumentSemantics
```

然后再进入后面的：

```text
IdentityConflict
PlanNotReady
UnsupportedArchiveCapability
MissingBinding
...
```

不要把所有问题都叫：

```text
InvalidPlan
```

------

# 32. `$schema` 可以以后加入，但不 authoritative

未来为了 IDE：

```json
{
  "$schema": "...",
  "schemaVersion": 1
}
```

可以很有用。

但建议：

```text
schemaVersion
```

才是 StowCrate reader 的权威版本字段。

`$schema`：

> IDE/editor discovery metadata。

即使 URI 失效：

> 不影响 StowCrate 读取。

具体 URI 等正式 JSON Schema 时再定。

------

# 33. 当前 v1 最重要的兼容性承诺可以压缩成 8 条

建议直接写进 `BACKUPPLAN.md`：

1. `schemaVersion` 必填且为正整数。
2. 已知版本使用对应 closed-world schema；未知 property/enum/variant 一律拒绝。
3. 未来 schemaVersion 旧 reader 安全失败，不尝试降级执行。
4. 已发布 schema 的新增字段、enum/variant、默认语义变化原则上需要新 schemaVersion。
5. Schema version 与独立 execution/fingerprint semantics version 分离。
6. 老版本由显式 version-specific reader + migrator 转换到当前 semantic model；读取不得自动改写 File-backed 文件。
7. Document schema/version/formatting 本身不进入 archive fingerprints；fingerprint 基于 resolved semantics。
8. Schema validity、semantic validity、local readiness 和 platform capability 是不同验证阶段。

------

# 34. 这次建议让 Codex顺手明确验证流水线

非常值得：

```text
Raw *.backupplan bytes
        ↓
UTF-8 / JSON parsing
        ↓
schemaVersion dispatch
        ↓
Version-specific closed schema validation
        ↓
Version-specific semantic validation
        ↓
Migration to current semantic model
        ↓
Plan authority resolution
        ↓
Device Local Binding
        ↓
ResolvedPlanSnapshot
        ↓
Capability / Readiness validation
        ↓
Execution
```

这样以后 JSON Schema、parser、Application resolver 的职责不会混。

------

# 35. 仍然不要创建真正 JSON Schema

这一轮 Codex 应只：

- 更新 `BACKUPPLAN.md`；
- 必要时同步 `ARCHITECTURE.md / PRODUCT.md / AGENTS.md`；
- 更新设计工作稿；
- **不要创建 `backupplan-v1.schema.json`**；
- 不实现 serializer；
- 不实现 migrator；
- 不改 SQLite。

因为还剩最后一个：

> **Import Identity Conflict / Merge Semantics**

这个会决定 Import、Update、Clone 到底怎样解释对象集合，最终仍可能影响 document constraints。

------

## 要求：

> 本轮采用 closed-world compatibility：不要为了 forward compatibility 设计“忽略未知字段”或任意 extension bag。若认为某项需要 extension mechanism，请作为未来未决项记录，不得加入 v1。完成后将下一个且最后一个 Backup Plan P0 推进为 Import Identity Conflict / Merge Semantics。

这个 P0 完成后，我们再设计最后的 Import/Update/Clone 冲突语义。**最后一个 P0 定完，就可以正式冻结 Backup Plan v1 领域模型并开始 JSON Schema。**