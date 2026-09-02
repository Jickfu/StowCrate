# config.db v1 Schema Design Review

评审对象：[`CONFIG-DB-v1-SCHEMA-DESIGN.md`](../plan/CONFIG-DB-v1-SCHEMA-DESIGN.md)
结论：**PASS**
日期：2026-08-31

## Review checklist

| Gate | 结论 | 证据 |
|---|---|---|
| Placement 单一真相 | PASS | ArchiveVersion 无 placement；Current/History 独立；layout 仅 fingerprint |
| Output Reorganization | PASS | 单一 transaction 只更新 Current placement + layout，不触碰 version/baseline |
| Publish restart recovery | PASS | journal 保存 new metadata、Current path、完整 BaselineCandidate、layout、old Current facts、History proof |
| Baseline association | PASS | baseline 强制关联 committed ArchiveVersionId 并与 Current 一致 |
| Authority | PASS | Managed canonical payload + monotonic revision；File-backed 明确禁止 fallback copy |
| Local state coverage | PASS | database/device、bindings、secret revision、FILE_MANAGED registration、schedule、maintenance 均覆盖 |
| Encoding freeze | PASS | UUID/digest/time/bool/enum/path/document 均有稳定 encoding，不依赖 CLR/EF/culture |
| Transaction boundary | PASS | Application 仅暴露 aggregate operations；metadata commit 不存在 table CRUD ports |
| Non-destructive lifecycle | PASS | 全部 FK restrict、无 cascade；removed identity 保留 inactive/runtime state |
| Version isolation | PASS | DB schema version 与 document/portable/fingerprint versions 明确独立，future schema fail closed |
| Scope | PASS | 未引入 package、DbContext、Entity、Migration 或 repository implementation |
| Nullable maintenance scope | PASS | surrogate row PK 仅供 SQLite；两个 partial unique indexes 分别保证 Plan/unit scope singleton |

## Blocker disposition

1. 原 `ArchiveVersion.Location`、`CommittedOutputLayoutState.CurrentRelativePath` 已移除。Current path 只存在于 `CurrentVersion`，History path 只存在于 `HistoryVersionPlacement`。
2. `PendingPublishIntent` 已成为 self-contained durable journal，并能从 `CURRENT_PUBLISHED` state 直接重建 `DurableUnitMetadataCommitPlan`。恢复判断只需 filesystem observed integrity 与 config.db payload。
3. M3.10 Initial migration 前复核发现 nullable `ArchiveUnitId` 不能可靠参与 singleton composite PK；已改为非领域 surrogate row key，并以 Plan-scope/unit-scope partial unique indexes修正。该修正不改变 Application consistency boundary。

## Residual implementation risks for M3.10

- 必须测试 RFC 4122 UUID byte order，尤其禁止误用 mixed-endian `Guid.ToByteArray()`。
- 必须以 corruption fixtures 测试未知 DB version/token、错误 digest length、非法 stage/null combinations。
- 必须用 transaction fault injection 测试 metadata commit 任一步失败均不暴露部分 state。
- 必须测试 Portable Update removed identity、authority conversion 与 unregister 不 cascade runtime state。
- 必须测试 Output Reorganization 不改变 baseline ArchiveVersionId 或 ArchiveVersion row。

以上 implementation verification 已在 M3.10 以真实 SQLite 测试覆盖，包括 known-vector codec、低层 schema probe、restart recovery、六个 transaction fault points、corruption fixtures、authority/unregister non-cascade 与 Output Reorganization。Review 继续 PASS，无新增 schema-shaping blocker。

## M5.1 hardening addendum — schema v2

结论：**PASS**。v1 journal缺少当次 History capture requirement 是durable recovery blocker，已通过独立 schema v2 migration修正，未修改 Initial v1 migration。`REQUIRED|NOT_REQUIRED|UNKNOWN_LEGACY` 是 closed durable token；v1 old-Current incomplete intent只可迁移为 `UNKNOWN_LEGACY`并进入 ambiguous recovery，不依据当前 Plan猜测。真实 SQLite v1→v2 migration、schema version、列存在性、codec round-trip和 EF pending-model-change gate均纳入测试/验证。

# M5.2 schema v3 addendum review

结论：PASS for implementation。artifact-level `RetentionDeletionIntent` 是 destructive authorization 与 crash recovery 的必要边界；它不得由 coarse `MaintenanceState` 替代。placement removal 与 intent completion 必须同一 SQLite transaction，`ArchiveVersion` 保留，completed intent 延迟 compact。v1/v2 migrations 不修改，新增 v2→v3 additive migration且无猜测式 backfill。

M5.2 completion re-review：**PASS**。最终实现进一步要求 destructive path 把 `HistoryRoot` 本身纳入 no-follow native identity proof，并要求 completed-intent compaction 在同一 SQLite transaction 内重验 `HistoryVersionPlacement` 已不存在。review ref `e872ed74c6f171d430d710ce660ff601a250bded`，三平台 CI run `33628437400` 全部通过。
