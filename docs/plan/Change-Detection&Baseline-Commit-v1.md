> [!NOTE]
> 本文是设计讨论的初始建议稿，不是规范真相源。已经确认并可用于后续设计的正式语义以 [`docs/CHANGE-DETECTION.md`](../CHANGE-DETECTION.md) 为准；两者冲突时不得依据本文修改行为。

这里我会对之前的粗略设想做一个重要收紧：

> **Committed Baseline 的“事实真相”不应该只放在 `cache.db`。**
>
> 它本质上描述“当前有效归档到底对应哪一份输入状态”，应该和成功的 `ArchiveVersion` 一起持久化在 `config.db`。
> `cache.db` 只负责加速。

这个设计会可靠很多。

------

# 1. 三个状态必须严格区分

正式定义：

```text
Observed State
     ↓
Candidate State
     ↓
Committed Baseline
```

### Observed State

Scanner 本次实际看到的物理文件系统事实：

```text
SourceSnapshot
+
ScanIssue[]
```

它是：

- 临时的；
- 不代表最终要备份什么；
- 不代表备份成功；
- 不允许直接成为 baseline。

------

### Candidate State

Planning Kernel 根据：

```text
SourceSnapshot
+
BackupPlan
+
Archive Unit
+
Rules
+
LinkPolicy
+
External Sources
```

得到的：

> **如果现在执行备份，这个 Archive Unit 应该包含什么。**

也就是：

```text
ArchivePlan / PlannedArchive
```

再加上用于 Change Detection 的 fingerprints。

Candidate 仍然只是：

> “准备备份的状态”。

不是成功状态。

------

### Committed Baseline

正式定义为：

> **最近一个已经验证、成功发布为 Current 的 ArchiveVersion 所对应的输入状态。**

只有它可以作为：

```text
下一次 Change Detection
```

的比较基准。

------

# 2. Baseline 的提交粒度必须是 Archive Unit

绝不能按：

```text
整个 Backup Plan
```

一次性提交。

例如：

```text
Plan
├─ B
├─ D
└─ F
```

本次：

```text
B  ✅
D  ❌
F  ✅
```

那么：

```text
B baseline → 推进
D baseline → 保持旧版本
F baseline → 推进
```

Backup Plan 整体结果：

```text
PartialSuccess
```

这样下一次只需要重新处理：

```text
D
```

而不是全部重来。

所以 baseline identity：

```text
PlanId + ArchiveUnitId
```

------

# 3. Committed Baseline 应该属于 `config.db`

这是我现在比较强烈的建议。

之前可以想成：

```text
cache.db
└─ baseline
```

但仔细看是不合适的。

因为 `cache.db` 的定义是：

> 可以随时删除。

如果 Baseline 也只有这里：

```text
删除 cache.db
```

之后程序连：

> 当前的 `B.7z` 是根据什么输入生成的？

都不知道了。

更合理的是：

```text
config.db
└─ ArchiveVersion
   ├─ VersionId
   ├─ ArchiveUnitId
   ├─ InputFingerprint
   ├─ SelectionFingerprint
   ├─ ArchiveSpecFingerprint
   ├─ PublishedAt
   ├─ ArchiveHash
   └─ ...
```

而：

```text
ArchiveUnit.CurrentVersionId
```

指向：

```text
成功发布的 ArchiveVersion
```

于是：

> **CurrentVersion 对应的 fingerprints 就是 Committed Baseline。**

------

# 4. `cache.db` 只负责加速

建议：

```text
cache.db
├─ FileHashCache
├─ MetadataCache
├─ ScanCache
├─ PlatformCursor
└─ Future Journal State
```

删除：

```text
cache.db
```

以后：

```text
BackupPlan       还在
ArchiveVersions  还在
Current baseline 还在
```

只是下一次可能：

> 需要重新计算更多 hash。

这才符合 disposable cache 的定义。

------

# 5. Baseline 不需要保存几百万条文件记录

这是一个很重要的优化。

我们并不需要：

```text
CommittedEntries
├─ file1
├─ file2
├─ ...
└─ file5,000,000
```

永久保存在 config.db。

因为现在 Planning Kernel 已经能够：

```text
当前 SourceSnapshot
     ↓
生成确定性 ArchivePlan
```

所以只需要把整个 Archive Unit 的状态压缩成 deterministic fingerprint。

例如：

```text
Current Source
     ↓
CandidateFingerprint = ABC123

Last Published Version
     ↓
BaselineFingerprint = ABC123
```

相同：

```text
UNCHANGED
```

不同：

```text
CHANGED
```

这样 `config.db` 非常小。

------

# 6. 不要只有一个 Fingerprint

建议至少拆成三个。

## ① EntrySetFingerprint

表示：

> 最终被选中进入这个 Archive Unit 的数据状态。

例如：

```text
路径
EntryKind
大小
mtime
Link target
metadata
content hash（按策略）
```

经过：

```text
稳定排序
+
规范序列化
+
SHA-256
```

得到：

```text
EntrySetFingerprint
```

------

## ② SelectionFingerprint

表示：

> 为什么这些东西被选中。

至少包括：

```text
Rule semantics version
Global Rules
Plan Rules
Local Rules
RuleMode
CaseSensitivity
LinkPolicy
Archive Boundaries
External Source Mapping
Archive Unit logical identity/path
```

例如：

文件一个没动，但：

```text
node_modules/
```

从排除规则里删除了。

那么：

```text
EntrySetFingerprint
```

最终大概率也会变化。

但保留 SelectionFingerprint 可以告诉用户：

> **规则发生变化。**

而不是笼统显示：

> 数据变化。

------

## ③ ArchiveSpecFingerprint

表示：

> 同样的数据应该怎样生成归档。

例如：

```text
7z / ZIP / TAR.ZST
压缩算法
压缩等级
solid mode
volume size
metadata preservation policy
encryption mode
secret revision
manifest schema version
```

所以即使：

```text
文件完全没变
```

用户把：

```text
ZIP → 7z
```

也必须：

```text
REBUILD
```

------

# 7. 最终可以组合成 InputFingerprint

例如：

```text
InputFingerprint =
SHA256(
    EntrySetFingerprint
    +
    SelectionFingerprint
)
```

然后：

```text
RebuildFingerprint =
SHA256(
    InputFingerprint
    +
    ArchiveSpecFingerprint
)
```

代码上我建议使用强类型：

```csharp
EntrySetFingerprint
SelectionFingerprint
ArchiveSpecFingerprint
InputFingerprint
```

不要全部：

```csharp
string fingerprint
```

否则以后特别容易传错。

------

# 8. Fingerprint 本身统一用 SHA-256

这里和“文件内容 Change Detection Hash”要区分。

Fingerprint 本身只是几十 KB 以内的 canonical metadata 流：

```text
entries descriptors
settings
rules
```

所以直接：

```text
SHA-256
```

没有性能问题。

不要用：

```text
GetHashCode()
```

也不要依赖：

```text
JSON serialization 后直接 hash
```

因为 serializer 配置变化可能导致 fingerprint 漂移。

应该定义一个明确：

> **Canonical Fingerprint Encoding v1**

例如每个字段：

```text
version
field id
length
UTF-8 value
```

固定顺序。

以后升级：

```text
FingerprintFormatVersion = 2
```

旧 baseline 自动失效重新建立。

------

# 9. 文件 Entry State v1

普通 File 至少：

```text
LogicalPath
EntryKind
Size
LastWriteTime
MetadataFingerprint
```

如果启用 content hash：

```text
ContentHashAlgorithm
ContentHash
```

------

### Directory

至少：

```text
LogicalPath
EntryKind
MetadataFingerprint
```

如果最终归档格式保留：

```text
permissions
ACL
xattr
mtime
```

对应 metadata 必须参与 fingerprint。

------

### Link

至少：

```text
LogicalPath
LinkKind
RawTarget
TargetScope
IsDangling
TargetIsDirectory?
MetadataFingerprint
```

我们前面已经决定：

```text
RawTarget
```

变化必须导致 ArchivePlan fingerprint 变化。

------

# 10. Change Detection v1 建议支持两种模式

这里必须承认一个现实问题。

如果只检查：

```text
path
size
mtime
```

那么这种情况：

```text
内容修改了
但 size 没变
mtime 被保留/恢复
```

便无法检测。

没有 Change Journal，也不重新读取内容的情况下：

> **这是数学意义上无法解决的。**

所以产品必须明确。

------

## Standard 模式

建议默认：

```text
ChangeDetectionMode.Standard
```

判断依据：

```text
Path
Kind
Size
mtime
Metadata
LinkTarget
```

必要时：

```text
metadata 变化
→ 重新计算快速 content hash
```

优点：

> 快。

缺点：

> 理论上可能漏掉“内容改变但 size/mtime 完全不变”的文件。

这个限制必须写进文档。

------

## Strict 模式

```text
ChangeDetectionMode.Strict
```

普通文件每次候选状态计算时：

> 重新读取内容计算 hash。

这样才能发现：

```text
same size
same mtime
different bytes
```

代价就是：

> 每一次相当于读一遍全部待备份文件。

这也是为什么以后：

```text
USN Journal
FSEvents
```

会很有价值——它们可以在保证更高可靠性的同时减少重新 Hash。

------

# 11. 不建议 v1 搞三个四个模糊模式

不要：

```text
Fast
Smart
Balanced
Safe
UltraSafe
```

用户根本不知道区别。

v1 就两个：

```text
Standard
Strict
```

非常明确。

UI 以后可以写：

```text
标准检测（推荐）
依据文件元数据快速判断变化

严格检测
读取文件内容验证变化，速度较慢
```

------

# 12. 内容 Hash 与 Archive Hash 必须分开

不要混淆：

### Change Detection Hash

目标：

> 文件内容有没有变化。

可以是：

```text
xxHash
```

强调速度。

------

### Archive Integrity Hash

目标：

> 最终归档是否损坏。

应该：

```text
SHA-256
```

所以：

```text
FileChangeHash
≠
ArchiveIntegrityHash
```

代码里也不要共用一个模糊的：

```csharp
Hash
```

类型。

------

# 13. `cache.db` 的 FileHashCache

建议：

```text
FileHashCache
├─ SourceId
├─ RelativePath
├─ EntryKind
├─ Size
├─ LastWriteTime
├─ MetadataFingerprint
├─ Algorithm
├─ ContentHash
└─ UpdatedAt
```

Standard 模式：

如果：

```text
Size
mtime
metadata
```

都没变：

> 可以复用之前 hash。

------

Strict 模式：

如果没有可信 Change Journal：

> 不允许仅凭 metadata 复用 content hash。

否则 Strict 就不 Strict 了。

以后有：

```text
USN
FSEvents
```

证明该文件没有变化时，再允许复用。

------

# 14. Change Reason 不应该只有 bool

不要 API：

```csharp
bool HasChanged
```

建议：

```csharp
ChangeDecision
{
    Status,
    Reasons
}
```

例如：

```text
ChangeStatus
├─ FirstBackup
├─ Unchanged
└─ RebuildRequired
```

Reason：

```text
NoBaseline
EntrySetChanged
RulesChanged
BoundaryChanged
LinkPolicyChanged
ExternalSourceChanged
ArchiveFormatChanged
CompressionChanged
EncryptionChanged
ManifestSchemaChanged
SemanticsVersionChanged
Forced
BaselineInvalid
```

以后 UI 就能显示：

> 需要重新备份：`.backupignore` 规则发生变化

而不是：

> changed = true

------

# 15. 有些配置变化不应该重新压缩

例如：

```text
Schedule
History retention
UI 展开状态
窗口大小
日志等级
```

都不属于：

```text
ArchiveSpecFingerprint
```

否则用户：

> 把定时任务从每天 8 点改成每天 9 点

竟然触发所有 Archive Unit 重压缩，非常荒谬。

------

# 16. Output Root 改变也不等于内容变化

例如：

```text
D:\Backup
→
E:\Backup
```

归档字节本身可能完全一样。

所以：

```text
CurrentRoot
HistoryRoot
```

不建议进入 ArchiveSpecFingerprint。

它们应该产生另一类任务：

```text
StorageRelocationRequired
```

而不是：

```text
ArchiveRebuildRequired
```

具体移动/复制语义留给 Current/History Milestone。

------

# 17. Secret 变化怎么判断

密码绝不能进入 fingerprint。

但如果用户把：

```text
password A
→
password B
```

归档显然需要重新加密。

因此 Secret Store 应该为秘密提供：

```text
SecretReferenceId
SecretRevision
```

例如：

```text
secret://archive-password/123
revision = 4
```

ArchiveSpecFingerprint 只记录：

```text
SecretReferenceId
SecretRevision
```

不记录真实密码。

密码旋转：

```text
revision 4 → 5
```

自然触发 rebuild。

------

# 18. App 升级不应该自动触发所有重备份

不要把：

```text
StowCrateVersion = 1.2.3
```

直接放 ArchiveSpecFingerprint。

否则：

```text
升级软件
→ 所有归档全部重做
```

非常糟糕。

应该只放：

```text
RuleSemanticsVersion
ScannerSemanticsVersion
ArchiveSemanticsVersion
ManifestSchemaVersion
FingerprintVersion
```

也就是说：

> **只有行为语义改变才失效 baseline。**

------

# 19. Baseline Commit 的唯一允许时机

这是整个规范最重要的一条：

```text
Scan
 ↓
Candidate State
 ↓
Change Detection
 ↓
Archive Write
 ↓
Archive Test
 ↓
Integrity Verification
 ↓
Persist History（后续）
 ↓
Atomic Publish Current
 ↓
Durable ArchiveVersion Commit
 ↓
COMMITTED BASELINE
```

在：

```text
Atomic Publish Current
```

之前：

> **绝对不能推进 baseline。**

------

# 20. 更准确地说，Baseline Commit 点应该是 config.db 事务

以后：

```text
ArchiveUnit.CurrentVersionId
```

指向一个：

```text
Status = Published
```

的 ArchiveVersion。

例如：

```text
BEGIN TRANSACTION

INSERT ArchiveVersion(...)
status = Published

UPDATE ArchiveUnit
SET CurrentVersionId = newVersion

COMMIT
```

这个成功之后：

> 新 ArchiveVersion 才正式成为 Committed Baseline。

------

# 21. 为什么 cache baseline 必须最后更新

顺序必须：

```text
Filesystem Current
      ↓
config.db durable state
      ↓
cache.db
```

而不能：

```text
cache.db
↓
filesystem
```

假设：

```text
cache baseline 已更新
↓
电脑断电
↓
Current 根本没发布
```

下一次系统可能认为：

> 没有变化。

这就是严重 bug。

所以：

> `cache.db` 永远不能领先 durable state。

------

# 22. 最安全的失败后果应该是“多备份一次”

StowCrate 的设计原则应该是：

发生异常时宁愿：

```text
错误判断为 changed
→ 多压缩一次
```

也绝不能：

```text
错误判断为 unchanged
→ 漏备份
```

也就是：

> **False Positive 可以接受，False Negative 尽量禁止。**

------

# 23. Crash Matrix 建议正式写进规范

### 扫描后崩溃

```text
Observed 有
Baseline 没动
```

安全。

------

### Candidate 后崩溃

```text
Baseline 没动
```

安全。

------

### `.partial` 写一半崩溃

```text
Baseline 没动
Current 没动
```

安全。

------

### Archive 验证成功但 Publish 前崩溃

```text
Baseline 没动
Current 没动
```

安全。

------

### Publish Current 后、config.db Commit 前崩溃

这是唯一比较麻烦的窗口：

```text
Current = 新版本
config.db = 旧版本
```

但：

> baseline 仍然不能假装提交成功。

下一次启动应该进入：

```text
Reconciliation
```

根据：

```text
Current archive manifest
+
pending ArchiveVersion
```

恢复一致性。

这个属于后面的 Current/History Milestone。

------

### config.db Commit 成功，cache.db 更新前崩溃

```text
Current = 新
config.db = 新
cache = 旧
```

完全安全。

下一次：

```text
重算 cache
```

即可。

------

# 24. 我建议提前为 Publish 留一个 Pending 状态

虽然 Milestone 3 不实现归档，但 schema 可以预留：

```text
ArchiveVersionStatus
├─ Prepared
├─ Verified
├─ Published
├─ Superseded
└─ Failed
```

未来流程：

```text
新归档验证通过
↓
ArchiveVersion = Verified
↓
Atomic Publish
↓
ArchiveVersion = Published
↓
CurrentVersionId = version
```

这样 crash recovery 很容易做。

但：

> **只有 Published 可以作为 Baseline。**

------

# 25. Plan 在运行中被修改怎么办

这是个很容易漏掉的问题。

运行开始：

```text
Plan Revision = 10
```

压缩期间用户改配置：

```text
Plan Revision = 11
```

那么旧任务不能静默把：

```text
Revision 10
```

的结果发布成：

> “当前最新配置备份”。

我建议：

运行捕获：

```text
PlanRevision
PlanSemanticFingerprint
```

Publish 前再次检查。

如果当前 plan revision 已变化：

```text
PlanChangedDuringRun
```

默认：

> **不发布 Current，不提交 baseline。**

这样最干净。

以后 UI 也可以直接：

> 备份运行期间禁止编辑该 Plan。

------

# 26. Scan Warning 也要区分“是否允许发布”

我们 Milestone 2 已经有：

```text
Info
Warning
Fatal
```

但从 Change Detection / Publish 角度还需要一个维度：

```text
CompletenessImpact
```

建议：

```text
None
IntentionalSkip
IncompleteObservation
```

------

### IntentionalSkip

例如：

```text
Filesystem boundary
Unsupported Special
LinkPolicy = Skip
```

这是规则已知行为。

可以：

```text
Publish
+
Commit baseline
```

但结果显示 warning。

------

### IncompleteObservation

例如：

```text
AccessDenied
文件扫描中消失
目录 enumeration 失败
metadata 无法读取
```

这里我们无法确定：

> 本次 ArchivePlan 是否完整反映 Source。

因此我建议 v1 默认：

```text
AllowIncompletePublish = false
```

即：

> 可以产生 Preview / ArchivePlan，但不能用它覆盖一个已有且完整的 Current。

这样特别符合备份工具的保守原则。

------

# 27. 为什么这一点很重要

假设昨天：

```text
Project.7z
包含 important.docx
```

今天：

```text
important.docx
AccessDenied
```

如果我们：

```text
Warning
↓
跳过
↓
生成新 Project.7z
↓
覆盖 Current
```

就等于：

> 把一个原本完整的备份覆盖成不完整备份。

这是不能接受的。

所以：

```text
IncompleteObservation
→ 默认禁止 Publish
```

非常重要。

------

# 28. 第一次备份也一样

如果第一次备份就：

```text
3 个文件 AccessDenied
```

可以：

- 允许用户查看计划；
- 告诉用户问题；
- 甚至生成 diagnostic archive；

但默认不要宣称：

> Backup Successful。

状态应该：

```text
BlockedByIncompleteSource
```

------

# 29. Change Detection 算法正式顺序

对于每个 Archive Unit：

```text
1. 获得 Candidate State

2. 检查 CurrentVersion
   不存在
   → FirstBackup

3. 检查 Semantics Versions
   不同
   → RebuildRequired

4. 比 SelectionFingerprint
   不同
   → RebuildRequired

5. 比 EntrySetFingerprint
   不同
   → RebuildRequired

6. 比 ArchiveSpecFingerprint
   不同
   → RebuildRequired

7. 全部一致
   → Unchanged
```

------

# 30. `Unchanged` 不代表“不扫描”

v1 即使 unchanged：

```text
SourceScanner
```

仍然需要运行。

因为没有：

```text
USN Journal / FSEvents
```

时，我们必须重新观察 source 才知道没变。

所以：

```text
Unchanged
```

只是：

> 不需要重新归档。

不是：

> 不需要扫描。

以后平台 journal 才能进一步减少扫描。

------

# 31. SourceFingerprint 不要受文件系统枚举顺序影响

继续保持 Milestone 1 的原则：

```text
A
B
C
```

无论 Scanner 返回：

```text
C A B
```

还是：

```text
A C B
```

都必须：

```text
Canonical sort
↓
same fingerprint
```

跨平台：

```text
Windows
Linux
macOS
```

只要逻辑快照相同：

> fingerprint 一致。

------

# 32. Baseline corruption

如果 config.db 中：

```text
CurrentVersionId
```

存在，但：

```text
fingerprint missing
schema invalid
unsupported semantics version
```

不要：

```text
assume unchanged
```

应该：

```text
BaselineInvalid
→ RebuildRequired
```

仍然遵循：

> 宁可多做一次，不漏做。

------

# 33. cache.db 丢失

正式规定：

```text
delete cache.db
```

结果：

```text
配置不丢
Current 不丢
History 不丢
Baseline 不丢
```

只发生：

```text
File hashes/cache miss
↓
扫描/Hash 变慢
```

这是非常重要的灾难恢复承诺。

------

# 34. Change Detection 不应该决定 History

Change Detection 只回答：

```text
NeedRebuild?
```

HistoryPolicy 回答：

```text
旧 Current 怎么处理？
```

不要耦合。

所以：

```text
ChangeDetector
```

完全不知道：

```text
KeepLast5
Daily7
Monthly12
```

这些属于后面的 History 模块。

------

# 35. 建议领域 API

大致：

```csharp
public sealed record ChangeDetectionResult(
    ChangeStatus Status,
    ImmutableArray<ChangeReason> Reasons,
    CandidateUnitState Candidate,
    CommittedBaseline? Baseline);
```

Candidate：

```csharp
public sealed record CandidateUnitState(
    ArchiveUnitId UnitId,
    EntrySetFingerprint EntrySet,
    SelectionFingerprint Selection,
    ArchiveSpecFingerprint ArchiveSpec,
    InputFingerprint Input,
    ScanCompleteness Completeness);
```

Baseline：

```csharp
public sealed record CommittedBaseline(
    ArchiveVersionId VersionId,
    EntrySetFingerprint EntrySet,
    SelectionFingerprint Selection,
    ArchiveSpecFingerprint ArchiveSpec,
    InputFingerprint Input,
    DateTimeOffset PublishedAt);
```

这样非常清晰。

------

# 36. Change Detection 自己应该仍然是纯逻辑

最好放：

```text
StowCrate.Core
```

或者 Application 中一个纯服务。

它不应该直接：

```text
SELECT * FROM cache.db
```

应该：

```text
Application
↓
load CurrentVersion
↓
build CandidateState
↓
ChangeDetector.Compare(...)
```

所以 ChangeDetector 可以写大量纯单元测试。

------

# 37. 这一阶段至少要有这些测试

我建议 Codex做到至少：

1. 无 Baseline → FirstBackup
2. 完全一致 → Unchanged
3. 新增文件 → changed
4. 删除文件 → changed
5. size 变化 → changed
6. mtime 变化 → changed
7. Link raw target 变化 → changed
8. Rules 变化 → changed
9. Archive Boundary 变化 → changed
10. LinkPolicy 变化 → changed
11. ZIP → 7z → changed
12. compression level 变化 → changed
13. schedule 变化 → unchanged
14. retention policy 变化 → unchanged
15. output root 变化 → archive bytes unchanged
16. secret revision 变化 → changed
17. AppVersion 变化 → unchanged
18. ManifestSchemaVersion 变化 → changed
19. cache.db 缺失不影响 Baseline
20. partial unit success 独立 commit
21. failed unit baseline 不推进
22. publish 前 baseline 不推进
23. config commit 前 baseline 不推进
24. stale PlanRevision 不允许 publish
25. IncompleteObservation 阻止 publish
26. IntentionalSkip 允许 publish
27. fingerprint 不受 entry 输入顺序影响
28. semantics version 变化 → rebuild
29. baseline invalid → conservative rebuild
30. Standard / Strict 模式语义分别正确

------

# 38. 我建议把最核心的规范压缩成这 10 条

以后 `CHANGE-DETECTION.md` 首页可以直接写：

> 1. Change Detection 以 Archive Unit 为最小单位。
> 2. Observed State、Candidate State 和 Committed Baseline 必须严格分离。
> 3. 只有成功发布为 Current 的 ArchiveVersion 才能成为 Baseline。
> 4. Committed Baseline 随 ArchiveVersion 持久化在 `config.db`，`cache.db` 只用于加速。
> 5. Baseline 至少包含 EntrySet、Selection、ArchiveSpec 三类确定性 fingerprint。
> 6. 文件变化、规则变化和归档参数变化都可能要求 rebuild，但调度、History retention 等不应触发重压缩。
> 7. Baseline 只允许前进，绝不能在 Archive Publish 成功前更新。
> 8. 异常情况下宁可触发额外 rebuild，也不得错误判定 unchanged。
> 9. Incomplete source observation 默认禁止覆盖已有 Current。
> 10. 删除 `cache.db` 不得影响配置、Current、History 或 Committed Baseline 的正确性。

------

## 我现在对 Milestone 3 的建议顺序

不要先建 EF 表。

应该：

```text
① CHANGE-DETECTION.md
        ↓
② BACKUPPLAN.md
        ↓
③ PERSISTENCE.md
        ↓
④ Domain/Application contracts
        ↓
⑤ SQLite schema
        ↓
⑥ implementation
```

其中这份 **Change Detection & Baseline Commit v1** 我认为已经可以作为第一个正式设计。
