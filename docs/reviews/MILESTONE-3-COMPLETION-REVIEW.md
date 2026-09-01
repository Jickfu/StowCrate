# Milestone 3 Completion Review

评审对象：[`MILESTONE-3-BACKUP-PLAN-DOCUMENT.md`](../milestones/MILESTONE-3-BACKUP-PLAN-DOCUMENT.md) 及 M3.1–M3.12 实现
结论：**PASS / M3 COMPLETE**
日期：2026-09-01

## Completion gates

| Gate | 结论 | 证据 |
|---|---|---|
| Portable contract | PASS | Backup Plan v1 strict closed-world schema、deterministic writer、semantic mapper与 authority规则已冻结 |
| Resolution/readiness | PASS | device-local path/External/SecretRevision facts分层解析，incomplete observation与缺失 readiness显式阻止执行 |
| Change detection | PASS | strong fingerprints、Candidate/Baseline边界、ArchiveVersion/placement与 Output Reorganization规则已实现 |
| config.db schema | PASS | v1独立 schema version、frozen codecs、restrict FK、Initial migration与 model保持一致 |
| Durable publish recovery | PASS | self-contained PublishIntent、expected-stage CAS、atomic metadata commit与 restart reconciliation均有真实 SQLite覆盖 |
| Authority/local binding | PASS | Managed/File-backed统一 workflow、无 fallback、single-device identity与共享 root safety已实现 |
| Secret consistency | PASS | path aggregate不再重写 Secret metadata；Set/Replace/Rebind/Deactivate CAS与 COW failure windows已验证 |
| Secret material boundary | PASS | material仅经 zeroizable lease进入 platform-neutral port；无 durable value/hash/verifier；headless availability可探测 |
| Snapshot/recovery | PASS | SQLite Online Backup、metadata/schema/integrity验证、atomic snapshot与保留损坏副本的显式恢复已验证 |
| Maintenance scope | PASS | 仅清理 completed journal；incomplete journal及 artifact/baseline runtime state保留 |
| Architecture boundary | PASS | Core/Application不引用 EF/SQLite/DbContext/Entity/IQueryable，Infrastructure异常不泄漏 |
| Verification | PASS | 完整 build与239项测试通过，EF `has-pending-model-changes`报告无 drift，`git diff --check`通过 |

## Secret failure-window review

1. material create成功而 DB CAS失败：旧 active metadata/locator不变；新 locator best-effort删除，失败时只形成 orphan。
2. DB switch成功而 old delete失败：新 revision与新 locator已是唯一 active semantics；旧 locator仅为可清理 orphan。
3. commit前进程中断：旧 binding保持 active；commit后进程中断：新 binding保持 active，绝不回删新 material。
4. Unbind先提交 inactive metadata，再删除 material；delete失败不会留下 active metadata指向缺失 material。

## Schema disposition

M3.12 未发现 schema-shaping blocker。现有 `SecretBinding` row已能表达 provider、opaque locator、monotonic revision与 active状态；snapshot、restore、integrity diagnostics和 completed journal cleanup无需新增 durable table/token。因此 config.db v1、Initial migration与 Schema Design Review结论均保持不变。

## Residual scope transferred to M4+

- OS Credential Manager/Keychain/Secret Service真实 adapters随 Secure Archiving integration逐平台实现；
- Physical Current/History Publisher、storage relocation与 retention artifact cleanup仍未实现；
- M4先实现 Archive Writer capability、manifest、`.partial` 与 archive verification，不以 publisher作为前置。
