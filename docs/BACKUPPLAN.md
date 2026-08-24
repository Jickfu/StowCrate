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

文档保存 portable desired configuration，例如：Plan name、logical sources、Archive Units、pinned Global Rules Snapshot、Plan/UI-managed rules、ArchiveSpec、LinkPolicy、Change Detection mode、History policy、schedule intent 与 External Source definitions。

以下永不进入文档：Committed Baseline、ArchiveVersion records、CurrentVersionId、last run/success、cached hashes、scan/journal cursor、scheduler task ID 和 secret values。

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

每次运行捕获 `ExecutionSemanticSnapshot`。它包含 Managed 的 Revision（适用时）、PlanSemanticFingerprint，以及所有本轮解析的外部规则源 fingerprint。Publish 前必须重新读取并验证；`*.backupplan` 或任一 FILE_MANAGED `.backupignore` 变化时返回 PlanChangedDuringRun，不发布 Current、不推进 baseline。外部规则源 fingerprint 基于实际读取的文件 bytes 与版本化解析语义，不能只比较 mtime。

## 7. Fingerprint 与文件移动

PlanAuthority、Import/Register 方式、registration path、authority conversion、`PlanId`、`ArchiveUnitId`、`ExternalSourceId`、`DeviceId` 与 Global Rule Library provenance 不属于内容选择语义，因此不进入 SelectionFingerprint 或 ArchiveSpecFingerprint。`PlanId + ArchiveUnitId` 仍用于 baseline identity；新 identity 没有 baseline 时自然得到 FirstBackup。

`SourceId`、Archive Unit Source-relative logical path、pinned Global Rules Snapshot、Plan/Local Rules、Boundary、LinkPolicy，以及 External Source 的逻辑 mapping/archive destination 属于 SelectionFingerprint。identity 与逻辑路径必须分开处理：明确迁移 identity 不代表内容变化，而 logical path 变化仍可能改变 manifest 与 Current 逻辑结构，必须触发 rebuild。

- authority 切换前后 Plan Snapshot 语义相同：不 rebuild；
- `E:\configs\Code.backupplan` 移到其他位置但内容语义相同：不 rebuild；
- 仅 JSON formatting/property order 变化：PlanSemanticFingerprint 不变；
- 文档内规则、ArchiveSpec 等语义变化：对应 fingerprint 变化；
- 运行期间 Plan 文档或已解析 `.backupignore` 变化：PlanChangedDuringRun。

## 8. 灾难恢复

`*.backupplan` + Current + History 应足以在新设备重新注册并重新绑定 portable configuration，但不承诺恢复旧机器的 baseline 或运行历史；这些本地 durable state 需要 `config.db` 一致快照。

重新注册不能依据文档物理路径认定 Plan identity。Plan 的 portable identity 可以跨设备识别同一份声明配置，但每台设备的 registration、binding、baseline 与运行状态保持本机隔离。

## 9. 稳定 Identity

以下 portable 对象必须具有显式、持久、稳定的 UUID v4：

- `PlanId`；
- `SourceId`；
- `ArchiveUnitId`；
- `ExternalSourceId`。

外部文本使用 RFC 4122/9562 常见的 canonical lowercase `8-4-4-4-12` 格式，并验证 version 为 4、variant 合法。领域层应使用互不混用的强类型 ID；数据库 row id 即使存在，也不是领域 identity。

Name、LogicalPath、RelativePath、physical/absolute path、realpath、文件位置、数组下标、hostname 和数据库自增键都不能生成或替代 identity。

- `PlanId` 跟随 portable document；不同设备 Register 同一文档时看到相同 PlanId；
- `SourceId` 表示逻辑 Source，与当前机器的 SourceRoot 无关；
- `ArchiveUnitId` 表示逻辑 Crate，与其当前 Source-relative path 无关；
- `ExternalSourceId` 表示逻辑外部输入，与本机实际文件位置无关。

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

Portable Configuration 描述可 Git 管理、Import/Export 和跨设备复用的 desired configuration，包括 portable IDs、显示名、逻辑路径、Archive Units、pinned Global Rules Snapshot、其他规则、ArchiveSpec、LinkPolicy、Change Detection mode、History policy、schedule intent 与 External Source declaration。

Device Local Binding 描述本机如何解析这些逻辑对象，包括：

- SourceId → physical SourceRoot；
- plan storage slots → physical CurrentRoot、HistoryRoot；
- ExternalSourceId → physical file/directory；
- portable secret slot → 本机 Secret Store reference；
- scheduler intent → 本机 scheduler installation state。

SourceRoot、CurrentRoot、HistoryRoot 和 External Source physical path 不属于 portable document，不得写入 `*.backupplan v1`，也不随普通 Export 导出。未来若提供 Device Binding Export，必须使用独立格式和明确隐私提示。

ArchiveUnit path 永远是相对于 SourceId 对应 SourceRoot 的逻辑路径，使用 `/`，不得包含盘符、反斜杠、绝对路径或 `..`。External Source 的 archive destination 也是 archive-relative LogicalPath；其物理输入来自 Local Binding。

## 12. DeviceId 与绑定作用域

每个本机安装生成并持久保存一个 UUID v4 `DeviceId`。DeviceId 是 local runtime identity，不进入 `*.backupplan`、PlanSemanticFingerprint、SelectionFingerprint 或 ArchiveSpecFingerprint。DeviceName/hostname 仅用于显示，修改设备名称不创建新 DeviceId。

Local Binding 至少按 `PlanId + DeviceId` 命名空间，并引用 SourceId、ExternalSourceId 或 plan storage slot。多个设备 Register 同一 Plan 时 portable IDs 相同，但 bindings、ArchiveVersion、Committed Baseline 与运行状态不得串用。

`CHANGE-DETECTION.md` 的 `PlanId + ArchiveUnitId` baseline identity 保持为 portable unit key；在持久化/运行时它位于当前 DeviceId/registration 的本机命名空间中。本文不提前定义 SQLite 复合键。

缺少任何 required Source、Current、History（启用时）或 External Source binding 时，Plan 状态为 `PlanNotReady` 并安全失败，不能静默跳过。External Source v1 默认 required；optional 语义尚未设计。

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

## 16. 与实现现状的差异和迁移约束

1. **`.backupignore v1` Directive 集合变化**：规范最初只允许 `@version/@mode/@case`；现在已正式加入可选 `@id`。当前 parser 尚未实现 `@id`，后续业务实现必须同步 parser、领域返回类型和兼容性测试。
2. **Fingerprint 强类型与字段**：ArchiveUnitId/ExternalSourceId 已正式排除于 SelectionFingerprint，logical source/path/mapping 仍包含；当前 Core 尚未实现这些强类型 fingerprint，不得把旧聚合 string 当作 v1 durable baseline。
3. **Baseline key 与 DeviceId**：Change Detection 的 `PlanId + ArchiveUnitId` 是 portable unit key；DeviceId 只作为本机 registration/binding/runtime namespace，不替换该 key。
4. **实现现状**：当前 Core 仍使用 string ID/逻辑 root，尚无完整 declaration/discovery resolver、ExternalSource identity、DeviceId、Local Binding、Global Rules Snapshot 或 `ExecutionSemanticSnapshot`。本规范不表示这些实现已完成，也不授权 SQLite/JSON Schema 设计。

## 17. 当前未决顺序

Identity、Portable Path/Local Binding、Global Rules 与 FILE_MANAGED declaration/discovery P0 已确认。JSON Schema 前继续按以下顺序解决：

1. Secret Reference / Encryption configuration；
2. Schedule portability；
3. History / output 配置的可移植边界；
4. Schema compatibility 与 unknown fields；
5. Import identity conflict / merge semantics。

ArchiveSpec override 与 External Source 的完整行为仍需设计，但不得提前固化 JSON 字段。本轮同样不定义 JSON Schema、SQLite schema、Entity、Repository 或 migration。
