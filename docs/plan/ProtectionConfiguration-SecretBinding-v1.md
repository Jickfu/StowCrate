这一项现在可以正式设计了。我建议把它定义成 **Protection Configuration + Secret Binding v1**，重点不是先决定 7-Zip CLI 怎么传密码，而是先把 portable 配置、local secret、fingerprint 和三种保护模式的边界定死。

当前 PRODUCT 已经确定三种保护模式：无保护、隐私保护、安全加密；安全加密的秘密进入系统 Secret Store，而“隐私保护”的跨格式承载方式仍待原型验证。

------

# 1. 先确定三个模式

建议领域枚举：

```text
ProtectionMode
├─ None
├─ Privacy
└─ Secure
```

分别定义为：

### `None`

```text
不加密
不需要 Secret
```

### `Privacy`

```text
归档内容经过加密/遮蔽
+
恢复所需信息随备份一起保存
```

安全承诺必须明确：

> 只能阻止预览、索引、误打开或低成本扫描，**不提供机密性保证**。

### `Secure`

```text
真正加密
+
恢复秘密不随归档保存
```

没有外部 secret：

> 无法恢复。

这一层定义与具体：

```text
7z AES
ZIP AES
TAR.ZST 外层加密
```

暂时分离。

------

# 2. `*.backupplan` 永远不能保存 Secret Value

这个直接写成不可违反约束。

禁止：

```json
{
  "password": "123456"
}
```

也禁止：

```text
❌ password hash
❌ encrypted password blob
❌ DPAPI blob
❌ Keychain locator
❌ Credential Manager target name
❌ Linux Secret Service object path
❌ recovery key
```

Portable Plan 只能表达：

> **“这里需要一个什么秘密。”**

不能表达：

> “秘密是什么/这台机器把它存在哪里。”

------

# 3. 引入 Portable `SecretSlot`

建议不要让 Plan 引用 OS Secret Store。

定义一个 portable logical slot：

```text
SecretSlot
├─ SecretSlotId
├─ Name
└─ Purpose
```

例如：

```text
SecretSlotId = UUID v4
Name = "Archive Password"
Purpose = ArchiveEncryption
```

它和：

```text
SourceId
ArchiveUnitId
ExternalSourceId
```

是同类概念：

> 逻辑身份。

不是本机 locator。

------

# 4. 为什么需要 SecretSlotId

假设同一个 File-backed Plan：

```text
Code.backupplan
```

电脑 A：

```text
SecretSlot X
→ Windows Credential Manager entry A
```

电脑 B：

```text
SecretSlot X
→ macOS Keychain item B
```

Portable Plan 仍然表达：

```text
使用 SecretSlot X
```

因此：

```text
*.backupplan
       ↓
SecretSlotId
       ↓
Device Local Binding
       ↓
OS Secret Store
```

正好符合现在已经建立的 Local Binding 模型。

------

# 5. Local Secret Binding

建议正式定义：

```text
SecretBinding
├─ PlanId
├─ DeviceId
├─ SecretSlotId
├─ SecretReference
└─ SecretRevision
```

其中：

### `SecretReference`

仅 Infrastructure 能解释，例如：

```text
Windows Credential Manager / DPAPI
macOS Keychain
Linux Secret Service
```

Core 不知道这些东西。

### `SecretRevision`

由 StowCrate 自己维护：

```text
1
2
3
...
```

表示：

> 此逻辑 secret 的本机有效值发生过一次替换。

------

# 6. 修正 Change Detection 中的 Secret fingerprint

这里建议正式修改现有规范。

目前写的是：

```text
Secret Reference ID + revision
→ ArchiveSpecFingerprint
```

现在应该改成：

```text
SecretSlotId
+
SecretRevision
```

进入 `ArchiveSpecFingerprint`。

而：

```text
OS SecretReference
Credential Manager locator
Keychain locator
```

**不进入 fingerprint。**

原因是它们只是：

> 本机存储实现。

不是归档语义。

------

# 7. 为什么 SecretRevision 必须进入 fingerprint

例如：

```text
Archive password A
```

换成：

```text
Archive password B
```

文件完全没变：

```text
EntrySetFingerprint = same
SelectionFingerprint = same
```

但旧归档使用 A，新配置要求 B。

因此：

```text
SecretRevision 4
→
SecretRevision 5
```

必须：

```text
ArchiveSpecFingerprint changed
→ RebuildRequired
```

这是正确行为。

------

# 8. 不要通过 Hash Secret Value 来判断 Revision

不要：

```text
SHA256(password)
```

然后写到：

```text
config.db
backupplan
manifest
```

即使 SHA-256 不是明文，它仍然可能为低熵密码提供离线验证材料。

所以：

> **不要持久化 secret-derived verifier 来做 change detection。**

用户执行：

```text
Replace Secret
```

时直接：

```text
SecretRevision++
```

即可。

哪怕用户其实重新输入了同一个密码：

> 多 rebuild 一次也比泄漏 secret-derived metadata 更合理。

这也符合已经确定的保守重建原则。

------

# 9. SecretRevision 是 Local Runtime State

因此：

```text
*.backupplan
```

保存：

```text
SecretSlotId
```

不保存：

```text
SecretRevision
```

Revision 属于：

```text
config.db
+
DeviceId namespace
```

这意味着不同电脑：

```text
PC A revision = 3
Mac B revision = 1
```

完全正常。

baseline 本来就是设备本地状态。

------

# 10. Secure 模式必须引用 SecretSlot

如果：

```text
ProtectionMode = Secure
```

则必须：

```text
SecretSlotId != null
```

本机还必须存在：

```text
SecretBinding
```

否则：

```text
PlanNotReady
Reason = MissingSecretBinding
```

绝不能：

```text
弹窗后自动生成一个未知密码
```

更不能：

```text
退化成 None
```

------

# 11. None 模式禁止携带 SecretSlot

例如：

```text
ProtectionMode = None
SecretSlotId = X
```

建议直接：

```text
Validation Error
```

而不是忽略 X。

因为声明式配置最好保持：

> 没有无意义字段。

------

# 12. Privacy 模式 v1 不使用用户 SecretSlot

这是我比较推荐的一点。

`Privacy` 的目标不是用户自己保管密码。

它应该：

```text
StowCrate 自动生成恢复材料
→ 加密内容
→ 恢复材料随备份保存
```

因此：

```text
ProtectionMode = Privacy
```

**不需要 SecretBinding**。

不要让用户输入：

```text
privacy password
```

否则用户会误以为这是安全加密。

------

# 13. Privacy 的恢复材料属于 Archive Artifact

例如逻辑上：

```text
Archive
+
PrivacyRecoveryMaterial
```

但现在**不要决定它具体放在哪里**。

因为现有 PRODUCT 正确地保留了：

> archive comment 是否可靠、不同格式怎么承载，需要原型验证。

所以 v1 文档层只定义：

```text
PrivacyRecoveryCarrier
=
Archiver capability / execution concern
```

暂不固定：

```text
❌ comment
❌ manifest
❌ sidecar
❌ extra field
```

先通过 7z/ZIP/TAR.ZST prototype 后再定。

------

# 14. Privacy Recovery Material 不能进入普通 manifest

这一点建议先安全收紧。

我们现在有：

```text
__stowcrate__/manifest.json
```

这是非秘密 metadata。

不要直接：

```json
{
  "privacyPassword": "..."
}
```

塞进去。

即使 Privacy 本身不是 secure：

> manifest 的职责也不应该突然变成 credential carrier。

以后可以设计独立：

```text
Recovery Envelope
```

这样职责更明确。

------

# 15. Secure 模式的 Password 与 Recovery Export 分开

Secure 模式：

```text
Secret Store
→ operational secret
```

而用户主动导出：

```text
Recovery Package
```

应该是另一个显式操作。

不能：

```text
开启 Secure
↓
默认偷偷把密码复制到 Current
```

否则 Secure 就失去意义。

因此：

```text
Secure archive
```

默认没有任何随归档恢复秘密。

------

# 16. Recovery Export 不属于 `*.backupplan`

即使以后支持：

```text
Export Recovery Key
```

也必须是独立 artifact。

例如概念：

```text
StowCrate Recovery Package
```

但具体格式后续再设计。

绝不能混入：

```text
*.backupplan
```

否则 Git 管理 Plan 时很可能把秘密一起提交。

------

# 17. Portable Plan 可以表达“需要 Secure Secret”

概念模型可以是：

```text
ProtectionConfiguration
├─ Mode
└─ SecretSlotId?
```

规则：

```text
None
  SecretSlotId = forbidden

Privacy
  SecretSlotId = forbidden

Secure
  SecretSlotId = required
```

很干净。

暂时不要把：

```text
AES method
password length
KDF iteration
```

放进去。

这些属于 Archive Format Capability / ArchiveSpec 后续设计。

------

# 18. SecretSlot 的 Scope

v1 我建议：

> **SecretSlot 默认 Plan-scoped，但可以被多个 Archive Unit 引用。**

例如：

```text
Plan
├─ B → SecretSlot X
├─ D → SecretSlot X
└─ F → SecretSlot Y
```

意味着：

```text
B/D 同密码
F 独立密码
```

这为以后 per-unit ArchiveSpec override 留出了空间。

但 SecretSlot 本身仍声明在 Plan portable configuration 中。

------

# 19. SecretSlotId 生命周期

建议与其他 portable ID 类似：

| 操作                  | SecretSlotId |
| --------------------- | ------------ |
| rename slot           | 保持         |
| 更换 password         | 保持         |
| Export / Import       | 保持         |
| Managed ↔ File-backed | 保持         |
| Save As               | 保持         |
| Clone Plan            | **重新生成** |

Clone 以后：

```text
SecretSlotId
```

全部重新生成。

但：

> **不复制 SecretBinding。**

因此 Clone 后如果使用 Secure：

```text
PlanNotReady
```

直到用户重新绑定 secret。

这是安全的默认行为。

------

# 20. Import / Register 绝不能自动关联“同名密码”

例如：

```text
SecretSlot:
Name = Archive Password
```

本机 Secret Store 里刚好也有：

```text
Archive Password
```

不能因为名字一样就自动使用。

必须：

```text
explicit secret binding
```

原因：

> display name 不是 identity。

这和 Source identity 规则完全一致。

------

# 21. Secret Binding 操作建议明确区分

至少三个：

### Bind Existing

```text
SecretSlot
→ existing SecretStore item
```

如果平台允许。

### Set / Replace

用户输入新的 secret：

```text
Secret Store write
↓
SecretRevision++
```

### Unbind

删除 StowCrate binding：

```text
Secure Plan
→ PlanNotReady
```

注意：

> Unbind 不一定删除 OS Secret Store 中的 secret。

“取消引用”和“销毁秘密”应该是两个不同操作。

------

# 22. Delete Secret 必须显式

如果 StowCrate 确实拥有该 SecretStore item，可以提供：

```text
Delete stored secret
```

但这是 destructive security operation。

不能因为：

```text
删除 Plan
```

就默认删除 Secret。

因为可能：

```text
多个 Archive Unit / Plan registration
```

仍然引用它。

以后持久层需要 reference check。

------

# 23. Secret 在运行期间变化

现在已经有：

```text
ExecutionSemanticSnapshot
```

这正好可以扩展。

运行开始捕获：

```text
SecretSlotId
SecretRevision
```

Publish 前再次校验。

如果：

```text
revision 4 → 5
```

则：

```text
ConfigurationChangedDuringRun
```

或者统一：

```text
PlanChangedDuringRun
```

结果：

```text
不发布
不推进 baseline
```

否则可能：

```text
规划时密码 A
压缩时密码 A
发布前用户改成 B
↓
Current 实际还是 A
baseline 却表示配置 B
```

必须防止。

------

# 24. Secret Availability 也是 Plan Readiness

例如 scheduler 后台执行时：

```text
SecretSlot 有 binding
```

但当前进程：

```text
无法访问 Secret Store
```

不能：

```text
退回 GUI prompt
```

无头任务也不能挂在那里等输入。

应该：

```text
SecretUnavailable
PlanRunBlocked
```

并产生清晰日志。

所以建议区分：

```text
MissingSecretBinding
SecretUnavailable
SecretStoreError
```

------

# 25. Headless 是这个设计必须考虑的

首版会支持系统 scheduler，因此 Secure Encryption 必须满足：

```text
scheduled/headless execution
```

能够获取 secret。

也就是说后续 Secret Store prototype 不只测试：

```text
GUI 手动点击备份
```

还必须测试：

```text
Windows Task Scheduler
launchd
systemd timer
```

否则设计不能算通过。

------

# 26. Secret 绝不能出现在进程参数

这一条继续保持为硬约束：

```text
❌ 7zz -pMyPassword ...
```

如果密码能在：

```text
process list
command history
diagnostics
```

中出现，就不接受。

后续 7-Zip prototype 的核心任务就是验证：

> 是否存在可靠的 stdin / library / IPC 等安全输入方式。

如果 7zz CLI 做不到：

> 换 library/interface。

不能为了方便降低安全要求。

------

# 27. 日志与异常同样禁止 Secret

建议正式规定 Sensitive Value Policy：

以下组件都不得输出：

```text
SecretValue
RecoveryMaterial
Secret-derived verifier
```

包括：

```text
logs
exceptions
telemetry
crash dump annotations
manifest
config.db
cache.db
*.backupplan
command line
process environment
```

尤其不要通过：

```text
Environment.SetEnvironmentVariable("PASSWORD", ...)
```

传给 7zz。

环境变量同样可能被其他诊断工具读取。

------

# 28. Core 不应该持有 OS SecretReference

Core 最多看到：

```text
SecretSlotId
SecretRevision
ProtectionMode
```

真正 secret 获取应该发生在执行边界。

架构大致：

```text
ResolvedPlanSnapshot
      │
      │ SecretSlotId
      ▼
Application Executor
      │
      ▼
ISecretProvider
      │
      ▼
Infrastructure
 ├─ Windows Secret Store
 ├─ macOS Keychain
 └─ Linux Secret Service
```

Archiver 最终获得：

> 临时 Secret Material

而不是 Secret Store locator。

------

# 29. Planning Kernel 不读取 Secret

这点很重要。

Planning 只需要知道：

```text
Secure
SecretSlotId X
SecretRevision 3
```

足够生成：

```text
ArchiveSpecFingerprint
```

它不应该：

```text
去 Secret Store 读取密码
```

因此：

```text
Preview / Dry-run
```

不需要把 secret value 暴露给 Planning Kernel。

最多做：

```text
Binding existence / availability validation
```

由 Application 层完成。

------

# 30. Fingerprint 最终建议

### ArchiveSpecFingerprint 包含：

```text
ProtectionMode

Secure 时：
SecretSlotId
SecretRevision

Privacy 时：
PrivacyProtectionSemanticsVersion

以及：
Archive format
compression
metadata policy
manifest/archive semantics version
```

### 不包含：

```text
SecretValue
OS SecretReference
SecretStore implementation
DeviceId
Recovery material bytes
```

------

# 31. Privacy 每次随机生成 key 不应该导致“下一轮永远 changed”

如果 Privacy 模式每次生成随机 protection material：

这个随机值绝不能进入：

```text
ArchiveSpecFingerprint
```

否则：

```text
每次运行 random key 不同
→ fingerprint 永远不同
→ 每次都 rebuild
```

Fingerprint 只描述：

```text
Privacy protection semantics
```

而不是某次构建的随机 nonce/key。

------

# 32. Encryption 算法暂时不要固化

现在不要写：

```text
AES-256
PBKDF2
Argon2
```

到 `BACKUPPLAN.md` 的 v1 核心语义里。

原因是：

```text
7z
ZIP
TAR.ZST
```

的能力不同。

应该等 Archiving prototype 得到：

```text
ArchiveFormatCapabilities
```

以后，再决定具体合法组合。

当前只固定：

```text
Protection intent
+
secret semantics
```

------

# 33. Format 不支持某模式时必须验证失败

例如未来 prototype 得到：

```text
ZIP adapter
不支持 StowCrate Privacy carrier
```

那么：

```text
format = zip
protection = privacy
```

应：

```text
UnsupportedArchiveCapability
```

不能：

```text
自动降级为 None
```

同样：

```text
Secure
```

不能偷偷变 Privacy。

------

# 34. 建议增加 `ProtectionCapabilities`

以后 Archiving 层：

```text
ArchiveCapabilities
├─ SupportsSecureEncryption
├─ SupportsPrivacyProtection
├─ SupportsEncryptedHeaders
├─ SupportsLinks
...
```

Plan validation：

```text
ProtectionConfiguration
+
ArchiveCapabilities
↓
Valid / Unsupported
```

这样 Portable Plan 不与 7zz CLI 参数绑死。

------

# 35. `config.db` 与 Secret Store 的职责

建议以后：

```text
config.db
```

只保存：

```text
SecretSlotId
SecretBinding metadata
SecretRevision
SecretStoreProvider
opaque SecretReference
```

不保存：

```text
SecretValue
```

其中 opaque SecretReference 即使 config.db 泄漏：

> 也不应该直接包含可解密秘密。

------

# 36. config snapshot 也因此安全很多

以后：

```text
config.snapshot.db
```

可以随备份保存。

因为里面只有：

```text
SecretSlot / binding metadata
```

没有 SecretValue。

灾难恢复后：

```text
恢复 config
↓
Secure Plan 显示 Missing Secret
↓
用户重新绑定/导入 Recovery Package
```

这比把 DPAPI blob 等机器绑定密文塞进去更干净。

------

# 37. 灾难恢复语义必须明确

只有：

```text
*.backupplan
+
Current
+
History
```

对于 Secure Plan：

> **不足以保证恢复加密内容。**

还必须有：

```text
Secret
或
独立 Recovery Export
```

产品 UI 应明确提醒。

不能给用户产生：

> “我已经 Export 了 Backup Plan，所以密码也备份了”

这种错觉。

------

# 38. Secure 模式最好提供“恢复能力状态”

以后 UI 可以有：

```text
Secure Protection

Secret bound            ✅
Recovery export created ⚠️ / ✅
```

但：

> Recovery Export 是否必须、格式是什么

暂时仍作为后续安全设计，不要现在写死。

------

# 39. 建议正式修订的 Change Detection 一点

把目前：

> Secret Reference ID + revision

修改为：

> **portable `SecretSlotId` + local `SecretRevision`**

理由是：

- `SecretSlotId` 表示归档配置引用哪个逻辑 secret；
- `SecretRevision` 表示这个设备上的 secret 值版本；
- OS locator 只是 binding implementation。

这是当前规范体系下更一致的模型。

------

# 40. 这次的正式结论

我建议固化以下规则：

> 1. `*.backupplan` 只保存 Protection Configuration 和 portable SecretSlot declaration，不保存 SecretValue 或 OS SecretReference。
> 2. v1 ProtectionMode 为 `None / Privacy / Secure`。
> 3. `Secure` 必须引用 portable `SecretSlotId`；本机通过 `PlanId + DeviceId + SecretSlotId` 绑定 OS Secret Store。
> 4. `None / Privacy` 不使用 SecretSlot；Privacy recovery material 由执行阶段产生，其 carrier 等待 Archiving prototype，不提前写死。
> 5. Secret value 只存在于 OS Secret Store 和最短必要运行内存中，不进入 Plan、SQLite、manifest、cache、日志、CLI 参数、环境变量或 fingerprint。
> 6. StowCrate 维护本机 `SecretRevision`；Replace/Rebind secret 时保守递增，不持久化 secret-derived hash。
> 7. `ArchiveSpecFingerprint` 使用 `ProtectionMode + SecretSlotId + SecretRevision` 等语义，而不是具体 SecretReference 或 secret value。
> 8. SecretRevision 在运行期间变化属于 Execution Semantic drift，禁止 Publish 和 baseline commit。
> 9. Secure Plan 缺少 binding 或 Secret Store 不可用时 `PlanNotReady/SecretUnavailable`，无头任务不得降级或等待交互输入。
> 10. 具体 7zz 密码安全传递、Privacy carrier、加密算法组合留给 Archiving capability prototype。

------

这项完成以后，我建议下一个 P0 进入 **Schedule portability**。它比 Encryption 简单很多，主要需要决定 portable document 保存的是“调度意图”还是 OS cron/Task Scheduler 细节——我会明确推荐前者。