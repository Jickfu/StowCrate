# `.backupignore v1` 正式规范

本文是 `.backupignore v1` 解析器、规则引擎和兼容性测试的行为真相源。它采用开发者熟悉的 gitignore-like 表达，但 Archive Boundary、include-only、三层规则和跨平台行为以本文为准，不以 Git 的实现细节为准。

## 1. 文件与 Archive Unit

目录中存在 `.backupignore` 同时表示：

- 该目录是一个 `FILE_MANAGED` Archive Unit；
- 该文件是此 Archive Unit 完整的 Local Rule Source。

0 字节空文件合法，等价于 v1、`exclude` mode、`auto` case 且没有局部 pattern。FILE_MANAGED 时，SQLite 不得另存 `RuleMode` 或 `Rules`。

`.backupignore` 使用 UTF-8，允许 BOM，允许 LF 或 CRLF。空行忽略。行首和行尾未转义的空格或制表符忽略；需要字面边缘空白时使用反斜杠转义。

第一个非空白字符为未转义 `#` 的行是整行注释。不支持行尾注释，pattern 中间的 `#` 是字面字符。

## 2. Directive

v1 只支持：

```text
@version 1
@mode exclude | include-only
@case auto | sensitive | insensitive
```

- Directive 可省略；默认分别为 `1`、`exclude`、`auto`；
- Directive 必须位于第一条 pattern 之前；
- 同一种 Directive 不得重复；
- 未知 Directive、未知值、重复 Directive 和不支持的版本都是 fatal validation error；
- 字面 `@` 开头的 pattern 使用 `\@`。

`auto` 必须由 Source 所在文件系统的实际能力解析为 `sensitive` 或 `insensitive`，解析结果写入 `ArchivePlan`。比较使用 `Ordinal`/`OrdinalIgnoreCase`，匹配键统一做 Unicode NFC normalization；真实文件访问仍使用扫描器提供的原始物理路径。

## 3. Rule Action、Mode 与顺序

- 普通 pattern 永远是 `EXCLUDE`；
- `!pattern` 永远是 `INCLUDE`；
- `exclude` mode 的默认结果是 `INCLUDE`；
- `include-only` mode 的默认结果是 `EXCLUDE`；
- 所有规则按原始顺序执行，最后一条匹配规则生效。

三层规则按以下顺序拼接：

```text
Global Rules → Backup Plan Rules → 当前 Archive Unit Local Rules
```

因此 Last Match Wins 自然形成 Local > Plan > Global。Global 和 Plan 只提供 action overlay，不改变默认 mode 或 case policy。Parent Local Rules 不传给 Child Archive Unit。

## 4. 路径模型与 pattern 定位

所有 pattern 都针对当前 Archive Unit 的相对逻辑路径，统一使用 `/`，不匹配物理绝对路径。

- 开头 `/` 表示 Archive Unit Root Anchor；
- 去掉尾部 `/` 后不包含 `/` 的 pattern，匹配任意层级 basename；
- 包含内部 `/` 的 pattern，无论是否显式写开头 `/`，都从 Archive Unit Root 匹配；
- 尾部 `/` 表示只匹配目录；
- pattern 命中一个目录时也作用于 descendants；后续更具体的 INCLUDE/EXCLUDE 仍可改变 descendant 的最终结果；
- `.`、`..`、空 segment、NUL 和逃出 Archive Unit 的路径非法；leading `/` 只是逻辑 anchor；
- Windows 盘符和反斜杠没有物理路径语义，反斜杠只用于 escape。

例如：

```text
/build/                 # 只匹配根目录 build
build/                  # 匹配任意层级名为 build 的目录
build/release/app.exe   # 从 Archive Unit Root 匹配
**/build/               # 显式匹配任意深度 build 目录
```

## 5. Wildcard 与 escape

v1 支持：

- `*`：0 个或多个非 `/` 字符；
- `?`：1 个非 `/` 字符；
- `**`：可跨目录，`**/` 也可匹配 0 层目录；
- `[abc]`、`[a-z]`、`[!abc]`：character class、range 和否定 class。

反斜杠转义下一个字符，包括 `\!`、`\#`、`\@`、`\*`、`\?`、`\[`、`\]`、`\\` 和 `\ `。尾部孤立反斜杠、未闭合 character class 或无效 range 是语法错误。

v1 不支持 regex、brace expansion、环境变量、文件大小/日期/MIME 条件、脚本表达式或条件指令。

## 6. Archive Boundary 与保留条目

Planner 必须先独立发现全部 Archive Unit，再执行过滤。`.backupignore` 的发现不受 Global、Plan 或 Local Rules 影响。

单个 entry 的优先级固定为：

```text
Safety Policy
  → Archive Boundary
  → Reserved Control Entry
  → Mode Default
  → Global Rules
  → Plan Rules
  → Local Rules
  → Final Action
```

- Parent 遇到 Child Archive Unit 必须 STOP；任何 `!` 或上层规则都不能穿透；
- FILE_MANAGED Archive Unit 自己的 `.backupignore` 永远进入自己的归档；
- Child 的 `.backupignore` 不进入 Parent；
- Source 在 Archive Unit 根下占用 `__stowcrate__` 文件或目录是 planning error；
- 生成的 `__stowcrate__/manifest.json` 不参与普通规则匹配。

UI_MANAGED Archive Unit 的根目录若同时存在 `.backupignore`，属于规则来源冲突，必须阻止规划并要求用户选择一个来源。

## 7. 重新包含 descendant

StowCrate 描述最终备份集合，不复刻 Git 为遍历优化设置的限制。父目录被排除后，descendant 仍能被后续 INCLUDE 重新包含：

```text
build/
!build/release/app.exe
```

`build/release/app.exe` 最终包含，不要求额外写 `!build/` 和 `!build/release/`。扫描优化不得改变该结果。

## 8. 确定性与 fingerprint

同一份 Source Snapshot、Backup Plan、解析后的规则和已解析 case sensitivity 必须产生相同、稳定排序的 `ArchivePlan`。

Rules fingerprint 至少包含：Global Rules、Plan Rules、Local Rules、mode、case policy、resolved case sensitivity 和 Boundary Tree。任何一项变化都必须重新规划。FILE_MANAGED 的 `.backupignore` 自身属于源内容；即使只修改注释，源快照和最终归档也已变化。

## 9. 错误处理

Syntax Error、Unsupported Version、Invalid Pattern、Rule Source Conflict、Reserved Namespace Conflict 和非法路径都是 fatal planning error。不得忽略错误继续备份；所有错误必须在写入任何 `.partial` 之前暴露。

## 10. v1 核心原则

1. `.backupignore` 的存在声明一个 FILE_MANAGED Archive Unit。
2. 空文件合法，默认 `exclude` mode。
3. 普通 pattern 永远 EXCLUDE，`!pattern` 永远 INCLUDE。
4. mode 只决定默认 action。
5. Global → Plan → Local，Last Match Wins。
6. Parent Local 不跨 Boundary，Boundary 高于任何规则。
7. pattern 使用 Archive Unit 相对逻辑路径和 `/`。
8. `.backupignore`、生成 metadata 和安全约束不能被普通规则破坏。
