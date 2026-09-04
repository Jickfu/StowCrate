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

### 2.1 原始输入离线边界

维护者已确认：原始 Backup Source/External input 离线时允许迁移已有归档，不要求解密密钥。协议中的 copy source/旧源指旧 Current/History 归档，不是原始输入树；旧归档和目标根不可访问仍必须失败。迁移按 immutable ArchiveVersion 的 SHA-256/length 验证 opaque bytes，不调用扫描、FILE_MANAGED discovery、备份 Candidate/readiness、Archiver 或 Secret material 服务。

authoritative Plan 文档与 registration 必须有效；File-backed 文档即使恰好位于离线输入盘，也不得回退到缓存。仍检查已有 Source/External binding 与全设备 output/reservation 安全事实；不可证明路径安全时阻止，不因输入缺失而把其命名空间视为空闲。提交前的 configuration guard 必须独立于 unit backup ExecutionSemanticSnapshot；现有 manifest 的 `ExecutionSemanticDigest` 尚未接入该 guard，不能直接拿备份指纹或任意摘要充当迁移授权。后续接入时必须显式版本化，不能静默重解释旧 journal。

### 2.2 冻结集合

维护者已确认配置漂移策略：外部编辑 File-backed 文档仅改变名称/描述、定时任务、过滤规则或压缩级别时，不阻断现有归档搬迁；改变 Plan/Source/Archive Unit identity、unit 的来源/path/规则来源、SourceOutputPath、输出编码或有效归档格式时，属于 identity/layout drift，保留旧 authority/journal 并拒绝提交。格式决定输出扩展名，不能与压缩级别混为一谈。停用、authority/registration path 改变及配置无效也拒绝；root/binding drift 继续由事务内冻结 binding 与 reservation 校验负责。

`StorageRelocationConfigurationFingerprint` 使用独立 canonical domain `storage-relocation-configuration-v1`，固定编码版本 1；按 UUID 排序 Source 与 declared Unit，保存 identity/layout 投影，并保守纳入默认格式以覆盖离线时不可重新发现的未声明 FILE_MANAGED 单元。其他只影响未来归档内容或维护策略的配置不进入该指纹；此规则不放宽未完成迁移期间已有的数据库 mutation 互斥。`RevalidateAsync` 重读有效配置并比较该投影，不以完整 Plan fingerprint 或 Managed revision 的变化直接阻断。schema v5 已把新指纹写入独立 configuration checkpoint，不重新解释既有 `ExecutionSemanticDigest`。

`StorageRelocationConfigurationReader` 已提供独立的配置观察入口：复用 strict authoritative reader，每次重新取得 active Plan，并返回 authority/revision 与完整 `PlanSemanticFingerprint`。该指纹只用于发现配置变化，不是 `ExecutionSemanticDigest`，不裁定任意配置变化是否阻止迁移，也不单独构成 commit permission。入口不依赖 Source/External binding、FILE_MANAGED discovery 或 Secret material；root safety 与物理完整性仍由其他门槛负责。带 configuration observation 的 Begin 重验后冻结独立 checkpoint，供原子切换事务校验。

Begin transaction 必须重验 active registration、expected roots/placements/layout、迁移条目集合完整性及全设备 root safety。相同 Plan 不得有未完成 publish、PREPARED retention 或未完成 storage maintenance。COMPLETED retention 必须先完成旧根 absence reconciliation/compaction；旧路径 cleanup 必须先收敛。

从 Begin 到 cleanup 完成期间持久保留 old/new root reservation；其他 Plan 的激活、binding 保存与执行也必须检查 reservation。仅依赖进程锁不够。该 Plan 的 publish、retention、另一次 relocation/reorganization、影响迁移的 binding/configuration/identity mutation 均拒绝。File-backed 外部文件不能锁住，commit 前必须重读并验证冻结的 relevant semantics；变更只产生 out-of-sync，不重新解释旧 journal。

Journal 必须保存 transaction UUID、PlanId、DeviceId、kind、协议版本、old/new root canonical path/comparison key/native identity、expected source/layout facts、全部 artifact 的 ArchiveVersionId/UnitId/slot/old path/new path/temp path、SHA-256/length 和旧对象 identity。根与对象 identity 是带平台/编码版本的 opaque Infrastructure evidence，不进入 Core、portable document 或 baseline。

这些字段是不可变 transaction manifest，不是通用 JSON extension bag。config.db v4 已实现 root relocation 的 pre-commit journal/reservation；具体字段、FK、CHECK、canonical codec 与 CAS 约束见 `CONFIG-DB-v1-SCHEMA-DESIGN.md` v4 addendum。v5 已扩展 configuration checkpoint 与 metadata commit，v6 已实现 post-commit cleanup persistence；Output Reorganization 清单仍待实现，不能把当前实现当作完整迁移落地。

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

显式 pre-commit 单条目恢复通过 `ResumeRelocationEntryAsync(transaction, revision, version, physical)` 进入 repository 事务：从 durable journal 重读 Pending/Staged、configuration checkpoint、旧 binding、完整 placement/reservation 和 maintenance 互锁，再执行 Stage 或 PublishTarget 并保存 proof。事务持有数据库写锁覆盖物理操作，竞争调用不能同时复制/发布；不复用调用者提供的内存 journal。新入口拒绝缺少 checkpoint 的 legacy 日志，不事后补签。物理成功 proof 返回后忽略 caller cancellation 保存进度；失败不自动重试、不清除 unknown temp、不切换 root。复制后写库失败可能留下未获 ownership 的 temp，仍属 ambiguous；rename 后写库失败可按已持久化 staged identity 重新验证 target 并补记。配置漂移在操作前拒绝，物理成功后仍须保存 ownership proof，最终切换另行重读配置。该单条目入口不自动 seal、commit 或 cleanup，整条显式恢复用例后续接入。

独立 compaction 只接受 COMPLETED 日志与精确 revision。repository 在同一事务重验完整 journal/reservation、当前新 binding、全部 placement/ArchiveVersion、跨 Plan root safety 及 maintenance 互锁，再调用只读物理 completion probe：全部旧/新根 identity（含空根）、新归档 identity/SHA-256/length、exact old path 与 journal temp path absence、目录 barrier，返回前再次核验整个 namespace。旧路径或 temp 重现一律保留，不授予删除权限；未知的其他文件不属于清理集合，不采用、不删除。成功后原子删除本事务 reservation 与 journal；失败或提交前取消全部保留，新 binding/baseline/ArchiveVersion 不变。该操作不在启动时自动调用；缺失旧根或祖先不等于可信 absence，暂不释放。COMPLETED 本身不是 compaction 授权，物理核验也不是可缓存 proof。

启动恢复枚举全部 relocation journal（不按 active Plan 过滤），逐份验证 manifest/progress/reservation；损坏不解释成无待办。Prepared/TargetsDurable 仅报告 ResumeRequired，不在启动时自动复制或提交。MetadataCommitted 在注入物理清理适配器时逐项调用 repository 清理事务，最后完成 absence 重验；缺少适配器、物理失败、并发冲突或提交后取消由清理 workflow 返回 CleanupPending，保留新 authority 和 journal，不误报迁移失败。启动整体仍可响应取消，但不撤销已提交迁移。错误详情不直接转储带物理路径的异常。Completed 仅报告完成但 reservation 仍保留，不把历史完成记录当作当前磁盘健康证明，也不再次删除文件。repository 每次从当前数据库重读，启动枚举快照不独立授权删除。有 relocation reservation 的 Plan 跳过旧 publish/retention recovery 并报告待处理，同时跳过 History inventory。该入口是 Application startup coordinator 的显式可选能力，尚未接入 App/CLI 组合根；pre-commit 显式恢复后续实现，独立 compaction 已提供事务接口。

Application 负责不可变 progress kernel、事务编排、recovery classification 与端口；Infrastructure 负责 SQLite manifest/CAS、reservation queries、native filesystem proof、copy/publish/cleanup；Core 不依赖 storage roots 或 OS。App/CLI 后续只调用用例。

验收必须覆盖多 artifact 部分成功不能 commit、重复/串 transaction proof、非法 restore 状态、取消稳定边界、每个 filesystem/SQLite crash window、同/跨 filesystem、source/target/root replacement、目标冲突、case/file-directory collision、retention/publish 互斥、inactive retained placements、cross-Plan reservation、File-backed drift、post-commit cleanup failure，以及三平台真实 fixture。断电承诺限于 OS/filesystem 成功 durability barrier 的保证，不用普通进程故障测试冒充真实断电测试。

## 6. 已实现的物理 pre-commit 适配器

`StorageRelocationPhysicalStore` 只消费 repository 已恢复的 journal，提供 Stage 和 PublishTarget；上层必须先保存 staged proof，再以新的 journal 调用 PublishTarget。这两个 pre-commit 方法不切换 metadata、不删除旧文件、不清理未知 temp，也没有默认接入 App/CLI。提交后清理使用下面独立的接口。

### 6.1 已提交旧副本物理清理

`IStorageRelocationOldCopyStore.RemoveOldCopyAsync` 只接受 MetadataCommitted journal 与 manifest 内 VersionId，重验该条目的 old/new roots、no-follow ancestors、新副本 identity/SHA-256/length，再验证旧副本的原始 identity 与 bytes。先探测旧父目录 barrier，再重验目标/旧副本，最后只删除 exact old file；不删除目录、unknown temp 或未在 manifest 中列出的内容。已有 OldCopyAbsent 记录后旧路径重新出现，一律不再授权删除。

删除后忽略 caller cancellation，完成旧父目录 barrier 与 absence re-proof 后返回关联 transaction/Plan/revision/artifact/old-root/old-object/target identity 的强类型 proof。删除前 barrier 不可用时保留旧文件；删除后 barrier 失败时不返回 proof，保留已提交日志，下一次按 absent 重新执行 barrier。missing ancestor/root drift 不当作可信 absence。新根不回滚。

该物理接口不写 SQLite。repository 清理事务按 revision CAS 加载 durable journal，重验新 binding、完整 placement 集合与 reservation 后调用物理接口，严格匹配返回 proof 的全部字段，再持久化 OldCopyAbsent。物理成功后日志保存忽略 caller cancellation；数据库故障仍可能留下 absent 但日志落后的状态，下次重新证明 absence，不回滚新根。全部条目已有 OldCopyAbsent 后，再逐项重新证明 absence 才写 COMPLETED；重新出现的旧文件不得再次删除。COMPLETED 仍保留 journal 和所有 reservation，直到独立 reconciliation/compaction。已提交清理启动恢复见第 5 节；pre-commit 恢复仍待实现，独立 compaction 见第 5 节。基于 path 的最后 identity 重验检测正常替换，不宣称防御所有主动 hostile race。

### 6.2 pre-commit 验证细节

Stage 使用 destination-local CreateNew、流式 SHA-256/length、WriteThrough/flush-to-disk、创建时的 native temp identity，以及源/目标根与祖先 no-follow 重验。PublishTarget 只接受已记录的 staged identity，no-overwrite rename 后重做目录 barrier 和最终 integrity/identity 验证；如果 rename 已发生而 journal 落后，只有 target 是同一对象且 temp 已不存在才可补发证明。不同对象的相同 bytes 不构成 adoption authority。

任何成功 proof 都要求 file data 与 namespace durability。平台 barrier 返回不可用时安全失败，尤其不能沿用旧 publisher 的 namespace-only 降级来声明 relocation durable。测试中的注入成功 barrier 仅验证控制流，不扩大平台能力；native barrier fixture 要么证明可用路径，要么验证拒绝。仍不宣称抵抗所有主动 hostile filesystem races，亦不将单机临时目录复制测试称为真实跨卷或突然断电验收。

`VerifyForCommitAsync` 已提供 `TARGETS_DURABLE` 后的全量物理重验：严格匹配 manifest/progress artifact set，检查所有旧/新根（含空集合）、源和目标 identity/SHA-256/length、temp absence，并重新执行目标目录及祖先 barrier。缺失目录不会重建；失败不修改 journal 或文件。返回前再次检查整个集合的 namespace/identity，防止后续条目 I/O 期间较早条目被正常替换。它不生成可缓存或持久复用的 commit authority，也不宣称跨文件瞬时快照；上层仍须紧接着重验 authoritative semantics、reservation、expected metadata 与 revision CAS 后才能原子切换。schema v5 的 CommitRelocationAsync 已在切换事务内调用该门槛。
