# Backup Plan v1 Domain Freeze Review

这一步的目标只有一个：

> 检查现有正式规范能否形成一套自洽、完整、可实现、可以无歧义投影成 JSON Schema 和领域模型的 Backup Plan v1。

如果 Review 没有 blocker，就把 Backup Plan v1 标记为 **Domain Frozen**。

## Review 重点

建议 Codex 横向审查：

```text
docs/BACKUPPLAN.md
docs/BACKUPIGNORE.md
docs/CHANGE-DETECTION.md
docs/FILESYSTEM.md
docs/ARCHITECTURE.md
docs/PRODUCT.md
docs/AGENTS.md
```

重点不是文字润色，而是检查以下几类问题。

### 1. Identity / Reference 完整性

检查所有 portable identity：

```text
PlanId
SourceId
ArchiveUnitId
ExternalSourceId
SecretSlotId
```

确认：

- 谁定义；
- 谁引用；
- Clone 如何重写；
- Import/Update 如何保持；
- 是否存在 dangling reference；
- 是否存在某对象在 Schema 中必须有 ID，但领域规范没有生命周期规则。

特别检查：

```text
ExternalSource.TargetArchiveUnitId
ArchiveSpec Secure → SecretSlotId
ArchiveUnit → SourceId
```

------

### 2. Portable / Local 边界

确保这些绝不会进入 `.backupplan`：

```text
SourceRoot
CurrentRoot
HistoryRoot
External physical path
SecretReference
SecretRevision
Scheduler native identity
ArchiveVersion
Baseline
DeviceId
```

同时确认 portable document 又确实包含足够信息，可以在另一台机器：

```text
Import/Register
→ rebind
→ PlanReady
```

不能出现“为了执行还需要某个未定义、又不在 Plan 也不在 Local Binding 的第三类配置”。

------

### 3. 三类 Semantic Fingerprint

这是 Review 最重要的部分之一。

逐字段核查：

```text
PlanSemanticFingerprint
ExecutionSemanticFingerprint
ExecutionBindingFingerprint
```

以及：

```text
EntrySetFingerprint
SelectionFingerprint
ArchiveSpecFingerprint
OutputLayoutFingerprint
ScheduleSemanticFingerprint
```

确认没有同一字段在不同文档中分类冲突。

尤其检查：

```text
ArchiveUnitId
ExternalSourceId
SourceId
SourceOutputPath
History Enabled
RetentionPolicy
ScheduleIntent
SecretSlotId
SecretRevision
External physical binding
ArchiveSpec override inheritance
```

------

### 4. “表达变化”与“Effective 变化”

我们现在有多个继承模型：

```text
ArchiveSpecDefault
→ ArchiveSpecOverride
→ EffectiveArchiveSpec

HistoryDefault
→ HistoryOverride
→ EffectiveHistoryPolicy
```

需要确保统一原则：

> 配置表达改变但 Effective execution semantics 不变，可以改变 PlanSemanticFingerprint，但不能错误触发 rebuild/publish cancellation。

例如：

```text
explicit Standard
→ inherit
```

当前 default 仍为 Standard。

应该：

```text
Plan semantic changed
EffectiveArchiveSpec unchanged
ArchiveSpecFingerprint unchanged
```

这类规则要在所有 override/inheritance 模型里一致。

------

### 5. FILE_MANAGED 职责边界

最终必须没有任何模糊之处：

```text
.backupignore
负责：
- Archive Unit existence
- optional @id
- RuleMode
- Case
- Local Rules
```

而 Plan declaration 负责：

```text
- identity association
- Source/path
- ArchiveSpecOverride
- HistoryOverride
- ExternalSource targetability
```

确认没有任何地方让 SQLite/Plan 再保存 FILE_MANAGED 的 Rules。

------

### 6. External Source 与普通 Source Entry 的统一点/差异点

确认最终模型清楚区分：

```text
Normal entry:
Scanner → Rules

External entry:
Explicit declaration → no-follow observation → staging
```

但两者之后统一进入：

```text
Candidate Archive Unit
EntrySet
Collision validation
Completeness
Change Detection
Archiver
```

特别检查：

> ExternalSource 是否真的不需要第二套 Scanner domain model。

如果现有 `SourceSnapshot` 无法表达 External observed tree，不要在 Freeze Review 里草率把 External 塞进去；应该明确需要：

```text
ExternalSourceSnapshot
```

或等价纯数据结构。

这是我认为 Review 很可能发现的一个真正领域模型缺口。

------

### 7. Archive Boundary 一致性

确认三个地方都遵循同样原则：

```text
Normal Source traversal
FILE_MANAGED child unit
External ArchiveDestination
```

必须保持：

> Child Archive Unit 是不可穿透的 archive namespace boundary。

不能 Rules 穿透，也不能 ExternalSource 写进去。

------

### 8. Current / History / ArchiveVersion

确认没有混淆：

```text
Archive bytes semantics
Output location semantics
Version identity
Baseline identity
```

特别检查：

```text
format change
SourceOutputPath change
CurrentRoot relocation
HistoryRoot relocation
```

分别应该是：

```text
format change
→ rebuild + output extension change

SourceOutputPath change
→ output reorganization, no recompression

CurrentRoot/HistoryRoot change
→ storage relocation

以上后两者
→ 不生成新 ArchiveVersion
→ 不推进 baseline
```

------

### 9. Readiness / Validation 错误层次

现在已经有很多状态。

Review 应统一成至少：

```text
Document invalid
Semantic invalid
Identity conflict
PlanNotReady
Capability unsupported
Incomplete observation
Execution semantic drift
Runtime execution failure
Maintenance warning
```

避免以后实现出现：

```text
所有错误都 throw InvalidOperationException
```

或者多个文档对同一个错误给不同分类。

------

### 10. V1 Feature Closure

最后检查：

> 有没有文档里写“支持”，但 Schema-shaping 设计其实还没完成的东西。

尤其搜索：

```text
TODO
TBD
future
未决
以后
prototype
可能
placeholder
```

然后分类：

### 可以冻结的 Future Capability

例如：

```text
split volume
Privacy carrier
Recovery Package
arbitrary cron
optional External Source
metadata configurable policy
```

明确：

> 不属于 Backup Plan v1 Schema。

### 会阻塞 v1 Schema 的未决项

如果还存在任何：

> “这个字段到底放在哪里/到底是谁 authoritative”

就不能 Freeze。

------

# 我建议让 Codex 产出一个正式 Freeze Review 文档

例如：

```text
docs/reviews/BACKUPPLAN-v1-DOMAIN-FREEZE-REVIEW.md
```

结构建议：

```text
# Backup Plan v1 Domain Freeze Review

## Result
PASS / PASS WITH REQUIRED FIXES / BLOCKED

## Normative Documents Reviewed

## Domain Aggregate Inventory

## Portable vs Local State Matrix

## Identity / Reference Matrix

## Fingerprint Classification Matrix

## Validation / Readiness Matrix

## Inheritance / Effective Semantics Matrix

## Cross-document Conflicts Found

## Required Fixes Applied

## Explicitly Deferred v1 Features

## Schema Readiness Decision
```

这份文件以后很有价值，因为我们可以知道：

> JSON Schema 是基于哪一套已经冻结的领域规则生成的。

------

# 本次任务

> 对当前 StowCrate Backup Plan v1 执行一次 **Domain Freeze Review**。
>
> 本轮目标不是增加新功能，不创建 JSON Schema，不实现 serializer、SQLite、Archiver 或业务代码。
>
> 横向审查 `BACKUPPLAN.md`、`BACKUPIGNORE.md`、`CHANGE-DETECTION.md`、`FILESYSTEM.md`、`ARCHITECTURE.md`、`PRODUCT.md`、`AGENTS.md`，重点检查：
>
> - portable identity/reference 完整性及 Clone/Import 生命周期；
> - Portable Configuration / Device Local State 边界；
> - PlanSemantic / ExecutionSemantic / ExecutionBinding 以及 EntrySet / Selection / ArchiveSpec / OutputLayout / Schedule fingerprint 分类是否一致；
> - default/override/inherit 与 Effective semantics 是否一致；
> - FILE_MANAGED authority 是否存在双真相源；
> - External Source 与 normal source 的 observation/Candidate/completeness 边界是否有领域模型缺口；
> - Archive Boundary 是否在 Rules、External Source 和 Planning 中一致；
> - Current/History/ArchiveVersion/Baseline/relocation/reorganization 是否存在冲突；
> - schema validity、semantic validity、readiness、capability、execution failure 等错误层是否一致；
> - 搜索所有 TODO/TBD/未决/future/prototype 等内容，区分“明确延后且不影响 v1 Schema”与“仍阻塞 Schema 的领域问题”。
>
> 如发现 blocker，优先修正文档使规范一致，并在 Review 中记录原冲突与最终决定；不要自行扩展 v1 产品范围。
>
> 新增 `docs/reviews/BACKUPPLAN-v1-DOMAIN-FREEZE-REVIEW.md`，给出 `PASS / PASS WITH REQUIRED FIXES / BLOCKED` 结论和完整矩阵。
>
> 只有在不存在 schema-shaping blocker 时，才将 Backup Plan v1 标记为 **Domain Frozen / Ready for JSON Schema Design**。
>
> 本轮不要创建 `backupplan-v1.schema.json`。

## Review 通过后的顺序

我建议之后严格按：

```text
Domain Freeze Review
        ↓
Backup Plan v1 Domain Frozen
        ↓
JSON Schema structure design
        ↓
backupplan-v1.schema.json
        ↓
BackupPlanDocumentV1 DTO
        ↓
strict reader / writer
        ↓
semantic mapper / validator
        ↓
Persistence model
        ↓
config.db schema
```

尤其不要直接从 JSON Schema 映射 EF Entity。我们之前已经确定 `.backupplan`、领域模型和 SQLite persistence 是三个不同边界。

现在已经非常接近 M3 真正进入实现阶段了；**Freeze Review 是最后一次适合大范围修改领域规范的节点**。之后 Schema 一旦冻结，再改这些核心语义的成本会明显上升。