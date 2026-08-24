下一项就应该正式设计 **History / Output Portability v1**。现有架构已经确定：Current 是唯一当前有效版本；启用 History 时必须先把旧 Current 持久化并验证成功，才能替换 Current；而 `CurrentRoot / HistoryRoot` 属于设备本地 binding。

我建议这次把它拆成五层：

```text
Portable Output Layout
Portable History Policy
        ↓
Device Storage Binding
        ↓
Current / History Publish
        ↓
ArchiveVersion Runtime State
```

# 1. Current 与 History 的基本定位

正式定义：

### Current

> 每个 Archive Unit 最多只有一个当前有效归档。

用于：

- 日常浏览；
- OneDrive/NAS/网盘同步；
- 恢复当前版本；
- Change Detection 的 Committed Baseline 对应物。

因此 Current 必须是：

```text
稳定路径
+
标准归档文件
+
不混入旧版本
```

### History

> 保存被新 Current 替代的旧 Current。

History 不是第二套 Current，也不是同步镜像。

```text
Current = 最新状态
History = 已被替换的历史状态
```

这一点继续保持现在的架构。

------

# 2. `CurrentRoot / HistoryRoot` 永远是 Local Binding

继续维持已经确定的规则：

```text
*.backupplan
❌ D:\Backup
❌ E:\History
❌ /Volumes/NAS/Backup
```

Portable Plan 只引用逻辑 storage slot：

```text
Current
History
```

设备本地：

```text
PlanId + DeviceId
├─ CurrentRoot → D:\Backup\Code
└─ HistoryRoot → E:\History\Code
```

所以同一 Plan：

```text
Windows:
Current → D:\Backup

macOS:
Current → /Volumes/Backup
```

完全正常。

------

# 3. CurrentRoot 是必需 binding

只要 Plan 可以执行：

```text
CurrentRoot
```

必须存在。

否则：

```text
PlanNotReady
Reason = MissingCurrentRootBinding
```

HistoryRoot 则只在：

```text
至少一个 Archive Unit
effective History = Enabled
```

时成为 required。

否则可以没有 HistoryRoot。

------

# 4. Portable Plan 应该保存“逻辑输出布局”

物理 root 不 portable，但：

> CurrentRoot 里面归档应该怎么组织

是 portable desired configuration。

我建议每个 BackupSource 增加：

```text
SourceOutputPath
```

例如：

```text
Source
Name = "代码"
SourceOutputPath = "Code"
```

另一个：

```text
Name = "我的资料"
SourceOutputPath = "Documents"
```

于是：

```text
CurrentRoot/
├─ Code/
└─ Documents/
```

------

# 5. 不要拿 Source Name 当输出路径

因为前面已经定义：

```text
Name = display metadata
```

如果：

```text
"代码"
→
"我的代码"
```

只是改显示名称，不应该把 Current 全部移动。

所以必须：

```text
SourceId
Name
SourceOutputPath
```

三个概念分开。

其中：

```text
SourceId          = identity
Name              = display
SourceOutputPath  = output layout
```

------

# 6. Archive Unit 默认镜像逻辑结构

推荐 Current 的逻辑映射继续符合项目最初设计：

```text
SourceOutputPath = A

Archive Unit:
B
C/D
C/D/F
```

Current：

```text
A/
├─ B.7z
└─ C/
   ├─ D.7z
   └─ D/
      └─ F.7z
```

也就是说：

> ArchiveUnit logical path 决定归档在 Current 中的结构位置。

只把 Archive Unit 最后一个目录节点换成：

```text
<name>.<archive-format>
```

------

# 7. v1 不做任意 filename template

暂时不要支持：

```text
${source}-${unit}-${date}.7z
{plan}/{date}/{unit}
```

否则马上引入：

- template language；
- escaping；
- collision；
- timezone；
- rename；
- portability。

v1 使用：

> **固定、确定性 Current layout。**

History 自己处理 version naming。

------

# 8. SourceOutputPath 是 portable，但不影响 archive bytes

例如：

```text
Code
→
Development
```

文件、规则、压缩参数全部没变。

结果应该：

```text
ArchiveSpecFingerprint unchanged
SelectionFingerprint unchanged
EntrySetFingerprint unchanged
```

不重新压缩。

而是：

```text
OutputReorganizationRequired
```

把已有 Current 安全迁移到新逻辑位置。

所以建议引入：

```text
OutputLayoutFingerprint
```

它描述：

```text
SourceOutputPath
output mapping semantics version
```

但不属于三类 archive fingerprint。

------

# 9. CurrentRoot 改变也不能触发 rebuild

同样：

```text
D:\Backup
→
E:\Backup
```

只是：

```text
StorageRelocation
```

而不是：

```text
ArchiveRebuild
```

只要已有 Current：

```text
copy / move
→ verify SHA-256
→ atomic publish in new root
→ update binding
```

原 ArchiveVersion 和 baseline 可以保持不变。

------

# 10. 已经存在 Current 后，禁止直接修改 binding 指针

这是非常重要的一条。

不能：

```text
CurrentRoot = D:\Backup
↓
用户改为 E:\Backup
↓
UPDATE config.db
```

然后结束。

否则数据库认为 Current 在：

```text
E:\Backup
```

但实际文件还在：

```text
D:\Backup
```

应该进入：

```text
StorageRelocationRequired
```

然后执行受控 relocation。

只有：

```text
新位置文件完整
+
hash 验证正确
```

以后才能提交新的 binding。

------

# 11. HistoryRoot 同样不能简单改路径

如果：

```text
HistoryRoot
D:\History
→
E:\History
```

而已有历史：

```text
D:\History
├─ v1
├─ v2
└─ v3
```

不能直接改 binding。

v1 建议只支持：

> **完整受控 History relocation。**

即：

```text
copy History versions
↓
verify
↓
commit new HistoryRoot
↓
旧位置才允许清理
```

不要在 v1 引入：

```text
“旧 history 留 D:
新 history 写 E:”
```

这种 multi-store History。

否则 ArchiveVersion location 模型会复杂很多。

------

# 12. History Policy 是 Portable Configuration

建议：

```text
HistoryPolicy
├─ Enabled
└─ RetentionPolicy
```

属于 `*.backupplan`。

但：

```text
HistoryRoot
```

不属于。

这符合：

```text
Portable:
我要不要保留历史，以及保留多少

Local:
这台机器把历史放在哪里
```

------

# 13. History 建议支持 Plan Default + Unit Override

例如：

```text
Plan default:
History = Enabled
Retention = KeepLast(10)
```

然后：

```text
Photos:
Inherit

Code:
KeepLast(5)

HugeVM:
Disabled
```

这也正好适配现在已经留下的：

```text
PortableOverrides
```

设计。

未 declaration 的 FILE_MANAGED Archive Unit：

> 使用 Plan default。

要设置 per-unit override：

> 必须先 declaration。

与前一个 P0 完全一致。

------

# 14. v1 Retention 建议只做两个模式

先控制范围：

```text
RetentionPolicy
├─ KeepAll
└─ KeepLastVersions(N)
```

其中：

```text
N >= 1
```

不要第一版就做：

```text
每日7份
每周4份
每月12份
每年永久
```

这些以后可以扩展。

`KeepLastVersions(1)`：

> 只保留上一个 Current。

------

# 15. History Disabled 不等于删除历史

这是必须写死的。

例如：

```text
History Enabled
↓
已经有 20 个版本
↓
用户改成 Disabled
```

行为：

```text
停止创建新的 History
```

绝不能：

```text
删除现有 20 个历史版本
```

删除 History 必须是独立 destructive operation：

```text
Purge History
```

需要明确确认。

------

# 16. History Version 何时创建

只有：

```text
Archive Unit changed
+
存在 old Current
+
History Enabled
```

才创建一个 History Version。

所以：

```text
第一次备份
→ 没有 History

Unchanged
→ 没有新 History

Changed
→ old Current 成为 History
```

符合现有 Current/History 模型。

------

# 17. 正式 Publish 顺序

继续保持当前正确的链路：

```text
New archive.partial
↓
Archive test
↓
SHA-256 verify
↓
History enabled?
│
├─ NO
│
└─ YES
     persist old Current to History temp
     ↓
     verify History copy
     ↓
     atomically publish History
↓
Atomic replace Current
↓
config.db durable ArchiveVersion commit
↓
baseline committed
↓
Retention maintenance
```

History capture 和 Retention 要分开。

------

# 18. History Capture 失败必须阻止 Current

例如：

```text
History Enabled
HistoryRoot disk full
```

旧 Current 无法安全保留下来。

那么：

```text
❌ 不允许覆盖 Current
```

任务失败，旧 Current 保持原位。

这和现有架构完全一致。

------

# 19. 但 Retention Cleanup 失败不应该回滚新 Current

例如：

```text
KeepLast(10)

当前已有 11 份
删除最旧版本失败
```

如果：

```text
old Current 已安全进入 History
new Current 已安全发布
config.db 已 durable commit
```

那么：

```text
Current backup = Success
Retention = Warning / OutOfSync
```

不要为了：

> 多留了一份 History

把一次有效备份判成失败。

因此建议：

```text
BackupStatus = SuccessWithWarnings
HistoryMaintenanceStatus = OutOfSync
```

------

# 20. Retention 一定在 Current durable commit 之后

不要：

```text
先删历史
↓
再做本次备份
```

作为正常事务链。

否则为了备份腾空间，可能先丢掉恢复版本，随后新备份又失败。

v1 默认安全策略：

> **先成功增加一个可靠版本，再删除过期版本。**

磁盘不足就阻止本轮备份，并明确提示。

后续可以做用户显式的空间回收操作。

------

# 21. Retention 不进入任何 archive fingerprint

例如：

```text
KeepLast(10)
→
KeepLast(5)
```

不能重新压缩。

因此：

```text
❌ EntrySetFingerprint
❌ SelectionFingerprint
❌ ArchiveSpecFingerprint
```

都不变化。

但：

```text
PlanSemanticFingerprint
```

应该变化。

并产生：

```text
HistoryMaintenanceRequired
```

------

# 22. History Enabled 则不同

```text
History Enabled
```

虽然不改变 archive bytes，但会改变：

> **Current 是否允许被替换。**

因此它属于：

```text
ExecutionSemanticFingerprint
```

例如任务开始：

```text
History = Disabled
```

执行过程中改为：

```text
History = Enabled
```

这次任务没有保存 old Current。

因此绝不能继续 Publish。

应该：

```text
ExecutionSemanticFingerprint changed
→ PlanChangedDuringRun
→ 不发布
```

------

# 23. Retention 变化则不阻止当前 Publish

执行中：

```text
KeepLast 10
→
KeepLast 5
```

不会改变：

- 扫描结果；
- archive bytes；
- old Current 是否需要保存。

所以：

> 不废弃已经生成几小时的新归档。

当前 Publish 可以完成。

如果 Publish 前发现 Retention policy 变化：

```text
跳过本轮 retention cleanup
+
HistoryMaintenanceOutOfSync
```

下次根据最新 policy 清理。

这个行为最安全。

------

# 24. Output Layout 变化应该阻止 Publish

例如运行开始：

```text
SourceOutputPath = Code
```

执行过程中改成：

```text
SourceOutputPath = Development
```

如果继续执行，会把 Current 发布到旧位置。

所以：

```text
OutputLayoutFingerprint
```

属于：

```text
ExecutionSemanticFingerprint
```

但不属于 archive rebuild fingerprints。

------

# 25. Local Storage Binding 变化也必须被 stale-check

这里建议新增一个概念：

```text
ExecutionBindingFingerprint
```

因为：

```text
CurrentRoot
HistoryRoot
SourceRoot
External Source physical paths
```

都不属于 portable PlanSemanticFingerprint。

但运行过程中改变：

```text
CurrentRoot D:
→
CurrentRoot E:
```

显然不能继续往旧路径 Publish。

因此：

```text
ExecutionSemanticSnapshot
├─ ExecutionSemanticFingerprint
├─ ExecutionBindingFingerprint
├─ External rule source fingerprints
└─ SecretSlotId + SecretRevision
```

这样职责很清楚。

------

# 26. `ExecutionBindingFingerprint` 包含什么

至少：

```text
resolved physical SourceRoot
resolved CurrentRoot
effective HistoryRoot
required ExternalSource physical bindings
storage semantics version
```

使用：

```text
physical canonical path
```

而不是用户原始字符串。

例如：

```text
${HOME}/Backup
```

和：

```text
C:\Users\User\Backup
```

如果解析为同一 physical path：

> fingerprint 相同。

------

# 27. Binding fingerprint 不成为 durable baseline

这一点很重要。

它只用于：

> **一次运行期间的一致性检查。**

它不进入：

```text
EntrySetFingerprint
SelectionFingerprint
ArchiveSpecFingerprint
Committed Baseline
```

所以换硬盘后，只要数据一样：

> 不要求重新生成归档。

------

# 28. Current 物理布局应该是稳定且干净的

建议 CurrentRoot：

```text
CurrentRoot/
├─ Code/
│  ├─ StowCrate.7z
│  └─ OtherProject.7z
└─ Documents/
   └─ ...
```

不要：

```text
CurrentRoot/
├─ current-v1
├─ current-v2
├─ database
├─ logs
└─ ...
```

因为 Current 的目标之一就是：

> 直接交给同步软件/NAS。

`.partial` 只能是临时状态，不能被视为 Current。

------

# 29. History 物理布局不需要成为 Portable Plan 语义

这里我建议和 Current 不同。

History 内部：

```text
timestamp
ArchiveVersionId
目录布局
```

应该由 StowCrate 管理。

`*.backupplan` 不提供：

```text
historyFilenameTemplate
historyDirectoryTemplate
```

v1 不需要。

只要求：

- 每个 HistoryVersion 是独立标准归档；
- 文件名不会碰撞；
- 可以从 manifest / config 重建身份；
- 可以不用 StowCrate 直接用 7-Zip 等打开。

------

# 30. ArchiveVersion 不应该保存 absolute path 作为身份

未来 `config.db` 更适合保存：

```text
ArchiveVersion
├─ VersionId
├─ ArchiveUnitId
├─ StorageSlot = Current | History
├─ RelativeStoragePath
├─ SHA256
├─ Size
├─ PublishedAt
└─ ...
```

而不是：

```text
D:\Backup\Code\A.7z
```

作为 version identity。

这样：

```text
StorageRoot relocation
```

以后 ArchiveVersion 不需要全部重写身份。

实际路径：

```text
StorageBinding.Root
+
RelativeStoragePath
```

得到。

------

# 31. History relocation 不生成新 ArchiveVersion

例如：

```text
D:\History
→
E:\History
```

只是 storage relocation。

已有：

```text
Version V1
Version V2
Version V3
```

仍然是同三个版本。

不能因为复制了一遍文件就变：

```text
V4 V5 V6
```

Version identity 表示：

> 备份历史版本

而不是：

> 物理文件副本。

------

# 32. Current relocation 也不推进 Baseline

同样：

```text
D:\Backup\A.7z
→
E:\Backup\A.7z
```

如果：

```text
SHA-256 identical
```

它仍然是同一个 Current ArchiveVersion。

因此：

```text
Committed Baseline unchanged
```

只更新 storage binding/location。

------

# 33. Plan import 到新设备的 readiness

Portable Plan：

```text
history = enabled
```

新设备 Import/Register 后：

```text
CurrentRoot 未绑定
HistoryRoot 未绑定
```

状态：

```text
PlanNotReady
```

绑定：

```text
CurrentRoot ✅
HistoryRoot ✅
```

之后才 Ready。

如果：

```text
history = disabled
```

则只要求：

```text
CurrentRoot ✅
```

------

# 34. Clone 不复制 storage state

Clone Plan：

```text
Portable History Policy ✅ copy

CurrentRoot binding ❌
HistoryRoot binding ❌
ArchiveVersion ❌
CurrentVersion ❌
HistoryVersion ❌
Baseline ❌
```

Clone 是全新的运行实例。

------

# 35. 输出冲突必须在写 `.partial` 前发现

例如两个 Archive Unit 最终映射到：

```text
Code/foo.7z
```

或者在 case-insensitive Current filesystem 上：

```text
Foo.7z
foo.7z
```

都必须：

```text
OutputPathConflict
Fatal planning/execution validation
```

不能：

> 后写的覆盖先写的。

目的文件系统的真实 case semantics 必须参与验证。

------

# 36. 输出路径编码必须 deterministic

还有一个跨平台问题：

Unix Source 允许某些 Windows 不允许的文件名。

所以：

```text
Logical Output Path
↓
Destination-safe physical path
```

之间需要一个：

```text
OutputPathEncoding
```

要求：

- deterministic；
- reversible/可诊断；
- 无碰撞；
- versioned；
- 不受 locale 影响。

**具体 `%xx` 还是别的编码现在可以不定。**

但 `OutputPathEncodingVersion` 应属于：

```text
OutputLayoutFingerprint
```

编码版本改变：

```text
StorageReorganizationRequired
```

而不是 archive rebuild。

------

# 37. 多 Plan 输出根建议禁止重叠

我建议 v1 安全规则再收紧：

在同一个 DeviceId 下：

```text
Active SourceRoots
Active CurrentRoots
Active HistoryRoots
```

应进行全局 overlap 检查。

至少禁止：

```text
Plan A CurrentRoot
位于
Plan B SourceRoot 内
```

否则很容易形成：

```text
A 备份进 B
B 又备份 A
```

甚至指数增长。

同样：

```text
两个 Plan 的 CurrentRoot
```

不要允许相同或祖先/子孙关系。

如果用户想都放：

```text
D:\Backup
```

应该绑定：

```text
Plan A → D:\Backup\PlanA
Plan B → D:\Backup\PlanB
```

这是更安全的 v1。

------

# 38. 建议 History 默认行为

为了避免首次配置就强迫用户准备第二个存储路径，我建议：

```text
History = Disabled by default
```

用户明确开启后：

```text
选择 HistoryRoot
+
选择 Retention
```

再开始版本历史。

而不是默认：

```text
KeepAll
```

导致磁盘无限增长。

如果启用 History，我建议要求显式 Retention：

```text
KeepAll
或
KeepLastVersions(N)
```

不要隐式猜一个 N。

------

# 39. Fingerprint 最终分工

这一轮可以正式得到：

| 配置                    | PlanSemantic | ExecutionSemantic       | Archive fingerprints |
| ----------------------- | ------------ | ----------------------- | -------------------- |
| SourceOutputPath        | ✅            | ✅                       | ❌                    |
| History Enabled         | ✅            | ✅                       | ❌                    |
| Retention Policy        | ✅            | ❌                       | ❌                    |
| CurrentRoot binding     | 不属于 Plan  | 通过 ExecutionBinding ✅ | ❌                    |
| HistoryRoot binding     | 不属于 Plan  | 启用时通过 Binding ✅    | ❌                    |
| History physical layout | ❌            | ❌                       | ❌                    |
| Archive format          | ✅            | ✅                       | ArchiveSpec ✅        |
| Compression             | ✅            | ✅                       | ArchiveSpec ✅        |

这张表建议直接进入正式规范。

------

# 40. 最终状态模型

可以形成：

```text
Plan
├─ OutputLayout
│  └─ SourceOutputPath
│
├─ HistoryDefaults
│  ├─ Enabled
│  └─ Retention
│
└─ ArchiveUnitOverrides
   └─ HistoryOverride?

Device Local State
├─ CurrentRootBinding
├─ HistoryRootBinding?
├─ StorageRelocationState
└─ HistoryMaintenanceState

Runtime
├─ Current ArchiveVersion
├─ History ArchiveVersions
├─ ExecutionBindingFingerprint
└─ Baseline
```

这样 SQLite 以后会非常清晰。

------

## 建议这轮要固化

只改文档，完成这些决定：

1. Current/History portable policy 与 physical binding 分离。
2. 引入 portable `SourceOutputPath`，不使用 Source display name 推导输出。
3. Current 使用确定性逻辑 mirror layout，v1 不支持 filename/path template。
4. CurrentRoot 永远 required；HistoryRoot 仅 effective History enabled 时 required。
5. History policy portable，支持 Plan default + declared Archive Unit override。
6. v1 retention 只支持 `KeepAll / KeepLastVersions(N)`。
7. Disabled 不删除既有 History。
8. History capture 是 Current publish 的前置安全步骤；失败阻止 Publish。
9. Retention maintenance 位于 Current durable commit 之后，失败只产生维护 warning/out-of-sync。
10. History Enabled / OutputLayout 进入 ExecutionSemanticFingerprint；Retention 不进入。
11. 引入 `ExecutionBindingFingerprint`，覆盖运行所依赖的本机 storage/source bindings。
12. Current/History relocation 不 rebuild、不创建新 ArchiveVersion、不推进 baseline，必须 copy/verify 后原子提交 binding。
13. ArchiveVersion 使用 StorageSlot + relative storage path，而不是 absolute path 作为持久身份。
14. Output collision 在 `.partial` 前 Fatal。
15. output path encoding 必须 deterministic/versioned，具体算法暂不固定。
16. 同设备不同 Plan 的 writable roots/source roots增加全局 overlap 安全检查。
17. 继续**不定义 JSON Schema / SQLite Schema / Entity**。

这项完成以后，剩余两个 Backup Plan P0——**Schema compatibility / unknown fields** 和 **Import identity conflict / merge semantics**——就可以收尾。然后我认为才真正到了定义 `*.backupplan v1 JSON Schema` 的时机。