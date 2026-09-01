# Backup Plan Document v1

本文是 `*.backupplan` 文档角色、Plan Authority、稳定 Identity、Portable Configuration、Device Local Binding 与 schema compatibility 的规范真相源。具体 JSON 字段和 JSON Schema 等尚未确认的部分仍以“未决”处理，不得从示例或工作稿推断。

## 1. 文档语义

`*.backupplan` v1 只有一种语义：**Portable Declarative Backup Plan Document**。它描述“用户希望 StowCrate 如何备份这些数据”。

它不是：

- `config.db` 的 JSON 备份或 EF/SQLite DTO 序列化；
- 某次运行、扫描或 UI 会话的状态快照；
- Committed Baseline、ArchiveVersion、CurrentVersion 或 History 数据；
- cache、hash cache、journal cursor 或 scheduler installation state；
- secret value、密码、token 或恢复密钥的载体。

同一个文档模型可被 Import 为 Managed Plan，也可 Register 为 File-backed Plan；这两种操作不会产生两套文档语义。

## 2. Plan Authority

```text
PlanAuthority
  Managed
  FileBacked
```

Authority 是 Application/Infrastructure 的配置管理概念，不是 Core BackupPlan 的领域字段。

### Managed

`config.db` 是计划配置唯一真相源，用户通常通过 GUI 修改。Export 产生当前配置的可移植声明快照；导出文件后续变化不会修改原 Managed Plan。

### File-backed

注册的 `*.backupplan` 文件是计划配置唯一真相源。StowCrate 在解析、预览或执行前读取并验证该文件。SQLite 不保存另一份可独立编辑的计划配置，只保存：

- registration 与文档物理位置；
- 本机路径/设备 binding；
- 本机 secret binding/reference；
- ArchiveVersion、CurrentVersion 与 Committed Baseline；
- last run、scheduler installation 等运行状态与审计。

文件删除、不可读、语法无效或 schema 不支持时安全失败，不能回退到 SQLite 中某份陈旧配置副本继续执行。

## 3. Import、Register 与 Export

- **Import**：读取、验证文档并复制为 Managed Plan。完成后与原文件脱离，删除或修改原文件不影响计划。
- **Register**：读取、验证文档并记录 File-backed registration。文件保持 authoritative，后续修改影响下一次解析与运行。
- **Export**：把 Managed Plan 当前语义写为新的 `*.backupplan` 声明快照。
- **Save As / Copy**：复制 File-backed 文档；不是从 SQLite 导出。

产品 UI 必须使用能解释后果的不同操作和文案，不能把 Import 与 Register 合并成一个含糊的“打开”。

## 4. 单一真相源与禁止同步

同一 Plan 在任一时刻只能是 Managed 或 File-backed，不能同时由 `config.db` 和文件控制。v1 禁止：

- 根据 timestamp 猜测文件或数据库谁更新；
- 文件与 SQLite 的自动双向同步；
- UI 修改 File-backed 配置后只写数据库覆盖层；
- 文件解析失败时静默使用最后一次数据库副本。

允许显式转换：

- Managed → 导出文档 → 验证 → 切换为 File-backed；
- File-backed → 读取当前文档 → Import/Detach → 保存为 Managed。

转换必须是显式、原子且可审计的配置管理操作。

## 5. Portable Configuration 与 Local Runtime State

文档保存 portable desired configuration，例如：Plan name、logical sources、portable SourceOutputPath、Archive Units、pinned Global Rules Snapshot、Plan/UI-managed rules、ArchiveSpec、Protection Configuration、portable Secret Slot declarations、LinkPolicy、Change Detection mode、History default/unit overrides、schedule intent 与 External Source definitions。

以下永不进入文档：Committed Baseline、ArchiveVersion records、CurrentVersionId、last run/success、cached hashes、scan/journal cursor、scheduler provider/native task ID/installed fingerprint/status、SecretRevision、OS SecretReference/locator、Secret Store provider、SecretValue、password hash/verifier、加密 secret blob、Privacy recovery material 和 Recovery Package。

File-backed 不等于无状态执行。持续执行仍需要本机 registration、binding、secret reference、ArchiveVersion 与 baseline。v1 不要求未注册文件支持无状态 one-shot backup；未来 `--ephemeral` 必须单独定义，不能复用持续任务语义。

## 6. 统一解析边界

Managed 和 File-backed 都先解析为不可变、已验证的统一 Plan Snapshot，再经过相同的扫描与 Archive Unit resolution。Application 负责把 authoritative declaration、物理 discovery、FILE_MANAGED `.backupignore` metadata/rules 与当前设备的 local registration 合并为 resolved units：

```text
Managed repository ─────────┐
                            ├─→ declaration + local binding → ResolvedPlanSnapshot → Scanner
Plan document loader ───────┘                                                    │
                                                                                 ▼
Device local registration ────────┐                              SourceSnapshot / physical discovery
                                  ├─→ Archive Unit resolution ←─ .backupignore metadata/rules
authoritative declaration ────────┘                    │
                                                       ▼
                                            Resolved ArchiveUnits → Planner / Change Detector / Executor
```

这里的 `ResolvedPlanSnapshot` 明确定义为 **device-resolved pre-observation immutable execution configuration snapshot**：位于 frozen `PortableBackupPlan` 之后、Source/External observation 与 FILE_MANAGED discovery 之前。它不是最终 execution-ready 的 Backup Plan。Authority 只负责上游取得 authoritative portable aggregate；authority/registration/document/persistence identity 与 publish-time revision guard 均不进入该 snapshot。

pre-observation resolver 只消费已经由 Infrastructure 展开并 physical-canonicalize 的 binding facts。Source、CurrentRoot、External Source binding required；HistoryRoot 和 SecretBinding 可以作为 facts 携带，但 MissingHistoryRootBinding、MissingSecretBinding、SecretRevision/capability 与最终 output collision 必须等 resolved Archive Unit set 形成后再按 effective policy 检查，不能因 default 或尚未 discovery 的 FILE_MANAGED unit 提前误判。

Core `BackupPlan` 不包含 `IsFileBacked`、registration path、SQLite identity 或 Declared/Discovered origin。Planning Kernel、Change Detector 和 Archiving 不感知配置来源；Scanner 只报告物理事实，不决定 declaration authority。

每次运行捕获 `ExecutionSemanticSnapshot`。它包含 Managed 的 Revision（适用时，用于发现配置可能变化）、PlanSemanticFingerprint、ExecutionSemanticFingerprint、ExecutionBindingFingerprint、所有本轮解析的外部规则源 fingerprint，以及 Secure protection 实际解析的 `SecretSlotId + SecretRevision`。

Publish 前必须重新读取并验证。PlanRevision/PlanSemanticFingerprint 变化时重新解析当前 Plan；只有 ExecutionSemanticFingerprint 不同才返回 PlanChangedDuringRun。Schedule-only 或 retention-only 变化不阻止本轮发布；retention-only 变化必须跳过本轮旧策略 cleanup 并标记 HistoryMaintenanceOutOfSync。ExecutionBindingFingerprint、任一 FILE_MANAGED `.backupignore` 或 SecretRevision 变化仍属于执行关键 drift，不发布 Current、不推进 baseline。外部规则源 fingerprint 基于实际读取的文件 bytes 与版本化解析语义，不能只比较 mtime。

## 7. Fingerprint 与文件移动

PlanAuthority、Import/Register 方式、registration path、authority conversion、`PlanId`、`ArchiveUnitId`、`ExternalSourceId`、`DeviceId`、Global Rule Library provenance、OS SecretReference/locator 与 Secret Store provider 不属于内容选择/归档规格语义，因此不进入 SelectionFingerprint 或 ArchiveSpecFingerprint。`PlanId + ArchiveUnitId` 仍用于 baseline identity；新 identity 没有 baseline 时自然得到 FirstBackup。

`SourceId`、Archive Unit Source-relative logical path、pinned Global Rules Snapshot、Plan/Local Rules、Boundary、LinkPolicy，以及 External Source 的逻辑 mapping/archive destination 属于 SelectionFingerprint。identity 与逻辑路径必须分开处理：明确迁移 identity 不代表内容变化，而 logical path 变化仍可能改变 manifest 与 Current 逻辑结构，必须触发 rebuild。

- authority 切换前后 Plan Snapshot 语义相同：不 rebuild；
- `E:\configs\Code.backupplan` 移到其他位置但内容语义相同：不 rebuild；
- 仅 JSON formatting/property order 变化：PlanSemanticFingerprint 不变；
- 文档内规则、ArchiveSpec 等语义变化：对应 fingerprint 变化；
- ScheduleIntent 变化：PlanSemanticFingerprint 与 ScheduleSemanticFingerprint 变化，但三个 archive fingerprint 和 ExecutionSemanticFingerprint 不变；
- SourceOutputPath/OutputPathEncodingVersion 变化：PlanSemanticFingerprint、OutputLayoutFingerprint 与 ExecutionSemanticFingerprint 变化，三个 archive fingerprint 不变；
- effective History Enabled 变化：PlanSemanticFingerprint 与 ExecutionSemanticFingerprint 变化，三个 archive fingerprint 不变；
- RetentionPolicy 变化：PlanSemanticFingerprint 变化，但 ExecutionSemanticFingerprint 与三个 archive fingerprint 不变；
- local Source/Current/effective History/External binding 变化：ExecutionBindingFingerprint 变化，不属于 Plan 或 archive fingerprint；
- 运行期间 Plan 文档变化时重新比较 ExecutionSemanticFingerprint；仅 schedule 等非执行关键语义变化不触发 PlanChangedDuringRun；
- 运行期间已解析 `.backupignore` 或 Secure SecretRevision 变化：PlanChangedDuringRun。

## 8. 灾难恢复

`*.backupplan` + Current + History 应足以在新设备重新注册并重新绑定 portable configuration，但不承诺恢复旧机器的 baseline 或运行历史；这些本地 durable state 需要 `config.db` 一致快照。对于 Secure archive，这些文件连同 `config.db` 快照都不保证具备解密能力；用户还必须独立持有 Secret 或未来显式导出的 Recovery Package。

重新注册不能依据文档物理路径认定 Plan identity。Plan 的 portable identity 可以跨设备识别同一份声明配置，但每台设备的 registration、binding、baseline 与运行状态保持本机隔离。

## 9. 稳定 Identity

以下 portable 对象必须具有显式、持久、稳定的 UUID v4：

- `PlanId`；
- `SourceId`；
- `ArchiveUnitId`；
- `ExternalSourceId`；
- `SecretSlotId`。

外部文本使用 RFC 4122/9562 常见的 canonical lowercase `8-4-4-4-12` 格式，并验证 version 为 4、variant 合法。领域层应使用互不混用的强类型 ID；数据库 row id 即使存在，也不是领域 identity。

Name、LogicalPath、RelativePath、physical/absolute path、realpath、文件位置、数组下标、hostname 和数据库自增键都不能生成或替代 identity。

- `PlanId` 跟随 portable document；不同设备 Register 同一文档时看到相同 PlanId；
- `SourceId` 表示逻辑 Source，与当前机器的 SourceRoot 无关；
- `ArchiveUnitId` 表示逻辑 Crate，与其当前 Source-relative path 无关；
- `ExternalSourceId` 表示逻辑外部输入，与本机实际文件位置无关；
- `SecretSlotId` 表示 Plan 内的逻辑 secret requirement，与本机 Secret Store item/locator 无关。

SourceId 变化属于来源语义变化并参与 SelectionFingerprint。ArchiveUnitId 与 ExternalSourceId 只作为 identity/manifest/version/baseline reference，不直接进入 SelectionFingerprint；对应 logical path、mapping、archive destination 与其他实际选择语义仍然进入。

文档 semantic validation 必须在任何 authority/local binding resolution 前验证完整 reference graph：每个 ArchiveUnitDeclaration.SourceId 必须引用同 Plan 唯一 Source；每个 ExternalSource.TargetArchiveUnitId 必须引用同 Plan declared Archive Unit；每个 Secure ProtectionConfiguration.SecretSlotId（default 或 override）必须引用同 Plan 唯一 SecretSlot。所有 portable ID 在各自集合中必须唯一，跨强类型 ID 不因文本 UUID 相同而成为同一对象。dangling、重复或类型错误引用均为 SemanticValidationFailed / InvalidDocument，不能降级为 PlanNotReady，也不能靠本机 binding 补齐。

## 10. ID 生命周期

| 操作 | PlanId | SourceId | ArchiveUnitId | ExternalSourceId | SecretSlotId |
|---|---|---|---|---|---|
| 修改显示名称 | 保持 | 保持 | 保持 | 保持 | 保持 |
| 修改本机 binding | 保持 | 保持 | 保持 | 保持 | 保持 |
| Archive Unit rename/move（明确为同一对象） | 保持 | 保持 | 保持 | 保持 | 保持 |
| External Source 修改本机路径或显示名 | 保持 | 保持 | 保持 | 保持 | 保持 |
| Export Managed Plan | 保持 | 保持 | 保持 | 保持 | 保持 |
| Import as Managed | 保持 | 保持 | 保持 | 保持 | 保持 |
| Register File-backed | 保持 | 保持 | 保持 | 保持 | 保持 |
| Save As / Copy 文档 | 保持 | 保持 | 保持 | 保持 | 保持 |
| Managed ↔ File-backed | 保持 | 保持 | 保持 | 保持 | 保持 |
| Update existing identity（用户明确选择） | 保持 | 保持 | 保持 | 保持 | 保持 |
| Clone as new Plan | 重新生成 | 全部重新生成 | 全部重新生成 | 全部重新生成 | 全部重新生成 |

Import 表示接管同一逻辑 Plan，默认保留全部 portable IDs；Clone 才表示创建新的逻辑 Plan。Clone 不继承 ArchiveVersion、CurrentVersion、Committed Baseline、local binding、secret binding 或 scheduler installation。

Save As 只是同一 Plan Document 的物理副本，不能把两个相同 PlanId 的副本在同一 DeviceId 下注册成两个独立计划。用户可以显式把既有 File-backed registration 重定位到副本；若想并存运行，必须使用 Clone 生成全新递归 identity。Managed Plan 的 Export 若随后要 Register 为同一 Plan，也必须走显式 authority conversion，不能形成双 authority。

导入或注册时发现同 PlanId 已存在但文档语义不同，必须返回 IdentityConflict；不得自动覆盖、合并或重新生成 ID。完整的 Import / Update / Clone / Register 冲突语义见第 20 节。

删除对象后从无 identity 的物理路径重新发现，不自动恢复旧 ID。只有 portable 声明、`.backupignore @id`、显式 Import/Restore 或用户确认的 identity migration 可以接续原 identity。

## 11. Portable Configuration 与 Device Local Binding

Portable Configuration 描述可 Git 管理、Import/Export 和跨设备复用的 desired configuration，包括 portable IDs、显示名、逻辑路径、SourceOutputPath、Archive Units、pinned Global Rules Snapshot、其他规则、ArchiveSpec、Protection Configuration、Secret Slot declarations、LinkPolicy、Change Detection mode、History default/unit overrides、schedule intent 与 External Source declaration。

Device Local Binding 描述本机如何解析这些逻辑对象，包括：

- SourceId → physical SourceRoot；
- Current/History logical storage slots → physical CurrentRoot、conditional HistoryRoot；
- ExternalSourceId → physical file/directory；
- SecretSlotId → 本机 Secret Store provider + opaque SecretReference + local SecretRevision；
- ScheduleIntent → 本机 ScheduleInstallation state。

SourceRoot、CurrentRoot、HistoryRoot 和 External Source physical path 不属于 portable document，不得写入 `*.backupplan v1`，也不随普通 Export 导出。未来若提供 Device Binding Export，必须使用独立格式和明确隐私提示。

ArchiveUnit path 永远是相对于 SourceId 对应 SourceRoot 的逻辑路径，使用 `/`，不得包含盘符、反斜杠、绝对路径或 `..`。External Source 的 archive destination 也是 archive-relative LogicalPath；其物理输入来自 Local Binding。

## 12. DeviceId 与绑定作用域

每个本机安装生成并持久保存一个 UUID v4 `DeviceId`。DeviceId 是 local runtime identity，不进入 `*.backupplan`、PlanSemanticFingerprint、SelectionFingerprint 或 ArchiveSpecFingerprint。DeviceName/hostname 仅用于显示，修改设备名称不创建新 DeviceId。

Local Binding 至少按 `PlanId + DeviceId` 命名空间，并引用 SourceId、ExternalSourceId、SecretSlotId 或 plan storage slot。Secret Binding 的完整逻辑 key 为 `PlanId + DeviceId + SecretSlotId`；ScheduleInstallation 位于 `PlanId + DeviceId`/local registration 命名空间。多个设备 Register 同一 Plan 时 portable configuration 相同，但 bindings、SecretRevision、scheduler installation、ArchiveVersion、Committed Baseline 与运行状态不得串用。

`CHANGE-DETECTION.md` 的 `PlanId + ArchiveUnitId` baseline identity 保持为 portable unit key；在持久化/运行时它位于当前 DeviceId/registration 的本机命名空间中。本文不提前定义 SQLite 复合键。

CurrentRoot 对所有可执行 Plan 都是 required binding；至少一个 Archive Unit effective History Enabled 时 HistoryRoot 才是运行所需 binding。缺少 required Source、Current、conditional History、External Source 或 Secure Secret binding 时，Plan 状态为 `PlanNotReady` 并安全失败，不能静默跳过。External Source v1 全部 required，不提供 optional 字段。binding 存在但当前执行上下文无法读取 secret 时，运行以 SecretUnavailable/SecretStoreError 阻止，不能降级或在 headless 中等待交互。

## 13. Local Path Expression v1

Local Binding 可以保存本机绝对路径，或使用 StowCrate 定义的有限 path variable。v1 只规定：

```text
${HOME}
```

`${HOME}` 由受控的平台用户目录服务解析，不读取任意同名环境变量作为不受审阅的输入。它只能作为 path expression 的根 anchor；展开、规范化后必须得到绝对路径。

v1 不支持 `${MY_CODE}`、`%APPDATA%`、shell expansion、命令替换、任意进程环境变量或未声明 variable。发现未知 `${...}` 必须 validation failure，不能保留原文、展开为空或交给 shell。

`${DESKTOP}`、`${DOCUMENTS}`、`${DOWNLOADS}` 等不属于 v1；未来增加时必须定义跨平台缺失行为和 semantics version。

所有 binding 解析后必须执行 lexical normalization、平台 case 规则、Link/Junction physical canonicalization，以及 SourceRoot/CurrentRoot/HistoryRoot 两两不重叠验证。SourceRoot 仍必须遵守 `FILESYSTEM.md` 的真实目录约束。

Binding 或文档物理位置不进入 archive semantic fingerprint。SourceRoot 改变后必须重新扫描，真实数据差异通过 EntrySetFingerprint 体现；CurrentRoot/HistoryRoot 改变产生 Storage Relocation，不伪装为 rebuild。已有 Current/History 时不得先直接修改 binding pointer；必须完成第 18 节的 copy/stage、SHA-256 verify、destination publish 后，才能 durable commit 新 binding。

## 14. Global Rules v1：Pinned Snapshot

Backup Plan v1 的 Global Rules 必须是 concrete、authoritative 的 **pinned snapshot**，不得是运行时解析本机 Global Rule Library 的 live reference：

```text
Global Rule Library revision N
  → user explicitly Apply / Update
  → Pinned Global Rules Snapshot in Plan
  → ResolvedPlanSnapshot
```

Global Rule Library 可以作为跨 Plan 的 authoring/reuse facility，保存可维护的模板。Library 后续变化不得自动改变既有 Plan；用户显式 Apply/Update 后才替换 snapshot，并按正常配置变更重新预览和执行。这样同一 File-backed 文档在不同设备、Git revision 或灾难恢复场景中不依赖本机隐式状态。

Plan 可以携带 GlobalRuleSet name、ID、revision 等 optional provenance metadata，用于显示来源和提示可用更新，但：

- concrete rule action、pattern 与顺序才是 authoritative execution input；
- provenance 不存在或目标设备没有对应 Library 时，Plan 仍可完整执行；
- provenance 的 rename、revision label 或其他 metadata 不进入 SelectionFingerprint；
- 只有 concrete snapshot 或 rule semantics version 变化才改变 SelectionFingerprint；
- Managed 与 File-backed 使用完全相同的 snapshot 语义，Planning Kernel 不感知 Library。

本节只确定领域/文档语义，不固定 JSON 字段、Library 持久化 schema 或更新 UI。

## 15. Archive Unit Declaration 与 FILE_MANAGED Discovery

### 15.1 Declaration 与 Discovery 的职责

Archive Unit declaration 是 portable desired configuration；discovery 是对 Source 真实枚举树的观察。二者不得混为一体：

- 目录中存在真实 regular `.backupignore` 就声明一个 FILE_MANAGED Archive Unit；Plan 是否列出它不影响 discovery；
- declaration 为一个已存在/应存在的 Archive Unit 关联 ArchiveUnitId、SourceId、expected logical path、RuleSource 与可选 non-rule portable per-unit settings；
- `UI_MANAGED` declaration 自己携带完整 LocalRuleSet；
- `FILE_MANAGED` declaration 禁止携带 Local RuleMode、CasePolicy 或 Rules，这些只从 `.backupignore` 解析；
- 未 declaration 的 FILE_MANAGED unit 可以正常备份并使用 plan-level defaults，但 v1 不允许 portable per-unit override；要设置 override，必须先加入 declaration。

概念模型只约束领域关系，不固定 JSON 字段：

```text
ArchiveUnitDeclaration
  ArchiveUnitId
  SourceId
  Path
  RuleSource = UI_MANAGED | FILE_MANAGED
  LocalRuleSet?        # required for UI_MANAGED; forbidden for FILE_MANAGED
  ArchiveSpecOverride? # declared units only
  HistoryOverride?     # declared units only
```

`Declared`/`Discovered` 只描述 Application resolution 的来源，不属于 Core backup semantics。Application 产生统一的 Resolved ArchiveUnit 后，Planning Kernel 不感知 origin。

### 15.2 FILE_MANAGED resolution matrix

| `.backupignore @id` | matching declaration | 结果 |
|---|---|---|
| 有 | 有 | 两者 ID 与 path 都匹配时正常解析；任一 identity 冲突为 Fatal |
| 无 | 有 | declaration 提供 portable ArchiveUnitId；不要求也不得自动写入 `@id` |
| 有 | 无 | 使用 `@id` 作为 ArchiveUnitId，应用 plan-level defaults |
| 无 | 无 | 本机 registration 按 SourceId + RelativePath 复用或生成 ArchiveUnitId；该 ID 只在当前设备稳定 |

identity resolution 的查找顺序是：`@id` → explicit declaration → local registration by `SourceId + RelativePath` → generate new local ArchiveUnitId。该顺序只用于寻找 identity，后一步不能覆盖前一步；多个显式来源同时存在时必须相容。

没有 `@id` 和 declaration 的单元发生 path rename/move 时，默认视为旧单元删除 + 新单元发现。不得依据 inode、FileId、realpath、内容 hash 或相似性自动接续；用户显式确认 identity migration 后才可保持原 ID。StowCrate 也不得为了生成 identity 自动修改 `.backupignore` 或污染 Git working tree；只有用户主动发起、预览并确认“写入稳定 ID”时才允许。

### 15.3 安全失败条件

- `.backupignore @id = A` 而同 path declaration 的 ArchiveUnitId = B：`IdentityConflict` / Fatal，任何一方都不能静默覆盖另一方；
- 同一 Plan 的不同 path 解析出相同 ArchiveUnitId：`DuplicateArchiveUnitIdentity` / Fatal；ExternalSourceId 重复同样 Fatal；
- declaration 声明 ArchiveUnitId = X、Path = OldName，但 discovery 在 NewName 找到 `@id X`：`ArchiveUnitRelocated` + `PlanNotReady`，不得自动跟随、执行或修改 Managed/File-backed configuration；用户确认更新 path 后才可继续；
- FILE_MANAGED declaration 的 path 不存在真实 regular `.backupignore`：`MissingFileManagedRuleSource` + `PlanNotReady` / Fatal，不得退化为 UI_MANAGED 或无 local rules；
- UI_MANAGED declaration root 同时存在 `.backupignore`：`RuleSourceConflict` / Fatal，不得选择任一方优先；
- `.backupignore` 不是 regular file、不可读或解析失败：继续遵守 `FILESYSTEM.md` / `BACKUPIGNORE.md` 的 Fatal 语义。

### 15.4 Rule resolution 与运行一致性

Application 对 FILE_MANAGED unit 读取 `.backupignore` 的 ArchiveUnitId?、RuleMode、CasePolicy 与 Rules，和 pinned Global Rules Snapshot、Plan Rules、Boundary、LinkPolicy 等一起生成 Resolved ArchiveUnit。最终规则顺序保持：

```text
Pinned Global Rules Snapshot → Plan Rules → .backupignore Local Rules
```

解析时必须把每个 `.backupignore` 纳入 `ExecutionSemanticSnapshot`，Publish 前重新验证。运行中变化时按 PlanChangedDuringRun 安全失败，不发布、不推进 baseline；不能用本轮早先解析的规则完成一次语义已经漂移的发布。

## 16. Protection Configuration 与 Secret Binding v1

### 16.1 Protection intent

v1 只有三种 protection intent；它们不预先绑定 7z/ZIP/TAR.ZST 的具体算法或 CLI 参数：

```text
ProtectionMode
  None
  Privacy
  Secure
```

- `None`：不加密，不需要也禁止引用 SecretSlotId；
- `Privacy`：归档内容经过加密/遮蔽，恢复所需材料随备份 artifact 保存，只阻止预览、索引、误打开或低成本扫描，**不提供机密性保证**；不需要也禁止引用用户 SecretSlotId；
- `Secure`：使用外部 secret 真正加密，恢复秘密默认不随归档保存；必须引用 SecretSlotId。

概念模型为：

```text
ProtectionConfiguration
  Mode
  SecretSlotId?  # None/Privacy forbidden; Secure required
```

Plan validation 必须把 ProtectionConfiguration 与 Archiving adapter capabilities 合并判断。格式不支持请求的模式时返回 UnsupportedArchiveCapability，绝不能把 Secure 降为 Privacy、把 Privacy/Secure 降为 None。具体 encryption algorithm、KDF、header encryption 与合法格式组合等待 Archiving capability prototype，不在本轮固定。

Privacy recovery material 由执行阶段生成，其 carrier 属于 Archiving capability/execution concern。本轮不选择 archive comment、普通 manifest、sidecar、extra field 或其他载体；普通 `__stowcrate__/manifest.json` 继续只承载非秘密 metadata。每次执行随机生成的 Privacy key/nonce/recovery material 不属于 Plan 语义，也不进入 fingerprint。

### 16.2 Portable SecretSlot

SecretSlot 是 Plan-scoped portable logical requirement，可被同一 Plan 的多个 resolved Archive Unit 的 effective ProtectionConfiguration 引用：

```text
SecretSlot
  SecretSlotId  # UUID v4
  Name          # display only
  Purpose       # v1: ArchiveEncryption
```

Name 不是 identity；Import/Register 不得按名称、purpose 或 Secret Store 中的相似 item 自动绑定。SecretSlot declaration 可以进入 `*.backupplan`，但文档只表达“需要哪个逻辑 secret”，绝不能保存 secret value 或本机如何定位它。

SecretSlotId 生命周期：

| 操作 | SecretSlotId | SecretBinding |
|---|---|---|
| rename slot | 保持 | 保持 |
| Set/Replace secret | 保持 | 更新并递增 revision |
| Export / Import / Register | 保持 | 不随 portable document 复制；新设备显式绑定 |
| Managed ↔ File-backed / Save As | 保持 | 保持当前 registration 的 local binding |
| Clone Plan | 全部重新生成 | 不复制，Secure clone 为 PlanNotReady |

### 16.3 Device-local SecretBinding

SecretBinding 是 local runtime state，不属于 portable document：

```text
SecretBinding
  PlanId
  DeviceId
  SecretSlotId
  SecretStoreProvider
  opaque SecretReference
  SecretRevision
```

SecretReference 只能由 Infrastructure 解释；Core、Planning Kernel、manifest 和 portable configuration 都不能看到 OS locator。opaque reference 本身不得包含可直接解密的 secret。SecretRevision 是由 StowCrate 维护的本机单调版本，表示该 binding 的有效 SecretValue 被 Set/Replace/Rebind；这些操作必须保守递增，即使用户输入了相同值也不做 secret equality 检测。

不得持久化 SecretValue 的 hash、verifier 或其他 secret-derived metadata 来判断相等，因为低熵密码会因此获得离线验证材料。平台若不能在 Bind Existing 场景维持上述 revision contract，就不得提供该操作；不能用未受控的可变外部 item 绕过 revision。

至少区分以下显式操作：

- **Bind Existing**：平台能力允许且能满足 revision contract 时，显式关联已有 Secret Store item；
- **Set / Replace**：写入有效 secret，并递增 SecretRevision；
- **Unbind**：只删除 StowCrate binding，使 Secure Plan 进入 PlanNotReady；不自动销毁 Secret Store item；
- **Delete stored secret**：独立的 destructive security operation，必须显式确认并完成引用检查。删除 Plan、registration 或 slot 不得默认销毁可能仍被引用的 OS secret。

### 16.4 Readiness、执行与 headless

Secure mode 必须同时满足 portable SecretSlot reference 和当前设备 SecretBinding：

- 没有 binding：`PlanNotReady / MissingSecretBinding`；
- binding 存在但当前用户/任务上下文无法访问：`PlanRunBlocked / SecretUnavailable`；
- Secret Store provider 失败：`PlanRunBlocked / SecretStoreError`。

不能自动生成未知密码、按名称猜 binding、退化 protection mode，或在 scheduled/headless execution 中弹窗和无限等待输入。后续平台 prototype 必须覆盖 Windows Task Scheduler、launchd、systemd timer/cron 的真实访问上下文，而不只测试 GUI 手动执行。

Planning Kernel 不读取 SecretValue。Application 在 preview/run readiness 阶段验证 binding existence/availability，只把 ProtectionMode、SecretSlotId 与 SecretRevision 作为 resolved semantics；真正读取 secret 发生在执行边界。Archiver 获得最短生命周期的临时 Secret Material，而不是 Secret Store locator。

运行开始时把 Secure 使用的 `SecretSlotId + SecretRevision` 纳入 ExecutionSemanticSnapshot；Publish 前重新验证。revision 变化按 PlanChangedDuringRun 处理，不发布、不推进 baseline，避免 Current 使用旧 secret 而 baseline 表示新配置。

### 16.5 Sensitive Value Policy 与恢复边界

SecretValue 只允许存在于 OS Secret Store 和执行所需的最短生命周期内存。SecretValue、secret-derived verifier 和 recovery material 不得进入 `*.backupplan`、普通 manifest、config/cache 数据库、数据库快照、日志、异常、telemetry、crash annotations、命令行参数、进程环境或普通 fingerprint。唯一例外是未来明确设计的 Privacy 专用 recovery carrier 或用户显式导出的 Secure Recovery Package；二者都是独立受控 artifact，不能混入普通 manifest 或 Plan。

Secure Recovery Export 与 operational SecretBinding 是不同能力。开启 Secure 不得默认把 password/recovery material 复制到 Current；Recovery Package 的格式、保护与生命周期仍待后续安全设计。只有 Plan + Current + History，甚至加上 `config.db` 快照，都不足以保证 Secure archive 可恢复；恢复还需要外部 Secret 或独立 Recovery Package。

Secret 不得通过 `7zz -p...`、进程环境或其他可被 process list/history/diagnostics 读取的通道传递。Archiving prototype 必须验证可靠的受控 stdin、library、IPC 或等价接口；若 CLI 无法满足，就更换 library/interface，不能降低安全要求。

### 16.6 ArchiveSpecFingerprint

ArchiveSpecFingerprint 至少包含：

```text
resolved EffectiveArchiveSpec: Format + CompressionPreset + ProtectionConfiguration
Secure: SecretSlotId + local SecretRevision
Privacy: PrivacyProtectionSemanticsVersion
resolved format/compression/metadata capability semantics
manifest and ArchiveSemanticsVersion
```

它不包含 authored inherit/explicit 表达、SecretValue、secret-derived verifier、OS SecretReference/locator、Secret Store provider/implementation、DeviceId、Privacy 随机 material 或 Recovery Package bytes。SecretRevision 改变必须 RebuildRequired；只有 locator/provider 变化而逻辑 slot、revision 与有效语义不变时不得伪装成 archive spec 变化。ArchiveSpec 的 portable default/override 与 single-volume 边界见第 21 节。

本节只固定 protection/secret 的领域、portable/local 和安全边界，不定义 JSON Schema、SQLite tables、provider DTO、Recovery Package、Privacy carrier、具体算法或 CLI 参数。

## 17. Schedule Portability v1

### 17.1 Portable ScheduleIntent

`*.backupplan` 只保存跨平台调度意图，不保存 Windows Task Scheduler XML/GUID、launchd plist/label、systemd unit、cron expression/line number 或其他 native scheduler configuration/identity：

```text
Portable ScheduleIntent
  → Device ScheduleInstallation
  → Platform Scheduler
  → StowCrate CLI / common Application Run Plan use case
```

v1 默认 Manual-only，不自动创建系统任务。Manual-only 表示 schedule disabled，不是一个 ManualTrigger；用户明确启用自动备份后才允许安装 native task。启用的 ScheduleIntent 至少包含一个 trigger，并可组合多个不同 trigger：

```text
ScheduleIntent
  Enabled
  Triggers[]
    Daily(LocalTime)
    Weekly(DaysOfWeek, LocalTime)
    OnStartup
  MissedRunPolicy
```

v1 不支持 Monthly、arbitrary cron、idle、AC/battery、network、wake-computer 或其他 OS-specific condition。OnStartup 的短暂延迟属于固定 product/platform implementation policy，不是 portable field。多个 trigger 在同一时刻到期或多个 missed trigger 同时恢复可用时合并为一次 Plan run，不产生重复或积压执行。

同一 trigger 不得重复。Daily/Weekly 的 `LocalTime` 使用 locale-independent 24 小时 `HH:mm` 语义；Weekly days 是 Monday 到 Sunday 的语义集合，不使用平台相关数字。这里固定领域与 canonical fingerprint 语义，不提前固定 JSON 字段名或具体 serializer representation。

### 17.2 Local wall-clock、DST 与 missed run

Daily/Weekly 按 executing device 的 local wall-clock 解释，不绑定 portable IANA/Windows timezone ID。同一 Plan 在上海与东京设备上配置 `02:00`，分别表示各设备当地 02:00。

- DST 前跳导致目标时间不存在：在下一次可用当地时间执行一次；
- DST 回拨导致目标时间重复：该 trigger 只执行一次；
- 设备关机、睡眠或 scheduler 暂不可用造成 missed run：按 MissedRunPolicy 处理，不为每个错过周期累计任务。

v1 MissedRunPolicy 只有：

- `Skip`：错过后等待下一次正常 trigger；
- `RunOnceWhenAvailable`（默认）：恢复可执行状态后补执行一次，不论错过多少次都只补一次。

Platform adapter 必须可靠映射该 portable 语义；无法表达时应报告 unsupported capability 或 installation error，不得静默换成会重复运行、累计补跑或改变 wall-clock 语义的 native 配置。

### 17.3 Device-local ScheduleInstallation

ScheduleIntent 是合法 portable configuration；ScheduleInstallation 是独立的本机状态。概念模型为：

```text
ScheduleInstallation
  PlanId
  DeviceId
  SchedulerProvider
  NativeTaskId
  InstalledScheduleFingerprint
  LastSyncState
```

具体 SQLite schema 尚未定义。SchedulerProvider、NativeTaskId、native file/path/label、installed fingerprint 和 status 都不得进入 `*.backupplan`。安装任务绑定稳定 local registration，再由 registration 解析 PlanId 与当前 File-backed path；不得把文档物理路径作为长期 scheduler identity。

`ScheduleSemanticFingerprint` 对启用状态、验证后规范排序的 triggers、local-time/DST semantics version 和 MissedRunPolicy 做版本化 canonical encoding；重复 trigger 在 fingerprint 前就是 validation error，不能靠去重静默接受。InstalledScheduleFingerprint 与 desired fingerprint 相等才是 InSync；其他状态至少区分 ScheduleNotInstalled、ScheduleOutOfSync 与 ScheduleInstallationError。Manual-only 的 desired state 是“不存在 native automatic task”；残留 task 必须显示 out-of-sync 并由显式 reconcile 移除。

Schedule intent 已配置但尚未安装不使 Plan invalid/PlanNotReady。用户必须显式授权安装自动任务。Plan configuration 保存与 scheduler reconcile 是两个事务边界：

- Managed GUI 保存先提交 authoritative Plan，再尝试 reconcile；失败保留 Plan 并显示 out-of-sync/error；
- File-backed 重新加载发现 fingerprint 不一致时提示 reconcile；
- 普通 scheduled/headless backup run 不得顺手安装、更新或删除 scheduler task。

Application 通过 scheduler port 执行 Install/Update/Remove/GetStatus；Infrastructure 负责 Windows Task Scheduler、launchd、systemd user timer 与 cron fallback。native scheduler 只负责唤醒 StowCrate CLI，不能承载规则、Secret、ArchiveSpec、History 或其他业务配置。

### 17.4 Fingerprint 与运行中变更

ScheduleIntent 是完整 desired configuration 的一部分，因此进入 PlanSemanticFingerprint 和独立 ScheduleSemanticFingerprint。它不进入 EntrySetFingerprint、SelectionFingerprint、ArchiveSpecFingerprint 或 ExecutionSemanticFingerprint，schedule 变化不触发 archive rebuild。

发布 stale check 不得把整个 PlanSemanticFingerprint 直接当作执行关键 fingerprint。Plan 变化时重新解析并比较 ExecutionSemanticFingerprint：

```text
PlanSemanticFingerprint
  includes ScheduleIntent
  → configuration/reconcile identity

ExecutionSemanticFingerprint
  excludes ScheduleIntent
  → Current publish safety
```

因此运行中 `02:00 → 03:00` 可以完成当前归档发布；scheduler installation 状态由独立 reconcile 决定，未同步时保持 OutOfSync，备份执行本身不得修改它。Rules、Source、ArchiveSpec、SecretRevision、OutputLayout、effective History Enabled 或 ExecutionBinding 等执行关键语义变化仍阻止发布；RetentionPolicy-only 变化不阻止发布，但跳过本轮 cleanup。

### 17.5 Headless 与并发

Task Scheduler/launchd/systemd/cron 必须调用与 GUI Run Now 相同的 Application Run Plan use case。scheduled run 全程非交互：PlanNotReady、MissingSecretBinding、SecretUnavailable 或其他 readiness failure 必须记录、返回明确非成功结果并退出，不能打开 GUI 或等待用户输入。

v1 固定 `ConcurrentRunPolicy = SkipIfRunning`，不是 portable configurable field。手动与 scheduled trigger 对同一 `PlanId + DeviceId` 使用同一运行锁；已有任务运行时，新触发返回 AlreadyRunning/Skipped，不排队且不影响正在执行的任务。不同 Plan 是否并行由后续资源管理决定，但相互冲突的输出路径仍必须受保护。

本节只固定 portable schedule、local installation、fingerprint、headless 与 concurrency 语义，不定义 JSON Schema、SQLite schema、native task 格式、CLI 参数形状或平台安装命令。

## 18. History / Output Portability v1

### 18.1 Current、History 与 storage binding

Current 是每个 Archive Unit 最多一个、位于稳定确定性路径的最新有效标准归档，也是 Committed Baseline 对应的 Current ArchiveVersion。CurrentRoot 保持干净，可直接交给第三方同步；不得混入旧版本、config/cache 数据库、日志或把 `.partial` 当作有效 Current。

History 只保存被新 Current 替代的旧 Current，不是第二套 Current 或同步镜像。每个 HistoryVersion 仍是可以由标准工具独立打开的标准归档；History 内部 collision-free version naming/layout 由 StowCrate 管理，不属于 portable configuration，v1 不支持 history filename/directory template。

Portable Plan 只引用 Current/History logical storage slot，不保存 CurrentRoot、HistoryRoot 或任何绝对物理路径。设备本地 binding 位于 `PlanId + DeviceId` 命名空间：CurrentRoot 永远 required；只有至少一个 Archive Unit effective History Enabled 时 HistoryRoot 才是 backup run readiness 的 required binding。History Disabled 时既有 History binding/version 可以保留用于恢复或未来 purge，不因“不再 required”而被删除。

Clone 复制 portable output/history policy，但不复制 CurrentRoot/HistoryRoot binding、relocation/maintenance state、ArchiveVersion、CurrentVersion、HistoryVersion 或 baseline。Import/Register 到新设备后必须按 effective policy 重新绑定，缺少 required root 时为 PlanNotReady/MissingCurrentRootBinding 或 MissingHistoryRootBinding。

### 18.2 Portable deterministic OutputLayout

每个 BackupSource 必须把稳定 SourceId、display Name 与 portable SourceOutputPath 分开：Name 只用于显示；SourceOutputPath 是独立于 display/physical source path 的 non-empty portable LogicalPath，使用 `/`，不得是绝对路径、包含 `..`、空 segment、反斜杠、盘符或 NUL。

Current relative layout 固定为：

```text
SourceOutputPath
  + ArchiveUnit logical parent path
  + <ArchiveUnit final segment>.<archive format extension>
```

例如 SourceOutputPath `A` 下的 units `B`、`C/D`、`C/D/F` 映射为 `A/B.7z`、`A/C/D.7z`、`A/C/D/F.7z`。v1 不支持任意 output filename/path template、日期变量或 display name 推导。

Logical Output Path 到目标文件系统 physical path 必须经过 deterministic、reversible/diagnosable、collision-free、locale-independent 且 versioned 的 OutputPathEncoding。具体编码算法仍待实现设计；`OutputPathEncodingVersion` 与 output mapping semantics version 必须进入 OutputLayoutFingerprint。

在写任何 `.partial` 前，必须以目标 Current filesystem 的真实 case/path semantics 验证所有 SourceOutputPath、Archive Unit mapping、format extension 与编码结果。相同路径、case-fold collision、file/directory collision 或编码 collision 都是 `OutputPathConflict` / Fatal，绝不能 later writer wins。

### 18.3 OutputLayoutFingerprint 与 reorganization

OutputLayoutFingerprint 至少包含 SourceOutputPath、resolved Archive Unit-to-output relative mappings、format extension、OutputPathEncodingVersion 和 output mapping semantics version。它属于 PlanSemanticFingerprint 与 ExecutionSemanticFingerprint，但不进入 EntrySetFingerprint、SelectionFingerprint 或 ArchiveSpecFingerprint。

仅 SourceOutputPath 或 encoding/mapping version 变化时，archive bytes 和 ArchiveVersion identity 不变，状态为 OutputReorganizationRequired。reorganization 必须在 CurrentRoot 内/目标 root staging 新 relative layout，验证每个 artifact SHA-256 与冲突，发布全部目标后再 durable commit location/layout state；旧位置只在 commit 后允许清理。失败保留旧 authoritative layout，不推进 baseline，也不重新压缩。

ArchiveUnit logical path 或 archive format 的变化还可能分别触发 Selection/ArchiveSpec rebuild；OutputLayoutFingerprint 不替代三类 archive fingerprint 的既有职责。

### 18.4 Portable History policy

History 默认 Disabled。Plan 定义 default；只有 declared Archive Unit 可以携带 override，未声明 FILE_MANAGED unit 使用 Plan default：

```text
HistoryPolicy
  Disabled
  Enabled(RetentionPolicy)

HistoryOverride
  Inherit
  Disabled
  Enabled(RetentionPolicy)

RetentionPolicy
  KeepAll
  KeepLastVersions(N), N >= 1
```

`KeepLastVersions(N)` 只计 History versions，不计 Current；`N = 1` 表示只保留刚被替换的上一个 Current。启用 History 必须显式给出 RetentionPolicy，不猜默认 N；v1 不支持 daily/weekly/monthly/yearly tiering、age/size quota 或 GFS policy。

History Enabled 改为 Disabled 只停止捕获新 History，绝不删除已有版本。Purge History/单版本删除是独立 destructive operation，必须显式预览确认并与普通 policy save 分开。

### 18.5 Capture、publish 与 retention

只有 Archive Unit 为 Changed、存在 old Current 且 effective History Enabled 时才创建一个 HistoryVersion。首次备份和 Unchanged 都不创建 History。

```text
Write/test/verify new .partial
  → if History Enabled and old Current exists:
       persist old Current to History temp
       verify SHA-256
       atomically publish HistoryVersion
  → Revalidate ExecutionSemanticSnapshot
  → Atomic replace Current
  → Durable ArchiveVersion / CurrentVersion / baseline commit
  → Retention maintenance
```

History capture 是 Current replace 的前置安全事务：HistoryRoot 不可用、空间不足、copy/test/hash/publish 失败时必须失败并保留 old Current。Retention maintenance 是 durable commit 后的独立维护：cleanup 失败不得回滚新 Current，结果为 SuccessWithWarnings + HistoryMaintenanceOutOfSync。正常流程不得先删除旧 History 为本轮腾空间；需要空间回收时由用户显式执行维护操作。

RetentionPolicy 进入 PlanSemanticFingerprint，但不进入 ExecutionSemanticFingerprint 或 archive fingerprints；改变它产生 HistoryMaintenanceRequired。运行期间 retention-only 变化不废弃已生成归档，但本轮必须跳过基于旧 policy 的 cleanup 并标记 out-of-sync。effective History Enabled 进入 ExecutionSemanticFingerprint；运行中变化必须 PlanChangedDuringRun，因为它改变 old Current 是否必须先捕获。

PlanSemanticFingerprint 保留 authored History default/override/inherit intent。ExecutionSemanticFingerprint 只使用每单元 effective History Enabled；effective RetentionPolicy 仍只属于 maintenance。因而 inherit ↔ explicit-same-policy 在当前 default 不变时只改变 Plan semantic，不阻止相同单元发布；若仅 effective retention 改变则走 HistoryMaintenanceRequired，只有 effective Enabled 改变才是执行关键 drift。

### 18.6 ExecutionBindingFingerprint

ExecutionBindingFingerprint 是一次运行的一致性输入，至少包含：

- physical-canonical resolved SourceRoot；
- physical-canonical CurrentRoot；
- 任一 unit effective History Enabled 时的 physical-canonical HistoryRoot；
- required External Source physical bindings；
- 目标 filesystem case/capability identity；
- storage binding semantics version。

它使用解析后的 physical identity，而不是 `${HOME}` 等原始 expression；两个表达式解析到同一 canonical path 时 fingerprint 相同。它不进入 PlanSemanticFingerprint、三类 archive fingerprint、InputFingerprint 或 durable baseline。Publish 前变化必须阻止本轮发布；换盘/重新绑定后数据与 archive semantics 未变时不要求 rebuild。

### 18.7 Current/History relocation

已有 Current/History 时，修改 root 必须进入 StorageRelocationRequired，不能先更新 binding pointer。Current relocation 对全部 Current artifacts 执行“copy/stage 到新 root → SHA-256 verify → destination 内 publish → durable commit new binding/location”；History relocation 对全部 History versions 执行同样流程。整个流程中旧 binding 保持 authoritative，旧文件只在新 binding commit 后允许显式/安全清理。

v1 只支持完整 History relocation，不支持 old/new multi-store History。relocation 不生成新 ArchiveVersion、不改变 VersionId/PublishedAt、不推进 baseline。ArchiveVersion 不拥有 slot/path；CurrentVersion 与 HistoryVersionPlacement 分别保存唯一 relative placement，实际路径由对应 binding root + relative path 解析。

### 18.8 Root overlap safety

单 Plan 的 SourceRoot、CurrentRoot、HistoryRoot 继续两两不重叠。同一 DeviceId 上，任一 active writable CurrentRoot/HistoryRoot 还不得等于、包含或位于任何其他 active Plan 的 SourceRoot、CurrentRoot 或 HistoryRoot 之下；验证同时使用 lexical 与 physical-canonical path。不同 Plan 的 SourceRoot 可重叠，因为均为只读输入；共享父目录下互不包含的 sibling plan storage roots 合法。

root overlap、output path collision、destination capability 与可用空间等验证必须在 destructive move、History capture 或写 `.partial` 前完成。平台专用优化不能削弱这些 portable safety rules。

### 18.9 Fingerprint summary

| 配置 | PlanSemantic | ExecutionSemantic / Binding | Archive fingerprints |
|---|---|---|---|
| SourceOutputPath / output encoding semantics | 是 | ExecutionSemantic | 否 |
| effective History Enabled | 是 | ExecutionSemantic | 否 |
| RetentionPolicy | 是 | 否 | 否 |
| CurrentRoot binding | 不属于 Plan | ExecutionBinding | 否 |
| effective HistoryRoot binding | 不属于 Plan | ExecutionBinding | 否 |
| SourceRoot / required External physical binding | 不属于 Plan | ExecutionBinding | 数据变化另由 EntrySet 观察 |
| History physical layout | 否 | 否 | 否 |
| Archive format / compression | 是 | ExecutionSemantic | ArchiveSpec |

本节只固定 portable output/history policy、local binding、publish/maintenance、relocation 和 fingerprint 边界，不定义 JSON Schema、SQLite schema/Entity、具体 OutputPathEncoding、History physical naming 或跨文件系统复制实现。

## 19. Schema Compatibility 与 Unknown Fields v1

### 19.1 版本分派与严格输入

每份 `*.backupplan` 必须包含权威的正整数 `schemaVersion`。它是 Document Contract Version，不是 StowCrate 软件 SemVer；缺失、非整数或小于等于零分别以 `MissingSchemaVersion` / `InvalidSchemaVersion` 拒绝。reader 只把已支持版本交给对应版本解析器；未来未知版本返回 `UnsupportedSchemaVersion`，不得尝试按最近旧版本降级读取或执行。v1 允许 optional `$schema` URI 作为 IDE/editor discovery metadata；它不取代 `schemaVersion`，URI 不可用不影响 reader 分派，也不进入 semantic fingerprint。

v1 portable document 必须显式固定 `RulesSemanticsVersion`、`ArchiveSemanticsVersion` 与 `OutputPathEncodingVersion`（Schema property shape 见 Schema Design）。它们分别固定 Plan 内规则、EffectiveArchiveSpec backend mapping 与 Current logical-output encoding 的长期含义。FingerprintFormat、scanner、External mapping、Schedule/DST、Privacy 子语义、manifest schema 和 storage binding 等版本仍属于各自 runtime/baseline/artifact 或已由 schemaVersion/archive pin 覆盖，不得为实现方便继续暴露为 portable Plan 字段。

v1 只接受 UTF-8；允许读取 UTF-8 BOM，writer 默认输出无 BOM 的 UTF-8。输入必须是严格标准 JSON，不接受 comment、trailing comma、single quote、NaN 或 Infinity。property name 大小写敏感；重复 property 必须在解析阶段主动检测并以 `DuplicateProperty` 拒绝，不能采用 first-wins 或 last-wins。

### 19.2 Closed-world document contract

每个已知 schemaVersion 都使用版本专属的 closed-world schema。所有对象层级默认禁止未知 property；未知 enum value 和未知 discriminator/union variant 同样 Fatal。v1 不提供 `x-*`、任意 `extensions` bag 或“忽略但 round-trip 保存”的扩展机制。未来若需要插件扩展，必须先定义 namespace、capability declaration、required/optional semantics 与 round-trip 契约，并通过新的 schema contract 引入。

因此，已正式发布 schema 的以下改变原则上必须增加 `schemaVersion`：新增、删除或改变字段结构；新增 enum/union/trigger/retention variant；改变 omitted optional field 的默认含义。同一受支持文档不得因应用升级而静默改变 desired configuration。若 JSON 结构未变且行为由独立的 RulesSemanticsVersion、ArchiveSemanticsVersion、OutputPathEncodingVersion、FingerprintFormatVersion 等显式版本固定，则只升级对应 semantics version，不必机械增加 document schemaVersion。

每个 optional field 的缺省值属于版本化 document semantics。版本专属解析器必须在进入 current semantic model 前展开默认值；省略字段与显式写出相同默认值时，ResolvedPlanSnapshot 及其 semantic fingerprints 必须相同。

### 19.3 Version-specific reader 与显式升级

兼容读取使用清晰的版本边界，而不是一个包含历代 nullable 字段的最新 DTO：

```text
Raw UTF-8 bytes
  → strict JSON parse + duplicate detection
  → schemaVersion dispatch
  → version-specific closed-schema validation
  → version-specific semantic validation
  → version-specific migrator
  → current semantic model
```

已正式发布的 schema version 应尽可能长期保留 read/import migration 支持；只有严重安全问题或无法安全解释时才可停止，并必须提供迁移说明。Migrator 在内存中转换旧文档，不得因 read、preview、register 或 run 自动改写 File-backed 文件或污染 Git working tree。

升级 File-backed 文档是明确的 `Upgrade Backup Plan` / `Upgrade & Save` 操作，必须先告知将修改文件并允许预览语义变化。若 reader 能读旧版本但 writer 只写当前版本，普通编辑保存旧文档必须返回 `DocumentUpgradeRequired`，不得偷偷升版。Managed Import 可以把旧文档迁移为当前内部模型而不修改原文件；以后 Export 使用当前 writer/schemaVersion。

writer 只能从合法 current semantic model 投影文档，并执行 schema validation、read/validate round-trip；path-level 临时文件写入与原子替换属于后续 Application/Infrastructure 保存用例。

Backup Plan v1 canonical writer formatting 固定为 UTF-8 no BOM、两个空格缩进、LF、final newline 和 JSON 默认安全 escaping。UUID 固定 canonical lowercase `D` text，时间固定 `HH:mm`，enum/discriminator 固定 Schema 定义的 lower-camel string。writer 不输出尚未配置正式公开 URI 的 optional `$schema`。object property 按 frozen v1 DTO declaration 顺序稳定输出；顶层依次为 `schemaVersion`、`planId`、`name`、optional `description`、`semantics`、`sources`、`globalRules`、`planRules`、`archiveSpecDefault`、`archiveUnits`、`secretSlots`、`linkPolicy`、`changeDetection`、`historyDefault`、`schedule`、`externalSources`。

Rule arrays 保留 authored order。无顺序语义的集合必须按 Schema Design 的 canonical keys 排序：Sources 按 SourceId；Archive Units 按 SourceId/path/ArchiveUnitId；Secret Slots 按 SecretSlotId；External Sources 按 target/destination/kind/id；Schedule triggers 按 type/time/canonical days；weekly days 明确 Monday→Sunday，不依赖平台 enum 数值。writer 不以排序后去重掩盖 semantic invalid。生成 bytes 返回前必须交给同版本 strict reader 与正式 Schema 做 postcondition validation。

### 19.4 验证阶段与错误边界

完整流水线固定为：

```text
Raw *.backupplan bytes
  → Encoding / JSON parsing
  → schemaVersion dispatch
  → Document schema validation
  → Version-specific semantic validation
  → Migration to current semantic model
  → Plan authority resolution
  → Device Local Binding resolution
  → ResolvedPlanSnapshot (device-resolved, pre-observation)
  → Source / External observation + FILE_MANAGED discovery
  → ResolvedArchiveUnitSet
  → Execution Readiness / Capability validation
  → Execution
```

文档 schema 有效不代表当前设备可执行。缺少 Source/Current/conditional History/External/Secret binding 是 `PlanNotReady` / `MissingBinding`；合法 ArchiveSpec 在当前 adapter 不可用是 `UnsupportedArchiveCapability`，都不是 schema invalid。错误模型至少区分：

```text
MalformedJson / InvalidEncoding
MissingSchemaVersion / InvalidSchemaVersion / UnsupportedSchemaVersion
DuplicateProperty / UnknownProperty / MissingRequiredProperty
InvalidPropertyValue / UnknownEnumValue / UnknownVariant
SchemaValidationFailed / DocumentMigrationFailed / UnsupportedDocumentSemantics
IdentityConflict / PlanNotReady / UnsupportedArchiveCapability / MissingBinding
```

### 19.5 Fingerprint 与 schema evolution

raw JSON bytes、formatting、property order、DocumentSchemaVersion 和纯结构迁移不直接进入 PlanSemanticFingerprint、ExecutionSemanticFingerprint 或三类 archive fingerprint。fingerprint 必须基于验证、默认值展开并迁移后的 resolved semantics；不同 schemaVersion 若解析为相同 desired configuration，semantic fingerprints 应相同。迁移真的改变配置语义时，才由对应 resolved semantics 改变相应 fingerprint。

Schema evolution 必须分类处理：实现优化、错误消息或文档修正不 bump；受独立显式 semantics version 控制的算法变化升级该版本；字段集合/结构、enum/variant 或缺省语义变化升级 schemaVersion。任何无法由显式 version pin 保持旧含义的变化，都不得在相同 schemaVersion 下发布。

本节只固定 compatibility、closed-world、migration、validation pipeline、writer safety 与 fingerprint 边界；不创建 JSON Schema，不定义具体字段名/结构，不实现 serializer/migrator，也不定义 SQLite schema、Entity、Repository 或 migration。

## 20. Import / Update / Clone 与冲突语义 v1

### 20.1 操作与 whole-document replacement

v1 明确区分四种操作：Import 把不存在的 PlanId 导入为 Managed Plan；Update Existing 以相同 PlanId 的 incoming document 完整替换既有 Managed Plan 的 portable desired configuration；Clone 递归生成新 portable identity 后创建新的 Managed Plan；Register 把不存在的 PlanId 注册为 File-backed Plan。

一个 `*.backupplan` 是完整 Plan aggregate，不是 patch。v1 不支持 automatic/field/partial/three-way merge，也不允许只选择部分 Source 导入。Update 后 active 对象集合完全等于 incoming aggregate；不得把 existing-only 与 incoming 集合求并集。File-backed 文本冲突由用户使用 Git 等文本工具处理，StowCrate 只解析最终完整文档。

对象对应只使用 `SourceId`、`ArchiveUnitId`、`ExternalSourceId`、`SecretSlotId` 等稳定 ID，绝不按 name、logical/physical path、数组位置、内容或相似性猜 identity。相同 ID 的 rename、move 或配置改变是 Modified；相同 name/path 但不同 ID 是 Removed + Added。显式 identity migration 是独立操作，不属于 Update 猜测。

### 20.2 幂等、冲突与 Semantic Diff

同 PlanId 且 PlanSemanticFingerprint 相同的重复 Import/Update 返回 `AlreadyExistsSameSemantic` / NoOp：不修改 PlanRevision、binding、baseline、runtime state 或 scheduler installation，也不触发 rebuild/reconcile。同 PlanId 但语义不同返回 `IdentityConflict`，只能由用户明确选择 Update Existing、Clone As New 或 Cancel；不得自动 overwrite、merge 或生成新 PlanId。

Update 前必须基于 validated、migrated、defaults-expanded current semantic model 生成 `PlanSemanticDiff`，而不是 diff raw JSON。diff 至少区分 Metadata、Added、Removed、Modified，以及 ExecutionCritical、ArchiveRebuild、OutputReorganization、HistoryChange、ScheduleChange、BindingRequirementChange、SecretRequirementChange。formatting、property order、旧 schema 结构和 omitted-vs-explicit default 不产生假差异。Update Requires Confirmation；incoming schema/semantic 无效、identity 重复或引用损坏时 existing state 保持零修改。

### 20.3 原子配置提交与状态保留

Update Existing 是单个原子 config operation：验证与 semantic diff 完成并获得确认后，在一个事务内替换 authoritative portable configuration，按 identity 保留适用 local/runtime state，标记后续 readiness/reconciliation 状态，再整体 commit。不得出现 Source 已换而 Archive Unit 尚未换的半更新状态。

运行状态按对象分类处理：

| 分类 | 处理 |
|---|---|
| Preserved | incoming/existing 都有相同 ID：保留 local binding、Current、History、ArchiveVersion 与 Committed Baseline，并在后续使用前重新验证 |
| Added | incoming 新 ID：没有 local/runtime state；按需 MissingBinding / PlanNotReady，Archive Unit 自然 FirstBackup |
| Removed | existing-only ID：退出 active Plan，但 binding、baseline、Current/History 等转为 retained inactive/recovery state，不自动删除 |
| Modified | 相同 ID、语义变化：保留 runtime identity，由现有 fingerprints、Change Detector 和 reconciliation 状态决定 rebuild/relocation/maintenance |

Update/Import 层不得因为“配置改变”清空 baseline，也不得自行判断 rebuild。`CHANGE-DETECTION.md` 的 EntrySet/Selection/ArchiveSpec fingerprints 与 Change Detector 是唯一业务判断者。恢复此前 removed identity 时必须重新验证 artifact/baseline 完整性，不能盲目信任 dormant state。

清理 removed Archive Unit 的 Current、History、ArchiveVersion、baseline 或 detached binding 是独立的 destructive operation，必须列出影响并明确确认。删除 SecretSlot/Plan/registration 也不得自动删除 OS Secret。History Disabled 仍不等于 Purge History。

### 20.4 Readiness 与 commit 后协调

相同 Source/External/Secret identity 的本机 binding 在 Update 后保留；新增 identity 不按名称自动绑定，缺少 binding 时 config update 仍可成功，但状态为相应 `PlanNotReady` / `MissingSourceBinding` / `MissingExternalBinding` / `MissingSecretBinding`。删除 identity 的 binding 转为 inactive/detached state，等待显式 cleanup。

Scheduler reconcile、Secret binding、OutputReorganization、StorageRelocation 与 History maintenance 都是 config commit 后的独立操作，不进入 Update transaction。合法结果可以是 Updated + PlanNotReady、ScheduleOutOfSync 或 OutputReorganizationRequired；这些状态不得回滚已确认的 portable configuration。Schedule installation 继续遵守第 17 节，输出与 History 继续遵守第 18 节。

### 20.5 Register、registration relocation 与 authority conversion

同一 DeviceId 下同一 PlanId 只能有一个 registration/authority：

- 相同 path、PlanId 与语义的重复 Register 返回 `AlreadyRegistered` / NoOp；
- 同 PlanId 已是 File-backed、但 incoming 是另一文件路径：`RegistrationConflict`，只允许 Relocate Existing Registration、Clone As New 或 Cancel；
- Existing Managed 遇到同 PlanId Register：`AuthorityConflict`，只允许显式 Convert Managed → File-backed、Clone As New 或 Cancel；
- Existing File-backed 遇到同 PlanId Import：`AuthorityConflict`，只允许显式 Convert File-backed → Managed、Clone As New 或 Cancel。

语义相同也不得静默切换 authority。显式 conversion/relocation 必须验证同 PlanId，展示 semantic diff 和 authority/path 后果，并原子切换唯一真相源；新 File-backed 目标随后整体成为 authoritative document，不发生 merge。File-backed → Managed 后原文件变化不再影响 Plan。

注册后的 authoritative File-backed 文件内容从语义 X 改为 Y 是正常 desired configuration change，不是 Import/IdentityConflict；Application 按本节相同 ID 分类保留 runtime state，并运行 fingerprint/reconciliation。若文件中的 PlanId 从 registration 绑定的 A 变为 B，则返回 `RegisteredDocumentIdentityChanged` + PlanNotReady，不得自动改变 registration identity；用户必须恢复原 ID，或显式 Unregister 后 Register/Clone/identity migration。

### 20.6 Clone 与 Save As

Clone 必须为 PlanId、全部 SourceId、ArchiveUnitId、ExternalSourceId、SecretSlotId 递归生成 UUID v4，并同步重写所有内部引用，尽可能保持其他 portable semantics。Clone 不复制 Source/Current/History/External/Secret binding、ScheduleInstallation、ArchiveVersion、Current、History、baseline 或任何 runtime state，因此通常 PlanNotReady，所有 Archive Unit 首次运行都是 FirstBackup；即使 archive bytes 可能相同，也不得借用原 Plan Current。

Save As / Copy 只复制同一个 document，保留 PlanId 与全部 child IDs，不是 Clone。同一设备不能同时 Register 原件与副本；只能显式 relocation 或先 Clone。

至少区分 `IdentityConflict`、`AuthorityConflict`、`RegistrationConflict`、`RegisteredDocumentIdentityChanged`、`AlreadyExistsSameSemantic`、`AlreadyRegistered`、`UpdateRequiresConfirmation` 与 `DocumentUpgradeRequired`。本节不定义 UI 布局、JSON Schema、SQLite schema/transaction 实现或 cleanup 物理算法。

## 21. ArchiveSpec Default / Override v1

### 21.1 Portable intent 与解析模型

每个 Plan 必须持久化完整 `ArchiveSpecDefault`；不得在读取旧 Plan 时使用当前应用默认值补猜。创建新 Plan 的产品默认是 `SevenZip + Standard + None`，一旦形成 portable document 就成为显式 desired configuration。

v1 portable ArchiveSpec 只包含：

```text
ArchiveSpec
  Format = SevenZip | Zip | TarZstd
  CompressionPreset = Store | Fast | Standard | Extreme
  ProtectionConfiguration = None | Privacy | Secure(SecretSlotId)
```

declared Archive Unit 可以携带逐组件 `ArchiveSpecOverride`，每个组件要么 inherit，要么显式给值。Application 在进入 Planning/Change Detection/Archiving 前解析：

```text
ArchiveSpecDefault + ArchiveSpecOverride? → EffectiveArchiveSpec
EffectiveArchiveSpec += ArchiveSemanticsVersion
```

Archiver 只接收完整 EffectiveArchiveSpec，不感知 default、inherit 或 declaration origin。未声明 FILE_MANAGED unit 直接使用 Plan default；要设置 override 必须先加入 declaration。FILE_MANAGED 的规则仍只来自 `.backupignore`，而 ArchiveSpecOverride/HistoryOverride 来自 Plan declaration，两者不冲突。这里的 override 是文档内部明确的领域继承，不是第 20 节禁止的 Import merge。

### 21.2 Override、Protection 与 capability

Format、CompressionPreset、ProtectionConfiguration 可独立 override。Protection override 只能引用 Plan-scoped SecretSlotId，不能包含 inline SecretValue 或 local SecretReference；readiness 按每个 Unit 的 effective protection 判断。先完成 override resolution，再由当前 adapter 对完整 EffectiveArchiveSpec 做 capability validation；不支持的有效组合返回 `UnsupportedArchiveCapability`，不得降级 format、preset 或 protection。

同一个 ArchiveSemanticsVersion 下，Format + CompressionPreset 到具体 algorithm、level、solid behavior、metadata behavior 和 backend 参数的映射必须永久稳定。改变映射时升级 ArchiveSemanticsVersion，不能用应用版本替代。TarZstd 只表示 portable format intent；tar/zstd 实现、level、ACL/xattr 表示继续由 versioned semantics 与 capability prototype 决定。

v1 document 禁止 algorithm、dictionary/word/solid block size、thread count、lzma/deflate/zstd level、raw CLI arguments 等 backend-specific 字段。metadata preservation 也不是用户可配置 override，而由 Format、平台 capability 与 ArchiveSemanticsVersion 固定；无法忠实满足已规划条目时安全失败。

### 21.3 Single-volume v1

v1 固定每个 Archive Unit 的 Current 为单一 archive artifact，不支持 split volume，也没有 portable volume size/policy。分卷需要 Archive Artifact Set、逐卷 integrity、原子发布、History、relocation、restore 和 manifest 的新模型，必须留给未来 schema/semantics version。产品仍可提供大归档 warning、预计大小、文件数和最大文件提示，但不能实际 split 或接受 raw backend volume 参数。

### 21.4 Fingerprint 与发布

PlanSemanticFingerprint 保留 authored inheritance intent，因此区分 inherit 与 explicit-same-value。ExecutionSemanticFingerprint 与每单元 ArchiveSpecFingerprint 使用 resolved EffectiveArchiveSpec；当前 effective semantics 相同时，从 explicit same value 改为 inherit 或反向改变不触发 rebuild，也不废弃本轮相同单元归档。Plan default 变化只影响真正继承该组件的单元；publish stale revalidation 必须比较本轮每个单元的 resolved execution semantics，不能因其他单元无关的 default 变化废弃全部结果。

ArchiveSpecFingerprint 至少基于 EffectiveArchiveSpec、ArchiveSemanticsVersion、resolved format/capability semantics、适用的 SecretRevision、PrivacyProtectionSemanticsVersion 与 manifest semantics version。解析出的 algorithm/solid/metadata behavior 可以进入 fingerprint 以证明归档语义，但它们不是 portable fields。

- Format 变化同时改变 ArchiveSpecFingerprint 与 OutputLayoutFingerprint，需要 rebuild，并允许新 Current RelativeStoragePath 使用新扩展名；旧 Current 仍按 History capture → 新文件验证/发布 → durable commit → 清理旧路径的顺序处理。
- CompressionPreset 或 Protection/SecretRevision 变化通常只改变 ArchiveSpecFingerprint 并 rebuild，不改变 output path。
- 仅 output path inheritance 表达变化而 effective format 不变时，不产生 OutputReorganization。

本节只固定 portable intent、inheritance、effective resolution、single-volume、capability 与 fingerprint 边界；不定义 JSON Schema、具体 backend 参数、Archiver 实现、SQLite schema 或 metadata carrier。SevenZip、Zip、TarZstd 均已冻结为 v1 portable Format intent；某设备/adapter 暂不能忠实实现某组合时属于 `UnsupportedArchiveCapability`，不改变文档 schema validity，也不从 v1 enum 动态移除。

## 22. External Source v1

### 22.1 Explicit supplemental input

External Source 是显式附加输入：把 BackupSource 之外的一个本机文件或目录完整映射到一个 Archive Unit 的指定归档内路径。它不是第二套 BackupSource、Rule Source、Archive Unit discovery、生成 hook 或远程输入。

portable declaration 概念模型为：

```text
ExternalSourceDeclaration
  ExternalSourceId       # stable UUID v4
  Name                   # display only
  Kind                   # File | Directory
  TargetArchiveUnitId    # must reference a declared unit
  ArchiveDestination     # non-empty archive-relative LogicalPath
```

目标必须是 Plan 中显式 declared Archive Unit；FILE_MANAGED 可以作为目标，但必须先 declaration。一个 ExternalSourceId 对应一个 device-local physical root，不支持 glob、wildcard、multi-root、optional 或按名称/path 猜 binding。Clone 重写 ExternalSourceId 和 TargetArchiveUnitId 引用，但不复制 physical binding；新增/Clone 缺 binding 时 PlanNotReady，删除时 binding metadata 转为 inactive state 而不自动 purge。

### 22.2 Binding、root 与扫描

Kind 是 portable contract。File binding 必须 physical-canonicalize 为真实 regular file，Directory binding 必须是 ordinary directory；symlink、junction、mount-point alias、special/unknown reparse root 均为 `ExternalSourceInvalidRoot`，类型不符为 `ExternalSourceKindMismatch`。缺少 binding 是 `MissingExternalSourceBinding` / PlanNotReady；binding target 缺失、不可读分别是 `ExternalSourceMissing`、`ExternalSourceUnreadable`，都阻止目标单元执行，绝不能当作用户删除输入。

External Directory 内部使用 `FILESYSTEM.md` 的同一 no-follow、filesystem boundary、ScanIssue、IncompleteObservation 与 IntentionalSkip 语义，并使用目标 Archive Unit/Plan 的 Standard 或 Strict change detection；External Source 没有独立 ChangeDetectionMode。目录内部不执行 Archive Unit discovery：`.backupignore` 是普通 payload，不解析为 Local Rules/Boundary，默认随 payload 进入归档（仍受 control-entry safety）。LinkPolicy 使用目标 Archive Unit 的 effective policy。

External Source 是 explicit inclusion，不经过 Global/Plan/Local include/exclude Rules；但绝不绕过 no-follow、filesystem/Archive boundary、reserved/control namespace、collision、completeness、TOCTOU、LinkPolicy 或 archive capability validation。

### 22.3 ArchiveDestination 与 ownership collision

ArchiveDestination 必须是非空、使用 `/` 的 archive-relative LogicalPath；禁止 absolute path、drive letter、`..`、empty segment、反斜杠、NUL，以及 `__stowcrate__` reserved namespace。File destination 是文件完整归档路径，不追加原 basename；Directory destination 是映射根，external root basename 不参与。

External destination 不得等于目标 Archive Unit 根控制条目（包括根 `.backupignore`），也不得等于或位于任何 child Archive Boundary 之下；需要进入 child unit 时 TargetArchiveUnitId 必须直接指向该 child declaration。

normal selected entries、External entries 与 generated/reserved entries 必须在写归档前进入统一 path-trie ownership/collision validation。相同 archive logical path 由不同 input owner 提供时一律 `ArchiveEntryConflict` / Fatal，包括 file/file、file/directory 和 directory metadata collision；v1 不支持 directory overlay/merge 或 last-writer-wins。只共享没有独立 owner 冲突的 parent container 可以合法存在。

### 22.4 Observation、staging 与 TOCTOU

运行时流程固定为：

```text
declaration → local binding → no-follow observation → explicit inclusion
            → run-scoped private staging → collision/boundary validation
            → Candidate Archive Unit → Change Detection → IArchiveWriter
```

physical external input 永远只读；不得 rename、写临时文件、改 metadata 或生成 manifest 到原路径。staging 是 run-scoped implementation detail，不进入 Plan、baseline、ArchiveVersion 或 Current；陈旧 staging 只能 cleanup/diagnostics。staging 不能位于 SourceRoot、任一 External input tree 或有效 Current/History artifact namespace，也不能被 Scanner 当输入。

External observation 必须形成独立、不可变、平台无关的 `ExternalSourceSnapshot + ScanIssue[]`（或等价强类型纯数据边界），至少关联 ExternalSourceId、observed root kind、相对 entries 与原始业务 metadata。它可以复用 SourceEntry/FileSystemEntry value types 和同一 filesystem scanner primitive，但不能伪装成 BackupSource `SourceSnapshot`，也不能携带 FileInfo/Stream/Handle、physical binding 或 staging path。Application 将其按 declaration mapping 规范化为 Candidate entries 后，Planning Kernel 才与 normal entries 合流。

staging implementation metadata 不能替代业务 metadata。Candidate/EntrySetFingerprint 必须使用与真正 staged payload 对应的 external observed path/kind/metadata/content state；执行 materialization 要重新验证 path、entry kind 与 metadata identity，File→Link、Directory→Junction、copy/enumeration 不完整或 observation/payload 不一致均产生 IncompleteObservation 并阻止目标单元发布。其他独立单元仍按 per-unit commit 语义运行。

### 22.5 Fingerprints、manifest 与生命周期

目标单元的 SelectionFingerprint 包含 Kind、ArchiveDestination、mapping semantics version 和该单元 canonical explicit mapping set；declaration 数组顺序不具语义，集合按真正语义字段稳定排序。ExternalSourceId、Name、physical binding 与 TargetArchiveUnitId identity 本身不直接进入 archive fingerprints：移动 mapping 在旧目标移除、在新目标加入，自然改变两个单元的 mapping set。

external file/directory 的最终 logical archive path、kind、size、mtime、metadata、link raw target 和 Standard/Strict 所需 content hash 进入目标单元 EntrySetFingerprint。内容/metadata 改变或删除 declaration 会正常 RebuildRequired；纯 ExternalSourceId migration 在 mapping、effective content 不变时不要求重建。

resolved `ExternalSourceId → physical-canonical path/kind` binding 进入 ExecutionBindingFingerprint。运行中 binding drift 必须阻止本轮 Publish，下一轮重新扫描；仅换物理路径而下一轮 observed logical data 完全一致时不因地址本身 rebuild。

manifest 可记录 external logical destination、kind 和创建时的非秘密 provenance identity，但不得保存 device-local physical path。removed ExternalSource 的 local metadata 不在 whole-document Update transaction 中破坏性删除；显式 cleanup、Clone 与 identity migration 继续遵守第 20 节。

v1 明确不支持 optional external、glob/multi-root、external rules/`.backupignore` semantics、nested units、follow links、overlay、transform、command-generated source、pre-backup scripts、remote URL、cloud object 或 database dump hook。这些能力必须另行设计安全与版本语义。

本节只固定 External Source 的 portable/local、mapping、selection、observation、staging、fingerprint 与 lifecycle 语义；不定义 JSON Schema、staging 实现、SQLite schema、Entity、Repository 或 migration。

## 23. 与实现现状的差异和迁移约束

1. **`.backupignore v1` Directive 集合变化**：规范最初只允许 `@version/@mode/@case`；现在已正式加入可选 `@id`。parser 已通过兼容旧 API 的完整 parse result 返回 optional canonical lowercase UUID-v4 identity，并保留 RuleSet；解析和 identity resolution 均不修改文件。
2. **Fingerprint 强类型与字段**：ArchiveUnitId/ExternalSourceId 已正式排除于 SelectionFingerprint，logical source/path/mapping 仍包含；当前 Core 尚未实现这些强类型 fingerprint，不得把旧聚合 string 当作 v1 durable baseline。
3. **Baseline key 与 DeviceId**：Change Detection 的 `PlanId + ArchiveUnitId` 是 portable unit key；DeviceId 只作为本机 registration/binding/runtime namespace，不替换该 key。
4. **实现现状**：M3 已实现 strict document runtime、frozen portable domain、device resolution/observation、Candidate/Readiness、strong fingerprints/change decision、ExecutionSemanticSnapshot、M3.9 config.db schema/ports、M3.10 EF Core SQLite migration/repositories，以及 M3.11 startup recovery、统一 authority 与 Local Binding application workflows。尚未实现物理 publisher、confirmed relocation、SecretValue transaction 或完整 Current/History application workflow。

## 24. 当前未决顺序

Identity、Portable Path/Local Binding、Global Rules、FILE_MANAGED declaration/discovery、Protection Configuration/Secret Binding、Schedule Portability、History/Output Portability、Schema Compatibility/Unknown Fields 与 Import/Update/Clone 冲突语义 P0 已确认，Backup Plan P0 已全部冻结。

Backup Plan v1 Domain Freeze Review 已完成，结论见 `reviews/BACKUPPLAN-v1-DOMAIN-FREEZE-REVIEW.md`。required fixes 已进入规范，当前无 schema-shaping blocker，Backup Plan v1 标记为 **Domain Frozen / Ready for JSON Schema Design**。

Draft 2020-12 Schema、fixtures、自动测试与 Schema Review 已完成，见 `schemas/backupplan-v1.schema.json`（仓库根相对路径）、`plan/BACKUPPLAN-v1-SCHEMA-DESIGN.md`、`reviews/BACKUPPLAN-v1-SCHEMA-DESIGN-REVIEW.md` 和 `reviews/BACKUPPLAN-v1-SCHEMA-REVIEW.md`。Review PASS 后 Backup Plan v1 标记为 **Document Contract Frozen**。canonical `$id` 在长期稳定公开 URI 确认前保持省略，不虚构发布域名。

M3.9 Schema Design Review 已 PASS，M3.10 persistence implementation/tests 已完成且无 model/migration drift，M3.11 startup/recovery、authority 与 Local Binding workflows 已完成。下一阶段优先推进 **Secret Binding workflow + Config DB backup/recovery/maintenance integration** 或 **Physical Current/History Publisher Contract**；不得自动跳到 Archiver。
