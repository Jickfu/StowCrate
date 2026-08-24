# Backup Plan v1 设计工作稿

> [!IMPORTANT]
> 本文用于收敛 Backup Plan v1，尚不是产品或序列化规范真相源。已确定行为仍以 `PRODUCT.md`、`ARCHITECTURE.md`、`BACKUPPLAN.md`、`BACKUPIGNORE.md`、`FILESYSTEM.md` 和 `CHANGE-DETECTION.md` 为准。未决项在维护者确认前不得进入实现或 JSON Schema。

## 1. 设计顺序

Backup Plan v1 分两步设计：

1. 先确定领域语义、稳定 identity、revision、路径绑定和各配置分区；
2. 领域契约稳定后再定义 `*.backupplan` JSON Schema、导入/导出与版本兼容。

不能先围绕某个早期 JSON 形状修改领域模型，也不能先创建 SQLite Entity。

## 2. 仓库已经确定的约束

- `*.backupplan` 是扩展名，不是固定文件名；
- 文件是版本化、可导出、可审阅的完整方案，用于迁移、灾难恢复和 Git 管理；
- 路径使用逻辑源与分设备/平台绑定，不把某个 Windows 盘符当作唯一身份；
- `SourceRoot`、`CurrentRoot`、`HistoryRoot` 的 lexical 与 physical canonical path 必须两两不重叠；
- Global → Plan → Local Rules，Archive Boundary 优先于规则；
- FILE_MANAGED 单元的 mode/rules 只来自 `.backupignore`，Backup Plan 不得复制第二份局部规则真相；
- UI_MANAGED 单元由持久配置保存完整 local mode/rules；同一单元不能同时是 FILE_MANAGED；
- LinkPolicy v1 只有 Preserve（默认）和 Skip；Scanner 不读取该策略；
- Change Detection v1 只有 Standard（默认）和 Strict；
- Archive Unit 必须获得可跨 revision 持续识别的稳定 identity，供 Baseline 使用；
- 密码、token、恢复密钥和 secret value 不得进入文件；只允许非秘密引用与 revision；
- 调度、History retention、输出根迁移不属于 ArchiveSpecFingerprint，不应触发归档重建；
- 首版输出标准归档，并同时支持 Windows、macOS、Linux。

## 3. 建议的领域分区（待确认）

```text
BackupPlan
├─ Identity
│  ├─ PlanId
│  ├─ DisplayName
│  ├─ Revision
│  └─ SemanticFingerprint
├─ Sources[]
│  ├─ SourceId
│  ├─ LogicalName
│  └─ DeviceBindings[]
├─ Storage
│  ├─ CurrentBinding
│  └─ HistoryBinding
├─ Rules
│  ├─ GlobalRuleSetRef / snapshot
│  └─ PlanRuleSet
├─ ArchiveSpecDefault
├─ ArchiveUnits[]
│  ├─ ArchiveUnitId
│  ├─ SourceId
│  ├─ LogicalRoot
│  ├─ RuleSource
│  ├─ UiManagedLocalRules? 
│  ├─ ArchiveSpecOverride?
│  └─ HistoryOverride?
├─ ExternalSources[]
├─ ChangeDetection
├─ Retention
└─ Schedule
```

这里的分区表达职责，不代表最终 JSON 字段名。

## 4. Identity 与 Revision（P0 已确认）

### 已确定需求

Committed Baseline identity 需要稳定 `PlanId + ArchiveUnitId`。物理路径、数组位置、显示名称和数据库 row id 都不能作为 identity。

### 已确认

- `PlanId`、`SourceId`、`ArchiveUnitId`、`ExternalSourceId` 使用 canonical lowercase UUID v4；
- rename、移动输出根、规则修改和普通 revision 不改变 ID；
- clone/fork Plan 递归生成新的 PlanId、SourceId、ArchiveUnitId、ExternalSourceId，避免错误继承 baseline；
- `Revision` 是单调递增的并发控制值，执行捕获 revision 与 semantic fingerprint，发布前重验；
- 纯格式化、字段顺序和注释（如果格式未来支持）不应改变 semantic fingerprint。
- 文档物理路径不是 PlanId；移动 File-backed 文档不改变 identity；
- Managed 与 File-backed 必须解析成相同的 identity/value types，authority 不进入 Core；
- File-backed 以 PlanSemanticFingerprint 处理运行中变化，不要求用户手工维护单调 revision。

### Import/Update/Clone P0 已确认

- v1 不做 automatic/field/partial/three-way merge；Git 分支的文本 merge 由 Git 和用户完成；
- same PlanId + same semantic 是幂等 NoOp；语义不同时必须明确选择 Update Existing、Clone As New 或 Cancel；
- Update 是 whole-document semantic replacement，只按稳定 ID 对应并保留适用 runtime state；
- Clone 递归生成全部 portable IDs，不继承 binding、Current、History、baseline 或 scheduler state。

## 5. Source 与路径绑定（P0 已确认）

逻辑 identity 与设备路径必须分离。正式语义见 `BACKUPPLAN.md`；概念模型为：

```text
BackupSource
  SourceId
  LogicalName

DeviceBinding
  BindingId
  DeviceSelector
  Platform
  SourceRootExpression
  CurrentRootExpression
  HistoryRootExpression
```

File-backed 文档只保存 portable logical configuration；registration 在每台设备保存 local binding。Managed Plan 也使用相同 binding 模型，不能因为配置存在 SQLite 就把盘符混进 Core identity。

路径表达式是 untrusted input，解析后必须执行平台绝对化、变量白名单、case 规则、Link/Junction physical canonicalization 和三根两两不重叠验证。

以下细节已经确认：

- Plan/Source/ArchiveUnit/ExternalSource 使用 canonical lowercase UUID v4；
- DeviceId 也是 UUID v4，但只属于本机 runtime namespace；
- Source/Current/History/External physical path 全部属于 Local Binding，不进入普通 Export；
- v1 只支持受控 `${HOME}` anchor，不支持任意环境变量；
- ArchiveUnit path 是 Source-relative `/` 逻辑路径；
- required binding 缺失时 PlanNotReady；External Source v1 全部 required，不提供 optional；
- binding 变化不进入 Selection/ArchiveSpec fingerprint，数据变化由重新扫描后的 EntrySet 体现。

Local Binding 的数据库结构、UI 编辑方式和未来 Device Binding Export 格式属于 Persistence/UX 后续设计，不改变这里已经确认的语义，也不能提前固化 SQLite Schema。

## 6. Archive Unit 与规则表示

每个单元至少需要稳定 ArchiveUnitId、SourceId、LogicalRoot、RuleSource、ArchiveSpec 和可选单元级策略。

- FILE_MANAGED：文件只声明 `RuleSource = FILE_MANAGED` 和逻辑 root；mode/rules 在运行时从该 root 的 `.backupignore` 读取；
- UI_MANAGED：文件携带完整 local `RuleMode`、case policy 和有序 rules；
- Global/Plan/UI Local pattern 必须使用 `BACKUPIGNORE.md` 相同的 v1 pattern/action 语义，不创建第二套 glob 方言；
- 所有 rule list 保持原始顺序；JSON object/property 顺序不能承担规则优先级；
- Boundary 由所有 Archive Unit logical roots 独立构造，不保存可漂移的派生 boundary list。

已确认 Global Rules 在 Plan 中保存 authoritative pinned snapshot；Library ID/revision 等只可作为 optional provenance，不是运行时依赖。FILE_MANAGED declaration/discovery 合成和 `.backupignore @id` 冲突规则也已进入 `BACKUPPLAN.md`。

## 7. ArchiveSpec（schema-shaping design 已确认）

Plan 必须显式保存完整 ArchiveSpecDefault；declared Archive Unit 可逐组件 override，Application 解析为每单元 EffectiveArchiveSpec。v1 portable intent 只有：

- Format：SevenZip / Zip / TarZstd；
- CompressionPreset：Store / Fast / Standard / Extreme；
- ProtectionConfiguration：None / Privacy / Secure(SecretSlotId)。

新 Plan 产品默认 SevenZip + Standard + None。未声明 FILE_MANAGED unit 继承 Plan default。algorithm/level/dictionary/solid/thread/raw CLI option、volume size 与 metadata toggle 都不是 portable fields；它们由 Format + Preset + ArchiveSemanticsVersion 固定并在 adapter capability validation 中解析。v1 固定 single-volume。

PlanSemanticFingerprint 区分 inherit 与 explicit；ExecutionSemanticFingerprint/ArchiveSpecFingerprint 使用 resolved effective semantics。Format 改变同时影响 archive 与 output layout，compression/protection 通常只影响 archive。完整规范见 `BACKUPPLAN.md` 第 21 节。

## 8. Change Detection、Retention 与 Schedule

- ChangeDetection 只允许 Standard/Strict，并携带 semantics/hash policy version；
- Retention 只决定已持久化旧 Current 的保留，不参与 Change Detection；
- Schedule 是调用 CLI/use case 的意图描述，不保存平台 scheduler 的内部 task ID；
- 这些非归档设置的修改可以增加 PlanRevision，但不能改变 ArchiveSpecFingerprint。

Schedule 已确认为 portable ScheduleIntent，本机 scheduler installation 独立；local wall-clock、DST、missed-run 与并发策略见 `BACKUPPLAN.md`。History/Output portable policy、retention 与 storage relocation 也已确认。

## 9. External Sources

External Source v1 已确认为 explicit supplemental input，完整规范见 `BACKUPPLAN.md` 第 22 节：

- declaration 使用 ExternalSourceId、Name、File/Directory Kind、declared TargetArchiveUnitId 与非空 ArchiveDestination；
- 一个 declaration 对应一个 required device-local physical root，不支持 optional/glob/multi-root；
- external entries 绕过普通 Rules，但遵守 no-follow、filesystem/child boundary、LinkPolicy、reserved namespace、collision、completeness、TOCTOU 与 capability；
- external directory 不做 Archive Unit discovery，`.backupignore` 是普通 payload；
- normal/external/generated entry 不允许不同 owner overlay；
- private staging 保持原路径只读，Candidate fingerprint 必须对应真正 staged payload；
- mapping 进入 SelectionFingerprint，payload 进入 EntrySetFingerprint，physical binding 只进入 ExecutionBindingFingerprint。

## 10. 文件角色与真相源关系

此 P0 已确认，正式行为见 [`docs/BACKUPPLAN.md`](../BACKUPPLAN.md)：`*.backupplan` 只有 Portable Declarative Document 一种语义，StowCrate 提供 Managed 与 File-backed 两种互斥 authority。

- Import 复制成 Managed Plan，与原文件脱离；
- Register 保持文件为 File-backed authoritative source；
- 禁止文件与 SQLite 隐式双向同步；
- authority、registration path 和 authority conversion 不进入备份 fingerprint；
- Core 与执行管线只接收统一 ResolvedPlanSnapshot，不感知配置来源；
- v1 不要求未注册文档支持无状态 one-shot execution。

## 11. Schema v1 之前的 P0 状态

1. ~~Backup Plan Document Authority~~：已确定，见 `BACKUPPLAN.md`；
2. ~~Plan / Source / ArchiveUnit / ExternalSource identity 与基本 ID 生命周期~~：已确定；
3. ~~Portable Path、Local Binding、DeviceId 与 Source/Current/History 所有权~~：已确定；
4. ~~Global Rules 的 snapshot/reference 语义~~：已确定；
5. ~~FILE_MANAGED `.backupignore` 的引用与发现语义~~：已确定；
6. ~~Secret Reference / Encryption configuration~~：已确定；
7. ~~Schedule portability~~：已确定；
8. ~~History / output 配置的可移植边界~~：已确定；
9. ~~Schema compatibility、unknown fields 与新版本读取策略~~：已确定；
10. ~~Import identity conflict / merge semantics~~：已确定。

Backup Plan P0、Domain Freeze、Schema Design 与实际 Schema Review 均已完成；Backup Plan v1 Document Contract Frozen。

## 12. Identity + Portable Binding 结论

该设计包已经确认：

```text
Portable identity
  PlanId
  SourceId
  ArchiveUnitId
  ExternalSourceId
        ↓
Local registration identity
        ↓
Device/path bindings
  SourceRoot
  CurrentRoot
  HistoryRoot
        ↓
ResolvedPlanSnapshot
```

未来实现必须验证：

1. 同一 Git 管理文档分别注册到 Windows `E:\code` 与 macOS `/Users/foo/code`，portable IDs 相同但 runtime/baseline 不串用；
2. File-backed 文档移动位置，registration 更新而 semantic identity 不变；
3. Source 或 Archive Unit rename/move 时，明确是保留 identity 还是创建新对象；
4. Import 同 PlanId、Register 同 PlanId、Clone 与 Fork 不会误继承 Current/baseline；
5. Source/Current/History binding 在 lexical 与 physical canonicalization 后仍两两不重叠；
6. 未绑定设备、离线输出盘和 selector 冲突都安全失败；
7. portable document 不包含本机盘符、secret value、baseline 或 scheduler task ID。

这些语义已经进入 `BACKUPPLAN.md` 与 `BACKUPIGNORE.md`，但仍不生成 canonical JSON 示例。

## 13. 当前实现焦点：Semantic validation and frozen domain mapping

Backup Plan v1 Document Contract Runtime 已完成：frozen DTO、strict UTF-8/duplicate-property reader、`schemaVersion` dispatch 和 Draft 2020-12 structural validation 均位于 Infrastructure document adapter boundary。

下一项推进为 **BackupPlanDocumentV1 Semantic Validator + DTO→Frozen Domain Mapper**。该阶段独立处理 reference graph、规则语法、Archive Boundary、External collision 与 reader 支持的 semantics pins；仍不实现 Import/Update、Local Binding、writer、SQLite 或 EF，也不从 Schema/DTO 直接生成或复用 persistence Entity。

## 14. 后续产物

后续依次产出：

1. 继续增量完善正式 `docs/BACKUPPLAN.md`；
2. ~~完成 Domain Freeze Review~~；
3. 创建 canonical JSON 示例与 JSON Schema；
4. 导入、注册、clone、update、冲突和 secret rebinding 测试矩阵；
5. Application ports 与纯领域 contract；
6. `docs/PERSISTENCE.md`；
7. 最后才设计 SQLite schema 和 migration。
