> [!NOTE]
> 本文是设计讨论的初始建议稿，不是规范真相源。已经确认并可用于实现的正式语义以 [`docs/BACKUPIGNORE.md`](../BACKUPIGNORE.md) 为准；两者冲突时不得依据本文修改行为。

我建议把 `.backupignore v1` 定义成一套**“Gitignore-like，但行为更适合备份场景”**的规则语言。核心原则是：语法尽量让开发者一眼能懂，但 Archive Unit 边界、Include-only、三层规则和跨平台行为必须比 `.gitignore` 更明确。

我建议下面这套直接作为 v1 规范基础。

# 1. `.backupignore` 的双重职责

文件存在本身就有语义：

```text
某目录存在 .backupignore
=
该目录是一个 FILE_MANAGED Archive Unit
+
该文件定义这个 Archive Unit 的 Local Rules
```

因此：

```text
Project/
└─ .backupignore
```

即使 `.backupignore` 是 **0 字节空文件**，也合法，表示：

> Project 是一个独立归档箱，默认备份其全部内容，没有额外局部过滤规则。

这个行为建议正式固定，不需要额外 `.backup` 文件。

------

# 2. 文件基础格式

建议规定：

```text
编码：UTF-8
BOM：允许
换行：LF / CRLF 都允许
```

空行忽略。

注释：

```text
# 这是注释
```

只有**第一个非空白字符是 `#`**时才代表注释。

不支持行尾注释：

```text
*.log # 日志
```

这里的 `# 日志` 应视为 pattern 的一部分，而不是注释。

这样可以避免文件名中 `#` 的歧义。

------

# 3. 推荐支持三个 Directive

v1 只需要三个，保持克制：

```text
@version 1
@mode exclude
@case auto
```

完整支持：

```text
@version 1

@mode exclude
@mode include-only

@case auto
@case sensitive
@case insensitive
```

Directive：

- 必须出现在第一条 pattern 之前；
- 同一 Directive 不能出现两次；
- 未知 Directive 直接报错；
- 不允许静默忽略。

------

# 4. `@version`

推荐 StowCrate 生成文件时写：

```text
@version 1
```

但为了保持：

> 空 `.backupignore` 合法

我建议：

**省略 `@version` 时永远解释为 v1。**

因此：

```text
空文件
```

与：

```text
@version 1
```

在格式版本上完全相同。

以后出现：

```text
@version 2
```

而当前程序不支持时：

> 必须拒绝执行该 Backup Plan，并明确提示版本不支持。

绝不能：

> 当作 v1 猜着解析。

------

# 5. `@mode` 是整个 Archive Unit 的“默认选择状态”

这是最关键的定义之一。

## exclude

默认：

```text
INCLUDE
```

然后规则把部分文件排除。

```text
@mode exclude

node_modules/
target/
*.log
```

相当于：

> 全部备份，但这些不备份。

这是默认模式。

所以：

```text
@mode
```

完全省略时，相当于：

```text
@mode exclude
```

这样空 `.backupignore` 自然等价于：

> 全部备份。

------

## include-only

默认：

```text
EXCLUDE
```

只有被显式 INCLUDE 的内容才备份。

例如：

```text
@mode include-only

!src/
!pom.xml
!README.md
```

表示：

> 默认什么都不备份，只备份 src、pom.xml、README.md。

------

# 6. 一个很重要的决定：`!` 的意义永远不改变

这里我建议**不要**根据 `@mode` 改变 pattern 的含义。

固定为：

```text
普通 pattern
=
EXCLUDE

!pattern
=
INCLUDE
```

永远如此。

所以：

### exclude 模式

```text
@mode exclude

*.log
!important.log
```

结果：

```text
普通文件        INCLUDE
*.log           EXCLUDE
important.log   INCLUDE
```

这跟 `.gitignore` 很接近。

------

### include-only 模式

```text
@mode include-only

!src/
!README.md
src/generated/
```

结果：

```text
默认                 EXCLUDE
src/                 INCLUDE
README.md            INCLUDE
src/generated/       EXCLUDE
```

我认为这比：

> include-only 模式里普通 pattern 突然变成 INCLUDE

要好得多。

因为这样整个规则系统始终只有两个动作：

```text
pattern  → 排除
!pattern → 包含
```

程序员不用切换脑回路。

------

# 7. Rule Engine 的正式决策算法

对于一个 Archive Unit 中的每个候选 Entry：

首先根据 mode 设置：

```text
@mode exclude
→ decision = INCLUDE

@mode include-only
→ decision = EXCLUDE
```

然后规则按照顺序执行。

伪代码：

```text
decision = DefaultDecision

for rule in EffectiveRules:
    if rule matches entry:
        if rule starts with !
            decision = INCLUDE
        else
            decision = EXCLUDE

return decision
```

因此：

> **Last matching rule wins。**

这是整个规则引擎最重要的原则之一。

例如：

```text
*.log
!important.log
important*.log
!important-keep.log
```

最后谁匹配，谁决定。

------

# 8. 三层规则正式定义

我们已经确定：

```text
Global Rules
      ↓
Backup Plan Rules
      ↓
Local Rules
```

v1 我建议把它定义成：

```text
EffectiveRules =
    GlobalRules
    + PlanRules
    + LocalRules
```

顺序严格保持：

```text
Global → Plan → Local
```

因为 Last Match Wins，所以自然形成：

```text
Local > Plan > Global
```

例如：

Global：

```text
node_modules/
```

Plan：

```text
!vendor/node_modules/
```

Local：

```text
vendor/node_modules/cache/
```

最终：

```text
普通 node_modules
→ Global 排除

vendor/node_modules
→ Plan 重新包含

vendor/node_modules/cache
→ Local 再次排除
```

不需要另外实现复杂“优先级计算”。

------

# 9. v1 中只有 Archive Unit Local Rules 决定 Mode

这一点我建议明确。

Global Rules 和 Plan Rules 在 v1 中只提供：

```text
EXCLUDE / INCLUDE overlay
```

它们**不改变 Archive Unit 默认模式**。

Archive Unit 的：

```text
Exclude
Include-only
```

由 Local Rules 决定。

FILE_MANAGED：

```text
.backupignore
```

决定。

UI_MANAGED：

```text
SQLite
```

决定。

这样三层合并非常容易理解。

以后如果确实发现需求，再考虑让 Plan 控制默认 mode，不要现在增加复杂度。

------

# 10. Parent Local Rules 绝不传给 Child Archive Unit

这个一定正式写进规范。

例如：

```text
D
├─ .backupignore
├─ E
└─ F
   └─ .backupignore
```

那么：

```text
D Local Rules
```

只作用于 D。

F 的有效规则是：

```text
Global
+
Plan
+
F Local
```

**不是：**

```text
Global
+
Plan
+
D Local
+
F Local
```

所以：

> Global 和 Plan 跨 Crate；Local 不跨 Crate。

这会让模型特别干净。

------

# 11. Archive Boundary 优先级高于任何规则

这个比 Rule Engine 本身优先级还高。

例如：

```text
D
├─ .backupignore
└─ F
   └─ .backupignore
```

D 的规则就算写：

```text
!F/**
```

也：

> **不能让 F 进入 D.7z。**

正式优先级：

```text
Safety
    ↓
Archive Boundary
    ↓
Rule Engine
```

父 Archive Unit 一旦遇到子 Archive Unit：

```text
STOP
```

不允许任何：

```text
!
Include-only
Plan Rule
Global Rule
```

穿透边界。

所以：

> Archive Boundary 不是普通 Ignore Rule。

这一点非常重要。

------

# 12. 建议 Archive Unit Discovery 与过滤规则完全分开

我建议 planner 做成两个逻辑阶段。

### Pass 1：Boundary Discovery

只确定：

```text
Source
↓
有哪些 Archive Unit
↓
形成 ArchiveUnit Tree
```

`.backupignore` 的发现**不受 include/exclude 规则影响**。

例如：

```text
node_modules/
```

即使 Global Rule 排除了它，但里面存在：

```text
node_modules/X/.backupignore
```

从纯语义上：

> X 仍然是一个显式 Archive Unit。

这意味着：

> `.backupignore` 是结构配置，而不是普通文件内容。

实现上以后可以通过缓存等方式优化 discovery，但规范结果应该保持一致。

------

# 13. 所有 pattern 都针对 Archive Unit Relative Path

绝对不能匹配：

```text
E:\Code\Project\src\App.cs
```

只匹配：

```text
src/App.cs
```

统一使用：

```text
/
```

作为逻辑路径分隔符。

所以 Windows 用户选：

```text
src\main\java
```

UI 写进 `.backupignore` 时必须转换成：

```text
src/main/java
```

这样：

```text
Windows
macOS
Linux
```

同一个 `.backupignore` 都能使用。

------

# 14. `/` 开头表示 Archive Unit Root Anchor

例如：

```text
/build/
```

只匹配：

```text
ArchiveUnitRoot/build/
```

不匹配：

```text
src/build/
foo/build/
```

而：

```text
build/
```

则可以匹配任意层级叫 build 的目录：

```text
build/
src/build/
foo/bar/build/
```

这和 `.gitignore` 的使用习惯比较接近。

------

# 15. 没有 `/` 的 pattern 匹配任意层级 basename

例如：

```text
*.log
```

匹配：

```text
a.log
logs/a.log
src/test/a.log
```

例如：

```text
node_modules
```

匹配任意层级名为：

```text
node_modules
```

的 file/directory entry。

不过智能向导生成目录规则时，建议始终输出：

```text
node_modules/
```

让意图更清楚。

------

# 16. 尾部 `/` 表示目录规则

例如：

```text
node_modules/
```

只匹配目录。

如果一个普通文件就叫：

```text
node_modules
```

不匹配。

目录匹配成功后，默认影响其全部 descendants：

```text
node_modules/
node_modules/react/
node_modules/react/index.js
```

都处于排除状态。

但后续 INCLUDE 规则可以重新包含 descendant。

------

# 17. StowCrate 应允许“重新包含被排除目录中的文件”

这里我建议**有意与 Git 的一些 traversal 行为不同**。

例如：

```text
build/
!build/release/app.exe
```

StowCrate 应该允许：

```text
build/release/app.exe
```

重新进入归档。

不要求用户额外写：

```text
!build/
!build/release/
!build/release/app.exe
```

原因很简单：

> StowCrate 的目标是描述最终备份集合，而不是复刻 Git 的目录遍历优化。

因此逻辑规则：

> 父目录被排除，不代表 descendant 永远不可重新 INCLUDE。

Scanner 可以优化，但：

> 优化绝不能改变规则结果。

这点我非常建议保留，会比 `.gitignore` 好用很多。

------

# 18. Wildcard 语义

建议 v1 支持：

### `*`

匹配 0 个或多个非 `/` 字符：

```text
*.log
foo*.txt
```

------

### `?`

匹配一个非 `/` 字符：

```text
file?.txt
```

匹配：

```text
file1.txt
fileA.txt
```

------

### `**`

可以跨目录。

例如：

```text
**/*.log
```

任意深度 `.log`。

```text
src/**/obj/
```

匹配：

```text
src/obj/
src/A/obj/
src/A/B/obj/
```

------

### Character Class

我建议 v1 就支持：

```text
[a-z]
[abc]
[!abc]
```

例如：

```text
file[0-9].txt
```

这个 `.gitignore/glob` 用户已经比较熟悉，实现成本也不高。

------

# 19. v1 不支持这些

刻意保持简单：

```text
❌ Regex
❌ {a,b} brace expansion
❌ 环境变量
❌ 文件大小条件
❌ 日期条件
❌ MIME 类型
❌ JavaScript 表达式
❌ if/else
```

例如不要允许：

```text
@size > 100MB
@if os == windows
```

这些以后有实际需求再加。

`.backupignore` 应始终保持：

> 一眼能看懂。

------

# 20. Escape 规则

建议使用：

```text
\
```

作为 escape。

例如真实文件名：

```text
!important.txt
```

写：

```text
\!important.txt
```

真实文件名：

```text
#data.txt
```

：

```text
\#data.txt
```

真实名字包含：

```text
*
```

：

```text
\*
```

还包括：

```text
\?
\[
\]
\\
\@
\ 
```

------

# 21. `@` 开头保留给 Directive

例如：

```text
@version
@mode
@case
```

以后可以扩展。

因此如果实际文件名：

```text
@data
```

规则应该写：

```text
\@data
```

这样未来增加 Directive 不会破坏旧语法。

------

# 22. `@case` 语义

这是跨平台项目必须正式规定的。

## auto

```text
@case auto
```

默认值。

表示：

> 按 Source 所在文件系统的实际大小写语义匹配。

不能简单写：

```text
Windows = insensitive
Linux = sensitive
```

因为：

- Windows 存在大小写敏感目录；
- macOS 文件系统可能 sensitive/insensitive；
- 挂载文件系统也可能不同。

所以最终应该通过：

```text
IFileSystem / IPlatformMetadata
```

解析为：

```text
Sensitive
Insensitive
```

然后把结果记录进 `ArchivePlan`。

------

## sensitive

```text
@case sensitive
```

：

```text
Foo
foo
```

不同。

------

## insensitive

```text
@case insensitive
```

：

```text
Foo
foo
```

相同。

比较必须：

> culture invariant

C# 层面倾向：

```text
Ordinal
OrdinalIgnoreCase
```

不要受到：

```text
中文 Windows
土耳其语 Windows
日语 macOS
```

系统语言影响。

------

# 23. Unicode 规范化

为了 macOS/Windows/Linux 的一致性，我建议：

规则匹配使用：

> **Unicode NFC normalization**

也就是：

```text
Physical File Name
        ↓
Logical Match Key
        ↓
Unicode NFC
```

但是打开真实文件时：

> 仍使用文件系统返回的原始路径。

不要因为 normalization 去改真实文件名。

------

# 24. `.` 和 `..` 不允许作为路径 segment

规则：

```text
../secret
foo/../../bar
```

直接：

> Syntax Error

因为 `.backupignore` 永远不能逃出 Archive Unit。

Leading `/` 只是：

> Archive Unit root anchor

不是操作系统 absolute path。

Windows 路径：

```text
C:\foo
```

也不应该有任何特殊含义。

------

# 25. own `.backupignore` 建议强制备份

这是我们前面已经形成的设计，我建议直接写进 v1。

FILE_MANAGED Archive Unit：

```text
Project/.backupignore
```

自己的 `.backupignore`：

> **永远进入 Project 的归档。**

即使规则写：

```text
.backupignore
*
```

它仍然保留。

原因是：

> 规则本身就是恢复这个 Archive Unit 结构的重要信息。

所以它属于 Reserved Control Entry。

例如：

```text
Project.7z
├─ .backupignore
├─ src/
└─ ...
```

------

# 26. 子 Archive Unit 的 `.backupignore` 不进入父归档

例如：

```text
D
├─ .backupignore
└─ F
   └─ .backupignore
```

结果：

```text
D.7z
└─ D自己的.backupignore

F.7z
└─ F自己的.backupignore
```

D 永远看不到：

```text
F/.backupignore
```

因为：

```text
F
```

已经是 Boundary。

------

# 27. 保留 `__stowcrate__/` archive namespace

我们现在已经决定归档里存在：

```text
__stowcrate__/manifest.json
```

所以建议正式规定：

> Archive Unit 根目录下的 `__stowcrate__/` 是 StowCrate Reserved Namespace。

如果真实源文件本身存在：

```text
Project/__stowcrate__/
```

v1 不要偷偷覆盖或重命名。

直接：

> **Planning Error：reserved archive namespace conflict**

这样最安全。

以后 v2 有必要再考虑 escaping/remapping。

------

# 28. Generated Metadata 不参与 `.backupignore`

例如：

```text
__stowcrate__/manifest.json
```

是归档生成阶段注入的。

用户写：

```text
__stowcrate__/
```

也不能把它排除。

也就是说：

```text
Backup Rule
```

只作用于：

> Source Entries / External Source Entries

不作用于 StowCrate 自己的 metadata。

------

# 29. UI_MANAGED 与 FILE_MANAGED 同目录冲突

建议正式规定：

如果 SQLite 说：

```text
Project
RuleSource = UI_MANAGED
```

但磁盘同时存在：

```text
Project/.backupignore
```

这是：

> **Configuration Conflict**

不要：

```text
偷偷优先 SQLite
```

也不要：

```text
偷偷优先文件
```

应该提示：

> 此归档箱同时存在 UI 配置和 `.backupignore`，请选择一个规则来源。

然后阻止执行。

这样不会产生“到底谁生效”的问题。

------

# 30. Rule Parse Error 必须是 Fatal Validation Error

例如：

```text
@mode abc
```

或者：

```text
@unknown xxx
```

或者：

```text
foo[
```

都不能：

> 忽略这一行继续备份。

因为备份软件最危险的行为之一就是：

> 用户以为东西没备份，实际上备了；
>
> 或用户以为东西备了，实际上被规则跳过了。

所以：

```text
Syntax Error
Unsupported Version
Invalid Pattern
Rule Source Conflict
```

都应该在：

```text
ArchivePlan Validation
```

阶段失败。

在任何 `.partial` 写入之前解决。

------

# 31. Change Detection 必须包含 Rules Fingerprint

Archive Unit 是否变化，不能只考虑：

```text
path
size
mtime
```

还应该包括：

```text
EffectiveRulesFingerprint
```

即：

```text
Global Rules
+
Plan Rules
+
Local Rules
+
Mode
+
Case Policy
+
Boundary Tree
```

发生变化：

> Archive Unit 必须重新规划。

尤其 `.backupignore` 自身又会被包含进归档，所以：

```text
只改一个注释
```

归档本身也已经发生了内容变化。

------

# 32. 推荐的 `.backupignore` 示例

普通 Java 项目：

```text
@version 1
@mode exclude
@case auto

# Maven
target/

# IDE
.idea/
*.iml

# Temporary
*.log
*.tmp
```

Python：

```text
@version 1
@mode exclude

.venv/
__pycache__/
.pytest_cache/
*.pyc
```

------

Include-only：

```text
@version 1
@mode include-only

# 只保留源代码和重要项目文件
!src/
!pom.xml
!README.md
!.gitignore

# src 中的 generated 不需要
/src/generated/
```

------

更复杂：

```text
@version 1
@mode exclude

build/
*.log

# 保留一个特殊构建结果
!build/release/app.exe

# 但 debug 全部不要
build/debug/
```

------

# 33. 最终优先级建议正式写成这样

一个 entry 的处理顺序：

```text
1. Safety Policy
   路径逃逸 / reserved namespace / 非法路径

            ↓

2. Archive Boundary
   属于子 Archive Unit？
   YES → 父 Unit 永远 STOP

            ↓

3. Reserved Control Entries
   own .backupignore 等必须保留项

            ↓

4. Default Mode
   exclude      → INCLUDE
   include-only → EXCLUDE

            ↓

5. Global Rules

            ↓

6. Plan Rules

            ↓

7. Local Rules

            ↓

8. Final INCLUDE / EXCLUDE
```

其中 5～7：

> **Last Match Wins。**

这是我认为最重要的一张规则图。

------

# 34. 建议 Domain Model 不要直接存 `!`

代码层最好不要让业务逻辑到处解析字符串：

```text
!foo
```

Parser 应该第一时间转换成：

```csharp
BackupRule
{
    Action = RuleAction.Include,
    Pattern = "foo"
}
```

领域对象建议大概有：

```text
RuleAction
├─ Include
└─ Exclude

RuleMode
├─ Exclude
└─ IncludeOnly

CaseSensitivity
├─ Auto
├─ Sensitive
└─ Insensitive

BackupRule
├─ Pattern
└─ Action

RuleSet
├─ Mode
├─ CaseSensitivity
└─ Rules
```

三层 flatten 以后：

```text
EffectiveRuleSet
├─ DefaultAction
├─ ResolvedCaseSensitivity
└─ OrderedRules
```

这样 Rule Engine 根本不需要知道：

> 这一条原来是不是 `!` 开头。

------

# 35. 我建议 Codex 至少写这些 v1 回归测试

第一批 Rule Engine tests 至少覆盖：

1. 空 `.backupignore` 合法；
2. 默认 mode 是 exclude；
3. include-only 默认全部排除；
4. 普通 pattern 永远是 EXCLUDE；
5. `!` 永远是 INCLUDE；
6. Last Match Wins；
7. Local > Plan > Global；
8. Parent Local 不传给 Child；
9. Archive Boundary 高于 `!`；
10. `/foo/` 只匹配 Unit root；
11. `foo/` 匹配任意层级目录；
12. `*` 不跨 `/`；
13. `**` 跨目录；
14. `?`；
15. Character Class；
16. `\!` / `\#` / `\@`；
17. own `.backupignore` 无条件进入归档；
18. include descendant 不要求父目录显式 include；
19. case sensitive；
20. case insensitive；
21. Unicode NFC；
22. `..` 拒绝；
23. 未知 Directive 拒绝；
24. 未支持 version 拒绝；
25. `UI_MANAGED + .backupignore` 同目录冲突；
26. `__stowcrate__/` 冲突；
27. Global/Plan/Local Rules fingerprint 改变导致重新规划。

这些测试写完，StowCrate 最核心的一块基础就非常稳了。

------

## 我建议把 v1 的核心原则浓缩成 8 句话

后面写进 `PRODUCT.md` / 单独的 `docs/BACKUPIGNORE.md` 都可以：

> 1. `.backupignore` 的存在声明一个 FILE_MANAGED Archive Unit。
> 2. 空文件合法，默认 `exclude` 模式。
> 3. 普通 pattern 永远表示 EXCLUDE，`!pattern` 永远表示 INCLUDE。
> 4. `exclude` 模式默认 INCLUDE，`include-only` 模式默认 EXCLUDE。
> 5. 规则按 Global → Plan → Local 顺序执行，最后匹配规则生效。
> 6. Parent Local Rules 不跨 Archive Boundary；Boundary 高于任何 include/exclude。
> 7. 所有 pattern 使用 Archive Unit 相对路径和 `/`，不依赖操作系统路径格式。
> 8. `.backupignore`、StowCrate metadata 和安全约束不允许被普通规则破坏。

我认为这套足够作为 **`.backupignore v1` 正式规范**。特别是“**`!` 含义固定 + mode 只决定默认状态**”这个设计，我建议不要轻易改，它会让整个 Rule Engine、三层规则合并和用户认知都简单很多。
