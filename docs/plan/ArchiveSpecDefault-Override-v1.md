很好。现在进入 **ArchiveSpec Default / Override v1**。这一项我建议顺便把一个潜在问题收紧：目前规范里曾把 `algorithm / level / solid mode / volume size` 都当成 ArchiveSpecFingerprint 输入，但这些不一定都应该成为 v1 的 portable 用户配置。尤其“分卷”会直接打破目前“一 Archive Unit → 一个 Current 标准归档文件”的输出模型。

我建议 v1 先把 ArchiveSpec 做成**稳定的跨格式意图层**，不要把 7-Zip 参数暴露进 Backup Plan。

## 1. 两层 ArchiveSpec

正式模型建议：

```text
Plan
└─ ArchiveSpecDefault   # 必须是完整配置

Declared ArchiveUnit
└─ ArchiveSpecOverride? # 可选、逐组件继承
```

最终：

```text
EffectiveArchiveSpec
=
Resolve(
    Plan.ArchiveSpecDefault,
    ArchiveUnit.ArchiveSpecOverride
)
```

未 declaration 的 FILE_MANAGED Archive Unit：

```text
→ 直接使用 Plan ArchiveSpecDefault
```

这与 History override 的模型保持一致。

------

## 2. v1 ArchiveSpec 只暴露三个 portable 组件

建议：

```text
ArchiveSpec
├─ Format
├─ CompressionPreset
└─ ProtectionConfiguration
```

其中 Protection 已经设计完成。

### Format

建议 v1 portable enum：

```text
SevenZip
Zip
TarZstd
```

具体某个平台/版本有没有 adapter 支持，是：

```text
Capability Validation
```

问题，而不是 Schema validity 问题。

### CompressionPreset

建议：

```text
Store
Fast
Standard
Extreme
```

默认：

```text
Standard
```

用户表达的是：

> 我要什么压缩倾向。

而不是：

```text
LZMA2
dictionary=128M
threads=12
solid=on
Deflate level=9
zstd level=19
```

这些都交给 adapter。

------

# 3. 不把底层算法参数写入 `*.backupplan v1`

v1 禁止 portable ArchiveSpec 出现：

```text
algorithm
dictionarySize
wordSize
solidBlockSize
threadCount
lzmaLevel
deflateLevel
zstdLevel
7zzRawArguments
```

否则 Backup Plan 会立刻与：

```text
7zz CLI
某个特定版本
某种 archive backend
```

绑定。

正确关系是：

```text
Format + CompressionPreset
        ↓
Archive adapter
        ↓
ResolvedArchiveParameters
```

具体映射由版本化的：

```text
ArchiveSemanticsVersion
```

固定。

------

# 4. 为什么一定要有 ArchiveSemanticsVersion

例如今天：

```text
SevenZip + Standard
→ LZMA2 level X
```

未来升级后算法参数优化。

如果没有版本约束，相同 `.backupplan`：

```text
SevenZip + Standard
```

可能悄悄产生不同 archive semantics。

所以必须满足：

> 同一个已支持的 ArchiveSemanticsVersion，其 Format + CompressionPreset 含义永久稳定。

以后真正修改 preset mapping：

```text
ArchiveSemanticsVersion 1 → 2
```

由对应版本参与：

```text
ArchiveSpecFingerprint
```

但不要拿：

```text
StowCrate 2.5.1
```

版本号代替它。

------

# 5. Plan-level ArchiveSpecDefault 应 required

我建议不要依赖：

```text
“没写就使用当前 App 默认”
```

这种模式。

Portable Plan 应始终能够回答：

> 默认生成什么格式的归档？

因此创建 Plan 时，StowCrate 可以自动填：

```text
SevenZip
Standard
None
```

但一旦形成 Backup Plan Document：

> ArchiveSpecDefault 就是其 portable desired configuration。

这样未来应用默认值变化不会改变旧 Plan。

------

# 6. Unit Override 使用“逐组件继承”

我不建议整个 ArchiveSpec 二选一：

```text
InheritAll
ReplaceAll
```

因为用户最常见需求会是：

```text
全局：
7z + Standard + Secure

某个大目录：
7z + Fast + Secure
```

如果 ReplaceAll，就必须把完全相同的 Format/Protection 再复制一遍。

更好的领域模型：

```text
ArchiveSpecOverride
├─ Format: inherit | explicit
├─ CompressionPreset: inherit | explicit
└─ Protection: inherit | explicit
```

例如：

```text
Plan Default
SevenZip
Standard
Secure(S1)

Unit F Override
CompressionPreset = Fast
```

最终：

```text
F:
SevenZip
Fast
Secure(S1)
```

------

# 7. 但 Override 是声明式继承，不是 Merge

注意这和刚刚冻结的 Import “不支持 Merge”完全不冲突。

这里的：

```text
override
```

是 Backup Plan 本身明确设计的领域继承机制：

```text
Plan Default → Unit Effective
```

不是：

```text
Existing document + Incoming document
```

的配置 merge。

要在规范里明确区分这两个词。

------

# 8. Inherit 与“显式写相同值”不是完全相同的 Plan 语义

这是一个比较重要的细节。

例如：

```text
Plan Default = Standard
```

Unit A：

```text
Compression = inherit
```

Unit B：

```text
Compression = explicit Standard
```

本轮：

```text
Effective A = Standard
Effective B = Standard
```

所以：

```text
ArchiveSpecFingerprint(A)
=
ArchiveSpecFingerprint(B)
```

如果其他因素相同。

但是从 desired configuration 来看：

```text
A 会跟随以后 Plan Default 改动
B 不会
```

所以：

### PlanSemanticFingerprint

应区分：

```text
inherit
vs
explicit same value
```

### ExecutionSemanticFingerprint / ArchiveSpecFingerprint

基于当前：

```text
EffectiveArchiveSpec
```

因此两者当前可以相同。

这正好继续沿用我们前面建立的：

> configuration semantics ≠ current execution semantics

原则。

------

# 9. ArchiveSpecOverride 只允许 declared Unit

继续保持此前规则：

```text
未声明 FILE_MANAGED
→ 使用 Plan default
```

如果用户要：

```text
这个 Crate 用 ZIP
```

就必须将它加入：

```text
ArchiveUnitDeclaration
```

然后增加 ArchiveSpecOverride。

不能偷偷用：

```text
SourceId + path
```

维护一个本机 override。

这样 File-backed plan 的行为才真正 portable。

------

# 10. FILE_MANAGED 一样允许 ArchiveSpecOverride

例如：

```text
Project/.backupignore
```

仍然只控制：

```text
RuleMode
Case
Rules
ArchiveUnitId?
```

而 Backup Plan declaration 可以控制：

```text
ArchiveSpecOverride
HistoryOverride
```

两边没有 authority 冲突。

最终：

```text
FILE_MANAGED
├─ Local Rules          ← .backupignore
├─ ArchiveSpecOverride  ← Backup Plan
└─ HistoryOverride      ← Backup Plan
```

这个边界很干净。

------

# 11. Protection override

因为 Protection 是 ArchiveSpec 的一个组件，所以允许：

```text
Plan default:
Secure(S1)

Unit A:
inherit

Unit B:
Protection = None

Unit C:
Protection = Secure(S2)
```

于是：

```text
A → Secure S1
B → None
C → Secure S2
```

Secret readiness 按每个 Unit 的：

```text
EffectiveArchiveSpec
```

判断。

------

# 12. 不要让 Unit 自己声明 SecretValue

仍然只能：

```text
Secure(SecretSlotId)
```

Unit override 只是引用 Plan-scoped：

```text
SecretSlot
```

不能创造：

```text
inline password
local secret reference
```

现有 Secret 安全边界保持不动。

------

# 13. Format override 会同时影响两个 fingerprint

例如：

```text
SevenZip → Zip
```

必然：

```text
ArchiveSpecFingerprint changed
```

因为归档 bytes/格式改变。

同时：

```text
OutputLayoutFingerprint changed
```

因为：

```text
A.7z → A.zip
```

因此这是：

```text
ArchiveRebuildRequired
+
OutputLayoutChange
```

不是单纯：

```text
OutputReorganization
```

现有 OutputLayout 规范已经把 format extension 纳入 mapping，因此这一点需要正式说明。

------

# 14. Format change 时旧 Current 怎么处理

例如：

```text
Current:
A.7z
```

新配置：

```text
A.zip
```

不能：

```text
先删 A.7z
再写 A.zip
```

仍走统一 publish：

```text
write A.zip.partial
↓
verify
↓
如果 History Enabled：
    persist old A.7z as HistoryVersion
↓
publish A.zip
↓
durable CurrentVersion commit
↓
旧 A.7z Current path 才允许清理
```

所以 Current 的：

```text
RelativeStoragePath
```

允许版本之间改变。

------

# 15. CompressionPreset 变化只 rebuild，不重组路径

例如：

```text
Standard → Extreme
```

归档扩展名没变：

```text
A.7z → A.7z
```

因此：

```text
ArchiveSpecFingerprint changed
→ Rebuild

OutputLayoutFingerprint unchanged
```

非常清楚。

------

# 16. Protection 变化同样只 rebuild

例如：

```text
None → Secure
```

或者：

```text
Secure(S1 rev3)
→ Secure(S1 rev4)
```

结果：

```text
ArchiveSpecFingerprint changed
→ Rebuild
```

路径不变。

------

# 17. v1 建议明确“不支持分卷”

这是我建议本轮顺便纠正的地方。

目前某些规范文字把：

```text
volume size
```

列在 ArchiveSpecFingerprint 的候选项里。

但现有 Current 模型明确是一 Unit 对应：

```text
<name>.<archive extension>
```

一旦支持：

```text
A.7z.001
A.7z.002
A.7z.003
```

Current 就从：

> 一个 Archive Artifact 文件

变成：

> Archive Artifact Set。

这会牵动：

- CurrentVersion；
- HistoryVersion；
- SHA-256；
- atomic publish；
- relocation；
- cloud sync；
- manifest；
- restore。

所以建议：

> **v1 固定 Single Volume。分卷进入后续 schema/version。**

目前产品可以保留：

```text
大归档 warning
estimated size
```

但不实际 split。

------

# 18. 因此从 v1 normative ArchiveSpec 中删除 volume size

未来可以设计：

```text
VolumePolicy
Single
Split(maxBytes)
```

但不是现在。

如果现有 `CHANGE-DETECTION.md` 写着：

```text
volume size
```

应修改为：

> future/resolved archiver parameter，不属于 Backup Plan v1 当前 configurable ArchiveSpec。

这是一次规范收紧，不影响现有代码，因为本来还没实现。

------

# 19. Solid mode 也不作为 v1 portable field

同样建议：

```text
solid mode
```

不要用户配置。

它属于：

```text
Format
+
CompressionPreset
+
ArchiveSemanticsVersion
```

解析后的 archiver parameter。

因此用户选择：

```text
SevenZip + Extreme
```

adapter 可以固定：

```text
solid = ...
dictionary = ...
algorithm = ...
```

但这些是实现语义。

------

# 20. Metadata policy 也建议暂不做用户 override

当前架构很重视：

- link；
- POSIX permissions；
- ACL；
- metadata。

但现在还没有 Archiving capability prototype。

因此 v1 更稳妥的是：

> **Metadata preservation semantics 由 Format + platform capabilities + ArchiveSemanticsVersion 固定，不作为 Backup Plan 可配置字段。**

例如未来可能：

```text
TarZstd
→ Preserve POSIX metadata profile

Zip
→ Compatibility metadata profile
```

如果 adapter 无法忠实保存 Scanner/Plan 所要求的条目：

```text
UnsupportedArchiveCapability
```

而不是让用户组合十几个 metadata toggle。

等 M4 prototype 后，如果真有必要，再在新 schema 中增加明确 portable policy。

------

# 21. ArchiveSpecFingerprint 应基于 Effective + Resolved Semantics

建议重新表述成：

```text
ArchiveSpecFingerprint =
fingerprint(
    EffectiveArchiveSpec,
    ArchiveSemanticsVersion,
    resolved format capability semantics,
    applicable SecretRevision,
    manifest semantics version
)
```

它可以包含最终解析出来的：

```text
algorithm
level
solid behavior
metadata behavior
```

用于证明 archive semantics。

但这些：

> **不等于 portable document 必须直接保存这些字段。**

这是当前规范里需要明确区分的地方。

------

# 22. Adapter capability 验证必须发生在 override resolution 之后

例如：

```text
Plan default:
SevenZip + Standard + None

Unit A:
Format = Zip
Protection = Privacy
```

先得到：

```text
EffectiveArchiveSpec(A)
=
Zip + Standard + Privacy
```

再：

```text
ArchiveCapabilities.Validate(...)
```

如果当前 ZIP adapter 不支持 Privacy：

```text
UnsupportedArchiveCapability
```

不能在处理 Plan default 时提前判断。

------

# 23. Effective Spec 是每 Unit 独立的

所以：

```text
B → 7z Standard
D → ZIP Fast
F → 7z Secure
```

完全合法。

Change Detection：

```text
B ArchiveSpecFingerprint
D ArchiveSpecFingerprint
F ArchiveSpecFingerprint
```

分别计算。

这与 Archive Unit 级 baseline 完全一致。

------

# 24. 改 Plan Default 只影响真正继承的 Unit

例如：

```text
Default:
Standard → Extreme
```

B：

```text
inherit
→ Effective changed
→ rebuild
```

D：

```text
explicit Fast
→ Effective unchanged
→ no rebuild
```

F：

```text
explicit Standard
→ Effective unchanged
→ no rebuild
```

这是采用逐组件 override 最大的价值。

------

# 25. 改 Override 但 Effective 不变，不应 rebuild

例如：

之前：

```text
Default = Standard
Unit = explicit Standard
```

用户改成：

```text
Unit = inherit
```

当前 effective 仍：

```text
Standard
```

那么：

```text
PlanSemanticFingerprint changed
```

因为继承意图改变；

但：

```text
ExecutionSemanticFingerprint
ArchiveSpecFingerprint
```

当前 effective semantics 不变。

因此：

> 不废弃正在进行的相同 archive，也不 rebuild。

这个细节建议进入测试矩阵。

------

# 26. Plan Default 改动在运行期间如何 stale-check

执行开始：

```text
Unit A effective = Standard
```

期间 Default：

```text
Standard → Extreme
```

如果 A = inherit：

```text
effective changed
→ ExecutionSemanticFingerprint changed
→ PlanChangedDuringRun
```

如果 A = explicit Standard：

```text
effective unchanged
```

则这次 Unit A 理论上可以继续发布。

由于 Run Plan 可能同时包含多个 Unit，我建议：

> ExecutionSemanticFingerprint 最终应能按 Unit 派生，或者 stale revalidation 至少比较本轮每个 Unit 的 resolved effective execution semantics。

不要让一个与该 Unit 无关的 default 改动废弃所有已经生成的归档。

这与 Archive Unit 独立 commit 的总体方向一致。

------

# 27. 这里建议引入 `EffectiveArchiveSpec`

Core/Application 最终接收：

```text
EffectiveArchiveSpec
```

而不是让 Archiver 自己理解继承。

关系：

```text
Portable Plan Default
        +
Unit Override
        ↓
Application Resolution
        ↓
EffectiveArchiveSpec
        ↓
Planning / Change Detection / Archiving
```

Archiver 不知道：

```text
inherit
override
Plan default
```

它只知道最终要求。

------

# 28. Domain 可以这样概念化

```text
ArchiveSpec
  Format
  CompressionPreset
  ProtectionConfiguration

ArchiveSpecOverride
  FormatOverride?
  CompressionPresetOverride?
  ProtectionOverride?

EffectiveArchiveSpec
  Format
  CompressionPreset
  ProtectionConfiguration
  ArchiveSemanticsVersion
```

注意：

```text
ArchiveSpecOverride
```

是 portable desired config；

```text
EffectiveArchiveSpec
```

是 resolved runtime semantic value。

------

# 29. 建议默认值

新 Plan UI 默认创建：

```text
Format = SevenZip
CompressionPreset = Standard
Protection = None
```

这是**产品创建默认值**。

一旦进入 document：

> 应作为 Plan ArchiveSpecDefault 明确持久化，而不是每次 reader 根据 App 当前默认重新推断。

------

# 30. 对于 TAR.ZST

建议现在只固定：

```text
TarZstd
```

是一个 portable format intent。

不要现在固定：

```text
tar implementation
zstd level
POSIX xattr representation
ACL encoding
```

这些交给：

```text
ArchiveSemanticsVersion
+
capability prototype
```

如果最终 M4 prototype 证明 v1 无法安全实现：

> 在正式 Schema freeze 前仍可从 v1 format enum 中移除。

所以这一项完成后，仍建议在 JSON Schema 真正落盘之前做一次很小的 Archiving capability sanity check。

------

# 31. 这一步完成后 `PortableOverrides` 就不应该再是模糊占位

Archive Unit declaration 可以明确变成概念：

```text
ArchiveUnitDeclaration
├─ identity / source / path
├─ RuleSource
├─ LocalRuleSet?         # UI_MANAGED only
├─ ArchiveSpecOverride?
└─ HistoryOverride?
```

而不是：

```text
PortableOverrides?
```

这种模糊容器。

这对后面 JSON Schema 很重要。

------

# 32. 建议测试矩阵至少覆盖

```text
Plan default only
undeclared FILE_MANAGED inherits
declared UI_MANAGED inherits
declared FILE_MANAGED inherits

override format only
override compression only
override protection only
multiple overrides

default changes inherited component
default changes overridden component

explicit same value → inherit
inherit → explicit same value

format change:
ArchiveSpec + OutputLayout change

compression change:
ArchiveSpec only

protection / SecretRevision:
ArchiveSpec only

unsupported effective combination
missing effective SecretBinding
```

以及：

```text
volume split rejected/not representable in v1
raw archiver option rejected
```

------

## 建议的规范结论

本轮只改文档，并固化：

> 1. Plan 必须具有完整 `ArchiveSpecDefault`；declared Archive Unit 可具有逐组件 `ArchiveSpecOverride`。
> 2. v1 portable ArchiveSpec 只暴露 `Format + CompressionPreset + ProtectionConfiguration`。
> 3. v1 CompressionPreset 为 `Store / Fast / Standard / Extreme`；新 Plan 产品默认 `SevenZip + Standard + None`，形成文档后默认必须显式持久化。
> 4. `algorithm/dictionary/solid/thread/raw CLI parameter` 等不是 portable fields，由 Format + Preset + versioned ArchiveSemantics 解析。
> 5. v1 暂不支持 split volume；修订此前把 volume size 当成当前 ArchiveSpec 配置项的表述，分卷留给未来 Archive Artifact Set 设计。
> 6. metadata preservation 暂不作为用户 configurable override，由格式 capability + ArchiveSemanticsVersion 固定；不能忠实满足时安全失败。
> 7. ArchiveSpecOverride 是领域内明确的逐组件继承，不属于 Import/Merge；未声明 FILE_MANAGED unit 只能使用 Plan default。
> 8. `PlanSemanticFingerprint` 区分 inherit 与 explicit；`ExecutionSemanticFingerprint / ArchiveSpecFingerprint` 使用 resolved `EffectiveArchiveSpec`。
> 9. effective semantics 不变时，仅改变 inherit/explicit 表达不得 rebuild。
> 10. Format 变化同时改变 ArchiveSpecFingerprint 和 OutputLayoutFingerprint；Compression/Protection 通常只改变 ArchiveSpecFingerprint。
> 11. Secret readiness 根据每个 Unit 的 EffectiveArchiveSpec 判断。
> 12. Adapter capability validation 在 override resolution 后执行。
> 13. 将 `ArchiveUnitDeclaration.PortableOverrides?` 收敛为明确的 `ArchiveSpecOverride? + HistoryOverride?`。
> 14. 继续不创建 JSON Schema、不实现 Archiver/SQLite。

之后只剩 **External Source 完整语义**。那一块做完后，我建议再做一次短的 **Backup Plan v1 Domain Freeze Review**，专门查跨规范冲突；通过后才生成 JSON Schema。