# StowCrate 技术架构

## 1. 架构目标

StowCrate 采用 C#、.NET 10、Avalonia 和 MVVM，核心为平台无关的模块化架构。首版同时交付 Windows、macOS 和 Linux，不得把任一平台的路径、API 或调度模型写入领域层。

系统优先保证：长期可恢复、规划可解释、写入原子性、失败不破坏上次有效备份、秘密不泄漏，以及无平台优化时仍能正确工作。

## 2. 解决方案结构

```text
StowCrate.slnx
├─ src/
│  ├─ StowCrate.Core
│  ├─ StowCrate.Application
│  ├─ StowCrate.Infrastructure
│  ├─ StowCrate.Archiving
│  ├─ StowCrate.App
│  └─ StowCrate.Cli                 (计划中的无头入口)
└─ tests/
   ├─ StowCrate.Core.Tests
   ├─ StowCrate.Application.Tests
   ├─ StowCrate.Infrastructure.Tests
   ├─ StowCrate.Archiving.Tests
   └─ StowCrate.Architecture.Tests
```

### StowCrate.Core

纯领域模型与确定性规则：

- `BackupPlan`、`BackupSource`、`ArchiveUnit`、`ArchiveBoundary`；
- `RuleSet`、`BackupRule`、`RuleMode`、`RuleSource`；
- `ArchivePlan`、`ArchiveEntry`、`ArchiveVersion`、`RetentionPolicy`；
- 逻辑路径、归档内路径、结果与错误类型。

不得依赖 Avalonia、SQLite、7-Zip、进程调用或 OS API。

### StowCrate.Application

用例、工作流和端口：

- 扫描源与识别 Archive Unit；
- 合并规则、构建并预览 `ArchivePlan`；
- 检测变化、执行备份、发布 Current、管理 History；
- 导入/导出 `.backupignore` 和 Backup Plan 文件（`*.backupplan`）；
- 智能项目识别与建议确认；
- 恢复、验证、配置快照和任务结果查询。

接口示例：`IFileSystem`、`IArchiveWriter`、`IPlanRepository`、`IFileStateStore`、`ISecretStore`、`IJobScheduler`、`IClock`、`IPlatformMetadata`。

### StowCrate.Infrastructure

实现应用端口：

- SQLite `config.db`、`cache.db` 与迁移；
- 物理文件系统、路径映射、staging 与原子替换；
- `.backupignore`/`*.backupplan` 序列化；
- Windows Task Scheduler、launchd、systemd timer/cron 适配器；
- Credential Manager/DPAPI、Keychain、Secret Service 适配器；
- 可选 USN Journal、FSEvents、inotify 加速器。

### StowCrate.Archiving

实现 `IArchiveWriter` 等端口：

- 7-Zip/7zz 进程适配；
- ZIP；
- TAR.ZST 与平台元数据策略；
- 分卷、加密、测试、哈希和能力探测。

外部可执行文件的许可、版本、路径和能力必须显式管理。应用层不得拼接命令行。

首版发行包随平台携带固定兼容版本的 7-Zip/7zz；Archiving 通过能力探测验证随包二进制，打包流程必须附带对应许可证和第三方归属信息。

### StowCrate.App / StowCrate.Cli

二者是组合根：注册依赖、解析用户输入并调用相同应用用例。GUI 负责 Avalonia Views、ViewModels、导航、进度和交互；CLI 负责非交互命令、退出码和调度器集成。两者不实现备份规则。

模板阶段允许直接创建无依赖的 `MainViewModel`。一旦出现 Scanner、Repository、ArchiveWriter 等业务服务，App/CLI 使用 `Microsoft.Extensions.DependencyInjection` 作为轻量组合容器；ViewModel 不得直接构造 Infrastructure 或 Archiving 实现。只有在后台生命周期、配置和日志需求确实需要时才引入完整 Generic Host。

## 3. 依赖方向

```text
StowCrate.App ─────────┐
StowCrate.Cli ─────────┤
                       ▼
             StowCrate.Application ──▶ StowCrate.Core
                       ▲                        ▲
                       │                        │
StowCrate.Infrastructure ──────────────────────┘
StowCrate.Archiving ───────────────────────────┘
```

Infrastructure 和 Archiving 只实现向内层定义的端口。组合根选择具体实现。禁止 Core 反向引用外层项目。

## 4. 规划算法

执行前必须先生成不可变、可审阅的 `ArchivePlan`，不得边扫描边直接改写 Current。

```text
BackupPlan
  → SourceSnapshot
  → Archive Unit Discovery / Tree
  → Rule Resolution
  → ArchivePlan
  → Dry-run / Preview
```

`SourceSnapshot` 是 Scanner 输出给纯规划内核的不可变边界。同一份 Source Snapshot、BackupPlan 与规则必须生成内容、顺序和 fingerprint 均相同的 ArchivePlan。Milestone 1 先用可构造快照验证规划内核；真实文件系统 Scanner 在首版符号链接策略确定后实现。

1. 解析设备路径映射并按目标平台规则规范化路径；验证 `SourceRoot`、`CurrentRoot`、`HistoryRoot` 两两不相等且不存在任何祖先/子孙关系，并验证 staging 不会形成递归输入。
2. 发现 UI 管理或 `.backupignore` 管理的 Archive Unit。
3. 构建 Archive Unit 树；把所有直接子单元注册为父单元的停止边界。
4. 分别合成每个单元的全局、方案和局部规则。
5. 遍历单元内容；先检查边界，再匹配规则。边界优先于普通 include/exclude。
6. 解析外部源映射和归档内逻辑路径，检测路径冲突。
7. 生成规范排序的 entries、警告、预估与变更指纹。
8. 只有经过用户确认或无头策略允许的计划才进入执行阶段。

路径匹配器必须独立测试以下情况：不同分隔符、根路径、`!`、`*`、`**`、尾随斜杠、Unicode、大小写敏感文件系统和嵌套边界。Scanner 实现时还必须独立测试符号链接循环及逃逸 Source 的链接。

## 5. 执行与原子发布

单个 Archive Unit 的执行状态机：

```text
Planned
  → Staging (仅外部源/清单等需要时)
  → Writing <name>.<format>.partial on Current filesystem
  → Archive Test
  → SHA-256 / manifest verification
  → Persist previous Current as a History Version while Current remains valid
  → Verify durable History Version
  → Atomic replace Current on Current filesystem
  → Commit metadata/cache
  → Cleanup
```

关键约束：

- 新归档的 `.partial` 必须创建在 `CurrentRoot` 所在文件系统，确保最终发布可使用同文件系统原子替换；
- 保存历史版本时不得先移走或删除旧 Current。History 与 Current 在同一文件系统时可采用经过验证的链接、克隆或复制策略；跨文件系统时采用“复制到 History 临时文件 → 完整性验证 → History 内原子发布”。这些是持久化策略，不是 Current 的事务切换；
- 启用历史时，历史版本未持久化并验证成功就不得替换 Current；历史持久化失败时任务失败，旧 Current 保持原位；
- 最终切换只在 Current 所在文件系统内执行 atomic replace。替换前崩溃时旧 Current 有效，替换后崩溃时新 Current 有效，不能出现先移走旧 Current 导致 Current 路径缺失的窗口；
- 取消、断电或进程失败后，`.partial` 不得被识别为有效备份；
- 启动时可识别并安全清理陈旧 staging/partial，但必须保留诊断信息；
- 并发任务对同一 Backup Plan 或输出路径加锁；不同单元可在资源预算内并行；
- 日志和结果必须记录被跳过、不可读、变化中或锁定的文件。

无历史或首次发布时，同样只允许把已验证的目标文件系统临时文件原子发布为 Current。元数据提交必须可从 Current 与 History 的实际文件状态重建，不能成为判断归档有效性的唯一依据。

## 6. 变更检测

便携基线为：规范化相对路径、类型、大小、`mtime`，必要时追加内容哈希。每个文件状态关联 Archive Unit 和上次成功版本。

```text
config.db  — 计划、规则、调度、历史策略、归档版本与必要审计（重要）
cache.db   — 文件状态、哈希、扫描缓存和平台游标（可重建）
```

只有成功发布后才能把本次状态设为基线。失败或取消的扫描不得造成下一次误判为“未变化”。USN/FSEvents/inotify 只减少扫描范围，检测结果仍能回退到便携算法。

## 7. SQLite 与配置恢复

- 使用 schema migration 和事务；数据库 DTO 不直接充当领域对象。
- `config.db` 的灾难恢复副本通过 SQLite Online Backup API 生成一致的 `config.snapshot.db`。
- `cache.db` 不备份，缺失时自动重建。
- 秘密只保存引用 ID；值位于平台 Secret Store。
- `*.backupplan` 和归档 manifest 使用显式 `schemaVersion`，读取器对未知新版本安全失败并给出可操作提示。

## 8. 归档清单

每个归档内保留 `__stowcrate__/manifest.json`，建议字段：

- schema、StowCrate 版本、archive/plan/unit ID；
- 逻辑源、归档路径和创建时间（UTC）；
- 格式、压缩、分卷、保护模式的非秘密描述；
- 文件数、原始/归档大小、规则摘要、排除的子 Archive Unit；
- 内容或卷的 SHA-256、上一个版本 ID；
- 跨平台元数据能力与未保留项警告。

manifest 不保存真实密码、密钥、token 或不必要的主机隐私信息。

## 9. 平台抽象

| 能力 | Windows | macOS | Linux | 便携回退 |
|---|---|---|---|---|
| Secret Store | Credential Manager/DPAPI | Keychain | Secret Service/libsecret | 无明文回退；要求用户输入 |
| Scheduler | Task Scheduler | launchd | systemd timer/cron | 手动/外部调用 CLI |
| Change hint | USN Journal | FSEvents | inotify | 全量元数据扫描 |
| Metadata | NTFS ACL/ADS | APFS xattr/ACL | POSIX/xattr/ACL | 明确警告并保存可支持子集 |
| Consistent files | VSS（后续） | 平台快照（后续） | LVM/文件系统能力（后续） | 普通读取并报告不一致风险 |

任何专有能力都不得成为 Core 的必要条件。

## 10. 安全与可靠性

- 归档密码不出现在命令行、日志、崩溃报告或进程列表；若 CLI 工具无法满足，应通过受控输入或库接口解决。
- `.backupignore`、`*.backupplan` 和源路径均视为不可信输入，防止路径穿越和归档条目逃逸。
- 默认不跟随会逃离 Source 的符号链接；最终策略必须可配置且防循环。
- 解压/恢复前检测目标覆盖、路径穿越、磁盘空间和大小写冲突。
- 第三方归档工具在发布包中固定兼容版本并附带许可说明。

## 11. 测试策略

- **Core 单元测试**：规则语义、层级边界、路径规范化、ArchivePlan 稳定性。
- **Application 测试**：变化/未变化、History 切换、取消、失败补偿、预览与结果。
- **Infrastructure 集成测试**：SQLite migration/backup、文件锁、原子替换、调度适配。
- **Archiving 契约测试**：每种格式的创建、测试、恢复、加密、Unicode、大文件和分卷。
- **跨平台测试**：Windows/macOS/Linux CI，大小写、权限、链接和长路径 fixture。
- **故障注入测试**：写入中断、空间不足、损坏归档、进程退出、Current/History 移动失败。
- **架构测试**：验证项目依赖方向、Core 不引用 Avalonia/SQLite、Application 不引用外层项目，以及 ViewModel 不直接引用 SQLite。

初始完成标准是 `dotnet build` 和所有自动化测试通过；涉及布局或交互的变更还需 Avalonia UI 验证。

## 12. 工程与依赖治理

- `global.json` 以仓库创建时实际使用的 `10.0.400` 为基线，并在 .NET 10 内按 `latestFeature` 前滚；
- `Directory.Build.props` 统一目标框架、Nullable、ImplicitUsings、分析级别、警告策略和确定性构建；
- `Directory.Packages.props` 开启 NuGet Central Package Management，所有项目文件只声明包名，不重复声明版本；
- `.editorconfig` 统一 UTF-8、LF、缩进、namespace、using、`var` 和命名规则；
- GitHub Actions 在 Windows、Linux、macOS 上执行 restore、Release build 和 test；
- `StowCrate.Architecture.Tests` 持续验证项目依赖方向以及 UI、数据库依赖没有泄漏进内层。

## 13. 分阶段实现建议

1. 领域模型、路径与规则引擎、Archive Unit 树及计划预览；
2. 本地文件系统、SQLite、ZIP/7z 适配、原子 Current 发布；
3. 变更检测、独立 History、恢复与完整性验证；
4. Avalonia 配置树、智能项目识别、导入导出；
5. CLI 与系统调度器；
6. TAR.ZST、平台元数据与高级文件系统优化。

阶段顺序可调整，但不得用 GUI 直接调用压缩进程绕过 Application 层，也不得以云端集成替代本地可靠性。
