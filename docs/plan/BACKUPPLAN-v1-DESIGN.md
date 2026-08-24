# Backup Plan v1 设计工作稿

> [!IMPORTANT]
> 本文用于收敛 Backup Plan v1，尚不是产品或序列化规范真相源。已确定行为仍以 `PRODUCT.md`、`ARCHITECTURE.md`、`BACKUPIGNORE.md`、`FILESYSTEM.md` 和 `CHANGE-DETECTION.md` 为准。未决项在维护者确认前不得进入实现或 JSON Schema。

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
├─ ArchiveUnits[]
│  ├─ ArchiveUnitId
│  ├─ SourceId
│  ├─ LogicalRoot
│  ├─ RuleSource
│  ├─ UiManagedLocalRules? 
│  └─ ArchiveSpec
├─ ExternalSources[]
├─ ChangeDetection
├─ Retention
└─ Schedule
```

这里的分区表达职责，不代表最终 JSON 字段名。

## 4. Identity 与 Revision

### 已确定需求

Committed Baseline identity 需要稳定 `PlanId + ArchiveUnitId`。物理路径、数组位置、显示名称和数据库 row id 都不能作为 identity。

### 建议

- `PlanId`、`SourceId`、`ArchiveUnitId` 使用文件中持久保存的不透明稳定 ID；
- rename、移动输出根、规则修改和普通 revision 不改变 ID；
- clone/fork Plan 默认生成新 PlanId，并为克隆单元生成新 ArchiveUnitId，避免错误继承 baseline；
- `Revision` 是单调递增的并发控制值，执行捕获 revision 与 semantic fingerprint，发布前重验；
- 纯格式化、字段顺序和注释（如果格式未来支持）不应改变 semantic fingerprint。
- 文档物理路径不是 PlanId；移动 File-backed 文档不改变 identity；
- Managed 与 File-backed 必须解析成相同的 identity/value types，authority 不进入 Core；
- File-backed 以 PlanSemanticFingerprint 处理运行中变化，不要求用户手工维护单调 revision。

### 未决

- ID 的外部表示采用 UUID、ULID 还是带前缀的 opaque identifier；
- 手工合并 Git 分支导致 revision 回退/分叉时的冲突策略；
- 导入同 PlanId 时是 update、fork，还是要求用户明确选择。
- 同一 portable Plan 在两台设备各自注册后，baseline 是按 PlanId + ArchiveUnitId + local registration 隔离，还是允许显式接续 Current manifest 中的 identity。

## 5. Source 与路径绑定

逻辑 identity 与设备路径必须分离。建议概念模型：

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

未决：

- `SourceRoot` 与 Current/History 是否属于同一个 binding，还是 Storage 独立绑定；
- DeviceSelector 使用用户命名、稳定设备 ID，还是按优先级匹配多个候选；
- v1 允许的变量白名单（例如 `${HOME}`）与转义语法；
- 未匹配当前设备时 CLI 是安全失败，还是允许交互式重新绑定；
- 路径表达式是否允许相对路径（建议 v1 不允许）。
- 多台设备同时运行同一 portable Plan 时，Current/History 是否必须使用独立 device namespace；
- binding 缺失、目标介质离线或多个 selector 同时匹配时的确定性选择和错误状态。

## 6. Archive Unit 与规则表示

每个单元至少需要稳定 ArchiveUnitId、SourceId、LogicalRoot、RuleSource、ArchiveSpec 和可选单元级策略。

- FILE_MANAGED：文件只声明 `RuleSource = FILE_MANAGED` 和逻辑 root；mode/rules 在运行时从该 root 的 `.backupignore` 读取；
- UI_MANAGED：文件携带完整 local `RuleMode`、case policy 和有序 rules；
- Global/Plan/UI Local pattern 必须使用 `BACKUPIGNORE.md` 相同的 v1 pattern/action 语义，不创建第二套 glob 方言；
- 所有 rule list 保持原始顺序；JSON object/property 顺序不能承担规则优先级；
- Boundary 由所有 Archive Unit logical roots 独立构造，不保存可漂移的派生 boundary list。

未决：Global Rules 在导出文件中保存 snapshot，还是通过稳定 RuleSet ID 引用并附带可选 snapshot。纯外部引用会削弱文件的灾难恢复完整性；完全复制又需要定义后续同步语义。

## 7. ArchiveSpec

建议 ArchiveSpec 只包含会影响归档字节、可恢复语义或执行能力的设置：

- format；
- compression algorithm/level、solid mode、volume size；
- metadata preservation policy；
- protection mode；
- SecretReferenceId + SecretRevision（没有 secret value）；
- manifest/archive semantics version。

格式能力必须在执行前验证。无法 Preserve 某种 Link/metadata 时安全失败，不能自动 dereference 或静默降级。

未决：

- 首版每种格式允许的精确参数集合与 portable default；
- SecretReferenceId 是否适合跨设备导出，还是只导出 logical secret slot 并要求重新绑定；
- ArchiveSpec 是 Plan 默认值 + unit override，还是每个 unit 完整展开。前者便于管理，后者更利于审阅和确定性。

## 8. Change Detection、Retention 与 Schedule

- ChangeDetection 只允许 Standard/Strict，并携带 semantics/hash policy version；
- Retention 只决定已持久化旧 Current 的保留，不参与 Change Detection；
- Schedule 是调用 CLI/use case 的意图描述，不保存平台 scheduler 的内部 task ID；
- 这些非归档设置的修改可以增加 PlanRevision，但不能改变 ArchiveSpecFingerprint。

未决：schedule 是否属于 portable `*.backupplan` 的规范主体，还是作为可选 deployment section；时区、DST、错过运行补偿和并发策略也尚未决定。

## 9. External Sources

External Source 必须通过稳定 ID、实际路径 binding、目标 ArchiveUnitId 与归档内 LogicalPath 表达。不得把外部内容临时写进真实 Source；执行使用独立 staging。

未决：

- external mapping 的规则应用层级；
- 文件与目录映射的冲突/覆盖规则；
- external source 是否允许另一个 Backup Source 内的路径；
- link、filesystem boundary 和 incomplete observation 如何传播到目标单元。

在这些问题确认前，v1 Schema 不应固定 ExternalSources 字段形状。

## 10. 文件角色与真相源关系

此 P0 已确认，正式行为见 [`docs/BACKUPPLAN.md`](../BACKUPPLAN.md)：`*.backupplan` 只有 Portable Declarative Document 一种语义，StowCrate 提供 Managed 与 File-backed 两种互斥 authority。

- Import 复制成 Managed Plan，与原文件脱离；
- Register 保持文件为 File-backed authoritative source；
- 禁止文件与 SQLite 隐式双向同步；
- authority、registration path 和 authority conversion 不进入备份 fingerprint；
- Core 与执行管线只接收统一 ResolvedPlanSnapshot，不感知配置来源；
- v1 不要求未注册文档支持无状态 one-shot execution。

## 11. Schema v1 之前必须确认的 P0

1. ~~Backup Plan Document Authority~~：已确定，见 `BACKUPPLAN.md`；
2. Plan / Source / ArchiveUnit identity 与 clone/import identity；
3. Portable Path、Local Binding 与 Source/Current/History 所有权；
4. Global Rules 的 snapshot/reference 语义；
5. FILE_MANAGED `.backupignore` 的引用与发现语义；
6. Secret Reference / Encryption configuration；
7. Schedule portability；
8. History / output 配置的可移植边界；
9. Schema compatibility、unknown fields 与新版本读取策略；
10. Import identity conflict / merge semantics。

ArchiveSpec override 与 External Source 字段形状仍需设计，但不能越过上述身份、路径和兼容性基础提前固化 Schema。

## 12. 当前设计焦点：Identity + Portable Binding

下一轮必须作为同一个设计包回答：

```text
Portable identity
  PlanId
  SourceId
  ArchiveUnitId
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

需要验证的关键场景：

1. 同一 Git 管理文档分别注册到 Windows `E:\code` 与 macOS `/Users/foo/code`，portable IDs 相同但 runtime/baseline 不串用；
2. File-backed 文档移动位置，registration 更新而 semantic identity 不变；
3. Source 或 Archive Unit rename/move 时，明确是保留 identity 还是创建新对象；
4. Import 同 PlanId、Register 同 PlanId、Clone 与 Fork 不会误继承 Current/baseline；
5. Source/Current/History binding 在 lexical 与 physical canonicalization 后仍两两不重叠；
6. 未绑定设备、离线输出盘和 selector 冲突都安全失败；
7. portable document 不包含本机盘符、secret value、baseline 或 scheduler task ID。

在这组语义确认前不生成 canonical JSON 示例。

## 13. 后续产物

P0 决策完成后依次产出：

1. 正式 `docs/BACKUPPLAN.md`（领域与文件行为真相源）；
2. canonical JSON 示例与 JSON Schema；
3. 导入、直接执行、clone、update、冲突和 secret rebinding 测试矩阵；
4. Application ports 与纯领域 contract；
5. `docs/PERSISTENCE.md`；
6. 最后才设计 SQLite schema 和 migration。
