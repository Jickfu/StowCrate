# StowCrate（归匣）

> A developer-friendly structured archive backup tool.  
> 面向开发者和个人重要资料的结构化归档备份工具。

StowCrate 将目录整理成一组可理解、可独立恢复的标准归档文件，而不是只有原软件才能读取的专有备份仓库。

项目处于设计与工程初始化阶段。

## 设计原则

- 普通 `.7z`、`.zip`、`.tar.zst` 就是最终备份数据；
- 使用层级归档箱避免一个巨大压缩包，也避免重复打包；
- 识别开发项目并推荐排除可重建内容；
- 同时提供可视化规则和 `.backupignore` / `.backupplan`；
- Current Backup 与 History Store 分离，方便交给任意同步工具。

## 文档

- [产品设计](docs/PRODUCT.md)
- [技术架构](docs/ARCHITECTURE.md)
- [仓库开发约束](AGENTS.md)

## 当前状态

当前只建立项目文档与初始解决方案骨架，尚未实现业务功能。待确认事项统一记录在产品设计文档中。

