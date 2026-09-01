# Milestone 4 — Archive Writer & Verified Archive Artifact（COMPLETE）

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

## M4.2 — SevenZip / ZIP Backend + bundled 7zz Capability Probe + Secure Secret-Transport Spike（COMPLETE）

M4.2 修复了三个 prerequisite blocker：同一 `SecretMaterialLease` 现在覆盖 writer 与 verifier 的完整 build verification window，直到成功、失败或取消后才 dispose/zeroize；verifier 不重新读取 durable secret binding。materializer 复用 M2 `IPhysicalFileSystem.Inspect` no-follow primitive，不再用 `File.Exists/Directory.Exists` 判断输入对象，能够观察并 staging dangling symbolic link；普通文件/目录的 Candidate mtime、attributes 与 Unix executable 会实际投影到 staging 并重新检查。`ArchiveCapabilityRequirements` 由 Candidate payload 的 link/metadata需要形成，Readiness 与 Build 均要求 capability 完整满足，ArchiveSpec 相同不再足以掩盖 fidelity 丢失。

bundled backend 固定为官方 7-Zip **26.02 (2026-06-25)**。descriptor 固定每个受支持 RID 的官方 package SHA-256 与解包后 executable SHA-256；acquisition/packaging script只从 pinned release下载并验证，不搜索 system `7z/7zz`。probe验证 executable integrity、26.02 version和7z/ZIP format，Application capability只接收不含绝对路径的 semantic identity。Windows官方 standalone console文件名为`7za.exe`，Linux/macOS为`7zz`，这是 Archiving/packaging detail。

None protection 的 SevenZip/ZIP真实 writer/verifier已实现。所有 invocation使用`UseShellExecute=false`与`ArgumentList`，显式`-t7z/-tzip`，限制输出，取消时kill entire process tree并等待退出。writer只以private staging为working directory，通过不含secret的UTF-8 input list固定精确entry set；verifier按exit code执行format test，以26.02 technical listing parser读取path/kind，只用`-so`读取唯一manifest，最终hash/length仍由M4.1 workflow cross-check。

compression mapping冻结为`Store=-mx=0`、`Fast=-mx=3`、`Standard=-mx=5`、`Extreme=-mx=9`。7z固定container、LZMA2与solid；ZIP固定container，Store使用Copy，其余使用Deflate。版本、mapping、format、protection、link/metadata、single-volume与secret transport结论均进入`CapabilitySemantics`。

Secure stdin spike的结论是 **Unsupported**：当前Windows 26.02 official console在`-p` switch + redirected stdin下可以创建archive，但同一Unicode material无法被后续test可靠重用，因此不能证明write/test/list/read-manifest完整窗口。绝不使用`-pPassword`，也没有argv/env/response-file fallback。7z/ZIP Secure在所有平台继续Unsupported，直到未来实现能够在三平台证明可靠与无泄漏的transport。ZIP即使未来启用AES-256，也不能宣称具备7z header/name encryption同等的metadata confidentiality。

Privacy继续Unsupported；TarZstd不由此resolver声明；symbolic-link与POSIX metadata组合在跨平台create/list/extract fidelity matrix完成前Unsupported。None + no-links + PortableBasic metadata是M4.2唯一已声明的真实backend capability。CI在Windows/Linux/macOS获取并验证对应26.02官方资产，再运行format contract tests；第三方notice与upstream License.txt进入release acquisition要求。

## M4.3 — Privacy Recovery Envelope + TarZstd Backend + Cross-format Metadata/Link Fidelity（COMPLETE）

metadata requirement/capability已由误导性的PortableBasic/Posix二分收敛为feature contract：UTC mtime独立表达，ReadOnly、Hidden、Executable使用required/preserved `SourceMetadata` flag mask并按required ⊆ preserved判定。现有SevenZip/ZIP只声明已由真实fixture证明的mtime，不再因格式名称隐式宣称完整POSIX fidelity。

TarZstd None v1使用.NET 10 `System.Formats.Tar` PAX writer/reader与固定`ZstdSharp.Port 0.8.8`（upstream zstd 1.5.7）直接流式处理`.partial`，不调用system tar/zstd，也不生成中间tar。mapping固定为Fast=1、Standard=6、Extreme=19，Store明确Unsupported；frame content checksum显式开启。PAX条目按archive path ordinal写入，uid/gid固定为0、user/group name固定为空，不继承host identity；writer只读取materialized staging。verifier以Zstd decompression stream和TarReader读取到EOF，收集实际path/kind并读取唯一manifest，最终hash/length仍针对原tar.zst bytes。

Privacy Recovery Envelope v1由`schemas/privacy-recovery-envelope-v1.schema.json`冻结为closed-world deterministic JSON结构，固定路径为`__stowcrate__/recovery.json`。每轮以crypto RNG生成32 bytes并编码成无padding base64url ASCII；它不属于SecretStore/SecretSlot/SecretRevision或普通manifest，也不得进入日志/diagnostics。Privacy Candidate的generated entry set为manifest加recovery envelope，随机material不进入EntrySetFingerprint或ArchiveSpecFingerprint；privacy/carrier semantics version进入generated plan与capability语义。Privacy仍明确不提供机密性保证。

7z/ZIP mixed-encryption未达到可声明capability的三平台完整证据门槛：公开recovery entry、stdin ASCII material加密payload/manifest、无password第三方读取recovery、携material完成test/list/read/extract及archived bytes一致性必须同时通过。禁止`-pRecoveryKey` argv fallback；在矩阵完成前SevenZip/ZIP Privacy继续Unsupported。TarZstd Privacy明确Unsupported，不使用skippable frame或私有wrapper。

最终保守矩阵：SevenZip/ZIP None支持mtime，不声明metadata flags或symbolic links；TarZstd None全RID支持mtime和ReadOnly，Linux/macOS另支持Executable与symbolic links，Windows不声明Executable/link；Hidden以及junction/mount point/other link均Unsupported。未来只有create → verify → private extract → no-follow re-observation fixture在目标RID通过后才能扩大矩阵，禁止silent loss。

本项未实现Physical Current/History Publisher、Secure native backend或M3 config.db durable schema。

## 下一项

## M4.4 — Archive Backend Integration Matrix + M4 Completion Review（COMPLETE）

M4.4修复Recovery Envelope format token fail-open、TarZstd corruption taxonomy与过期Privacy diagnostic；新增Archiving backend catalog/dispatcher，按0/1/多exact match分别返回Unsupported、唯一backend或ambiguous configuration failure。

最终矩阵以GitHub Actions三平台真实运行作为authority。Windows/Linux/macOS均验证bundled 7-Zip 26.02 integrity、7z/ZIP Unicode/empty-directory/mtime与真实extract；ZIP另由.NET `ZipArchive`读取。TarZstd验证PAX + Zstd标准流、checksum、corruption failure、compression mapping、metadata capability与byte reproducibility。Windows fixture发现并促成7zz console/ZIP name UTF-8参数显式冻结。完整结论见`docs/reviews/MILESTONE-4-COMPLETION-REVIEW.md`。

Privacy Envelope contract已冻结但所有backend Privacy仍Unsupported；Secure同样是合法portable configuration但当前无backend支持。两者均能在写入前准确阻止，因此不阻止M4关闭。

## 下一项

**Physical Current/History Publisher + PublishIntent execution integration**：消费M4 `VerifiedArchiveArtifact`，实现History capture、atomic Current replace、durable PublishIntent/metadata commit与post-commit retention；不再扩展Archive Writer。

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
