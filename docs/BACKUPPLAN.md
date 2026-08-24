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

文档保存 portable desired configuration，例如：Plan name、logical sources、Archive Units、Plan/UI-managed rules、ArchiveSpec、LinkPolicy、Change Detection mode、History policy、schedule intent 与 External Source definitions。

以下永不进入文档：Committed Baseline、ArchiveVersion records、CurrentVersionId、last run/success、cached hashes、scan/journal cursor、scheduler task ID 和 secret values。

File-backed 不等于无状态执行。持续执行仍需要本机 registration、binding、secret reference、ArchiveVersion 与 baseline。v1 不要求未注册文件支持无状态 one-shot backup；未来 `--ephemeral` 必须单独定义，不能复用持续任务语义。

## 6. 统一解析边界

Managed 和 File-backed 最终都解析为不可变、已验证的统一 Plan Snapshot：

```text
Managed repository ─────────┐
                            ├─→ ResolvedPlanSnapshot → Scanner / Planner / Change Detector / Executor
Plan document loader ───────┘
```

Core `BackupPlan` 不包含 `IsFileBacked`、registration path 或 SQLite identity。Scanner、Planning Kernel、Change Detector 和 Archiving 不感知配置来源。

Managed 运行捕获 Revision + PlanSemanticFingerprint；File-backed 运行至少捕获 PlanSemanticFingerprint。Publish 前必须重新验证当前 semantic identity；变化时返回 PlanChangedDuringRun，不发布 Current、不推进 baseline。

## 7. Fingerprint 与文件移动

PlanAuthority、Import/Register 方式、registration path 和 authority conversion 不属于备份语义，因此不进入 SelectionFingerprint 或 ArchiveSpecFingerprint。

- authority 切换前后 Plan Snapshot 语义相同：不 rebuild；
- `E:\configs\Code.backupplan` 移到其他位置但内容语义相同：不 rebuild；
- 仅 JSON formatting/property order 变化：PlanSemanticFingerprint 不变；
- 文档内规则、ArchiveSpec 等语义变化：对应 fingerprint 变化；
- 运行期间文件语义变化：PlanChangedDuringRun。

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

SourceId 变化属于来源语义变化并参与 SelectionFingerprint。ArchiveUnitId 当前是否直接进入 SelectionFingerprint 与现有 `CHANGE-DETECTION.md` 存在差异，按第 15 节处理。

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

Portable Configuration 描述可 Git 管理、Import/Export 和跨设备复用的 desired configuration，包括 portable IDs、显示名、逻辑路径、Archive Units、规则、ArchiveSpec、LinkPolicy、Change Detection mode、History policy、schedule intent 与 External Source declaration。

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

## 14. FILE_MANAGED Archive Unit Identity

UI_MANAGED ArchiveUnitId 由 authoritative Managed/File-backed plan configuration 保存。FILE_MANAGED Archive Unit 的 `.backupignore` 可以使用可选 `@id <uuid-v4>` 声明其 portable ArchiveUnitId；完整语法见 `BACKUPIGNORE.md`。

- `.backupignore` 为空时仍合法；`@id` 不是必填；
- StowCrate 不得为了生成 identity 自动修改用户文件或污染 Git working tree；
- 用户主动执行“写入稳定 ID”操作时才可修改文件，并必须预览/确认；
- 没有 `@id` 的自动发现单元可在本机 registration 中生成并记录 ArchiveUnitId；路径改变默认视为旧单元删除 + 新单元创建；
- 用户可以显式确认 rename/move 并迁移 identity；不得根据 inode、FileId、realpath 或内容 hash 自动猜测；
- portable plan 已声明 ArchiveUnitId 且 `.backupignore` 也有 `@id` 时，两者必须相同；不同则 IdentityConflict/Fatal；
- 同一 Plan 中重复的 ArchiveUnitId 或 ExternalSourceId 是 Fatal validation error。

`@id` 只标识当前 `.backupignore` 所在 Archive Unit，不声明 child identity，也不改变 RuleMode/Rules。它属于文档 metadata；修改该行会改变被归档 `.backupignore` 文件内容，但 identity 本身是否直接触发 SelectionFingerprint 仍受第 15 节现有规范约束。

## 15. 与现有正式规范的差异

1. **`.backupignore v1` Directive 集合变化**：现有规范此前只允许 `@version/@mode/@case`，未知 directive fatal。本次根据明确决定加入可选 `@id`，并在 `BACKUPIGNORE.md` 记录演进。当前 parser 尚未实现 `@id`，本轮禁止修改业务代码；实现前仓库存在已知规范/代码差距。
2. **ArchiveUnitId 与 SelectionFingerprint**：`CHANGE-DETECTION.md` 当前要求 Archive Unit 稳定 identity/path 进入 SelectionFingerprint；设计稿建议 ArchiveUnitId 只作为 manifest/version/baseline key，不直接触发 archive bytes rebuild。两者有实质差异，本次不覆盖 Change Detection：现行正式规则仍是 identity 参与 SelectionFingerprint，等待维护者后续明确。
3. **Baseline key 与 DeviceId**：Change Detection 将 portable baseline identity 写为 `PlanId + ArchiveUnitId`；本设计加入 DeviceId，但仅作为本机 registration/binding/runtime namespace，不替换 portable unit key，因此不构成覆盖。
4. **路径表达位置收紧**：PRODUCT 先前只说计划采用逻辑源和分平台映射并允许 `${HOME}`，未明确映射是否存入 portable document。本规范将 physical mapping 固定为 Device Local Binding；已同步 PRODUCT/ARCHITECTURE。
5. **实现现状**：当前 Core 仍使用 string ID/逻辑 root，尚无 ExternalSource identity、DeviceId、Local Binding 或 `@id` parser。本规范不表示这些实现已完成，也不授权 SQLite/JSON Schema 设计。

## 16. 当前未决顺序

Identity 与 Portable Path/Local Binding P0 已确认。JSON Schema 前继续按以下顺序解决：

1. Global Rules 的 snapshot/reference 语义；
2. FILE_MANAGED `.backupignore` 与 Backup Plan declaration 的完整规则/发现关系（identity 部分已确认）；
3. Secret Reference / Encryption configuration；
4. Schedule portability；
5. History / output 配置的可移植边界；
6. Schema compatibility 与 unknown fields；
7. Import identity conflict / merge semantics。

ArchiveSpec override 与 External Source 的完整行为仍需设计，但不得提前固化 JSON 字段。
