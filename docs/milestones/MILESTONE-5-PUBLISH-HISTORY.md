# Milestone 5 — Physical Current / History Publish & Durable Execution

## M5.1 — Physical Publish Contract + PublishIntent Execution

本阶段消费 M4 `VerifiedArchiveArtifact`，在不重新扫描、生成 Candidate 或运行 Archiver 的前提下完成 destination-local staging、History capture、atomic Current publish、PublishIntent journal 与 M3 单事务 metadata commit。

M4 private runtime `.partial` 与 M5 Current-filesystem publish temp 是两个边界。M5 先复制到最终 Current 文件的 sibling：

`.<finalFileName>.stowcrate-publish-<ArchiveVersionId:N>.partial`

复制必须 `CreateNew`、流式计算 SHA-256/length、durable flush，并与 Verified ArchiveVersion 一致。开始 journal 前不得改变 Current 或 History。

History layout v1 为：

`history-v1/<ArchiveUnitId:D>/<PublishedAtUtc:yyyyMMddTHHmmss.fffZ>--<ArchiveVersionId:D><extension>`

History v1 只 copy，不使用 hardlink/reflink。copy 到 History destination-local temp、验证并 flush 后 atomic move；Current publish 前重新取得 authoritative `ExecutionSemanticSnapshot`。语义、binding、raw rule source 或 SecretRevision drift 阻止发布；retention-only drift允许发布，但跳过 retention cleanup并标记 maintenance out-of-sync。

同路径 Current 使用原子 overwrite/replace；新路径使用原子 move，新 target 不得覆盖。旧路径仅在 metadata durable commit 后、重新验证旧 integrity 时删除。filesystem 已发布但 journal 落后的 recovery 必须以 expected-new/old integrity 和唯一 History v1 proof 推进或安全 abort；无法证明时保留 journal并返回 `AmbiguousPublishRecovery`。

本阶段不实现 retention physical deletion、Output Reorganization-only、CurrentRoot/HistoryRoot relocation、scheduler/CLI/UI 或新 Archiver capability。

下一项：**M5.2 — Retention Maintenance + Publish Recovery/Orphan Reconciliation**。
