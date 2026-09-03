# config.db v1 Schema Design

状态：**M3.9 Schema Design Frozen；M3.10 implementation complete**
Review：[`CONFIG-DB-v1-SCHEMA-DESIGN-REVIEW.md`](../reviews/CONFIG-DB-v1-SCHEMA-DESIGN-REVIEW.md)

## 1. 边界与版本

`config.db` 是单设备 local durable state。v1 schema version 固定为 `1`，只由 `DatabaseMetadata.SchemaVersion` 表达，与 Backup Plan `schemaVersion`、Portable Semantics pins、Fingerprint Encoding Version、archive/manifest semantics 完全独立。打开数据库必须先读取 metadata：版本大于应用支持值时拒绝打开；版本缺失、重复或不合法时判为损坏；旧应用不得猜测 future schema，也不得把 Backup Plan reader 用于数据库兼容。

本阶段只冻结关系设计、encoding、约束、事务与 Application ports；不添加 EF Core/SQLite package、`DbContext`、Entity、Migration 或 repository implementation。

## 2. Frozen durable encoding

所有实现必须显式 converter，不得依赖 EF/CLR 默认：

| 领域值 | SQLite encoding |
|---|---|
| UUID | `BLOB(16)`，RFC 4122/network byte order；禁止 .NET `Guid.ToByteArray()` mixed-endian |
| SHA-256 / fingerprint | 原始 `BLOB(32)` |
| UTC timestamp | `INTEGER`，Unix epoch milliseconds；写入前转 UTC，精度固定到毫秒 |
| boolean | `INTEGER`，仅 `0`/`1` |
| revision/count/version | `INTEGER`，按字段约束正数或非负数 |
| enum/union discriminator | 大小写敏感 stable ASCII `TEXT` token；不得保存 CLR numeric value |
| logical/relative path | NFC UTF-8 `TEXT`，`/` separator，按领域 parser 验证 |
| physical path | OS canonical display path 与 ordinal comparison key 分列保存 |
| canonical document | deterministic writer 产生的严格 UTF-8 JSON `BLOB` |

v1 stable tokens：authority `MANAGED|FILE_BACKED`；archive lifecycle `PREPARED|VERIFIED|PUBLISHED|SUPERSEDED`；format `SEVEN_ZIP|ZIP|TAR_ZSTD`；publish stage `PREPARED|HISTORY_CAPTURED|CURRENT_PUBLISHED|METADATA_COMMITTED`；schedule status `NOT_INSTALLED|INSTALLED|OUT_OF_SYNC|ERROR`；maintenance status `PENDING|OUT_OF_SYNC|COMPLETED`。新增 token 必须随 DB migration 明确处理，旧 reader 不忽略未知 token。

## 3. Schema overview

以下名字是持久 contract，不是 EF Entity 名。所有 FK 默认 `ON DELETE RESTRICT ON UPDATE RESTRICT`；schema **不使用 `ON DELETE CASCADE`**。Portable Update 移除 identity 只把相关 active flag 置 `0`，不删除 artifact、History 或 baseline。

### 3.1 Database and Plan authority

```text
DatabaseMetadata(
  SingletonKey INTEGER PK CHECK = 1,
  SchemaVersion INTEGER NOT NULL CHECK > 0,
  DatabaseId BLOB(16) NOT NULL UNIQUE,
  DeviceId BLOB(16) NOT NULL,
  CreatedAtUtcMs INTEGER NOT NULL
)

PlanRegistration(
  PlanId BLOB(16) PK,
  Authority TEXT NOT NULL CHECK token,
  FileDocumentPath TEXT NULL,
  IsActive INTEGER NOT NULL CHECK 0/1,
  RegisteredAtUtcMs INTEGER NOT NULL,
  CHECK ((Authority='MANAGED' AND FileDocumentPath IS NULL) OR
         (Authority='FILE_BACKED' AND FileDocumentPath IS NOT NULL))
)

ManagedPlanDocument(
  PlanId BLOB(16) PK FK -> PlanRegistration,
  Revision INTEGER NOT NULL CHECK > 0,
  CanonicalUtf8Payload BLOB NOT NULL CHECK length > 0,
  PayloadSha256 BLOB(32) NOT NULL,
  UpdatedAtUtcMs INTEGER NOT NULL
)
```

`ManagedPlanDocument` 是 MANAGED authoritative value，不机械展开 Backup Plan JSON Schema。保存前必须由 deterministic writer 生成 canonical payload；读取后仍必须走现有 strict UTF-8 reader、version-specific closed-world DTO/schema validation 与 semantic mapper，digest 只用于 corruption detection，不能替代解析。`Revision` 每次成功替换严格 `+1`，repository 用 expected revision 做 optimistic concurrency。

FILE_BACKED registration 在 DB 中不得存在 `ManagedPlanDocument` 行，也不得保存可 fallback 的 document payload、semantic copy 或 last-known-good JSON；文件不可读/无效时 PlanNotReady，而不是回退 DB copy。authority conversion 必须在一个 transaction 中切换 registration 并按目标 authority 插入或删除 Managed payload；删除 payload 不触碰 runtime state。

### 3.2 Device local bindings and secrets

```text
SourceLocalBinding(PlanId, SourceId, CanonicalPath, ComparisonKey, IsActive,
  PK(PlanId,SourceId), FK PlanId -> PlanRegistration)
ExternalLocalBinding(PlanId, ExternalSourceId, CanonicalPath, ComparisonKey, IsActive,
  PK(PlanId,ExternalSourceId), FK PlanId -> PlanRegistration)
OutputRootLocalBinding(PlanId, RootKind TEXT CURRENT|HISTORY, CanonicalPath, ComparisonKey, IsActive,
  PK(PlanId,RootKind), FK PlanId -> PlanRegistration)
SecretBinding(PlanId, SecretSlotId, ProviderToken, OpaqueReference, SecretRevision INTEGER CHECK > 0, IsActive,
  PK(PlanId,SecretSlotId), FK PlanId -> PlanRegistration)
```

binding aggregate 保存必须先完成本设备内三根与跨 active Plan overlap validation，并在一个 transaction 中替换本 Plan active binding view。DB 可用 comparison key 辅助候选查询，但最终 overlap 判断仍由 platform path semantics adapter 执行。`OpaqueReference` 只能是 Secret Store locator；不得保存 secret、password、token、derived hash、recovery key 或 encrypted secret blob。Secret material 变化必须显式推进 `SecretRevision`；纯 locator 重绑定但 material revision 不变不得伪造 revision。

### 3.3 FILE_MANAGED local identity registration

```text
FileManagedArchiveUnitRegistration(
  PlanId BLOB(16), SourceId BLOB(16), ArchiveUnitId BLOB(16),
  LogicalUnitPath TEXT NOT NULL, IdentityOrigin TEXT NOT NULL CHECK IN ('DIRECTIVE','LOCAL_REGISTRATION'),
  IsActive INTEGER NOT NULL CHECK 0/1,
  PRIMARY KEY(PlanId,ArchiveUnitId),
  UNIQUE(PlanId,SourceId,LogicalUnitPath),
  FK PlanId -> PlanRegistration
)
```

它是本机 identity association/可重建索引，不保存 `.backupignore` rules。无 `@id` 时只有经用户确认的 local registration 才能建立稳定关联；发现、rename/move 或 directive 冲突不得自动改写文件或 DB。

### 3.4 Archive artifact metadata and placement

```text
ArchiveVersion(
  ArchiveVersionId BLOB(16) PK,
  PlanId BLOB(16) NOT NULL,
  ArchiveUnitId BLOB(16) NOT NULL,
  ArchiveFormat TEXT NOT NULL CHECK token,
  ArchiveSpecFingerprint BLOB(32) NOT NULL,
  Lifecycle TEXT NOT NULL CHECK token,
  IntegritySha256 BLOB(32) NULL,
  Length INTEGER NULL CHECK >= 0,
  PublishedAtUtcMs INTEGER NULL,
  CHECK lifecycle-dependent required metadata,
  UNIQUE(PlanId,ArchiveUnitId,ArchiveVersionId),
  FK PlanId -> PlanRegistration
)

CurrentVersion(
  PlanId BLOB(16), ArchiveUnitId BLOB(16), ArchiveVersionId BLOB(16) NOT NULL,
  CurrentRelativePath TEXT NOT NULL,
  PRIMARY KEY(PlanId,ArchiveUnitId),
  UNIQUE(ArchiveVersionId),
  FK ArchiveVersionId -> ArchiveVersion
)

HistoryVersionPlacement(
  ArchiveVersionId BLOB(16) PK,
  PlanId BLOB(16) NOT NULL, ArchiveUnitId BLOB(16) NOT NULL,
  HistoryRelativePath TEXT NOT NULL,
  UNIQUE(PlanId,HistoryRelativePath),
  FK ArchiveVersionId -> ArchiveVersion
)
```

`ArchiveVersion` 不含 storage slot、absolute path 或 relative placement。Current placement 的唯一真相是 `CurrentVersion.CurrentRelativePath`；History placement 的唯一真相是 `HistoryVersionPlacement.HistoryRelativePath`。同一 version 不得同时出现在 Current 与 History，repository transaction 必须验证 version 的 Plan/Unit 一致。Output Reorganization 只更新 `CurrentVersion` 与 layout state，不更新 ArchiveVersion、不创建 version、不推进 baseline。Storage relocation 只更新 root binding，relative placement 不变。

### 3.5 Baseline and output layout

```text
CommittedArchiveUnitBaseline(
  PlanId BLOB(16), ArchiveUnitId BLOB(16), ArchiveVersionId BLOB(16) NOT NULL,
  FingerprintEncodingVersion INTEGER NOT NULL CHECK > 0,
  RulesSemanticsVersion INTEGER NOT NULL, ArchiveSemanticsVersion INTEGER NOT NULL,
  OutputPathEncodingVersion INTEGER NOT NULL,
  EntrySetFingerprint BLOB(32) NOT NULL, SelectionFingerprint BLOB(32) NOT NULL,
  ArchiveSpecFingerprint BLOB(32) NOT NULL,
  RulesComponent BLOB(32) NOT NULL, BoundaryComponent BLOB(32) NOT NULL,
  LinkPolicyComponent BLOB(32) NOT NULL, ExternalMappingComponent BLOB(32) NOT NULL,
  FormatComponent BLOB(32) NOT NULL, CompressionComponent BLOB(32) NOT NULL,
  ProtectionComponent BLOB(32) NOT NULL, ManifestComponent BLOB(32) NOT NULL,
  PRIMARY KEY(PlanId,ArchiveUnitId),
  FK ArchiveVersionId -> ArchiveVersion
)

CommittedOutputLayoutState(
  PlanId BLOB(16), ArchiveUnitId BLOB(16), OutputLayoutFingerprint BLOB(32) NOT NULL,
  PRIMARY KEY(PlanId,ArchiveUnitId), FK PlanId -> PlanRegistration
)
```

Baseline 必须显式指向当前 committed `ArchiveVersionId`，且 commit 后与 Current pointer 相同。它只保存 archive content decision 所需 EntrySet/Selection/ArchiveSpec 和诊断 components，不保存 OutputLayout、ExecutionBinding 或 physical path。layout state 独立且只保存 fingerprint，不再复制 Current path。

### 3.6 Recoverable publish journal

每个 unit 最多一个非完成 intent。journal 拆成 owner row 与完整 baseline payload row，仅为避免 nullable/重复列；二者必须同 transaction 写入和读取：

```text
PublishIntent(
  PlanId, ArchiveUnitId, NewArchiveVersionId, Stage,
  NewArchiveFormat, NewArchiveSpecFingerprint, ExpectedNewIntegritySha256, NewLength,
  CurrentRelativePath, OutputLayoutFingerprint, CurrentPublishedAtUtcMs NULL,
  OldArchiveVersionId NULL, OldArchiveFormat NULL, OldArchiveSpecFingerprint NULL,
  OldIntegritySha256 NULL, OldLength NULL, OldPublishedAtUtcMs NULL, OldCurrentRelativePath NULL,
  HistoryRelativePath NULL, HistoryVerifiedIntegritySha256 NULL,
  PRIMARY KEY(PlanId,ArchiveUnitId), UNIQUE(NewArchiveVersionId)
)

PublishIntentBaseline(
  PlanId, ArchiveUnitId,
  FingerprintEncodingVersion, RulesSemanticsVersion, ArchiveSemanticsVersion, OutputPathEncodingVersion,
  EntrySetFingerprint, SelectionFingerprint, ArchiveSpecFingerprint, OutputLayoutFingerprint,
  ExecutionSemanticFingerprint, ExecutionBindingFingerprint,
  RulesComponent, BoundaryComponent, LinkPolicyComponent, ExternalMappingComponent,
  FormatComponent, CompressionComponent, ProtectionComponent, ManifestComponent,
  PRIMARY KEY(PlanId,ArchiveUnitId), FK -> PublishIntent
)
```

`PublishIntentBaseline` 是完整 `BaselineCandidate`，包括当次 stale revalidation 需要但 committed baseline 不保存的 execution fingerprints。`PublishIntent` 同时保存 new ArchiveVersion 全 metadata、目标 Current path、layout fingerprint、old Current 的 version metadata/path，以及 History copy 的 path + verified hash proof。stage 约束要求：有 old Current 时进入 `CURRENT_PUBLISHED` 前必须存在完整 History proof；`CURRENT_PUBLISHED` 必须有 publish UTC；无 old Current 时所有 old/history 列均为空。

重启恢复只读取 filesystem 与这两张表：observed Current hash 等于 expected new hash 时可由 journal 重建 `DurableUnitMetadataCommitPlan`；等于 old hash 时恢复/中止旧 Current；否则进入 ambiguous recovery。不得依赖内存 Candidate、cache.db、日志或重新解析当前已变化的 Plan 来补 payload。

### 3.7 Schedule and maintenance

```text
ScheduleInstallation(
  PlanId, DeviceId, Status, AdapterToken NULL, OpaqueInstallationId NULL,
  InstalledIntentDigest BLOB(32) NULL, UpdatedAtUtcMs, LastError NULL,
  PRIMARY KEY(PlanId,DeviceId), FK PlanId -> PlanRegistration
)

MaintenanceState(
  MaintenanceStateRowId INTEGER PRIMARY KEY AUTOINCREMENT,
  PlanId BLOB(16), ArchiveUnitId BLOB(16) NULL, Kind TEXT, Status TEXT, Detail NULL, UpdatedAtUtcMs,
  FK PlanId -> PlanRegistration
)

UNIQUE INDEX UX_MaintenanceState_PlanScope
  ON MaintenanceState(PlanId,Kind) WHERE ArchiveUnitId IS NULL
UNIQUE INDEX UX_MaintenanceState_UnitScope
  ON MaintenanceState(PlanId,ArchiveUnitId,Kind) WHERE ArchiveUnitId IS NOT NULL
```

schedule installation 是 local reconciliation state，不进入 portable plan transaction。maintenance kinds v1 为 `HISTORY_RETENTION|OLD_CURRENT_PATH_CLEANUP|STORAGE_RELOCATION|OUTPUT_REORGANIZATION|SCHEDULE_RECONCILIATION`。Retention/cleanup 失败只能写 out-of-sync，不回滚已 durable committed Current/baseline。
`MaintenanceStateRowId` 只为绕开 SQLite nullable composite key 不能可靠表达 Plan-scope singleton 的内部 surrogate row key，不是领域 identity，不得进入 Application port、日志关联或业务判断。两个 partial unique indexes 分别冻结 Plan-scope 与 ArchiveUnit-scope singleton。

## 4. Atomic operations

Repository port 按一致性边界而不是 table 设计：

1. Plan authority transaction：registration + Managed payload/revision 原子保存；FILE_BACKED 禁止 payload。
2. Binding aggregate transaction：同 Plan/Device 的 bindings 验证后整体保存；inactive state 保留。
3. Publish journal progress：每次 stage 与 proof 原子覆盖并采用 expected previous stage，禁止倒退或跳步。
4. `CompleteMetadataCommit` 单一 transaction：插入/更新 new ArchiveVersion；old version 标为 superseded并插入 History placement；替换 Current pointer；替换 baseline且关联 new version；替换 layout fingerprint；将 intent 标记完成（随后可单独清理 completed journal）。Application 不得分别调用 InsertArchiveVersion/UpdateCurrent/SaveBaseline/SaveLayout。
5. Output Reorganization transaction：只更新 Current path + layout fingerprint；不得写 ArchiveVersion/baseline。
6. destructive cleanup 是未来显式用例；v1 普通 Update、unregister、authority conversion、History Disabled 和 binding detach 均不 cascade artifact state。

SQLite implementation 必须启用 foreign keys、以写 transaction 保证上述边界，并把 constraint/concurrency failure 映射为明确 repository error。Online Backup API 是一致 config snapshot 的唯一允许机制，禁止直接复制 live DB 文件。

## 5. Invariants checked above SQL

SQLite CHECK/FK 不能独立表达 UUID v4、RFC byte order、path grammar、Plan/Unit cross-row equality、Current/History exclusive placement、baseline-current version equality、stage transition、canonical JSON 或 root overlap。repository implementation 必须在 transaction 内验证这些 invariants；数据库 constraints 是第二道防线而不是领域 validator 替代品。

## 6. M3.10 entry gate

Schema Design Review 已逐项验证 blocker、durable encoding、authority、recovery completeness、atomic boundary 与 non-destructive lifecycle，结论 PASS。M3.10 已按本文生成 Initial EF Core SQLite migration并完成 repository tests。实现前唯一 schema 修正是 MaintenanceState nullable scope blocker，已先回写本文与 Review；其余 Entity/migration 未反向修改契约。

## 7. config.db schema v2 — durable History capture requirement

M5.1 Completion Hardening 发现 v1 `PublishIntent` 无法区分“旧 Current 本次要求 History capture”与“本次明确不要求”。恢复时重新读取当前 Plan 会用崩溃后可能已变化的配置解释旧事务，因此 schema v2 新增：

```text
PublishIntent.HistoryCaptureRequirement
  REQUIRED | NOT_REQUIRED | UNKNOWN_LEGACY
```

新 intent 在 `BeginPublish` 前冻结：无旧 Current 为 `NOT_REQUIRED`；有旧 Current且当次 effective History Enabled 为 `REQUIRED`；有旧 Current且 Disabled 为 `NOT_REQUIRED`。recovery 只能读取该 durable fact，禁止重新读取当前 Plan 判断旧事务。

v1→v2 migration 独立于 Initial v1 migration：旧 row 无旧 Current时可无歧义迁移为 `NOT_REQUIRED`；存在旧 Current的历史 row迁移为 `UNKNOWN_LEGACY`，未完成事务一律 fail closed为 `AmbiguousPublishRecovery`。已完成 intent仍可由 durability maintenance清理。`DatabaseMetadata.SchemaVersion` 同一 migration更新为2，open coordinator只允许显式1→2升级并继续拒绝未来版本。

# config.db schema v3 addendum — Retention deletion journal

M5.2 把 schema 从 v2 提升到 v3；v1/v2 migration 保持不可变。新增 artifact-level `RetentionDeletionIntent`，以 `ArchiveVersionId` 为主键，并保存 `PlanId`、`ArchiveUnitId`、`SelectionId`、`PREPARED|COMPLETED`、History relative path、expected SHA-256/length、RetentionSemanticsVersion=1、KeepLastVersionsCount、SelectedAtUtcMs 与 optional CompletedAtUtcMs。

该表是 destructive authorization/recovery journal，不是 History placement，也不替代 unit-level `MaintenanceState(HISTORY_RETENTION)`。它 FK 到 immutable `ArchiveVersion`，但不 FK 到 `HistoryVersionPlacement`，因为完成事务会有意删除 placement 并保留 completed intent。完成事务原子验证 PREPARED intent、exact placement 与同一 SUPERSEDED version，然后删除 placement并将 intent更新为COMPLETED；ArchiveVersion 不删除、不增加 Deleted lifecycle。

一次 retention evaluation 使用 `SelectionId` 分组，并在同一事务中重验所有 victims 后全部插入或全部拒绝。恢复不得读取当前 Plan 决定旧 intent 是否仍应执行。

# config.db schema v4 addendum — Storage relocation pre-commit journal

新增 `StorageRelocationIntent`：TransactionId BLOB(16) PK、PlanId BLOB(16) UNIQUE/FK registration RESTRICT、DeviceId BLOB(16)、ProtocolVersion=1、Revision>=1、Stage（本次仅 PREPARED/TARGETS_DURABLE）、ManifestPayload/ManifestSha256 与 ProgressPayload/ProgressSha256。payload 是独立 version-specific closed-world canonical UTF-8 DTO，不是 portable Backup Plan DTO 或 extension bag。reader 验证 SHA-256、必填字段、未知字段、canonical round-trip（拒绝重复字段及非 canonical encoding）、manifest/progress identity 与合法状态组合。Manifest 永不被 progress update 改写。

新增 `StorageRelocationRootReservation`：TransactionId + Slot PK、CanonicalPath、ComparisonKey；FK intent RESTRICT。Slot 固定 CURRENT_OLD/CURRENT_NEW/HISTORY_OLD/HISTORY_NEW。该表是 manifest 根集合的可查询投影，加载必须逐项核对，不能形成第二套 authority。Begin 在一个事务中保存 intent 与全部 reservation，并重验完整 tracked placement、integrity、old root binding、device、active Plan、既有 publish/retention/maintenance 互斥与所有 active roots。snapshot progress update 按 TransactionId + expected Revision CAS，仅允许 kernel 的 staged/target/seal 转移。

本次 schema 只支持 relocation 的 pre-commit durability，不提供 metadata commit、abort、delete 或 compact API；没有物理 workflow 调用入口。挂起 intent 保留 root reservation；同 Plan 的配置/绑定/publish/retention/identity mutation 和跨 Plan 冲突的 binding/activation 都必须拒绝。后续 metadata switch 与 cleanup 需要显式 schema/port 扩展，禁止把任意 progress save 当作 metadata commit。

v1 payload 的 enum 编码固定：RootKind Current=0/History=1；transfer stage Prepared=0/TargetsDurable=1（kernel 的 2/3 尚不允许持久化）；artifact stage Pending=0/Staged=1/TargetDurable=2（OldCopyAbsent=3 尚不允许在 v4 pre-commit journal 中出现）。字段顺序、UTF-8 serializer 格式与有序 root/entry 数组是内部 codec v1 的一部分，升级必须新增 version-specific reader，不可静默改写既有 payload。该 JSON 只在 config.db 中使用，不改变公开 portable Schema。

## config.db schema v5 — relocation metadata commit

StorageRelocationIntent 新增 nullable ConfigurationPayload/ConfigurationSha256；两者必须同时为 NULL（legacy）或为非空 payload/32-byte digest。closed-world canonical configuration checkpoint v1 保存 authority、registration path、迁移配置 fingerprint encoding version 与 digest；事务内 Begin 重读配置确认后与 journal 一起保存，不允许事后补签 legacy journal。既有 manifest bytes/ExecutionSemanticDigest 不变、不重解释。旧日志继续可读和执行 pre-commit 恢复，但缺少 checkpoint 时不能 metadata commit。

Stage 新增 METADATA_COMMITTED。progress codec v1 仍只读取 pre-commit，新增 version 2 只读取 MetadataCommitted + 全部 TargetDurable；不自动升级旧 progress。commit 事务重验 revision、配置 checkpoint、完整 placement/ArchiveVersion、old bindings、reservation 与跨 Plan root safety，执行全量 physical verification，再重读 File-backed 配置；仅更新选定 root path/key 和 journal stage/revision。baseline、ArchiveVersion、Current/History relative placements、旧文件、root reservations 均保留。任何异常整体回滚；metadata switch 后不能返回 pre-commit 状态。cleanup/completion/compaction 暂不开放。

## config.db schema v6 — relocation cleanup

v6 保持 manifest/configuration 与 progress v1/v2 不变，增加 progress v3：MetadataCommitted 可含 OldCopyAbsent，Completed 必须全部 OldCopyAbsent。只有 repository 在同一事务加载 journal、重验新 binding/placement/reservation、调用物理清理并匹配完整 absence proof 后才能写入。物理成功后的日志保存与提交不再响应 caller cancellation；失败允许下次以真实 absence 补记。完成前重新证明全部旧路径 absence。COMPLETED 不释放 journal/reservation，普通变更继续受互锁保护。v6 降级 v5 时拒绝任何 v3/Completed 记录，避免丢失清理进度语义。
