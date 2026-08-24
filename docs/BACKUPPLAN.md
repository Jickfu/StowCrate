# Backup Plan Document v1

本文是 `*.backupplan` 文档角色、Plan Authority 和配置真相源关系的规范真相源。Identity、Portable Path、JSON Schema 等尚未确认的部分仍以“未决”处理，不得从示例或工作稿推断。

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

重新注册不能依据文档物理路径认定 Plan identity。文档需要稳定逻辑 identity，但具体 ID 格式、clone/import 保留规则和跨设备 baseline 隔离属于下一项 P0。

## 9. 当前未决顺序

JSON Schema 前按以下顺序解决：

1. Plan / Source / ArchiveUnit identity；
2. Portable Path 与 Local Binding；
3. Global Rules 的 snapshot/reference 语义；
4. FILE_MANAGED `.backupignore` 的引用与发现语义；
5. Secret Reference / Encryption configuration；
6. Schedule portability；
7. History / output 配置的可移植边界；
8. Schema compatibility 与 unknown fields；
9. Import identity conflict / merge semantics。
