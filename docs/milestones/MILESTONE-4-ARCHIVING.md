# Milestone 4 — Archive Writer & Verified Archive Artifact

## 目标

把 `ExecutionReadyArchiveUnit` 确定性写成经过验证、工具无关的标准归档 artifact：

```text
ExecutionReadyArchiveUnit
  → Archive capability resolution
  → private staging / generated manifest materialization
  → .partial archive write
  → archive test + integrity computation
  → Verified Archive Artifact
```

## 本阶段范围

- 冻结 Application 的 Archive Writer、capability与 verification ports；
- Infrastructure/Archiving实现首批 `.7z`、`.zip`、`.tar.zst` adapters及明确 capability diagnostics；
- 生成并验证 `__stowcrate__/manifest.json`，确保不包含 physical path或 SecretValue；
- 所有输出先写目标文件系统内唯一 `.partial`，支持取消、失败诊断与陈旧 partial识别；
- archive完成后执行格式级 test/读取验证，并计算最终 artifact SHA-256与长度；
- Secure protection只通过 transient Secret material lease传给 adapter，不进入参数日志、环境、manifest或持久状态；
- capability、Unicode、链接/metadata保真、single-volume与损坏 archive契约测试。

## 明确不做

- Physical Current/History Publisher、atomic Current replace或 History capture；
- Output Reorganization、Storage Relocation与 retention artifact cleanup；
- Avalonia UI或云上传；
- 专有分块仓库、multi-volume、raw archiver CLI options；
- 在 M4 writer完成前把 verified artifact误标为 Published Current或推进 baseline。

## 完成标准

- 每个支持格式均能从同一 `ExecutionReadyArchiveUnit` 生成标准、可由第三方工具读取的 archive；
- unsupported capability在写入前明确失败，不静默降级 format/compression/protection；
- `.partial` 永不被识别为有效 archive，取消/失败不覆盖任何已发布 artifact；
- manifest与 archive entries匹配 Candidate ownership/path语义；
- archive test与 SHA-256/length全部成功后才返回 Verified Artifact；
- Secret material、physical binding与进程敏感信息不出现在 durable bytes或 diagnostics；
- build、格式契约测试与跨平台 CI通过。
