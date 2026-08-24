很好。现在只剩 **External Source v1 完整语义**。这块完成后，我建议不再继续加功能，而是做一次 **Backup Plan v1 Domain Freeze Review**，专门找跨规范冲突；通过后就可以正式进入 JSON Schema。

我建议 External Source 定义成：

> **显式附加输入（Explicit Supplemental Input）**
> 它把 BackupSource 之外的一个本机文件或目录，完整映射到某个 Archive Unit 的指定归档内路径。

而不是把它做成第二套 BackupSource、第二套 Rule Engine 或第二套 Archive Unit discovery。

------

# 1. Portable Declaration

概念模型建议收敛为：

```text
ExternalSourceDeclaration
├─ ExternalSourceId
├─ Name
├─ Kind
│  ├─ File
│  └─ Directory
├─ TargetArchiveUnitId
└─ ArchiveDestination
```

其中：

- `ExternalSourceId`：已有的稳定 UUID v4；
- `Name`：纯显示，不参与备份 fingerprint；
- `Kind`：明确期望本机绑定的是文件还是目录；
- `TargetArchiveUnitId`：它最终进入哪个 Crate；
- `ArchiveDestination`：在该 Crate 内出现在哪里。

物理路径继续只存在：

```text
ExternalSourceId
↓
Device Local Binding
↓
physical file/directory
```

不进入 `*.backupplan`。

------

# 2. Target Archive Unit 必须是 declared unit

我建议这里收紧：

> External Source 只能指向 **Backup Plan 中显式声明的 Archive Unit**。

不能指向：

```text
磁盘临时发现
+
没有 declaration
```

的 FILE_MANAGED unit。

也就是说即使：

```text
Project/.backupignore
@id X
```

已经有稳定 ID，如果用户想给它增加 External Source：

> 先把 X 加入 Plan declaration。

原因是 External Source 本身就是 portable desired configuration。如果目标 Crate 都没有进入 portable declaration，配置审阅会很不清晰。

这也符合已经确定的规则：

> 未声明 FILE_MANAGED 可以使用 Plan defaults，但要拥有 per-unit portable configuration，就必须 declaration。

------

# 3. FILE_MANAGED 完全可以作为目标

例如：

```text
Project/
└─ .backupignore
```

Plan declaration：

```text
ArchiveUnitId = X
RuleSource = FILE_MANAGED
```

External Source：

```text
SSH Config
→ ArchiveUnit X
→ machine/ssh/config
```

最终：

```text
Project.7z
├─ ...
└─ machine/
   └─ ssh/
      └─ config
```

`.backupignore` 继续只管理该 Crate 自身 Source tree 的 Local Rules。

External Source declaration 则由 Backup Plan 管。

没有 authority 冲突。

------

# 4. v1 一个 ExternalSource 对应一个物理 root

不要支持：

```text
glob
*.conf
多个 physical paths
文件集合 expression
```

v1：

```text
1 ExternalSourceId
=
1 Local Binding
=
1 File 或 Directory root
```

多个外部文件就建立多个 ExternalSource。

这样 identity、binding、change detection 都很简单。

------

# 5. `Kind` 必须 portable

例如：

```text
Kind = File
```

本机 binding：

```text
${HOME}/.ssh/config
```

必须解析为真实 regular file。

如果变成目录：

```text
ExternalSourceKindMismatch
```

阻止执行。

同理：

```text
Kind = Directory
```

不能绑定普通文件。

这样 ArchiveDestination 的解释永远确定。

------

# 6. External Source root 本身不能是 Link

继续遵守 no-follow 安全原则。

binding root 必须：

```text
File      → real regular file
Directory → real ordinary directory
```

以下不能作为 External Source root：

```text
SymbolicLink
Junction
MountPoint alias
Special
unknown reparse object
```

不能：

```text
用户绑定 symlink
→ StowCrate 自动解引用 target
```

否则我们刚刚在 Filesystem v1 建立的边界就被绕开了。

------

# 7. Directory 内部仍使用 Filesystem v1 no-follow semantics

例如：

```text
External Directory
├─ a.txt
├─ sub/
├─ link -> somewhere
└─ fifo
```

Scanner 仍记录：

```text
File
Directory
Link
Special
```

并：

- 不跟随 Link；
- 不递归 unknown reparse；
- 不读取 FIFO/socket/device；
- 默认不跨 filesystem boundary；
- ScanIssue / completeness 规则保持一致。

然后 target Archive Unit 的 effective `LinkPolicy` 决定：

```text
Preserve
或
Skip
```

也就是说 Scanner 仍然只记录事实，不根据 LinkPolicy 改枚举。

------

# 8. 但 External Directory 不进行 Archive Unit discovery

这一点必须明确。

例如外部目录：

```text
External/
├─ A/
│  └─ .backupignore
└─ B/
```

这里的：

```text
A/.backupignore
```

只是 ExternalSource payload 中的普通文件。

它：

```text
❌ 不声明新的 Archive Unit
❌ 不建立 Archive Boundary
❌ 不解析成 Local Rules
```

原因：

> Archive Unit discovery 只发生在 BackupSource tree 中。

External Source 是一个显式附加 payload tree。

否则 External Source 会突然变成第二套 BackupSource。

------

# 9. Global / Plan / Local Rules 不过滤 External Source

这是我建议正式定下的一个重要规则：

> **External Source 是显式 inclusion，因此不经过普通 include/exclude Rules。**

例如：

```text
Plan Rule:
*.json → EXCLUDE
```

External Source：

```text
settings.json
→ machine/settings.json
```

它仍然进入归档。

因为用户已经明确声明：

> 把这个文件作为 External Source 加进来。

否则 UI 会非常反直觉：

```text
“我明明添加了 External Source，
为什么规则又把它过滤掉了？”
```

所以最终选择结构是：

```text
Normal source entries
→ Global / Plan / Local Rules

External Sources
→ explicit inclusion
```

之后合并。

------

# 10. External Source 仍受 Safety Policy

绕过的是：

```text
普通 Rules
```

不是安全约束。

External Source 仍受：

- no-follow；
- filesystem boundary；
- reserved namespace；
- destination collision；
- output/Archive boundary；
- incomplete observation；
- TOCTOU；
- archive capability；

等所有安全规则。

------

# 11. ArchiveDestination 的语义要严格区分 File / Directory

### File

```text
Kind = File
ArchiveDestination = "machine/ssh/config"
```

这个路径就是：

> 文件在 archive 内的完整路径。

不自动追加原 basename。

------

### Directory

```text
Kind = Directory
ArchiveDestination = "machine/ssh"
```

假设本机：

```text
~/.ssh/
├─ config
└─ hosts
```

归档：

```text
machine/
└─ ssh/
   ├─ config
   └─ hosts
```

即：

> External root basename 不参与；ArchiveDestination 替代它。

这样跨设备完全确定。

------

# 12. ArchiveDestination 必须非空

v1 不支持：

```text
External Directory
→ 直接展开到 Archive Unit root
```

也不支持：

```text
External File
→ root unnamed entry
```

必须有显式非空 archive-relative path。

这样能显著降低：

- control entry collision；
- Source entry collision；
- restore ambiguity。

------

# 13. ArchiveDestination 使用标准 LogicalPath

规则继续保持：

```text
/
```

作为 separator。

禁止：

```text
absolute path
..
empty segment
\
drive letter
NUL
```

也不能进入：

```text
__stowcrate__/
```

reserved namespace。

例如：

```text
__stowcrate__/foo
```

直接 validation error。

------

# 14. External Source 不能写进 Child Archive Boundary

这个很重要。

假设：

```text
D
└─ F   ← child Archive Unit
```

目标是 D。

不能声明：

```text
External Source
→ D
→ ArchiveDestination = F/external.txt
```

因为：

```text
F/
```

在 D 内已经是 child Archive Boundary。

否则会出现：

```text
D.7z
└─ F/
   └─ external.txt
```

同时：

```text
F.7z
```

又是独立 Crate。

这破坏了 Boundary 的含义。

因此：

> External ArchiveDestination 不能等于或位于目标 Archive Unit 的任何 child Archive Boundary 之下。

用户要把内容加到 F：

> ExternalSource.TargetArchiveUnitId 应直接指向 F。

------

# 15. Destination collision 一律 Fatal

最终候选集合应该：

```text
Normal selected entries
+
External entries
+
Reserved generated entries
```

统一做 path-trie collision validation。

例如普通 Source 已经有：

```text
machine/settings.json
```

External Source 也映射：

```text
machine/settings.json
```

结果：

```text
ArchiveEntryConflict
Fatal
```

不能：

```text
External wins
Source wins
last wins
```

------

# 16. ExternalSource 之间也不能 overlay

例如：

```text
External A
→ config/app.json

External B
→ config/app.json
```

Fatal。

目录也一样。

v1 我建议：

> **不支持 directory overlay / merge。**

如果：

```text
External A directory → config
```

而另一 input 已经拥有：

```text
config/
```

这个 destination root ownership 冲突也应拒绝。

不过：

```text
普通 Source 有 parent `machine/`
External maps `machine/ssh/`
```

可以合法。

也就是说：

> existing parent container 可以共享；同一 archive entry path 不能由两个不同 input owner 提供。

这样比简单“祖先/子孙全部禁止”合理。

------

# 17. Directory metadata collision 也算 collision

即使双方都是：

```text
Directory
```

也不能静默合并。

否则：

```text
权限
mtime
ACL
xattr
```

到底采用哪一边不明确。

所以：

```text
same archive logical path
+
different input owner
=
Fatal
```

无论 entry kind 是否相同。

------

# 18. External Source 默认全部 required

把此前未决正式收口：

> **v1 所有 ExternalSource 都是 required，不提供 `optional` 字段。**

缺少 Local Binding：

```text
PlanNotReady
MissingExternalSourceBinding
```

不能：

```text
“这次先不备份它”
```

否则 Current 会在没有明确用户意图的情况下丢掉之前存在的外部数据。

以后如果需要 Optional Source：

> 必须新设计它对 Current 删除语义、IncompleteObservation 和 baseline 的影响。

不应该现在加一个 bool。

------

# 19. Binding 存在但目标异常

至少区分：

```text
ExternalSourceMissing
ExternalSourceKindMismatch
ExternalSourceUnreadable
ExternalSourceInvalidRoot
```

这些均阻止有效执行。

特别是：

```text
上次存在
这次文件暂时离线
```

绝不能解释为：

```text
“External Source 被删除了”
→ 更新 Current
```

否则会把 Current 中以前备份的内容删掉。

------

# 20. External Directory 中的 IncompleteObservation

继续使用 Change Detection 已确定的原则：

```text
AccessDenied
file disappears
metadata read failure
directory enumeration failure
```

→

```text
IncompleteObservation
```

因此默认：

```text
AllowIncompletePublish = false
```

目标 Archive Unit 不得替换 Current。

其他独立 Archive Unit 是否继续成功，仍遵循既有 per-unit execution/commit 模型。

------

# 21. IntentionalSkip 则继续允许

例如：

```text
LinkPolicy = Skip
filesystem boundary
unsupported Special
```

属于既有：

```text
IntentionalSkip
```

可以 Publish，但：

- Preview 可见；
- result 可见；
- manifest/report 可见。

External Source 不搞另一套 completeness 定义。

------

# 22. External Source content 进入 EntrySetFingerprint

例如 external file：

```text
machine/config
```

其：

```text
logical archive path
kind
size
mtime
metadata
content hash（按 Standard/Strict）
```

进入目标 Unit 的：

```text
EntrySetFingerprint
```

External directory descendants 同理。

因此外部文件 bytes 改变：

```text
EntrySetFingerprint changed
→ RebuildRequired
```

------

# 23. External mapping 进入 SelectionFingerprint

目标 Unit 的 SelectionFingerprint 应包含 External Source 的：

```text
Kind
ArchiveDestination
mapping semantics version
```

以及“本 Unit 存在哪些显式 external mappings”的 canonical set。

但继续遵守已经冻结的规则：

```text
ExternalSourceId ❌
Name             ❌
physical binding ❌
```

不直接进入 SelectionFingerprint。

因此：

```text
External physical path:
C:\foo
→
D:\foo
```

如果扫描后的逻辑数据完全一致：

> 不因为“地址变了”强制 rebuild。

实际 metadata 差异仍由 EntrySetFingerprint 捕获。

------

# 24. TargetArchiveUnitId 不需要直接作为 Unit fingerprint 字段

因为 ExternalSource 是按目标 Unit resolution 后参与该 Unit Candidate：

```text
从 Unit A 移除
→ A 的 external mapping set 变化

加入 Unit B
→ B 的 external mapping set 变化
```

A/B 自然都会得到正确的 Selection change。

不用再通过：

```text
TargetArchiveUnitId
```

这种 identity 强制 bytes rebuild。

继续保持：

> Identity ≠ archive selection semantics。

------

# 25. Name 完全是 display metadata

修改：

```text
"SSH Config"
→
"My SSH Config"
```

只改变：

```text
PlanSemanticFingerprint
```

如果 PlanSemanticFingerprint包含 display metadata（具体可以按既有规范）。

不能改变：

```text
ExecutionSemantic
Selection
EntrySet
ArchiveSpec
```

更不能 rebuild。

------

# 26. Local Binding 进入 ExecutionBindingFingerprint

现有规范已经把 required External physical bindings 纳入 `ExecutionBindingFingerprint`。

继续正式化：

```text
ExternalSourceId
→ physical-canonical path
```

的 resolved binding 参与本次运行 stale check。

运行中：

```text
binding C:\foo
→ D:\foo
```

即使内容相同：

> 本轮任务也不能继续 Publish。

因为它已经不是本轮开始时观察的 input source。

下一轮重新扫描即可。

------

# 27. External Source 不应该有独立 ChangeDetectionMode

它使用目标 Archive Unit / Plan 的既有：

```text
Standard
或
Strict
```

这样同一个 Archive Unit 的最终 EntrySet 只有一套变化检测语义。

不要：

```text
normal source = Strict
external source = Standard
```

增加混合状态。

------

# 28. External Directory `.backupignore` 作为普通 payload

前面提到它不作为 rule source，还要明确：

如果 External Directory 内真实存在：

```text
.backupignore
```

它默认与普通 payload 文件一样进入 archive。

但目标 Archive Unit 根路径：

```text
.backupignore
```

属于特殊控制位置。

因此 v1 建议禁止：

```text
External File
ArchiveDestination = ".backupignore"
```

或 Directory root 导致目标 Unit 根 `.backupignore` 被 external owner 占用。

这是 control-entry safety。

------

# 29. External Sources 运行时使用 private staging

继续保留 PRODUCT 已有方向：

```text
Physical External Input
        ↓
validated no-follow observation
        ↓
run-scoped private staging
        ↓
archive logical mapping
        ↓
IArchiveWriter
```

真实 External Source：

```text
永远只读
```

StowCrate 不允许：

```text
rename
临时写文件
生成 manifest
改 metadata
```

污染真实路径。

------

# 30. Staging 不是 backup state

Staging：

```text
❌ 不进入 Portable Plan
❌ 不进入 baseline
❌ 不成为 ArchiveVersion
❌ 不成为 Current
```

崩溃后残留：

```text
stale staging
```

只能：

```text
cleanup / diagnostics
```

不能当作有效备份恢复。

------

# 31. Staging 必须保留原始语义 metadata

不能因为复制到 staging：

```text
mtime → 当前时间
permissions → staging 默认权限
```

然后让它影响：

```text
EntrySetFingerprint
Archive bytes
```

正确模型是：

```text
External observed metadata
+
staged bytes
↓
ArchiveEntry semantics
```

staging 的实现 metadata 不是业务输入。

如果需要，执行层必须保存原 metadata side-data 或在 staging 上忠实恢复。

------

# 32. Staging placement 不能形成递归

run staging 物理目录必须保证：

```text
不位于 SourceRoot
不位于任一 ExternalSource input tree
不被 Scanner 当输入
不与有效 Current/History artifact namespace 冲突
```

具体放：

- OS temp；
- app data；
- destination sibling；

哪一个以后由实现选择。

但必须做 overlap validation。

------

# 33. Staging 不能削弱 TOCTOU

不要：

```text
scan external
↓
过十分钟直接 copy
↓
假装还是 scan 时的数据
```

执行 materialization 需要按 `FILESYSTEM.md` 的原则重新验证：

```text
path
entry kind
metadata identity
```

如果发生：

```text
File → Link
Directory → Junction
```

必须安全失败。

Copy 过程中出现不完整观察：

```text
IncompleteObservation
```

不能继续发布。

------

# 34. Staging 后的 Candidate 必须对应真正被归档的数据

这是 External Source 最重要的执行正确性规则之一。

无论实现采用：

```text
scan → stage → final validation
```

还是其他等价策略：

> 最终用于 `EntrySetFingerprint` 和写 archive 的 ExternalSource entry state，必须与实际 staged payload 对应。

不能：

```text
fingerprint 原文件版本 A
archive 实际写入版本 B
```

具体如何实现可以留给 Application/Infrastructure milestone。

------

# 35. External Sources 的 canonical ordering

External declarations/resolved mappings 不依赖 JSON 数组原始顺序。

在需要集合型 canonicalization 时至少按：

```text
Target Archive Unit
ArchiveDestination
Kind
```

等真正语义字段稳定排序。

`ExternalSourceId` 可以用于稳定内部 tie-break/diagnostic，但不应该使纯 identity 改变变成 archive semantic change。

实际同 destination 已在 canonicalization 前作为 conflict 拒绝。

------

# 36. Manifest 不保存 physical external path

以后 manifest 可以表达：

```text
该 artifact 创建时包含 external input
logical destination
kind
logical/provenance identity
```

但绝不能保存：

```text
C:\Users\xxx\Secrets\...
/home/foo/.ssh/...
```

这种 Device Local Binding。

这符合现有 portable/privacy 边界。

具体 manifest 字段仍留给 Archiving milestone。

------

# 37. Pure identity migration 不要求重写旧 artifact

这里顺手解决一个容易潜伏的问题。

假设显式 identity migration：

```text
ExternalSourceId A → B
```

而：

```text
Kind
ArchiveDestination
physical content
```

完全一样。

因为 ExternalSourceId 已正式排除 SelectionFingerprint，所以：

> 不要求仅为了改 ID 重建 archive。

如果旧 manifest 记录 A：

> 它代表 artifact 创建时的 provenance。

当前 runtime/config 可以维护 identity migration。

同样适用于此前的 ArchiveUnitId 原则。

这样不会重新引入“ID 改了 → 所有大归档重压”的问题。

------

# 38. External Source removal

如果从 Plan 删除：

```text
ExternalSource X
```

目标 Archive Unit 的：

```text
SelectionFingerprint
EntrySetFingerprint
```

都会反映 external entries 消失：

```text
RebuildRequired
```

但是根据已经冻结的 Update 规则：

```text
ExternalSource binding/runtime metadata
```

不能因为 document update 就 destructive purge；可以保留 inactive state，之后显式 cleanup。

------

# 39. 新 External Source 没 binding

Update/Import 本身可以成功。

随后：

```text
PlanNotReady
MissingExternalSourceBinding
```

这与 Source/Secret/History 的设计完全一致：

```text
Portable configuration validity
≠
Device readiness
```

------

# 40. Clone 继续不复制 External binding

Clone：

```text
ExternalSourceId
→ 全新 UUID
```

TargetArchiveUnitId 内部引用同步改写。

但：

```text
External physical binding ❌
```

不复制。

因此包含 External Sources 的 Clone：

> 在重新绑定前自然是 PlanNotReady。

------

# 41. v1 明确不支持这些能力

建议全部列为 future：

```text
optional external source
glob / wildcard binding
multiple physical roots per declaration
external include/exclude rules
external .backupignore semantics
external nested Archive Units
follow links
archive overlay / merge
transform/rename pipeline
command-generated external content
remote URL / HTTP source
cloud object source
database dump hook
pre-backup scripts
```

特别是：

```text
command-generated source
```

不要混进 ExternalSource v1。

那属于未来：

> Generated Source / Hook

安全模型完全不同。

------

# 42. External Source 最终进入流程的位置

建议正式画成：

```text
Backup Source
→ SourceScanner
→ Archive Unit discovery
→ Rules
→ selected normal entries ───────┐
                                  │
ExternalSource declarations       │
→ Local Binding                   │
→ no-follow external observation  │
→ explicit inclusion              ├─→ collision/boundary validation
→ private staging                 │
                                  │
Generated manifest metadata ──────┘
                                  ↓
                         Candidate Archive Unit
                                  ↓
                         Change Detection
                                  ↓
                           IArchiveWriter
```

这张图能很好地说明：

> ExternalSource 不是普通规则扫描出来的条目，但最后和普通 entry 一起形成 Candidate。

------

# 43. Fingerprint 分工最终可以定成

| External Source 属性    | PlanSemantic | ExecutionSemantic / Binding | Selection            | EntrySet          |
| ----------------------- | ------------ | --------------------------- | -------------------- | ----------------- |
| Name                    | 可是         | 否                          | 否                   | 否                |
| ExternalSourceId        | 是           | identity only               | 否                   | 否                |
| Kind                    | 是           | 是                          | 是                   | 通过 entry kind   |
| Target Unit association | 是           | 是                          | 通过 unit membership | 间接              |
| ArchiveDestination      | 是           | 是                          | 是                   | logical paths     |
| Physical binding        | 不属于 Plan  | ExecutionBinding            | 否                   | 内容/metadata另算 |
| 文件 bytes/metadata     | 否           | observed input              | 否                   | 是                |

这里 `ExternalSourceId` 的“是”只表示它属于完整 Plan desired identity，不代表 archive bytes semantic。

------

## 任务

本轮**只修改文档**，正式确定：

1. ExternalSource v1 是 explicit supplemental input，不是 BackupSource/Rule Source。
2. declaration 固定为稳定 ID、display name、`File/Directory Kind`、TargetArchiveUnitId、非空 ArchiveDestination。
3. Target 必须是 declared Archive Unit；FILE_MANAGED 可以作为目标。
4. 一个 ExternalSource 只有一个本机 physical root；v1 不支持 glob/multi-root/optional。
5. physical root 必须是真实 regular file/ordinary directory，不能是 Link/Special；directory 内按 FILESYSTEM v1 no-follow 扫描。
6. External directory 内 `.backupignore` 只是 payload，不参与 ArchiveUnit discovery/Local Rules。
7. ExternalSource 是 explicit inclusion，绕过 Global/Plan/Local Rules，但不绕过 Safety/LinkPolicy/Boundary/Completeness。
8. File destination 表示完整 archive file path；Directory destination 表示映射根；不隐式追加 basename。
9. External destination 不得进入 `__stowcrate__`、目标 root control entry 或任何 child Archive Boundary。
10. Normal entry / External / generated entry 之间任何 ownership collision Fatal；v1 不支持 directory overlay。
11. ExternalSource v1 全部 required；Missing binding → PlanNotReady，missing/unreadable/kind mismatch 不得被解释成“文件删除”。
12. external data 使用同一 Standard/Strict change detection，内容进入 EntrySetFingerprint，logical mapping 进入 SelectionFingerprint；ID/Name/physical binding不进入 archive fingerprints。
13. physical external binding 纳入 ExecutionBindingFingerprint；运行中 binding drift 阻止 Publish。
14. ExternalSource 必须通过 run-scoped private staging，原路径永远只读；staging 不得污染 fingerprint metadata，也不能形成 recursive input。
15. staging/materialization 必须执行 TOCTOU revalidation，最终 fingerprint state 必须对应真正写入 archive 的 payload。
16. Pure ExternalSourceId migration 不要求重建 artifact；manifest physical path 禁止记录。
17. 删除/new/clone ExternalSource 继续遵循现有 whole-document Update 与 Local Binding 生命周期。
18. 不创建 JSON Schema、不实现 staging/SQLite/ExternalSource code。

完成这块以后，我建议 **不要立刻生成 Schema**。先做一个最后的 `Backup Plan v1 Domain Freeze Review`，横向检查：

```text
BACKUPPLAN
BACKUPIGNORE
CHANGE-DETECTION
FILESYSTEM
PRODUCT
ARCHITECTURE
```

重点找：

- fingerprint 字段互相矛盾；
- PlanSemantic / ExecutionSemantic / ExecutionBinding 分类矛盾；
- FILE_MANAGED authority 冲突；
- required binding/readiness 冲突；
- ArchiveSpec/History/ExternalSource override 继承矛盾；
- Current/History publish 顺序冲突；
- “文档说支持但领域模型没有落点”的遗漏。

**Freeze Review 无 blocker 后，下一步就可以正式设计 `backupplan-v1.schema.json`。**