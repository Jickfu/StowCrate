# StowCrate 仓库开发约束

## 文档真相源

本仓库实现 **StowCrate（归匣）**：一款跨平台、开发者友好的结构化归档备份工具。

修改产品行为或技术架构前，必须先阅读：

- `docs/PRODUCT.md`：产品行为、术语、范围和未决产品问题；
- `docs/ARCHITECTURE.md`：模块边界、依赖规则、持久化与执行设计。

如果文档发生冲突，以用户较新的明确决定为准。不得擅自把文档中的“未决项”固化成永久设计。

## 不可擅自改变的产品决定

- 备份输出必须是普通、工具无关的标准归档文件（`.7z`、`.zip` 或 `.tar.zst`），不得采用专有分块仓库作为最终数据。
- **归档箱（Crate）**是产品用语，**归档单元（Archive Unit）**是领域模型和代码用语。
- Archive Unit 是层级边界。父单元扫描到子单元时必须停止进入，避免内容重复。
- 在文件管理模式中，`.backupignore` 的存在既表示该目录是 Archive Unit，也提供该单元的局部过滤规则；空文件有效。
- 规则分为全局规则、Backup Plan 规则和局部规则（`.backupignore` 或等价的 UI 配置）三层；子 Archive Unit 独立规划。
- UI 配置并保存到 SQLite 是默认方式；`.backupignore` 是高级用户的“配置即代码”方式。同一个 Archive Unit 不得同时由两种规则来源控制。
- Source、Current Backup 输出和 History Store 必须位于相互分离的位置；严禁把归档输出写回源目录。
- 只有 Archive Unit 发生变化时才生成历史版本。Current 输出保持干净，可直接交给第三方工具同步。
- 首版不负责上传云端。StowCrate 只生成本地归档，用户自行选择同步方式。
- 同时支持手动与定时执行。定时任务必须通过 CLI/无头入口调用应用用例，不得自动操作 GUI。
- 核心行为必须跨平台。平台专用服务只能作为适配器或可选优化。
- 密码、令牌和恢复密钥不得写入 `config.db`、`.backupplan`、归档清单、日志或版本控制文件。

## 架构约束

- 使用 C#、.NET 10、Avalonia 和 MVVM。
- `StowCrate.Core` 放置领域概念，不得依赖 UI、SQLite、归档程序或操作系统 API。
- `StowCrate.Application` 放置用例与端口，只能依赖 Core。
- `StowCrate.Infrastructure` 实现持久化、文件系统、配置、调度和平台端口。
- `StowCrate.Archiving` 实现归档适配器，依赖抽象而不是 UI。
- `StowCrate.App` 与未来的 `StowCrate.Cli` 是组合根。业务规则不得放进 View、ViewModel 或命令行处理器。
- USN Journal、FSEvents、inotify 等平台加速器必须具备便携回退方案。
- 归档先写入 `.partial` 临时文件，完成测试和完整性计算后再原子发布。不得用未验证结果覆盖有效 Current。
- 一致的 SQLite 配置快照必须通过 SQLite Online Backup API 创建，不得直接复制正在使用的数据库文件。

## 工作规范

- 变更应保持小而明确，并遵守文档中的 MVP 范围；未经产品决定，不得加入云存储、块级去重、磁盘镜像或双向同步。
- 在领域边界使用逻辑路径或可移植路径，不得假设盘符、大小写不敏感或 Windows 路径分隔符。
- 扫描、哈希、压缩等长时间操作必须支持取消。
- 对可恢复的单文件错误进行明确报告，不得静默漏掉数据；成功结果必须准确列出遗漏和警告。
- 行为变更必须补充测试，重点覆盖规则优先级、路径规范化、嵌套 Archive Unit、变更检测和原子发布。
- 完成实现工作前必须运行 `dotnet build` 和相关测试。

