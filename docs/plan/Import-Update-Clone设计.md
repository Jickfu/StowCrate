这一步完成后，**最后一个正式 P0 就是 Import Identity Conflict / Merge Semantics**。当前规范已经确定：Import 默认保留 portable IDs；Clone 才递归生成新 IDs；同一设备不能把同一个 `PlanId` 当成两个独立 Plan；相同 `PlanId` 但语义不同必须报 `IdentityConflict`，只是 `Update existing / Clone / Cancel` 的具体行为尚未定。

我建议 v1 的核心原则是：

> **不提供自动 Merge。Import/Update 都是 identity-aware 的完整声明替换（whole-document replacement），不是字段级合并。**

这样最符合备份软件的保守原则。

# 1. 先正式区分四种操作

最终建议形成：

```text
Import
Update Existing
Clone
Register
```

其中：

### Import

```text
*.backupplan
→ Managed Plan
```

只用于当前设备上不存在该 `PlanId` 的情况。

### Update Existing

```text
incoming document
+
existing Managed Plan
→ replace portable desired configuration
```

要求：

```text
incoming.PlanId == existing.PlanId
```

保留 identity，根据 ID 做对象级对应。

### Clone

```text
incoming document
→ recursively regenerate portable IDs
→ new Managed Plan
```

### Register

```text
*.backupplan
→ File-backed registration
```

文件继续 authoritative。

------

# 2. v1 明确“不支持 Merge”

不要提供这种东西：

```text
Existing Plan
      +
Incoming Plan
      ↓
自动猜哪些字段用谁的
```

也不要：

```text
名称相同 → merge
path 相同 → merge
Source 看起来相同 → merge
ArchiveUnit 内容类似 → merge
```

v1 的 Merge 规则应该直接是：

> **There is no merge.**

只有：

```text
Replace same identity
Clone to new identity
Cancel
```

这样 Git 本身负责 File-backed 文档的文本 merge；StowCrate 不再发明第二套配置 merge 算法。

------

# 3. 同 PlanId + 同语义：幂等

例如已有 Managed：

```text
PlanId = A
PlanSemanticFingerprint = X
```

Import：

```text
PlanId = A
PlanSemanticFingerprint = X
```

结果不应该报严重冲突。

建议：

```text
AlreadyExistsSameSemantic
```

然后：

```text
NoOp
```

不：

- 修改 PlanRevision；
- 影响 baseline；
- 重装 scheduler；
- rebuild；
- 修改 binding。

这使重复 Import 成为安全幂等操作。

------

# 4. 同 PlanId + 不同语义

这是核心情况：

```text
Existing:
PlanId = A
Semantic = X

Incoming:
PlanId = A
Semantic = Y
```

返回：

```text
IdentityConflict
```

用户只允许：

```text
Update Existing
Clone As New
Cancel
```

禁止：

```text
Auto Merge
Auto Overwrite
Auto Generate New PlanId
```

尤其不能因为冲突偷偷 Clone，否则用户会不知不觉产生两个几乎相同的备份计划。

------

# 5. Update Existing 是“完整 portable configuration 替换”

这是本 P0 最重要的语义。

例如：

```text
Existing Plan
├─ Source A
├─ Source B
└─ Source C

Incoming Plan
├─ Source A
├─ Source B
└─ Source D
```

Update 后：

```text
Active Plan
├─ Source A
├─ Source B
└─ Source D
```

不是：

```text
A + B + C + D
```

所以：

> Incoming document 是完整 desired state，不是 patch。

这和 Kubernetes/Terraform 一类声明式配置的思想更接近。

------

# 6. 对象匹配只看稳定 ID

Update Existing 时：

```text
SourceId
ArchiveUnitId
ExternalSourceId
SecretSlotId
```

是唯一对应依据。

例如 Existing：

```text
SourceId = A
Name = Code
```

Incoming：

```text
SourceId = A
Name = Development
```

这是：

```text
Modify existing Source A
```

而不是删除+新增。

反过来：

```text
Existing:
SourceId = A
Name = Code

Incoming:
SourceId = B
Name = Code
```

即使名称完全一样，也必须解释为：

```text
Remove A
Add B
```

绝不基于 Name 猜 identity。

------

# 7. Logical Path 也不能拿来猜 Identity

Existing：

```text
ArchiveUnitId = A
Path = projects/foo
```

Incoming：

```text
ArchiveUnitId = B
Path = projects/foo
```

必须：

```text
Remove A
Add B
```

而：

```text
ArchiveUnitId = A
Path:
projects/foo
→
projects/bar
```

才是：

```text
same ArchiveUnit
logical relocation/rename
```

后续由已经确定的 fingerprint/output semantics 判断：

- 是否 rebuild；
- 是否 output reorganization；
- 是否 baseline 可继续比较。

------

# 8. Update Existing 不应天然丢弃 Baseline

这点非常重要。

对于 Incoming 与 Existing 都有：

```text
ArchiveUnitId = U
```

保留：

```text
ArchiveVersion
CurrentVersion
Committed Baseline
History
```

然后让 Change Detection 判断。

例如只改：

```text
Schedule
02:00 → 03:00
```

那么：

```text
Baseline 保留
Archive fingerprints 一致
→ 不 rebuild
```

如果改：

```text
Compression
Fast → Ultra
```

则：

```text
ArchiveSpecFingerprint changed
→ RebuildRequired
```

如果改：

```text
SourceId
```

则 Selection 变化。

因此：

> **Update Existing 不应该简单地“清空所有 baseline 再重新备份”。**

那会浪费我们之前设计的整个强类型 fingerprint 系统。

------

# 9. 新 ArchiveUnitId 没有 Baseline

Incoming 新增：

```text
ArchiveUnitId = NEW
```

自然：

```text
No Baseline
→ FirstBackup
```

不需要额外规则。

------

# 10. 删除 ArchiveUnitId 绝不能自动删除 Current / History

例如：

```text
Existing:
B
D
F

Incoming:
B
F
```

D 从 desired configuration 删除。

不能立即：

```text
rm D.7z
rm History/D/*
```

因为那是破坏性数据删除。

建议：

```text
ArchiveUnit D
→ no longer active in Plan
```

它原来的：

```text
Current
History
ArchiveVersions
Baseline
```

进入：

> retained inactive/recovery state

而不是立即物理清除。

------

# 11. 清理遗留 Archive Unit 是独立 destructive operation

以后提供：

```text
Clean Removed Archive Units
```

或：

```text
Delete retained backup data
```

明确列出：

```text
D Current      2.3 GB
D History      18.7 GB
```

用户确认后才删。

这与：

```text
History Disabled ≠ Purge History
```

保持完全一致。

------

# 12. Git 回滚也因此很好用

例如 File-backed Plan：

今天：

```text
B D F
```

明天 commit 删除 D：

```text
B F
```

之后又：

```bash
git revert
```

恢复：

```text
B D F
```

因为 D 的：

```text
ArchiveUnitId
```

没变，而且旧 runtime state 没被自动销毁：

StowCrate 可以重新验证：

```text
Current
ArchiveVersion
Baseline
```

并重新关联。

这对 Config-as-Code 很有价值。

当然必须重新做 artifact/baseline 完整性验证，不能盲目信任陈旧数据库状态。

------

# 13. Local Binding 如何处理

Update Existing 时：

### identity 仍存在

例如：

```text
SourceId = A
```

Existing / Incoming 都存在。

本机：

```text
A → E:\code
```

继续保留。

因为 binding 不属于 portable document。

------

### 新 identity

Incoming：

```text
SourceId = NEW
```

没有 Local Binding。

结果：

```text
PlanNotReady
MissingSourceBinding
```

直到用户绑定。

------

### identity 被删除

Existing：

```text
SourceId = OLD
```

Incoming 不再存在。

本机 binding 不要立即物理删除。

可以变成：

```text
inactive/detached local state
```

至少在真正的 cleanup 操作前保留。

------

# 14. Secret Slot 同理

Incoming 新增：

```text
SecretSlotId = S2
```

Secure Archive 使用 S2。

本机没有绑定：

```text
PlanNotReady
MissingSecretBinding
```

不能拿：

```text
旧 SecretSlot Name 一样
```

就自动匹配。

删除 SecretSlot：

> 不自动删除 OS Secret。

已经有正式 Secret 生命周期规则，这里继续复用即可。

------

# 15. Schedule Installation 同样不能混入 Import 事务

例如 incoming：

```text
Daily 02:00
→ Weekly Monday 03:00
```

Update Existing 的 authoritative Plan transaction 成功后：

```text
ScheduleOutOfSync
```

然后执行独立：

```text
Scheduler Reconcile
```

不要把：

```text
修改 native scheduler
```

做成 Import 数据库事务的一部分。

因为现有规范已经明确：

```text
Plan config save
≠
native scheduler installation
```

------

# 16. Output / History 状态也一样

Incoming 改：

```text
SourceOutputPath
Code → Development
```

Update 可以成功提交 portable desired configuration。

运行状态变：

```text
OutputReorganizationRequired
```

不是：

```text
立刻移动 Current
```

Incoming 改：

```text
History Disabled → Enabled
```

但没有 HistoryRoot：

```text
Plan updated successfully
+
PlanNotReady / MissingHistoryRootBinding
```

这体现：

> config validity 和 device readiness 是两件事。

------

# 17. Update Existing 应先给 Semantic Diff

这会成为 UI 很重要的一部分。

更新之前：

```text
Incoming Document
        ↓
Current semantic model
        ↓
Existing semantic model
        ↓
PlanSemanticDiff
```

建议分类：

```text
Metadata
Added
Removed
Modified

ExecutionCritical
ArchiveRebuild
OutputReorganization
HistoryChange
ScheduleChange
BindingRequirementChange
SecretRequirementChange
```

比如：

```text
2 Sources unchanged
1 Archive Unit added
1 Archive Unit removed
3 Rules changed
Compression changed: Normal → Ultra
Schedule changed: 02:00 → 03:00
New Secret binding required
```

然后用户明确选择：

```text
Update Existing
```

------

# 18. 不要直接 diff JSON

Semantic Diff 应该比较：

```text
validated + migrated + defaults-expanded semantic model
```

不是 raw JSON。

因此：

```text
property order
formatting
旧 schema 的结构差异
显式默认 vs 省略默认
```

都不会产生假 diff。

和 fingerprint 的原则完全一致。

------

# 19. Update Existing 应该是原子的 config operation

推荐：

```text
Parse
↓
Validate
↓
Migrate
↓
Resolve same PlanId
↓
Build semantic diff
↓
User confirms
↓
BEGIN config transaction
↓
replace authoritative portable configuration
↓
preserve applicable local/runtime state by identity
↓
mark reconciliation/readiness states
↓
COMMIT
```

在 commit 前：

> Existing Plan 必须完全保持有效。

不能更新一半：

```text
Sources 已更新
ArchiveUnits 还没更新
```

------

# 20. 外部副作用全部在 commit 之后

包括：

```text
Scheduler reconcile
Storage relocation
Output reorganization
History maintenance
Secret binding
```

都不是 Update transaction 的组成部分。

Update 之后可以出现：

```text
Updated, but PlanNotReady
Updated, ScheduleOutOfSync
Updated, OutputReorganizationRequired
```

这都是合法状态。

------

# 21. 如果 Update 后暂时 PlanNotReady，不要回滚

例如新 Plan 要求：

```text
ExternalSource S
```

本机还没有 binding。

用户明确接受 Semantic Diff 并 Update：

```text
Config update = Success
Readiness = PlanNotReady
```

这是正确的。

否则 portable Plan 在新设备上的 Import 很难工作。

------

# 22. 但 Document 本身无效时不能 Update

以下任何情况：

```text
Schema invalid
Duplicate identity
Unknown field
Broken reference
Semantic invalid
```

必须在修改 Existing 前失败。

所以：

```text
Invalid incoming document
→ Existing untouched
```

这是硬约束。

------

# 23. Register 的冲突规则和 Import 不完全一样

## 没有同 PlanId

正常：

```text
Register
→ new FileBacked registration
```

------

## 已经是相同 File-backed registration

同 path，同 PlanId，同语义：

```text
NoOp / AlreadyRegistered
```

幂等。

------

## 同 PlanId 已 File-backed，但是另一个文件路径

不能创建第二个 registration。

返回：

```text
RegistrationConflict
```

只允许：

```text
Relocate Existing Registration
Clone As New
Cancel
```

------

# 24. File-backed registration relocation

例如：

```text
E:\config\Code.backupplan
→
D:\git\backup\Code.backupplan
```

目标文件：

```text
PlanId = same
```

用户明确选择：

```text
Relocate Registration
```

才改变 registration path。

如果目标文件语义和原文件不同：

> 必须先显示 Semantic Diff。

但仍然不是 Merge。

新文件一旦成为 registration target：

```text
它整体成为 authoritative document
```

------

# 25. Managed Plan 遇到 Register same PlanId

例如：

```text
Existing = MANAGED
Incoming Register = PlanId A
```

这不是普通 Register。

应该：

```text
AuthorityConflict
```

用户选择：

```text
Convert Managed → File-backed
Clone As New
Cancel
```

如果选择 Convert：

1. 验证文件；
2. 同 PlanId；
3. 显示 Semantic Diff；
4. 明确告诉用户 authority 将改变；
5. 原子切换 authority；
6. 文件成为唯一 configuration truth。

不能静默 Register 出第二个 Plan。

------

# 26. File-backed 遇到 Import same PlanId

反方向一样。

```text
Existing = FILE_BACKED
Import PlanId A
```

普通 Import：

```text
AuthorityConflict
```

允许：

```text
Convert File-backed → Managed
Clone As New
Cancel
```

转换完成后：

```text
config.db
```

成为唯一 truth。

原 `.backupplan` 以后修改：

> 不再影响 Plan。

------

# 27. 同 PlanId 相同语义也不能偷偷切 Authority

例如：

```text
Managed A
```

Register：

```text
File A
完全相同
```

仍然不能说：

> “反正一样，我直接变成 File-backed。”

Authority change 是有产品后果的：

```text
以后由谁控制配置
```

所以必须显式。

------

# 28. File-backed 文件自己改动不属于 Import

这个边界也要写清楚。

注册后：

```text
Code.backupplan
```

从：

```text
semantic X
→
semantic Y
```

这不是：

```text
IdentityConflict
```

因为文件本来就是 authority。

它就是：

```text
desired configuration changed
```

Application 重新解析，保留 runtime state by identity，再根据：

```text
ExecutionSemanticFingerprint
Archive fingerprints
```

决定后续动作。

------

# 29. 但 File-backed 文件中的 PlanId 改变必须 Fatal

例如 registration 原来绑定：

```text
PlanId = A
```

用户手工改文件：

```text
PlanId = B
```

绝不能让现有 registration：

```text
A
```

悄悄变成：

```text
B
```

建议：

```text
RegisteredDocumentIdentityChanged
→ PlanNotReady
```

解决方式必须显式：

```text
Restore original PlanId
Unregister + Register as new Plan
Clone/identity migration workflow
```

因为这已经不是“配置修改”，而是“这个文件声称自己变成另一个 Plan”。

------

# 30. Child Identity 的修改则按声明式 Update 解释

File-backed 中：

```text
ArchiveUnitId A → B
```

相当于：

```text
Remove A
Add B
```

不会自动 identity migration。

如果用户真的只是想修正 ID：

> 必须执行显式 identity migration 工具。

这样和此前 rename/move 不猜测 identity 的原则一致。

------

# 31. Clone 规则保持递归 regenerate

Clone：

```text
PlanId            new
SourceIds          all new
ArchiveUnitIds     all new
ExternalSourceIds  all new
SecretSlotIds      all new
```

所有内部引用同步重写。

但 portable semantics 尽可能相同。

Clone 不复制：

```text
Source bindings
CurrentRoot
HistoryRoot
External bindings
Secret bindings
Schedule installation
ArchiveVersions
History
Baseline
runtime state
```

因此 Clone 通常：

```text
PlanNotReady
```

直到本机重新绑定。

------

# 32. Clone 不应该继承 Current

即使：

```text
旧 Plan
和
Clone
```

archive bytes 理论上完全相同：

> Clone 仍然是新 Backup Plan。

不能直接把旧 Plan 的 Current 当 Clone 的 Current。

否则两个独立 Plan 的生命周期耦合起来。

所以：

```text
Clone first run
→ FirstBackup
```

是合理的。

------

# 33. Save As 仍然不是 Clone

继续保持：

```text
Save As
→ same PlanId
→ same child IDs
```

它只是同一个 Document 的另一份文件副本。

因此同一 Device：

```text
original.backupplan
copy.backupplan
```

不能同时 Register。

想两个独立 Plan：

```text
Clone
```

------

# 34. Import 不导入 Runtime State

无论 Update 还是第一次 Import：

```text
*.backupplan
```

都不携带：

```text
Baseline
History
Current
SecretBinding
StorageBinding
ScheduleInstallation
```

因此不存在：

> “incoming runtime state 和 local runtime state 怎么 merge”

这个问题。

这是之前 Portable/Local 分离带来的很大收益。

------

# 35. v1 不支持“只导入其中几个 Source”

我建议现在就禁止这种 partial import：

```text
Import
☑ Source A
☐ Source B
☑ Source C
```

因为那本质上就是 merge/patch。

v1：

> 一个 `.backupplan` 是一个完整 Plan aggregate。

Import/Update 的最小单位：

```text
whole BackupPlan
```

如果用户只想复用规则：

> 用 Global Rule Library / Smart Setup 等机制。

不要滥用 Import。

------

# 36. v1 也不做 Three-way Merge

不要：

```text
Base
Local
Incoming
```

做：

```text
Git-like merge
```

File-backed 已经可以直接交给 Git。

Managed Plan 的 Update 只需要：

```text
Existing
vs
Incoming
```

Semantic Diff + Replace。

这已经足够。

------

# 37. 建议正式 Conflict 类型

至少：

```text
IdentityConflict
AuthorityConflict
RegistrationConflict
RegisteredDocumentIdentityChanged

AlreadyExistsSameSemantic
AlreadyRegistered

UpdateRequiresConfirmation
DocumentUpgradeRequired
```

子对象则继续复用：

```text
DuplicateArchiveUnitIdentity
IdentityConflict
...
```

------

# 38. Update 后 Runtime State 的四类处理

可以正式总结成：

### Preserved

相同 identity：

```text
local binding
Current
History
Baseline
```

保留并重新验证。

### Added

新 identity：

```text
无 runtime state
需要 binding / FirstBackup
```

### Removed

旧 identity：

```text
退出 active plan
runtime artifacts 保留
不自动 purge
```

### Modified

相同 identity、语义改变：

```text
保留 runtime identity
由 fingerprints / reconcile 状态决定后续动作
```

这是非常漂亮的一套模型。

------

# 39. 最重要的是 Baseline 不由 Import 自己判断

Update 层不要写：

```text
if config changed:
    delete baseline
```

而应该：

```text
Update portable config
↓
ResolvedPlanSnapshot
↓
Candidate
↓
Change Detector
↓
compare against preserved baseline
```

是否需要重建，由之前已经设计好的：

```text
EntrySetFingerprint
SelectionFingerprint
ArchiveSpecFingerprint
```

决定。

这样不同模块职责不会重叠。

------

# 40. 可以直接交给 Codex 的最终 P0 结论

建议让 Codex只修改规范并固化：

> 1. v1 不支持 automatic merge、field merge、partial import 或 three-way merge。
> 2. `.backupplan` 是完整 Plan aggregate；Update Existing 是 same-PlanId whole-document semantic replacement。
> 3. 对象只按稳定 ID 对应，绝不按 name/path/content 猜 identity。
> 4. Same PlanId + same semantic 是幂等 NoOp；same PlanId + different semantic 返回 IdentityConflict，允许 Update Existing / Clone / Cancel。
> 5. Update Existing 保留相同 identity 的 local/runtime state，并由现有 fingerprint/change detection 判断是否 rebuild；新增 identity 无 baseline，删除 identity 的 Current/History 不自动删除。
> 6. Removed identity 的 runtime/artifacts 保留为 inactive/recovery state，物理 purge 是独立 destructive operation。
> 7. New Source/External/Secret identity 没有 binding 时 Update 可以成功，但 Plan 进入对应 PlanNotReady；config validity 与 local readiness 分离。
> 8. Scheduler reconcile、OutputReorganization、StorageRelocation、History maintenance、Secret binding 都是 config commit 后的独立 reconciliation，不加入 Import transaction。
> 9. Managed↔File-backed same PlanId 必须走显式 authority conversion，不因语义相同而偷偷切 authority。
> 10. 同 PlanId 的第二个 File-backed path 不能另建 registration；只能显式 Relocate Registration / Clone / Cancel。
> 11. 已注册 File-backed 文档的 PlanId 在原地改变时 `RegisteredDocumentIdentityChanged`，不得自动把 registration 换成新 identity。
> 12. Clone 递归生成全部 portable IDs，不复制任何 local/runtime state，因此新 Clone 自然 FirstBackup。
> 13. Semantic Diff 基于 validated/migrated/default-expanded semantic model，而不是 raw JSON。
> 14. Update 的 portable configuration commit 必须原子；incoming invalid 时 existing state 零修改。
> 15. Import/Update 自己不得删除 baseline；Change Detector 是是否 rebuild 的唯一业务判断者。

------

## 但这里有一个需要修正的项目顺序

完成这个 P0 后，**还不能立刻写最终 JSON Schema**。

当前正式 `BACKUPPLAN.md` 仍明确保留两个会改变 Schema 结构的未决设计：

> `ArchiveSpec override` 和 `External Source` 的完整行为尚未设计。

所以更准确的顺序应该变成：

```text
Import Conflict / Merge P0       ← 现在
        ↓
Backup Plan P0 全部冻结
        ↓
ArchiveSpec default/override     ← 还要设计
        ↓
External Source 完整语义         ← 还要设计
        ↓
Freeze Backup Plan v1 Domain Model
        ↓
backupplan-v1.schema.json
        ↓
Document DTO / serializer
        ↓
Persistence / SQLite
```

这两个虽然没有被列为“P0”，但**都是 schema-shaping design**。尤其 Archive Unit 的 `PortableOverrides` 现在还是占位概念，如果跳过就写 Schema，之后必然改结构。

完成后把“JSON Schema 前剩余事项”改成 **ArchiveSpec override + External Source 完整语义**，不要直接开始 Schema。