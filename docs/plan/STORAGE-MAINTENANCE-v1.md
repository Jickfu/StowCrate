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

目标根创建规则已由维护者确认：选定的新 CurrentRoot/HistoryRoot 必须预先存在，程序不自动创建。初次物理检查明确发现目标根不存在时返回 `RELOCATION_TARGET_ROOT_MISSING`，提示用户先创建目录；访问拒绝、链接或文件占位不映射为缺失。根内归档父目录仍可在已启动迁移的 Stage 中按原协议创建。durable journal 已捕获 identity 后根丢失/替换仍拒绝恢复，不通过重建根重新授权。

### 2.1 原始输入离线边界

维护者已确认：原始 Backup Source/External input 离线时允许迁移已有归档，不要求解密密钥。协议中的 copy source/旧源指旧 Current/History 归档，不是原始输入树；旧归档和目标根不可访问仍必须失败。迁移按 immutable ArchiveVersion 的 SHA-256/length 验证 opaque bytes，不调用扫描、FILE_MANAGED discovery、备份 Candidate/readiness、Archiver 或 Secret material 服务。

authoritative Plan 文档与 registration 必须有效；File-backed 文档即使恰好位于离线输入盘，也不得回退到缓存。仍检查已有 Source/External binding 与全设备 output/reservation 安全事实；不可证明路径安全时阻止，不因输入缺失而把其命名空间视为空闲。提交前的 configuration guard 必须独立于 unit backup ExecutionSemanticSnapshot；v1 manifest 的 `ExecutionDigest`（代码中的 `LegacyExecutionSemanticDigest`）不属于该 guard，不能直接拿备份指纹或任意摘要充当迁移授权。新建 manifest v2 使用本文规定的独立编码，不携带该历史字段，不能静默重解释旧 journal。

### 2.2 冻结集合

维护者已确认配置漂移策略：外部编辑 File-backed 文档仅改变名称/描述、定时任务、过滤规则或压缩级别时，不阻断现有归档搬迁；改变 Plan/Source/Archive Unit identity、unit 的来源/path/规则来源、SourceOutputPath、输出编码或有效归档格式时，属于 identity/layout drift，保留旧 authority/journal 并拒绝提交。格式决定输出扩展名，不能与压缩级别混为一谈。停用、authority/registration path 改变及配置无效也拒绝；root/binding drift 继续由事务内冻结 binding 与 reservation 校验负责。

`StorageRelocationConfigurationFingerprint` 使用独立 canonical domain `storage-relocation-configuration-v1`，固定编码版本 1；按 UUID 排序 Source 与 declared Unit，保存 identity/layout 投影，并保守纳入默认格式以覆盖离线时不可重新发现的未声明 FILE_MANAGED 单元。其他只影响未来归档内容或维护策略的配置不进入该指纹；此规则不放宽未完成迁移期间已有的数据库 mutation 互斥。`RevalidateAsync` 重读有效配置并比较该投影，不以完整 Plan fingerprint 或 Managed revision 的变化直接阻断。schema v5 已把新指纹写入独立 configuration checkpoint，不重新解释既有 `ExecutionSemanticDigest`。

`StorageRelocationConfigurationReader` 已提供独立的配置观察入口：复用 strict authoritative reader，每次重新取得 active Plan，并返回 authority/revision 与完整 `PlanSemanticFingerprint`。该指纹只用于发现配置变化，不是 `ExecutionSemanticDigest`，不裁定任意配置变化是否阻止迁移，也不单独构成 commit permission。入口不依赖 Source/External binding、FILE_MANAGED discovery 或 Secret material；root safety 与物理完整性仍由其他门槛负责。带 configuration observation 的 Begin 重验后冻结独立 checkpoint，供原子切换事务校验。

Begin transaction 必须重验 active registration、expected roots/placements/layout、迁移条目集合完整性及全设备 root safety。相同 Plan 不得有未完成 publish、PREPARED retention 或未完成 storage maintenance。COMPLETED retention 必须先完成旧根 absence reconciliation/compaction；旧路径 cleanup 必须先收敛。

从 Begin 到 cleanup 完成期间持久保留 old/new root reservation；其他 Plan 的激活、binding 保存与执行也必须检查 reservation。仅依赖进程锁不够。该 Plan 的 publish、retention、另一次 relocation/reorganization、影响迁移的 binding/configuration/identity mutation 均拒绝。File-backed 外部文件不能锁住，commit 前必须重读并验证冻结的 relevant semantics；变更只产生 out-of-sync，不重新解释旧 journal。

Journal 必须保存 transaction UUID、PlanId、DeviceId、kind、协议版本、old/new root canonical path/comparison key/native identity、expected source/layout facts、全部 artifact 的 ArchiveVersionId/UnitId/slot/old path/new path/temp path、SHA-256/length 和旧对象 identity。根与对象 identity 是带平台/编码版本的 opaque Infrastructure evidence，不进入 Core、portable document 或 baseline。

这些字段是不可变 transaction manifest，不是通用 JSON extension bag。config.db v4 已实现 root relocation 的 pre-commit journal/reservation；具体字段、FK、CHECK、canonical codec 与 CAS 约束见 `CONFIG-DB-v1-SCHEMA-DESIGN.md` v4 addendum。v5 已扩展 configuration checkpoint 与 metadata commit，v6 已实现 post-commit cleanup persistence；Output Reorganization 清单仍待实现，不能把当前实现当作完整迁移落地。

## 3. 顺序与持久点

`InspectTargetsAsync` 接受拟用的非空 transaction ID，并在物理 inventory 后检查全部 final 与该事务的 temp 路径；之后仍重读配置和 metadata。目标检查独立返回 transaction-bound observation，不构造含占位摘要的 manifest。检查包括 final/temp 字面重名或父子冲突、现存文件/目录占位、no-follow 父目录与冻结新根 identity；缺失父目录不创建。同内容文件也不能采用或删除。真实目标文件系统的 case/encoding 等价性仍是独立门槛，不能用该字面检查替代。实际 Stage 在容量检查后、创建任何目录/temp 前，重查全部 Pending 条目的 final/temp；Staged/TargetDurable 条目不按空路径处理，其 ownership 仍由既有恢复协议独立验证。

只读检查增量：`StorageRelocationInspectionWorkflow.InspectAsync` 先读取 authoritative configuration 与一致 metadata inventory，调用 `IStorageRelocationInventoryProbe` 验证旧归档 opaque bytes、旧/新根 identity、根内 ancestors、目标占用与容量，最后重新读取配置和 metadata 集合。配置相关漂移、placement/root 集合变化或新增维护互锁均拒绝返回成功；无关名称变化仍允许。物理检查末尾重验整个集合的 namespace/identity，包含零条目的根。预检不创建目录、不签发 durable proof、不构造 journal；结果不代表完整 Preview 可执行。仍需补齐目标真实 case/encoding collision 和写入/barrier capability 门槛后才可接入 Begin。容量为最近现存目标父目录所在卷的瞬时下界观察，不能替代完整目标布局的 filesystem 检查；也不承诺跨文件原子快照或防御主动 hostile race。

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

物理 inventory 与 Stage 逐条定位 final/temp 最近的现存父目录，逐级 no-follow 并捕获其 native identity；容量查询使用该目录所在卷，不用选定输出根所在卷替代。新建的根内父目录继承最近现存父目录的位置；查询前后完整重做目录定位，最近父目录路径或 identity 改变即拒绝旧容量结果。按 probe 返回的 volume identity 合并各目录需求，根内多个卷分别计费，同卷仍合并；零条目的 inventory 根保留零需求查询，Stage 只处理 Pending。此观察仍不是预留空间或抵抗所有并发挂载替换的瞬时快照，Windows reparse/mount alias 继续按现有 no-follow 规则拒绝。Application 按根分组的 convenience 方法仅为 metadata 估算，不进入实际复制许可链。

容量策略已由维护者确认：查询失败、未知或不可信即阻止，不提供 override。容量 guard 以当前调用用户可用 bytes 为准，按 native volume/device identity 合并同卷目标需求，并取同卷各观察值的最小值；不把旧副本空间提前抵扣。需求是待复制归档 Length 之和的下界，checked 溢出或非法事实不放行；不宣称预留空间或覆盖 filesystem allocation/metadata 开销。inventory 容量检查为只读；实际 Stage 在任何目标目录/temp 创建之前重新检查全部 Pending 条目的剩余需求，已 staged/target durable 不重复计费。容量不足/无法查询不会创建本次条目的输出；已提交阶段的 rename/cleanup 不以新一次容量查询为前提。平台 probe 重验目标目录 no-follow identity，路径替换不沿用旧查询结果。

只读 inventory 入口从单个数据库事务读取选定 Current/History 根下的完整 retained placements（不按当前 declared/active unit 过滤），附带 immutable ArchiveVersion integrity/length 与旧/新根路径。入口重读 authoritative configuration checkpoint、验证 maintenance/reservation/全设备 Source/External/output 冲突，不触碰原始输入，不创建 journal，不产生 native identity 或可执行 manifest。它是物理 preview 的输入，不是“迁移就绪”声明；目标文件系统、容量与逐文件物理检查仍须单独完成。

External local binding 与 Source 一样进入迁移 namespace 安全检查，即使原始输入离线也不得释放该占用。Begin、pre-commit/commit/compaction 重验须覆盖 active 或仍有恢复工作的 Plan 的 active External binding；其他 Plan 保存 External binding、激活或进入发布时也不得与迁移 reservation 重叠。这里只比较已持久化 canonical path/key，不扫描或打开 External 输入。

完整显式恢复入口 `ResumeAsync(PlanId, TransactionId, transfer)` 必须指定既有 transaction，不创建新迁移，也不替用户选择日志。按 durable artifact stage 逐项调用事务恢复入口，全部 target durable 后 seal，再调用独立 commit 门槛，最后衔接已提交清理；不自动 compaction。失败不在同一调用内重试；异常后仅重读日志以判定永久成功点是否已经到达，已提交返回 CleanupPending、未提交返回 ResumeRequired；无法重读或 transaction 已改变则返回 OutcomeUnknown，不能猜测失败或成功。取消后的证明落盘由 repository 保证，提交后取消不能反转迁移成功。启动仍不调用此显式 pre-commit 入口。

显式 pre-commit 单条目恢复通过 `ResumeRelocationEntryAsync(transaction, revision, version, physical)` 进入 repository 事务：从 durable journal 重读 Pending/Staged、configuration checkpoint、旧 binding、完整 placement/reservation 和 maintenance 互锁，再执行 Stage 或 PublishTarget 并保存 proof。事务持有数据库写锁覆盖物理操作，竞争调用不能同时复制/发布；不复用调用者提供的内存 journal。新入口拒绝缺少 checkpoint 的 legacy 日志，不事后补签。物理成功 proof 返回后忽略 caller cancellation 保存进度；失败不自动重试、不清除 unknown temp、不切换 root。复制后写库失败可能留下未获 ownership 的 temp，仍属 ambiguous；rename 后写库失败可按已持久化 staged identity 重新验证 target 并补记。配置漂移在操作前拒绝，物理成功后仍须保存 ownership proof，最终切换另行重读配置。该单条目入口不自动 seal、commit 或 cleanup，整条显式恢复由 ResumeAsync 编排。

独立 compaction 只接受 COMPLETED 日志与精确 revision。repository 在同一事务重验完整 journal/reservation、当前新 binding、全部 placement/ArchiveVersion、跨 Plan root safety 及 maintenance 互锁，再调用只读物理 completion probe：全部旧/新根 identity（含空根）、新归档 identity/SHA-256/length、exact old path 与 journal temp path absence、目录 barrier，返回前再次核验整个 namespace。旧路径或 temp 重现一律保留，不授予删除权限；未知的其他文件不属于清理集合，不采用、不删除。成功后原子删除本事务 reservation 与 journal；失败或提交前取消全部保留，新 binding/baseline/ArchiveVersion 不变。该操作不在启动时自动调用；缺失旧根或祖先不等于可信 absence，暂不释放。COMPLETED 本身不是 compaction 授权，物理核验也不是可缓存 proof。

启动恢复枚举全部 relocation journal（不按 active Plan 过滤），逐份验证 manifest/progress/reservation；损坏不解释成无待办。Prepared/TargetsDurable 仅报告 ResumeRequired，不在启动时自动复制或提交。MetadataCommitted 在注入物理清理适配器时逐项调用 repository 清理事务，最后完成 absence 重验；缺少适配器、物理失败、并发冲突或提交后取消由清理 workflow 返回 CleanupPending，保留新 authority 和 journal，不误报迁移失败。启动整体仍可响应取消，但不撤销已提交迁移。错误详情不直接转储带物理路径的异常。Completed 仅报告完成但 reservation 仍保留，不把历史完成记录当作当前磁盘健康证明，也不再次删除文件。repository 每次从当前数据库重读，启动枚举快照不独立授权删除。有 relocation reservation 的 Plan 跳过旧 publish/retention recovery 并报告待处理，同时跳过 History inventory。该入口是 Application startup coordinator 的显式可选能力，尚未接入 App/CLI 组合根；显式 pre-commit 恢复和独立 compaction 已提供用例/事务接口，用户入口待装配。

Application 负责不可变 progress kernel、事务编排、recovery classification 与端口；Infrastructure 负责 SQLite manifest/CAS、reservation queries、native filesystem proof、copy/publish/cleanup；Core 不依赖 storage roots 或 OS。App/CLI 后续只调用用例。

验收必须覆盖多 artifact 部分成功不能 commit、重复/串 transaction proof、非法 restore 状态、取消稳定边界、每个 filesystem/SQLite crash window、同/跨 filesystem、source/target/root replacement、目标冲突、case/file-directory collision、retention/publish 互斥、inactive retained placements、cross-Plan reservation、File-backed drift、post-commit cleanup failure，以及三平台真实 fixture。断电承诺限于 OS/filesystem 成功 durability barrier 的保证，不用普通进程故障测试冒充真实断电测试。

## 6. 已实现的物理 pre-commit 适配器

`StorageRelocationPhysicalStore` 只消费 repository 已恢复的 journal，提供 Stage 和 PublishTarget；上层必须先保存 staged proof，再以新的 journal 调用 PublishTarget。这两个 pre-commit 方法不切换 metadata、不删除旧文件、不清理未知 temp，也没有默认接入 App/CLI。提交后清理使用下面独立的接口。

### 6.1 已提交旧副本物理清理

`IStorageRelocationOldCopyStore.RemoveOldCopyAsync` 只接受 MetadataCommitted journal 与 manifest 内 VersionId，重验该条目的 old/new roots、no-follow ancestors、新副本 identity/SHA-256/length，再验证旧副本的原始 identity 与 bytes。先探测旧父目录 barrier，再重验目标/旧副本，最后只删除 exact old file；不删除目录、unknown temp 或未在 manifest 中列出的内容。已有 OldCopyAbsent 记录后旧路径重新出现，一律不再授权删除。

删除后忽略 caller cancellation，完成旧父目录 barrier 与 absence re-proof 后返回关联 transaction/Plan/revision/artifact/old-root/old-object/target identity 的强类型 proof。删除前 barrier 不可用时保留旧文件；删除后 barrier 失败时不返回 proof，保留已提交日志，下一次按 absent 重新执行 barrier。missing ancestor/root drift 不当作可信 absence。新根不回滚。

该物理接口不写 SQLite。repository 清理事务按 revision CAS 加载 durable journal，重验新 binding、完整 placement 集合与 reservation 后调用物理接口，严格匹配返回 proof 的全部字段，再持久化 OldCopyAbsent。物理成功后日志保存忽略 caller cancellation；数据库故障仍可能留下 absent 但日志落后的状态，下次重新证明 absence，不回滚新根。全部条目已有 OldCopyAbsent 后，再逐项重新证明 absence 才写 COMPLETED；重新出现的旧文件不得再次删除。COMPLETED 仍保留 journal 和所有 reservation，直到独立 reconciliation/compaction。已提交清理启动恢复见第 5 节；显式 pre-commit 恢复与独立 compaction 见第 5 节。基于 path 的最后 identity 重验检测正常替换，不宣称防御所有主动 hostile race。

### 6.2 pre-commit 验证细节

Stage 在创建 archive temp 前先探测其实际父目录 barrier，包含归档直接位于 root 的情况；不可用时不复制、不创建 archive temp。探测返回后重验旧归档 identity、新根/祖先、final/temp absence 并响应取消，再打开文件。该探测不证明后续写入必定成功，不替代复制/rename 后的真实 barrier，也不自动清除创建中途留下的目录；post-copy barrier 失败仍保留无 ownership temp 并按 ambiguous 处理。

Stage 使用 destination-local CreateNew、流式 SHA-256/length、WriteThrough/flush-to-disk、创建时的 native temp identity，以及源/目标根与祖先 no-follow 重验。PublishTarget 只接受已记录的 staged identity，no-overwrite rename 后重做目录 barrier 和最终 integrity/identity 验证；如果 rename 已发生而 journal 落后，只有 target 是同一对象且 temp 已不存在才可补发证明。不同对象的相同 bytes 不构成 adoption authority。

任何成功 proof 都要求 file data 与 namespace durability。平台 barrier 返回不可用时安全失败，尤其不能沿用旧 publisher 的 namespace-only 降级来声明 relocation durable。测试中的注入成功 barrier 仅验证控制流，不扩大平台能力；native barrier fixture 要么证明可用路径，要么验证拒绝。仍不宣称抵抗所有主动 hostile filesystem races，亦不将单机临时目录复制测试称为真实跨卷或突然断电验收。

`VerifyForCommitAsync` 已提供 `TARGETS_DURABLE` 后的全量物理重验：严格匹配 manifest/progress artifact set，检查所有旧/新根（含空集合）、源和目标 identity/SHA-256/length、temp absence，并重新执行目标目录及祖先 barrier。缺失目录不会重建；失败不修改 journal 或文件。返回前再次检查整个集合的 namespace/identity，防止后续条目 I/O 期间较早条目被正常替换。它不生成可缓存或持久复用的 commit authority，也不宣称跨文件瞬时快照；上层仍须紧接着重验 authoritative semantics、reservation、expected metadata 与 revision CAS 后才能原子切换。schema v5 的 CommitRelocationAsync 已在切换事务内调用该门槛。

迁移目标比较能力：无法可靠识别目标文件系统的大小写或 Unicode 比较规则时，必须阻止迁移并返回 RELOCATION_TARGET_COMPARISON_UNAVAILABLE；不提供强制继续，不创建探测文件。检查必须覆盖全部 final/temp 及父目录的实际规则和待创建目录的继承语义，不以操作系统默认值或规范化 comparison key 代替。Preview 保持只读。

目标比较适配器增量：StorageRelocationTargetComparisonProbe 在 Linux x64/arm64 上，仅接受目录句柄 fstatfs 返回 ext2/3/4 magic 且 FS_IOC_GETFLAGS 未启用 casefold/fscrypt 的目录。通过同句柄 statx identity 与路径 no-follow identity 交叉验证；按真实目录逐级查询，含空根和嵌套挂载，不以根卷规则代替子目录。缺失子目录只按已验证的 ext 继承语义推导，不创建；两次完整观察发现目录新增/替换或能力变化即拒绝。全部 final/temp 使用严格 UTF-8 和 255-byte component 限制，复用完整 namespace 冲突校验，跨路径目录 identity alias 保守拒绝。其他平台、文件系统、casefold/fscrypt 或原生接口不可用仍返回 RELOCATION_TARGET_COMPARISON_UNAVAILABLE。此结果仅是本次只读比较检查，不是 durable proof；完整 Begin 接入仍须在启动前重验；Stage 的接入与重验时点见下文，不承诺跨文件瞬时快照。

比较能力已接入物理执行默认路径：Stage 在任何父目录/temp 创建前验证完整 manifest 布局，并在父目录创建及 pre-copy barrier 后再次验证；PublishTarget 在恢复入口、rename 前及 rename/barrier 后重验；VerifyForCommit 在全量 I/O 前后重验。VerifyLayoutAsync 只检查冻结布局及实际目录比较规则，不要求 final/temp 全部为空；ownership、no-overwrite 和 unknown-file 拒绝仍由原物理协议独立负责。rename 后的重验忽略 caller cancellation，失败保留原 staged ownership/journal，允许后续按同对象恢复。显式 Resume 对能力不可用返回 RELOCATION_TARGET_COMPARISON_UNAVAILABLE，并保留旧 binding/baseline/reservation；已提交 cleanup/compaction 不因新一次比较能力查询而获得或丧失删除授权，继续按既有 exact-object 协议执行。构造物理 store 未注入比较端口时默认使用原生适配器，因此不支持的平台不能绕过 Preview 直接复制。

显式 compaction 用例 StorageRelocationCompactionWorkflow.CompactAsync 要求 PlanId、transaction UUID 和调用方选定的正 revision；选择已变化时拒绝，不自动改用新日志/revision。仅 COMPLETED 可调用仓储清理事务，未完成或缺少 completion adapter 仅报告状态，不推进恢复。成功响应后直接返回 Compacted，不因后续 caller cancellation 改报失败；失败只用不可取消读取重新观察，精确原 COMPLETED 日志仍在则返回 Retained，缺失/替换/revision 变化/读取不可用则 OutcomeUnknown，不自动重试，也不从 absence 推断本次提交成功。初次读取无日志返回 NotFound，不等于 Compacted。损坏日志继续显式抛出，不降级为普通待处理状态。该用例不在 startup 或 Resume 后自动调用，不删除归档、不清理未知文件，App/CLI 用户入口仍待装配。

Manifest 编码 v2 契约：StorageRelocationIntent.ProtocolVersion 仍为 transfer 状态协议 1，manifest payload 的 Version 独立取 1 或 2（与已独立版本化的 progress/configuration 编码一致），不改变 config.db schema v6。v1 保留原始 ExecutionDigest 字段和 canonical bytes；只能作为历史事实保存，不能重新解释为配置指纹。v2 使用独立 closed-world DTO，完全不包含 ExecutionDigest，禁止 null/占位字段；新建 v2 必须在同一 Begin 事务冻结 configuration checkpoint，缺少 checkpoint 的 v2 即为损坏状态，不能补签。reader 先验证 payload hash 和版本，再分派严格 reader 并检查 canonical 重编码；拒绝未知/重复/错版字段和未来版本，不自动升级或改写 v1。旧程序不能识别 v2 时须保持其既有 fail-closed 行为；不得向旧程序承诺 v2 日志可恢复。

目标目录持久化预检增量：`IStorageRelocationTargetDurabilityProbe.VerifyTargetDurabilityAsync` 接受冻结 manifest，只对目标根及全部现存父目录执行 metadata barrier（包括空迁移根），不创建目录、探测文件、归档临时文件或 journal。每次屏障前重验完整目录集合及 no-follow identity，屏障后重验当前对象，结束时再次检查完整集合和 final/temp absence；新增、替换、占位或取消均拒绝观察结果。barrier 返回不支持时报告 `RELOCATION_TARGET_DURABILITY_UNAVAILABLE`。该检查不证明写权限、未来目录或复制/rename 的持久性，不替代 Stage/Publish 的实际屏障，也不授予 Begin/复制权限；完整启动编排仍待接入。
