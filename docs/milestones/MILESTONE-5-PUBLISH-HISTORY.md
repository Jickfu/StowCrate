# Milestone 5 — Physical Current / History Publish & Durable Execution

实际复制加固：容量已按每条归档最近现存目标父目录所在卷计算，并在查询后重验目录布局；Stage 在创建 archive temp 前探测实际父目录 barrier，再重验 namespace/旧归档并响应取消。两项都不替代完整 Begin preflight，也不放宽复制后的持久化与 ownership 协议。

M5.3 最新增量：只读检查已串联 authoritative configuration、一致 metadata inventory、旧归档完整性/identity、目标占用/目标目录容量与末尾配置/metadata 重验。容量未知或不足按已确认规则阻止；不创建目录或 journal。目标真实 case/encoding collision、写入/barrier capability 和 Begin 用户入口仍待接入，检查结果不声明可执行。

M5.3 pre-commit 增量：单条目显式恢复事务已接入 Stage/PublishTarget，使用数据库写锁和 revision CAS 防止并发物理动作。真实文件/SQLite 验证 copy 后写库失败不接纳残留 temp，rename 后写库失败按已记录 identity 恢复；该接口不自动 seal/commit/cleanup，整条恢复用例已由 ResumeAsync 接通。

M5.3 compaction 增量：独立 completion probe 与原子 compaction 已实现并测试，完成日志不再是无法释放的永久互锁。只有现场重验和 repository CAS 同时成功才移除迁移日志/reservation，不删除文件；仍未接入用户入口或启动自动释放。全量 preflight、Output Reorganization 及完整用户流程继续待办，M5.3 不标记完成。

## M5.1 — Physical Publish Contract + PublishIntent Execution（COMPLETE）

本阶段消费 M4 `VerifiedArchiveArtifact`，在不重新扫描、生成 Candidate 或运行 Archiver 的前提下完成 destination-local staging、History capture、atomic Current publish、PublishIntent journal 与 M3 单事务 metadata commit。

M4 private runtime `.partial` 与 M5 Current-filesystem publish temp 是两个边界。M5 先复制到最终 Current 文件的 sibling：

`.<finalFileName>.stowcrate-publish-<ArchiveVersionId:N>.partial`

复制必须 `CreateNew`、流式计算 SHA-256/length、durable flush，并与 Verified ArchiveVersion 一致。开始 journal 前不得改变 Current 或 History。

History layout v1 为：

`history-v1/<ArchiveUnitId:D>/<PublishedAtUtc:yyyyMMddTHHmmss.fffZ>--<ArchiveVersionId:D><extension>`

History v1 只 copy，不使用 hardlink/reflink。copy 到 History destination-local temp、验证并 flush 后 atomic move；Current publish 前重新取得 authoritative `ExecutionSemanticSnapshot`。语义、binding、raw rule source 或 SecretRevision drift 阻止发布；retention-only drift允许发布，但跳过 retention cleanup并标记 maintenance out-of-sync。

同路径 Current 使用原子 overwrite/replace；新路径使用原子 move，新 target 不得覆盖。旧路径仅在 metadata durable commit 后、重新验证旧 integrity 时删除。filesystem 已发布但 journal 落后的 recovery 必须以 expected-new/old integrity 和唯一 History v1 proof 推进或安全 abort；无法证明时保留 journal并返回 `AmbiguousPublishRecovery`。

本阶段不实现 retention physical deletion、Output Reorganization-only、CurrentRoot/HistoryRoot relocation、scheduler/CLI/UI 或新 Archiver capability。

### Completion Hardening

- config.db schema v2 为每个 intent durable冻结 `HistoryCaptureRequirement = Required | NotRequired | UnknownLegacy`；recovery不再读取当前Plan解释旧事务，v1 old-Current incomplete intent迁移为UnknownLegacy并fail closed。
- `CompleteMetadataCommitAsync` 成功是永久success point。其后忽略caller cancellation，retention marker、old-path cleanup和runtime cleanup失败只产生warning、durable maintenance OutOfSync或强类型pending maintenance requirement，不得把已提交backup重新报告为failed/cancelled。
- Application状态机测试覆盖first/replacement、History Enabled/Disabled、same/new path、stale、retention drift、journal落后、proof重建、UnknownLegacy、safe abort、metadata fault和post-commit failure。
- 真实filesystem fixture直接执行Current sibling staging、same-path replace、new-path move、unexpected target、corrupt temp、temp cleanup及跨runtime root copy，并由现有Windows/Linux/macOS CI matrix运行。
- file data在rename前使用WriteThrough + flush-to-disk；rename/replace后执行platform metadata durability barrier。自动测试证明API结果和proof，不模拟真实突然断电；M5 Completion Review必须把实际power-loss保证限制在操作系统/文件系统对成功barrier的承诺内。

下一项：**M5.2 — Retention Maintenance + Publish Recovery/Orphan Reconciliation**。

## M5.2 — Retention Maintenance + Publish Recovery/Orphan Reconciliation（COMPLETE）

Retention v1 只自动执行 `KeepLastVersions(N)`，`Disabled` 与 `KeepAll` 均不产生新删除授权；Purge 不在本阶段。候选只来自 active `HistoryVersionPlacement` 与同 identity 的 `SUPERSEDED ArchiveVersion`，Current 在结构上不可能成为 victim。时间顺序固定为 `(PublishedAtUtcMs ASC, ArchiveVersionId RFC-4122/network bytes ASC)`，保留末尾 N 个。

每个 victim 必须先进入 config.db v3 `RetentionDeletionIntent(PREPARED)`。一次选择的全部 intent 在同一 SQLite 事务中重验并全有或全无地写入；intent 冻结 path、SHA-256、length、selection、语义版本及当时的 KeepLastVersions count，后续 Plan 漂移不得撤销或重新解释该授权。

物理删除只接受 no-follow ordinary file。实现必须在删除前验证 identity/integrity 并检测正常替换竞态；删除后完成 parent-directory metadata durability barrier，才允许在单一 SQLite 事务中删除 `HistoryVersionPlacement` 并把 intent 标为 `COMPLETED`。`ArchiveVersion` 作为不可变历史事实永久保留。单个 destructive work 开始后忽略 caller cancellation，直到形成可恢复稳定状态；不同 victim 之间可以取消。

Recovery 只依赖 intent 与物理事实。`PREPARED + matching/absent` 可重试删除或完成 metadata；mismatch、link、special 或 placement conflict 均保留 artifact、placement 与 intent并标记 OutOfSync。`COMPLETED` intent 暂留作 reconciliation authority；仅在后续再次证明 placement 与 artifact 均不存在且 barrier 成功后才可 compact。

Orphan reconciliation 不凭文件名、deterministic path 或裸 `ArchiveVersion` 猜测 ownership。只有 live PublishIntent 的 HistoryCaptureProof 可以交回 publish recovery，只有 retention intent 可以授权删除。tracked missing/corrupt、unplaced known version、unknown history-v1 file 与任意 HistoryRoot 内容都只报告诊断，不自动修复或删除，也不递归删除未知目录。

只读 inventory 仅遍历 StowCrate 管理的 `history-v1` namespace，逐级 no-follow；普通目录只用于遍历，link/reparse/special/unreadable component 作为诊断停止下钻。物理删除逐级拒绝异常 ancestor，并在 hash 前与 namespace 删除前捕获、比较 Windows volume/file ID 或 POSIX device/inode。该检查防止正常同步/替换竞态静默删掉另一对象，不宣称能在所有文件系统上抵御主动 hostile race，也不以此扩大删除授权。

### Completion Review

结论：**PASS**。最终评审确认 retention selection、durable deletion authorization、可信 History namespace 与 artifact identity 验证、目录 durability barrier、placement removal + intent completion 原子事务、后续 absence re-proof 与 placement-absence compaction revalidation 形成闭合链路；`ArchiveVersion` 始终保留为 immutable historical fact，orphan inventory 始终只有诊断权限。

最终实现提交为 `e872ed74c6f171d430d710ce660ff601a250bded`。本地 build 0 warning/error，334 项测试通过，EF model 无 pending change；GitHub Actions run `33628437400` 在 Windows、Ubuntu、macOS 全部通过。

下一项：**M5.3 — Output Reorganization + Storage Relocation**。编码前必须先冻结跨 filesystem/SQLite relocation journal 与 crash recovery；同一 Archive Unit 存在 `PREPARED RetentionDeletionIntent` 时不得开始 History/Storage relocation。

## M5.3 — Output Reorganization + Storage Relocation（IMPLEMENTING）

按维护者最新授权，本阶段由 Codex 自行设计、实现和审查，不再等待外部 ChatGPT 评审。完成仍要求实现自审、build/tests 与跨平台 CI 证据。

入口加固：普通 Local Binding 保存必须在事务内拒绝改变已有 placement 或恢复日志依赖的输出根，包含停用/省略；拒绝时不得部分更新 Source/External binding。无 placement/journal 的初始配置仍可编辑；保留原输出根时不阻止独立 Source/External 修改。该加固不表示物理 relocation 已实现。

后续仍需实现 compaction 用户入口、全量 preflight 与 Output Reorganization；不得把 M3 的 metadata-only reorganization port 当作物理迁移用例，也不得标记本阶段 COMPLETE。容量规则已确认并实现独立只读 guard 与 Stage 前重验：当前用户可用空间未知/失败/不足一律阻止，没有强制继续；同卷合并 Pending 需求，不提前抵扣旧副本。这不等于完整物理 Preview 或用户入口完成。

已冻结 Plan-scoped transfer protocol，见 [`STORAGE-MAINTENANCE-v1.md`](../plan/STORAGE-MAINTENANCE-v1.md)：copy → staged identity durable record → no-overwrite target publish → 全部目标 durable → 单事务 metadata switch → exact old-copy cleanup。Application 已有 immutable progress kernel 与恢复状态校验测试；该内核不执行 I/O，不独立授予删除权限。

config.db v4 已增加 root relocation 的 pre-commit manifest/progress journal 和 root reservations；v5 增加独立 canonical configuration checkpoint 与 METADATA_COMMITTED progress v2。Begin 原子冻结配置和完整 tracked set；commit 在同一事务内重验配置、placement/version、旧根、reservation/root safety，执行物理重验并再次读取 File-backed 配置，再只切换选定根和日志状态。旧日志不补签配置、不重解释 ExecutionSemanticDigest；缺少 checkpoint 时不能提交。没有用户可调用的物理迁移入口。

物理 pre-commit 适配器已实现 Stage/PublishTarget（包括 rename 后 journal 落后的同对象恢复），严格要求目录 barrier 成功才签发 proof，提交前原归档始终保留。已有真实文件 + SQLite 的复制/发布/metadata switch/cleanup 组合测试（注入 barrier），但完整运行编排、Output Reorganization 和最终恢复验收继续待办，不能标为 COMPLETE。

切换事务已调用全量物理重验端口：重新验证 sealed manifest/progress 完整集合、空根、旧/新对象、目标目录 barrier 和 temp absence；不复用历史 proof 作为当前磁盘事实，不重建缺失路径。

迁移专用配置观察入口不要求输入 binding、FILE_MANAGED discovery 或解密密钥。提交使用独立 identity/layout fingerprint：无关名称/调度/规则/压缩级别变更不阻断，相关变化则拒绝。完整 Plan fingerprint 仅用于发现变化，不作为提交授权。失败事务保留旧根；成功后旧副本、baseline/ArchiveVersion 与 reservation 保留，等待后续安全清理。

提交后 exact old-copy 物理清理适配器已实现：匹配新副本与旧 identity/bytes 后仅删除 manifest 指定文件，完成目录 barrier/absence re-proof，未知文件与目录保留。schema v6 已接入逐项 cleanup 持久化和完成前重新证明 absence；真实 SQLite 故障后可重开数据库补记，删除后取消不打断成功 proof 的持久化，旧路径重新出现则不删除且拒绝完成。COMPLETED 不释放 reservation。

Application 启动恢复已接入全量 journal 枚举和已提交清理 workflow，未注入适配器时仍显式报告 CleanupPending，不静默忽略日志。Prepared/TargetsDurable 不自动复制/提交；Completed 不重复删除，保留 reservation 并跳过同 Plan 的旧恢复路径。真实文件 + SQLite 验证 inactive journal、缺少适配器、删后日志落后、旧对象漂移、日志损坏和 publish 恢复冲突。App/CLI 装配继续待办，独立 compaction 已提供事务接口。

目标比较规则未知时的阻断策略已确认。InspectTargetsAsync 现要求独立的只读比较能力端口，缺失适配器或能力未知均拒绝；检查后的配置/metadata 重验仍有效。已提供 Linux x64/arm64 ext 非 casefold、非 fscrypt 目录的真实只读比较适配器；需显式装配，其他平台/文件系统继续阻断，普通 InspectAsync 仍只返回部分观察。Linux CI 必须运行原生成功分支；不得将局部比较能力或测试替身视为完整迁移能力。M5.3 仍为 IMPLEMENTING。

复制/发布/提交已默认接入目标布局比较重验，覆盖完整 manifest，恢复允许本事务已拥有的 staged/target 对象存在。能力不足明确暂停且保留 journal，rename 后仍按 durable staged identity 恢复。跨平台协议 fixture 显式替换原生能力查询，真实默认 Stage 由 Linux CI 强制验证；完整 Begin 用户入口与其他平台比较模型仍待实现。

独立 compaction 已增加 Application 显式用例，按 Plan/transaction/revision 选择，只调用已有 COMPLETED 仓储事务；响应丢失不重放，记录缺失不误报本次成功。真实 SQLite 回滚、旧/temp 重现、缺少适配器、取消与过期 revision 的保留行为已纳入测试；App/CLI 装配及完整 Begin 仍待完成。

Manifest v2 已移除旧 execution 摘要字段，新建日志强制原子冻结独立 configuration checkpoint；v1 仍按原 canonical bytes 读取和编码，不做升级或摘要重解释。transfer ProtocolVersion 仍为 1、config.db schema 仍为 6，manifest 编码版本独立分派。真实复制/恢复/commit/cleanup/compaction 组合测试已改用 v2；完整 Begin 用户入口及物理 preflight 装配仍待完成。

目标目录持久化预检端口及物理适配器已完成：覆盖空根、现存父链、屏障不可用、目录新增/替换、占位与取消，不创建探测文件或父目录。该增量不声称完整 Begin 或未来写权限检查已完成；启动编排仍待接入，M5.3 保持 IMPLEMENTING。

显式 Begin Application 用例已接入现有 inventory、容量、比较、namespace 与目录屏障预检，并在最终配置/metadata 重验后原子创建 v2 日志。真实 SQLite 组合验证预检拒绝、回滚、提交后响应丢失与取消，成功路径继续完成迁移和清理。M5.3 仍为 IMPLEMENTING；App/CLI 用户入口、其他平台比较能力和 Output Reorganization 继续待办。

桌面主窗口已从模板替换为存储维护预览：打开已有 config.db、选择启用方案、显示现有根并检查目标，支持取消和输入锁定，显示真实能力不足诊断。仅预览入口已开放；执行/恢复/compaction 用户入口、其他平台比较能力与 Output Reorganization 仍待完成，M5.3 保持 IMPLEMENTING。
