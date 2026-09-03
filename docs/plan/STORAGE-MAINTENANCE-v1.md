# M5.3 Storage Maintenance v1 协议

状态：协议已冻结，分层实现中；不是功能完成声明。维护者已授权由 Codex 自行设计与审查。

## 1. 事务边界

Output Reorganization 与 Storage Relocation 使用同一 Plan-scoped transfer protocol，但一次操作只能选择一种 kind；不隐式合并配置更新与迁移。

- Reorganization：冻结全部受影响 Current 的旧/新 relative path 与旧/新 OutputLayoutFingerprint；CurrentRoot 不变。只改变 Current placement 和 committed layout，不更新 portable desired configuration。
- Relocation：一次可以改变 CurrentRoot、HistoryRoot 或两者；冻结所选根下全部 tracked placement，包含 inactive unit 的 retained state。relative path 不变，只切换选定 root binding。
- 两者都保留 ArchiveVersionId、archive bytes、ArchiveVersion lifecycle/timestamp 和 baseline。不调用 Archiver，不用内容变化伪造迁移。
- 全部目标 durable 后才允许单一 SQLite transaction 切换全部 authoritative metadata。不得逐 unit 提前切换共享 root。
- 不接纳已有目标，即使 SHA-256 相同；不覆盖目标，不支持原地交换/循环路径。原位未变化的条目是验证项而不是 copy/delete 项，必须在 commit 前验证，不能进入旧副本清理集合。

## 2. 冻结事实和互斥

Begin transaction 必须重验 active registration、expected roots/placements/layout、迁移条目集合完整性及全设备 root safety。相同 Plan 不得有未完成 publish、PREPARED retention 或未完成 storage maintenance。COMPLETED retention 必须先完成旧根 absence reconciliation/compaction；旧路径 cleanup 必须先收敛。

从 Begin 到 cleanup 完成期间持久保留 old/new root reservation；其他 Plan 的激活、binding 保存与执行也必须检查 reservation。仅依赖进程锁不够。该 Plan 的 publish、retention、另一次 relocation/reorganization、影响迁移的 binding/configuration/identity mutation 均拒绝。File-backed 外部文件不能锁住，commit 前必须重读并验证冻结的 relevant semantics；变更只产生 out-of-sync，不重新解释旧 journal。

Journal 必须保存 transaction UUID、PlanId、DeviceId、kind、协议版本、old/new root canonical path/comparison key/native identity、expected source/layout facts、全部 artifact 的 ArchiveVersionId/UnitId/slot/old path/new path/temp path、SHA-256/length 和旧对象 identity。根与对象 identity 是带平台/编码版本的 opaque Infrastructure evidence，不进入 Core、portable document 或 baseline。

这些字段是不可变 transaction manifest，不是通用 JSON extension bag。config.db v4 已实现 root relocation 的 pre-commit journal/reservation；具体字段、FK、CHECK、canonical codec 与 CAS 约束见 `CONFIG-DB-v1-SCHEMA-DESIGN.md` v4 addendum。Output Reorganization 清单和 post-commit persistence 仍待实现，不能把当前 v4 当作完整迁移落地。

## 3. 顺序与持久点

1. Preview 无写入：inventory、容量、no-follow root/ancestor、collision 与 overlap 校验；未知内容只诊断，不加入 manifest。
2. SQLite 原子 Begin：保存完整 manifest、root reservation 和 `PREPARED`。此时旧 metadata 与旧文件保持不变。
3. 每个目标用 destination-local `CreateNew` temp 复制；no-follow 验证源 root/ancestor/leaf identity，流式 SHA-256/length、flush file data，并重验源 identity。
4. temp 的数据与 directory entry durable 后，将 temp native identity proof 原子写入 journal。没有该记录不能 rename。copy 后、proof 保存前崩溃留下的文件不因名称/hash 自动取得 ownership。
5. 重验 target root/ancestor 和已记录 temp identity，以 no-overwrite atomic rename 发布；flush directory metadata；journal 保存同一对象 identity 的 target durable proof。相同文件系统仍 copy，不 move 旧文件或使用 hardlink。
6. 所有条目完成 target proof 后进入 `TARGETS_DURABLE`。commit 前重新验证全部目标及 frozen metadata/semantics/root reservation；hash 匹配不替代 identity 与 namespace proof。
7. 单一 SQLite transaction CAS：重验 journal 与全部 expected metadata，切换根 binding 或全部 Current path/layout，同时标记 `METADATA_COMMITTED`。这是永久成功点。
8. 提交后仅对 manifest 中 exact old-copy cleanup 集合执行 identity + SHA/length/no-follow 检查、删除、directory barrier 与 absence re-proof；逐项保存 cleanup completion。任何漂移保留数据并报告 warning/out-of-sync，不回滚新 metadata。
9. 全部旧副本 durable absent 后 `COMPLETED`，再按独立 reconciliation/compaction 规则释放 reservation。绝不递归删除旧根或未知目录。

`MaintenanceState` 只汇总健康状态，不授权文件操作。单纯 kernel 状态/布尔标记也不产生物理权限，Infrastructure 必须生成和验证完整 proof，repository 必须独立重验 immutable manifest/CAS。

## 4. Recovery 与取消

| Durable journal | 物理事实 | 允许动作 |
|---|---|---|
| PREPARED，无 staged identity | temp/target 不存在 | 从旧源重新复制 |
| PREPARED，无 staged identity | temp/target 已存在 | ambiguous；保留文件，不凭名称/hash采用或删除 |
| staged identity 已保存 | temp 精确匹配、target 不存在 | 重验后发布 |
| staged identity 已保存 | target 是同一对象且 integrity 匹配 | 重做 barrier 后补记 durable target proof |
| 任一 pre-commit 状态 | source/root/target mismatch 或两处冲突 | 保留旧 authority 和 journal，out-of-sync；不猜 rollback/commit |
| TARGETS_DURABLE | 全部目标和 expected metadata 重验通过 | 原子 metadata commit |
| METADATA_COMMITTED | 新 authority 有效、旧副本匹配/已不存在 | exact cleanup 或 absence reconciliation |
| METADATA_COMMITTED | 旧副本/root 被替换 | 只报告 cleanup warning，禁止删除或回滚 |

取消在条目稳定边界生效：pre-commit 保留旧 authority 与 journal，可恢复继续；不是自动丢弃事务，也不自动清理无 proof 的副本。namespace mutation 开始后忽略取消直到写入可恢复状态。metadata commit 后取消不能把成功报告为失败，未完成清理持久保留。新操作不得绕过挂起事务。

## 5. 模块与验收

Application 负责不可变 progress kernel、事务编排、recovery classification 与端口；Infrastructure 负责 SQLite manifest/CAS、reservation queries、native filesystem proof、copy/publish/cleanup；Core 不依赖 storage roots 或 OS。App/CLI 后续只调用用例。

验收必须覆盖多 artifact 部分成功不能 commit、重复/串 transaction proof、非法 restore 状态、取消稳定边界、每个 filesystem/SQLite crash window、同/跨 filesystem、source/target/root replacement、目标冲突、case/file-directory collision、retention/publish 互斥、inactive retained placements、cross-Plan reservation、File-backed drift、post-commit cleanup failure，以及三平台真实 fixture。断电承诺限于 OS/filesystem 成功 durability barrier 的保证，不用普通进程故障测试冒充真实断电测试。

## 6. 已实现的物理 pre-commit 适配器

`StorageRelocationPhysicalStore` 只消费 repository 已恢复的 journal，提供 Stage 和 PublishTarget；上层必须先保存 staged proof，再以新的 journal 调用 PublishTarget。它不切换 metadata、不删除旧文件、不清理未知 temp，也没有默认接入 App/CLI。

Stage 使用 destination-local CreateNew、流式 SHA-256/length、WriteThrough/flush-to-disk、创建时的 native temp identity，以及源/目标根与祖先 no-follow 重验。PublishTarget 只接受已记录的 staged identity，no-overwrite rename 后重做目录 barrier 和最终 integrity/identity 验证；如果 rename 已发生而 journal 落后，只有 target 是同一对象且 temp 已不存在才可补发证明。不同对象的相同 bytes 不构成 adoption authority。

任何成功 proof 都要求 file data 与 namespace durability。平台 barrier 返回不可用时安全失败，尤其不能沿用旧 publisher 的 namespace-only 降级来声明 relocation durable。测试中的注入成功 barrier 仅验证控制流，不扩大平台能力；native barrier fixture 要么证明可用路径，要么验证拒绝。仍不宣称抵抗所有主动 hostile filesystem races，亦不将单机临时目录复制测试称为真实跨卷或突然断电验收。
