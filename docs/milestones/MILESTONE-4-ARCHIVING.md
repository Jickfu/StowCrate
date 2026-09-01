# Milestone 4 — Archive Writer & Verified Archive Artifact

## 目标

把当前 Application 已冻结的 `ExecutionReadyArchive` 确定性写成经过验证、工具无关的标准归档 artifact。早期里程碑使用过 `ExecutionReadyArchiveUnit` 草案名；M4 不重命名既有 API，而以明确的 `ArchiveBuildRequest` 作为单元构建输入：

```text
ExecutionReadyArchive → ArchiveBuildRequest
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

## M4.1 — Archive Materialization + Writer / Manifest / Verification Contract（COMPLETE）

M4.1 冻结 `ArchiveBuildRequest`、`IArchiveInputMaterializer`、`MaterializedArchiveInput`、`IArchiveFormatWriter`、`IArchiveArtifactVerifier`、`ArchiveBuildResult`、`VerifiedArchiveArtifact` 与 runtime-only workspace/partial handle。Application workflow 只编排工具无关 port；backend executable、path、switch 与 process transport 留在 Archiving。

materialization 是 writer 前的正式安全边界：normal/external Candidate 只按已冻结 owner/path 从 physical binding no-follow 重新读取到 private staging，不重新扫描或解释 child Archive Boundary；kind、size、UTC mtime、metadata 与 link identity 必须一致。Strict regular file 对 staged bytes 重算 full SHA-256，`.backupignore` 无条件校验 raw-byte SHA-256。任何 drift 统一为 `InputChangedDuringMaterialization`，writer 一旦开始只能读取 staging。

Archive Manifest v1 已由 `schemas/archive-manifest-v1.schema.json` 冻结，使用 deterministic UTF-8 JSON。它记录 schema/archive semantics、Plan/Source/ArchiveUnit identity、unit logical path、effective ArchiveSpec 与按 archive path ordinal 排序的 normal/external payload metadata；manifest 不列出自己，也不包含 physical/device/storage/secret/staging/process 信息。expected archive entry set 为 payload 加唯一 `__stowcrate__/manifest.json`。

verification 必须依次完成 format test、entry path/kind exact set、archive 内 manifest strict validation 与 Candidate cross-check、最终 archive bytes SHA-256/length。全部成功后才执行 `ArchiveVersion.Prepare(...).Verify(hash,length)` 并返回 `VerifiedArchiveArtifact`；`.partial` 即使可读也不是 Current 或 durable Verified state。M4.1 不创建 `CurrentVersion`、History placement、PublishIntent 或 Committed Baseline。

`ResolvedArchiveCapability` 已收紧为显式 Format、CompressionPreset、Protection、link semantics、metadata semantics、single-volume 与 versioned capability semantics；必须与 EffectiveArchiveSpec 精确匹配，不允许 silent downgrade。Secure writer 仅可接收 transient zeroizable `SecretMaterialLease`，取消/失败必须等待 adapter 返回、dispose lease，并 best-effort cleanup staging/partial；cleanup failure 单独形成结构化 warning。

M4.1 使用 fake/in-memory archive adapter 验证完整 workflow，并用真实 filesystem materializer 测试 normal/external、child-boundary、Standard/Strict/`.backupignore` drift。本项不集成 7zz，不实现 Physical Publisher。

### 后续 backend 约束

1. 7zz CLI 的 `-p{password}` 会把 Secret 暴露到 process argv，明确禁止。M4.2 必须先验证不经过 argv/environment 的安全 Secret transport；在能够证明安全前，7zz 对 Secure/相关 Privacy capability 必须返回 Unsupported，不能退化。
2. 当前官方 7-Zip packing format 列表未明确包含 ZSTD，TarZstd backend 不得假定单个 `7zz -tzstd` 可完成。TAR + Zstd 的实现与验证在后续独立确定。

## 下一项

**M4.2 SevenZip/ZIP Backend + bundled 7zz capability probe + Secure secret-transport spike**。仍不实现 Physical Current/History Publisher。

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
