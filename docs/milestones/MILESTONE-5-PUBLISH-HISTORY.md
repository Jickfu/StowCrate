# Milestone 5 — Physical Current / History Publish & Durable Execution

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

## M5.2 — Retention Maintenance + Publish Recovery/Orphan Reconciliation（IMPLEMENTED / REVIEW PENDING）

Retention v1 只自动执行 `KeepLastVersions(N)`，`Disabled` 与 `KeepAll` 均不产生新删除授权；Purge 不在本阶段。候选只来自 active `HistoryVersionPlacement` 与同 identity 的 `SUPERSEDED ArchiveVersion`，Current 在结构上不可能成为 victim。时间顺序固定为 `(PublishedAtUtcMs ASC, ArchiveVersionId RFC-4122/network bytes ASC)`，保留末尾 N 个。

每个 victim 必须先进入 config.db v3 `RetentionDeletionIntent(PREPARED)`。一次选择的全部 intent 在同一 SQLite 事务中重验并全有或全无地写入；intent 冻结 path、SHA-256、length、selection、语义版本及当时的 KeepLastVersions count，后续 Plan 漂移不得撤销或重新解释该授权。

物理删除只接受 no-follow ordinary file。实现必须在删除前验证 identity/integrity 并检测正常替换竞态；删除后完成 parent-directory metadata durability barrier，才允许在单一 SQLite 事务中删除 `HistoryVersionPlacement` 并把 intent 标为 `COMPLETED`。`ArchiveVersion` 作为不可变历史事实永久保留。单个 destructive work 开始后忽略 caller cancellation，直到形成可恢复稳定状态；不同 victim 之间可以取消。

Recovery 只依赖 intent 与物理事实。`PREPARED + matching/absent` 可重试删除或完成 metadata；mismatch、link、special 或 placement conflict 均保留 artifact、placement 与 intent并标记 OutOfSync。`COMPLETED` intent 暂留作 reconciliation authority；仅在后续再次证明 placement 与 artifact 均不存在且 barrier 成功后才可 compact。

Orphan reconciliation 不凭文件名、deterministic path 或裸 `ArchiveVersion` 猜测 ownership。只有 live PublishIntent 的 HistoryCaptureProof 可以交回 publish recovery，只有 retention intent 可以授权删除。tracked missing/corrupt、unplaced known version、unknown history-v1 file 与任意 HistoryRoot 内容都只报告诊断，不自动修复或删除，也不递归删除未知目录。

只读 inventory 仅遍历 StowCrate 管理的 `history-v1` namespace，逐级 no-follow；普通目录只用于遍历，link/reparse/special/unreadable component 作为诊断停止下钻。物理删除逐级拒绝异常 ancestor，并在 hash 前与 namespace 删除前捕获、比较 Windows volume/file ID 或 POSIX device/inode。该检查防止正常同步/替换竞态静默删掉另一对象，不宣称能在所有文件系统上抵御主动 hostile race，也不以此扩大删除授权。
