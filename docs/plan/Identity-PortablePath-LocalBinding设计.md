> [!NOTE]
> 本文是设计讨论的初始建议稿，不是规范真相源。已确认的 Identity、ID 生命周期、Portable Configuration、Device Local Binding 与 `.backupignore @id` 语义以 [`docs/BACKUPPLAN.md`](../BACKUPPLAN.md) 和 [`docs/BACKUPIGNORE.md`](../BACKUPIGNORE.md) 为准；冲突时不得依据本文修改行为。

`MANAGED / FILE_BACKED` authority 一旦稳定，接下来确实应该把 **Identity** 和 **Portable Path / Local Binding** 连在一起设计，因为二者实际上是同一个问题的两面：

> **“一个逻辑备份对象是谁”**，不能由它当前机器上的物理路径决定。

我建议下一个正式规范一次性确定下面这套。

# 一、Identity 总原则

StowCrate v1 中需要稳定身份的核心对象：

```text
BackupPlan
BackupSource
ArchiveUnit
ExternalSource
```

全部使用**持久稳定 ID**，不能使用以下内容作为身份：

```text
❌ Name
❌ RelativePath
❌ AbsolutePath
❌ 数组下标
❌ 数据库自增主键
```

原因很简单：

```text
E:\code
→
D:\work\code
```

不能变成“一个新的 Source”。

Archive Unit：

```text
Project/src
→
Project/source
```

重命名后也不应该自动成为完全无历史的新 Archive Unit。

------

# 二、ID 格式：建议 UUID v4

v1 建议统一：

```text
PlanId
SourceId
ArchiveUnitId
ExternalSourceId
```

使用：

```text
UUID v4
```

JSON 中保存标准小写字符串：

```json
"planId": "ab3bf307-391b-4202-a7ae-f60795e9402c"
```

不建议：

```text
UUID v7
ULID
Snowflake
数据库 long
```

原因是这里不需要时间排序，也不是分布式数据库主键。

UUID v4：

- 跨平台；
- 无中心生成；
- 稳定；
- JSON 友好；
- Git merge 时也容易识别。

C# Core 可以进一步做强类型：

```csharp
public readonly record struct PlanId(Guid Value);
public readonly record struct SourceId(Guid Value);
public readonly record struct ArchiveUnitId(Guid Value);
```

避免把几个 `Guid` 传错。

------

# 三、数据库主键 ≠ 领域 Identity

这个建议现在就写死。

例如数据库可以有：

```text
BackupPlans
-----------
Id INTEGER PRIMARY KEY
PlanId TEXT UNIQUE NOT NULL
```

甚至 EF Core 直接拿 `PlanId` 当 PK 也可以。

但架构语义上：

> `PlanId` 才是领域身份。

绝不能出现：

```text
SQLite RowId = 12
```

于是系统认为：

```text
Plan Identity = 12
```

因为这个值：

```text
Export
Import
另一台电脑
config.db 重建
```

都没有意义。

------

# 四、PlanId 是否跟随 `*.backupplan`

对于：

```text
FILE_BACKED
```

答案应该是：

> **跟随。**

也就是说：

```json
{
  "schemaVersion": 1,
  "planId": "...",
  "name": "My Code"
}
```

PlanId 属于文档本身。

这样：

```text
电脑 A
git clone
↓
register

电脑 B
git clone
↓
register
```

两边都知道：

> 这是同一个逻辑 Backup Plan。

------

# 五、Import 时是否保留 PlanId

这里需要一个明确规则。

我建议：

### 正常 Import

```text
MyCode.backupplan
↓
Import as Managed
```

默认：

> **保留 PlanId。**

因为 Import 的含义是：

> 把这个逻辑 Plan 转由 StowCrate 管理。

不是：

> 创建一个副本。

------

### Duplicate / Clone

如果用户明确选择：

> “复制为新的备份方案”

才生成：

```text
new PlanId
new SourceId
new ArchiveUnitId
...
```

这叫：

```text
Clone
```

而不是 Import。

这样语义非常清楚。

------

# 六、发生 PlanId 冲突怎么办

例如 config.db 已有：

```text
PlanId = AAA
```

用户又 Import：

```text
another.backupplan
PlanId = AAA
```

但内容不同。

绝对不能：

```text
偷偷覆盖
```

应该识别成：

```text
IdentityConflict
```

UI 提供：

```text
Update existing plan
Clone as new plan
Cancel
```

其中：

### Update

保留所有 ID：

```text
PlanId
SourceId
ArchiveUnitId
```

按导入文档更新配置。

### Clone

递归生成新的：

```text
PlanId
SourceId
ArchiveUnitId
ExternalSourceId
```

------

# 七、SourceIdentity

Backup Source 应该：

```json
{
  "sourceId": "...",
  "name": "Code"
}
```

注意：

```text
name = "Code"
```

只是显示名。

真正身份：

```text
sourceId
```

本地路径不属于 Source identity。

所以：

```text
电脑 A
SourceId = SRC-1
Path = E:\code
```

电脑 B：

```text
SourceId = SRC-1
Path = /Users/foo/code
```

仍然：

> 同一个逻辑 Source。

------

# 八、Backup Plan 文件不要直接存本机绝对路径作为主配置

这是 Portable Path 设计的核心。

不要：

```json
{
  "sourcePath": "E:\\code"
}
```

作为唯一 Source 配置。

否则所谓 Portable Plan 失去意义。

我建议拆成：

```text
Portable Plan
+
Local Binding
```

------

# 九、Local Binding 属于机器状态，不属于 Portable Document

例如 `.backupplan`：

```json
{
  "sources": [
    {
      "sourceId": "...",
      "name": "Code"
    }
  ]
}
```

电脑 A 的 config.db：

```text
SourceBinding
-------------
PlanId
SourceId
PhysicalPath = E:\code
```

电脑 B：

```text
SourceBinding
-------------
PlanId
SourceId
PhysicalPath = /Users/foo/code
```

所以：

```text
*.backupplan
```

描述：

> 我要备份逻辑上的 Code Source。

而：

```text
config.db / LocalBinding
```

描述：

> 这台电脑上的 Code 在哪里。

这正是 portable config 应该有的模型。

------

# 十、Current / History 也使用 Local Binding

同样不能把：

```text
D:\Backup
E:\History
```

作为 Portable Plan 的唯一配置。

建议：

```text
StorageTargetId
```

或者简单一些，v1 可以固定逻辑槽：

```text
CurrentRoot
HistoryRoot
```

然后本地绑定：

```text
PlanLocalBinding
├─ CurrentRoot
└─ HistoryRoot
```

电脑 A：

```text
Current = D:\Backup\Code
History = E:\BackupHistory\Code
```

电脑 B：

```text
Current = /Volumes/Backup/Code
History = /Volumes/Archive/Code
```

Portable Plan 只表达：

```text
history.enabled = true
```

以及：

```text
retention policy
```

不需要知道实际磁盘位置。

------

# 十一、一个重要区别：Portable Configuration vs Device Binding

建议在规范中正式使用这两个词：

```text
Portable Configuration
```

和：

```text
Device Binding
```

### Portable Configuration

可以：

```text
Git
Export
Import
跨机器
```

包括：

```text
Archive Units
Rules
Compression
Link Policy
Change Detection Mode
History Policy
External Source declarations
Schedule intent
```

### Device Binding

属于当前设备：

```text
Source physical paths
CurrentRoot
HistoryRoot
External Source physical paths
Secret references
可能的 scheduler installation id
```

这两个模型一定不要混。

------

# 十二、`${HOME}` 还要不要支持？

我们之前曾讨论过：

```text
${HOME}/Documents
```

我现在建议：

> **支持，但把它作为 Local Binding 的便捷路径表达，而不是 Portable Source Identity。**

例如用户可以绑定：

```text
${HOME}/code
```

而不是：

```text
/Users/jickfu/code
```

这样同一个 Local Binding 配置甚至可以更容易迁移。

推荐 v1 只支持少数明确变量：

```text
${HOME}
${DESKTOP}
${DOCUMENTS}
${DOWNLOADS}
```

但这里我甚至建议进一步保守：

### v1 必须支持

```text
${HOME}
```

### 其他

可以以后根据跨平台 API 再加。

不要直接：

```text
${任意系统环境变量}
```

因为：

```text
%APPDATA%
$USERPROFILE
$XDG_...
```

会让 Plan 行为严重依赖外部环境。

------

# 十三、环境变量不能成为 Portable Plan 的隐式输入

例如：

```text
${MY_CODE}
```

如果允许任意变量：

电脑 A：

```text
MY_CODE=E:\code
```

电脑 B：

```text
MY_CODE=/tmp/random
```

相同 Plan 的行为完全不同，而且不好审计。

所以建议：

> v1 不支持任意环境变量展开。

只允许 StowCrate 自己定义的 portable variables。

------

# 十四、ArchiveUnitId 是最重要的稳定 ID

目前 Change Detection 已经暴露出：

> 缺少稳定 `ArchiveUnitId`

这个确实该现在解决。

Archive Unit：

```text
Source
└─ ProjectA
```

建议文档中：

```json
{
  "archiveUnitId": "...",
  "sourceId": "...",
  "path": "ProjectA"
}
```

这里：

```text
archiveUnitId
```

是 identity。

```text
path
```

只是：

> 当前定位。

所以用户把：

```text
ProjectA
```

改名为：

```text
ProjectAlpha
```

如果 UI 能确认这是 rename：

```text
ArchiveUnitId 保持不变
Path 改变
```

于是：

```text
History
Committed Baseline
ArchiveVersions
```

仍然属于同一个 Crate。

这会非常重要。

------

# 十五、但是自动发现的 FILE_MANAGED Archive Unit 怎么获得稳定 ID？

这是本设计里最棘手的一点。

例如：

```text
Project/
└─ .backupignore
```

`.backupignore` 本身目前并没有：

```text
ArchiveUnitId
```

如果我们只用路径：

```text
Project
```

当 identity，那么重命名后 ID 就变了。

我建议 v1 做一个很实用的折中：

> **FILE_MANAGED Archive Unit 的稳定 ID 由 `.backupignore` 支持一个可选 `@id` Directive。**

例如：

```text
@version 1
@id 6c3ad16a-ae76-4d21-9738-c70e6264c209
@mode exclude

target/
node_modules/
```

------

# 十六、为什么 `@id` 很值得加

因为这使：

```text
.backupignore
```

不仅声明：

```text
“这里是一个 Crate”
```

还可以声明：

```text
“这个 Crate 是谁”
```

目录：

```text
Project
→ ProjectRenamed
```

`.backupignore` 跟着目录移动：

```text
@id 不变
```

StowCrate 就知道：

> 同一个 Archive Unit 被移动/重命名了。

这对：

```text
History
Baseline
Current archive mapping
```

非常有价值。

------

# 十七、空 `.backupignore` 怎么办？

我们已经规定：

```text
空文件
```

必须合法。

所以：

```text
@id
```

不能强制要求用户手工写。

流程建议：

### 第一次发现没有 `@id`

例如：

```text
Project/.backupignore
```

Scanner/Planning 得到：

```text
Unidentified FILE_MANAGED Archive Unit
```

StowCrate 为它生成：

```text
ArchiveUnitId
```

但这里有两个选择。

------

# 十八、我建议不要自动修改用户文件

不要第一次扫描时偷偷：

```text
打开 .backupignore
写 @id ...
```

因为：

```text
Git working tree 突然 changed
```

用户会非常困惑。

所以默认：

```text
config.db
```

保存：

```text
FileManagedUnitRegistration
├─ SourceId
├─ RelativePath
├─ ArchiveUnitId
└─ optional observed file fingerprint
```

如果用户主动：

> “将稳定 ID 写入 `.backupignore`”

才加入：

```text
@id ...
```

------

# 十九、那么没有 `@id` 时 rename 怎么办？

v1 保守处理：

```text
path 改变
+
没有 @id
```

视为：

```text
旧 Unit 删除
+
新 Unit 创建
```

除非 UI 由用户明确确认：

> 这是同一个归档箱。

然后迁移 identity。

这比用：

```text
inode
FileId
目录内容 hash
```

猜测 rename 要安全得多。

所以：

> 稳定 rename identity 是 opt-in。

未来智能功能可以建议用户把 `@id` 写入文件。

------

# 二十、UI_MANAGED Archive Unit 不需要 `@id`

UI_MANAGED：

```text
config.db
```

本来就是 authoritative source。

因此：

```text
ArchiveUnitId
```

直接存在 config.db。

移动路径：

```text
ArchiveUnit.Path
```

改变即可。

非常简单。

------

# 二十一、`@id` 只能表示当前 `.backupignore` 所在 Archive Unit

不允许：

```text
@id foo
@child-id bar
```

这种东西。

`.backupignore` 保持局部、简单。

建议新增：

```text
@id <uuid>
```

仅一个。

并规定：

```text
duplicate @id → Fatal
invalid UUID → Fatal
```

------

# 二十二、FILE_MANAGED 和 `*.backupplan` 中的 ArchiveUnit 怎么关联

File-backed Plan 可以声明：

```json
{
  "archiveUnits": [
    {
      "archiveUnitId": "...",
      "sourceId": "...",
      "path": "Project"
    }
  ]
}
```

如果：

```text
Project/.backupignore
```

包含：

```text
@id = same uuid
```

完美对应。

如果没有 `@id`：

注册时建立 binding。

如果两个地方 ID 冲突：

```text
*.backupplan ArchiveUnitId = A
.backupignore @id = B
```

应该：

> **IdentityConflict → Fatal configuration validation**

绝不能猜哪个优先。

------

# 二十三、Archive Unit 的 path 是 Logical Relative Path

始终：

```text
/
```

分隔符。

例如：

```json
"path": "projects/StowCrate"
```

而不是：

```json
"path": "projects\\StowCrate"
```

更不能：

```json
"path": "E:\\code\\projects\\StowCrate"
```

所以：

```text
ArchiveUnit.Path
```

天然跨平台。

------

# 二十四、SourceRoot 是 Local Binding 的 anchor

例如：

```text
SourceId = source-code
```

本机绑定：

```text
SourceRoot =
E:\code
```

ArchiveUnit：

```text
projects/StowCrate
```

实际路径：

```text
E:\code\projects\StowCrate
```

Mac：

```text
SourceRoot =
/Users/foo/code
```

实际：

```text
/Users/foo/code/projects/StowCrate
```

这很好地利用了已经完成的：

```text
LogicalPath / RelativePath
```

模型。

------

# 二十五、ExternalSource 也用同样模式

不要：

```json
{
  "path": "C:\\Users\\foo\\.ssh\\config"
}
```

Portable document 可以：

```json
{
  "externalSourceId": "...",
  "name": "SSH Config",
  "archivePath": "machine/ssh/config"
}
```

本机 binding：

```text
ExternalSourceId
→ ${HOME}/.ssh/config
```

Mac/Linux 同样可以：

```text
${HOME}/.ssh/config
```

这会非常漂亮。

------

# 二十六、ExternalSource 是否必须绑定

如果某 Plan 声明了：

```text
ExternalSourceId = ssh-config
```

但当前设备：

```text
没有 binding
```

我建议默认：

```text
PlanNotReady
```

而不是：

```text
默默跳过
```

因为 External Source 本来就是用户明确要求备份的数据。

除非以后增加：

```text
optional = true
```

v1 可以先不支持 optional。

------

# 二十七、Binding 需要设备身份吗？

建议需要。

至少 config.db 中有：

```text
DeviceId
```

生成一次 UUID。

然后：

```text
PlanBinding
├─ PlanId
├─ DeviceId
...
```

这样未来：

```text
同步 config.db backup
```

或者：

```text
恢复多个设备信息
```

不会混。

不过：

> DeviceId 不进入 `*.backupplan`。

它是 local runtime identity。

------

# 二十八、Device Name 只是显示

例如：

```text
DeviceId = UUID
DeviceName = "Jickfu-PC"
```

不要用：

```text
hostname
```

当稳定身份。

因为用户改电脑名：

```text
Jickfu-PC
→ DESKTOP
```

不应该变成新设备。

------

# 二十九、一个完整的 portable plan 例子

未来 JSON 可以接近：

```json
{
  "schemaVersion": 1,

  "planId": "61a36cf2-d0ea-428d-a92a-5ae418de11f8",
  "name": "My Code",

  "sources": [
    {
      "sourceId": "bf299a5b-bc5e-437c-ab1b-5b46e55f99f4",
      "name": "Code"
    }
  ],

  "archiveUnits": [
    {
      "archiveUnitId": "137ce904-d72b-48fd-97c6-f560388ff1af",
      "sourceId": "bf299a5b-bc5e-437c-ab1b-5b46e55f99f4",
      "path": "StowCrate"
    }
  ]
}
```

而本机 config：

```text
Plan 61a...

Device 4a2...

SourceBinding:
bf299...
→ E:\cloud\code

CurrentRoot:
→ D:\Backup\Code

HistoryRoot:
→ E:\BackupHistory\Code
```

Mac：

```text
bf299...
→ /Users/foo/code

CurrentRoot
→ /Volumes/Backup/Code
```

同一个 Plan Document，完全没有 Windows 盘符污染。

------

# 三十、Binding 变化不触发 archive semantic fingerprint

例如：

```text
SourceRoot
E:\code
→
D:\code
```

如果逻辑数据完全一样：

```text
SelectionFingerprint
ArchiveSpecFingerprint
```

不应该因为物理位置改变而变化。

同理：

```text
CurrentRoot
HistoryRoot
```

也不进入 backup semantic fingerprint。

但是 Source 内容重新扫描后：

```text
EntrySetFingerprint
```

自然决定是否真的相同。

------

# 三十一、SourceId 变化则一定是语义变化

如果把 ArchiveUnit：

```text
SourceId=A
Path=Project
```

改成：

```text
SourceId=B
Path=Project
```

即使两个目录内容碰巧一样：

> 这也是 logical source identity 变化。

应该进入：

```text
SelectionFingerprint
```

因为备份数据的来源语义发生了变化。

------

# 三十二、ArchiveUnitId 本身要不要进入 fingerprint？

这里要区别。

我建议：

```text
ArchiveUnitId
```

进入：

> manifest / version identity / baseline key

但**不直接导致 archive bytes rebuild**。

也就是说用户 Clone Plan：

```text
生成新的 UnitId
```

但内容和规则相同，技术上 archive 内容可能相同。

不过 Clone 本身是另一个 Plan，通常首次运行没有 baseline，自然会执行 backup。

因此不需要：

```text
UnitId 改变 → ArchiveSpecChanged
```

硬编码这种逻辑。

------

# 三十三、路径大小写问题

ArchiveUnit logical path 应保存：

> 用户/文件系统观察到的 canonical display spelling。

例如：

```text
Projects/StowCrate
```

匹配时：

```text
Windows insensitive
Linux sensitive
```

依照已经定好的 Filesystem/Rule semantics。

ID 不受大小写影响。

如果 insensitive FS：

```text
foo
→ Foo
```

是否视为 rename 可以由 Infrastructure 识别，但 identity仍靠 UnitId。

------

# 三十四、不要把 realpath 当 identity

这是一个值得写入规范的禁止项。

例如：

```text
realpath(source)
```

绝不能产生：

```text
SourceId
```

因为：

```text
磁盘换了
mount point 变了
用户目录变了
```

realpath 都会变。

身份应该是：

> 显式、持久、逻辑的。

路径只是 binding/location。

------

# 三十五、建议正式确定这些 ID 生命周期规则

| 操作                     | PlanId       | SourceId     | ArchiveUnitId |
| ------------------------ | ------------ | ------------ | ------------- |
| 修改名称                 | 保持         | 保持         | 保持          |
| 修改本机路径绑定         | 保持         | 保持         | 保持          |
| Archive Unit rename/move | 保持         | 保持         | 保持          |
| Export                   | 保持         | 保持         | 保持          |
| Import                   | 保持         | 保持         | 保持          |
| Managed ↔ File-backed    | 保持         | 保持         | 保持          |
| Save As 文档副本         | **保持**     | 保持         | 保持          |
| Clone Plan               | **重新生成** | **重新生成** | **重新生成**  |

这里注意：

```text
Save As
```

只是：

> 同一个 Plan Document 的另一份物理文件。

所以 identity 保持。

```text
Clone
```

才是新逻辑 Plan。

------

# 三十六、Binding 是否导出？

普通 `.backupplan`：

> **不导出。**

否则会泄露：

```text
C:\Users\真实用户名
公司目录
磁盘结构
```

并破坏 portability。

以后可以有一个独立：

```text
Device Binding Export
```

但不是 Backup Plan v1 的职责。

------

# 三十七、SecretReference 也是 binding/runtime

同理：

```text
SecretReferenceId
```

不建议把当前机器的：

```text
Windows Credential Manager ID
```

写到 portable plan。

Portable plan 可以表达：

```text
encryption:
  mode: secure
  secretSlot: archive-password
```

本机：

```text
secretSlot archive-password
→ SecretReference X
```

这跟 Source binding 是同一种模式。

这个细节后面 Encryption P0 再完善。

------

# 三十八、我建议把第 2、3 个 P0 正式合并成两条规范结论

### P0 — Identity

> BackupPlan、BackupSource、ArchiveUnit 和 ExternalSource 使用持久稳定 UUID 身份。Identity 与名称、逻辑路径、物理路径、数据库主键分离。Import/Export/authority 转换保持身份；Clone 才生成全新身份。UI_MANAGED ArchiveUnit 的 ID 存储于 config.db；FILE_MANAGED ArchiveUnit 可通过可选 `.backupignore @id <uuid>` 持久化身份，没有 @id 时路径变化不自动推断为同一 Unit。

### P0 — Portable Path & Local Binding

> `*.backupplan` 不保存设备绑定的绝对 Source/Current/History 路径作为 portable configuration。Source、ExternalSource 等由稳定逻辑 ID 描述，设备上的实际路径存储为 Local Binding。ArchiveUnit path 永远是相对于 BackupSource 的 `/` 分隔逻辑路径。Local Binding 可以支持 StowCrate 定义的 `${HOME}` 等有限 portable variables，但 v1 不展开任意环境变量。Local Binding 和文件物理位置不进入 archive semantic fingerprint。

------

## 接下来我建议

这两项确定以后，**暂时还不要写 JSON Schema**。

下一项应该先设计：

> **Global Rules / Plan Rules / Local Rules 在 `\*.backupplan` 中分别保存什么，以及 FILE_MANAGED `.backupignore` 和 Backup Plan ArchiveUnit declaration 到底如何关联。**

因为它会直接决定：

```json
"rules": ...
"archiveUnits": ...
```

的结构。

把这个 P0 定完之后，`*.backupplan` 的主体模型基本就成形了。
