# Backup Plan Document v1

本文是 `*.backupplan` 文档角色、Plan Authority、稳定 Identity、Portable Configuration 与 Device Local Binding 的规范真相源。JSON Schema 等尚未确认的部分仍以“未决”处理，不得从示例或工作稿推断。

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

文档保存 portable desired configuration，例如：Plan name、logical sources、Archive Units、pinned Global Rules Snapshot、Plan/UI-managed rules、ArchiveSpec、Protection Configuration、portable Secret Slot declarations、LinkPolicy、Change Detection mode、History policy、schedule intent 与 External Source definitions。

以下永不进入文档：Committed Baseline、ArchiveVersion records、CurrentVersionId、last run/success、cached hashes、scan/journal cursor、scheduler task ID、SecretRevision、OS SecretReference/locator、Secret Store provider、SecretValue、password hash/verifier、加密 secret blob、Privacy recovery material 和 Recovery Package。

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

Core `BackupPlan` 不包含 `IsFileBacked`、registration path、SQLite identity 或 Declared/Discovered origin。Planning Kernel、Change Detector 和 Archiving 不感知配置来源；Scanner 只报告物理事实，不决定 declaration authority。

每次运行捕获 `ExecutionSemanticSnapshot`。它包含 Managed 的 Revision（适用时）、PlanSemanticFingerprint、所有本轮解析的外部规则源 fingerprint，以及 Secure protection 实际解析的 `SecretSlotId + SecretRevision`。Publish 前必须重新读取并验证；`*.backupplan`、任一 FILE_MANAGED `.backupignore` 或 SecretRevision 变化时返回 PlanChangedDuringRun，不发布 Current、不推进 baseline。外部规则源 fingerprint 基于实际读取的文件 bytes 与版本化解析语义，不能只比较 mtime。

## 7. Fingerprint 与文件移动

PlanAuthority、Import/Register 方式、registration path、authority conversion、`PlanId`、`ArchiveUnitId`、`ExternalSourceId`、`DeviceId`、Global Rule Library provenance、OS SecretReference/locator 与 Secret Store provider 不属于内容选择/归档规格语义，因此不进入 SelectionFingerprint 或 ArchiveSpecFingerprint。`PlanId + ArchiveUnitId` 仍用于 baseline identity；新 identity 没有 baseline 时自然得到 FirstBackup。

`SourceId`、Archive Unit Source-relative logical path、pinned Global Rules Snapshot、Plan/Local Rules、Boundary、LinkPolicy，以及 External Source 的逻辑 mapping/archive destination 属于 SelectionFingerprint。identity 与逻辑路径必须分开处理：明确迁移 identity 不代表内容变化，而 logical path 变化仍可能改变 manifest 与 Current 逻辑结构，必须触发 rebuild。

- authority 切换前后 Plan Snapshot 语义相同：不 rebuild；
- `E:\configs\Code.backupplan` 移到其他位置但内容语义相同：不 rebuild；
- 仅 JSON formatting/property order 变化：PlanSemanticFingerprint 不变；
- 文档内规则、ArchiveSpec 等语义变化：对应 fingerprint 变化；
- 运行期间 Plan 文档、已解析 `.backupignore` 或 Secure SecretRevision 变化：PlanChangedDuringRun。

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

## 10. ID 生命周期

| 操作 | PlanId | SourceId | ArchiveUnitId | ExternalSourceId |
|---|---|---|---|---|
| 修改显示名称 | 保持 | 保持 | 保持 | 保持 |
| 修改本机 binding | 保持 | 保持 | 保持 | 保持 |
| Archive Unit rename/move（明确为同一对象） | 保持 | 保持 | 保持 | 保持 |
| External Source 修改本机路径或显示名 | 保持 | 保持 | 保持 | 保持 |
| Export Managed Plan | 保持 | 保持 | 保持 | 保持 |
| Import as Managed | 保持 | 保持 | 保持 | 保持 |
| Register File-backed | 保持 | 保持 | 保持 | 保持 |
| Save As / Copy 文档 | 保持 | 保持 | 保持 | 保持 |
| Managed ↔ File-backed | 保持 | 保持 | 保持 | 保持 |
| Update existing identity（用户明确选择） | 保持 | 保持 | 保持 | 保持 |
| Clone as new Plan | 重新生成 | 全部重新生成 | 全部重新生成 | 全部重新生成 |

Import 表示接管同一逻辑 Plan，默认保留全部 portable IDs；Clone 才表示创建新的逻辑 Plan。Clone 不继承 ArchiveVersion、CurrentVersion、Committed Baseline、local binding、secret binding 或 scheduler installation。

Save As 只是同一 Plan Document 的物理副本，不能把两个相同 PlanId 的副本在同一 DeviceId 下注册成两个独立计划。用户可以显式把既有 File-backed registration 重定位到副本；若想并存运行，必须使用 Clone 生成全新递归 identity。Managed Plan 的 Export 若随后要 Register 为同一 Plan，也必须走显式 authority conversion，不能形成双 authority。

导入或注册时发现同 PlanId 已存在但文档语义不同，必须返回 IdentityConflict；不得自动覆盖、合并或重新生成 ID。Update existing、Clone as new、Cancel 的最终交互与 merge 规则仍属于后续 P0。

删除对象后从无 identity 的物理路径重新发现，不自动恢复旧 ID。只有 portable 声明、`.backupignore @id`、显式 Import/Restore 或用户确认的 identity migration 可以接续原 identity。

## 11. Portable Configuration 与 Device Local Binding

Portable Configuration 描述可 Git 管理、Import/Export 和跨设备复用的 desired configuration，包括 portable IDs、显示名、逻辑路径、Archive Units、pinned Global Rules Snapshot、其他规则、ArchiveSpec、Protection Configuration、Secret Slot declarations、LinkPolicy、Change Detection mode、History policy、schedule intent 与 External Source declaration。

Device Local Binding 描述本机如何解析这些逻辑对象，包括：

- SourceId → physical SourceRoot；
- plan storage slots → physical CurrentRoot、HistoryRoot；
- ExternalSourceId → physical file/directory；
- SecretSlotId → 本机 Secret Store provider + opaque SecretReference + local SecretRevision；
- scheduler intent → 本机 scheduler installation state。

SourceRoot、CurrentRoot、HistoryRoot 和 External Source physical path 不属于 portable document，不得写入 `*.backupplan v1`，也不随普通 Export 导出。未来若提供 Device Binding Export，必须使用独立格式和明确隐私提示。

ArchiveUnit path 永远是相对于 SourceId 对应 SourceRoot 的逻辑路径，使用 `/`，不得包含盘符、反斜杠、绝对路径或 `..`。External Source 的 archive destination 也是 archive-relative LogicalPath；其物理输入来自 Local Binding。

## 12. DeviceId 与绑定作用域

每个本机安装生成并持久保存一个 UUID v4 `DeviceId`。DeviceId 是 local runtime identity，不进入 `*.backupplan`、PlanSemanticFingerprint、SelectionFingerprint 或 ArchiveSpecFingerprint。DeviceName/hostname 仅用于显示，修改设备名称不创建新 DeviceId。

Local Binding 至少按 `PlanId + DeviceId` 命名空间，并引用 SourceId、ExternalSourceId、SecretSlotId 或 plan storage slot。Secret Binding 的完整逻辑 key 为 `PlanId + DeviceId + SecretSlotId`。多个设备 Register 同一 Plan 时 portable IDs 相同，但 bindings、SecretRevision、ArchiveVersion、Committed Baseline 与运行状态不得串用。

`CHANGE-DETECTION.md` 的 `PlanId + ArchiveUnitId` baseline identity 保持为 portable unit key；在持久化/运行时它位于当前 DeviceId/registration 的本机命名空间中。本文不提前定义 SQLite 复合键。

缺少任何 required Source、Current、History（启用时）、External Source 或 Secure Secret binding 时，Plan 状态为 `PlanNotReady` 并安全失败，不能静默跳过。External Source v1 默认 required；optional 语义尚未设计。binding 存在但当前执行上下文无法读取 secret 时，运行以 SecretUnavailable/SecretStoreError 阻止，不能降级或在 headless 中等待交互。

## 13. Local Path Expression v1

Local Binding 可以保存本机绝对路径，或使用 StowCrate 定义的有限 path variable。v1 只规定：

```text
${HOME}
```

`${HOME}` 由受控的平台用户目录服务解析，不读取任意同名环境变量作为不受审阅的输入。它只能作为 path expression 的根 anchor；展开、规范化后必须得到绝对路径。

v1 不支持 `${MY_CODE}`、`%APPDATA%`、shell expansion、命令替换、任意进程环境变量或未声明 variable。发现未知 `${...}` 必须 validation failure，不能保留原文、展开为空或交给 shell。

`${DESKTOP}`、`${DOCUMENTS}`、`${DOWNLOADS}` 等不属于 v1；未来增加时必须定义跨平台缺失行为和 semantics version。

所有 binding 解析后必须执行 lexical normalization、平台 case 规则、Link/Junction physical canonicalization，以及 SourceRoot/CurrentRoot/HistoryRoot 两两不重叠验证。SourceRoot 仍必须遵守 `FILESYSTEM.md` 的真实目录约束。

Binding 或文档物理位置不进入 archive semantic fingerprint。SourceRoot 改变后必须重新扫描，真实数据差异通过 EntrySetFingerprint 体现；CurrentRoot/HistoryRoot 改变产生 Storage Relocation，不伪装为 rebuild。

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
  PortableOverrides?  # requires declaration
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

SecretSlot 是 Plan-scoped portable logical requirement，可被同一 Plan 的多个 resolved Archive Unit 引用；具体 per-unit ArchiveSpec override 位置仍属后续设计，不在本轮固定：

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
ProtectionMode
Secure: SecretSlotId + local SecretRevision
Privacy: PrivacyProtectionSemanticsVersion
Archive format / compression / metadata policy
manifest and archive semantics versions
```

它不包含 SecretValue、secret-derived verifier、OS SecretReference/locator、Secret Store provider/implementation、DeviceId、Privacy 随机 material 或 Recovery Package bytes。SecretRevision 改变必须 RebuildRequired；只有 locator/provider 变化而逻辑 slot、revision 与有效语义不变时不得伪装成 archive spec 变化。

本节只固定 protection/secret 的领域、portable/local 和安全边界，不定义 JSON Schema、SQLite tables、provider DTO、Recovery Package、Privacy carrier、具体算法或 CLI 参数。

## 17. 与实现现状的差异和迁移约束

1. **`.backupignore v1` Directive 集合变化**：规范最初只允许 `@version/@mode/@case`；现在已正式加入可选 `@id`。当前 parser 尚未实现 `@id`，后续业务实现必须同步 parser、领域返回类型和兼容性测试。
2. **Fingerprint 强类型与字段**：ArchiveUnitId/ExternalSourceId 已正式排除于 SelectionFingerprint，logical source/path/mapping 仍包含；当前 Core 尚未实现这些强类型 fingerprint，不得把旧聚合 string 当作 v1 durable baseline。
3. **Baseline key 与 DeviceId**：Change Detection 的 `PlanId + ArchiveUnitId` 是 portable unit key；DeviceId 只作为本机 registration/binding/runtime namespace，不替换该 key。
4. **实现现状**：当前 Core 仍使用 string ID/逻辑 root，尚无完整 declaration/discovery resolver、ExternalSource/SecretSlot identity、DeviceId、Local/Secret Binding、Global Rules Snapshot、ProtectionCapabilities 或 `ExecutionSemanticSnapshot`。本规范不表示这些实现已完成，也不授权 SQLite/JSON Schema 设计。

## 18. 当前未决顺序

Identity、Portable Path/Local Binding、Global Rules、FILE_MANAGED declaration/discovery 与 Protection Configuration/Secret Binding P0 已确认。JSON Schema 前继续按以下顺序解决：

1. Schedule portability；
2. History / output 配置的可移植边界；
3. Schema compatibility 与 unknown fields；
4. Import identity conflict / merge semantics。

ArchiveSpec override 与 External Source 的完整行为仍需设计，但不得提前固化 JSON 字段。本轮同样不定义 JSON Schema、SQLite schema、Entity、Repository 或 migration。
