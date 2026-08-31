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

## Blocker disposition

1. 原 `ArchiveVersion.Location`、`CommittedOutputLayoutState.CurrentRelativePath` 已移除。Current path 只存在于 `CurrentVersion`，History path 只存在于 `HistoryVersionPlacement`。
2. `PendingPublishIntent` 已成为 self-contained durable journal，并能从 `CURRENT_PUBLISHED` state 直接重建 `DurableUnitMetadataCommitPlan`。恢复判断只需 filesystem observed integrity 与 config.db payload。

## Residual implementation risks for M3.10

- 必须测试 RFC 4122 UUID byte order，尤其禁止误用 mixed-endian `Guid.ToByteArray()`。
- 必须以 corruption fixtures 测试未知 DB version/token、错误 digest length、非法 stage/null combinations。
- 必须用 transaction fault injection 测试 metadata commit 任一步失败均不暴露部分 state。
- 必须测试 Portable Update removed identity、authority conversion 与 unregister 不 cascade runtime state。
- 必须测试 Output Reorganization 不改变 baseline ArchiveVersionId 或 ArchiveVersion row。

以上是 implementation verification，不是遗留 schema-shaping blocker。Review PASS，下一阶段为 **M3.10 config.db EF Core SQLite Implementation + Repository Tests**。
