进入下一个 P0：**Schedule Portability v1**。

我建议核心原则直接定成：

> `*.backupplan` 只保存“调度意图”，绝不保存 Windows Task Scheduler、launchd、systemd timer 或 cron 的原生配置。

也就是说 Portable Plan 表达：

```text
“每天 02:00 执行”
“每周一、三、五 23:30 执行”
“开机后执行”
```

而不是：

```text
schtasks XML
launchd plist
systemd .timer
crontab expression
```

## 1. 两层模型

建议严格拆分：

```text
Portable Schedule Intent
        ↓
Device Schedule Binding
        ↓
Platform Scheduler
```

Portable：

```text
BackupPlan
└─ ScheduleIntent
```

本机：

```text
config.db
└─ ScheduleInstallation
   ├─ PlanId
   ├─ DeviceId
   ├─ SchedulerProvider
   ├─ NativeTaskId
   ├─ InstalledRevision
   └─ LastSyncState
```

平台实现：

```text
Windows → Task Scheduler
macOS   → launchd
Linux   → systemd timer
           ↓ fallback
          cron
```

------

## 2. v1 建议只支持少数明确 Schedule 类型

不要一开始搞任意 cron。

建议：

```text
ScheduleKind
├─ ManualOnly
├─ Daily
├─ Weekly
└─ OnStartup
```

我甚至建议 **v1 暂不支持 Monthly 和 arbitrary cron**。

例如：

```text
Daily
Time = 02:00
```

Weekly：

```text
Days = Monday, Wednesday, Friday
Time = 23:30
```

ManualOnly：

```text
不安装任何系统调度任务
```

这样 GUI、跨平台转换和验证都很简单。

------

## 3. 时间必须定义“时区语义”

这是 Schedule 最容易留下隐患的地方。

我推荐默认：

> **Wall-clock local time on the executing device**

也就是：

```text
每天 02:00
```

意味着：

> 当前这台设备当地时间每天 02:00。

而不是固定 UTC。

这样 File-backed Plan：

```text
东京机器 → 东京 02:00
上海机器 → 上海 02:00
```

通常更符合用户认知。

Portable Plan 不需要写：

```text
Asia/Tokyo
```

作为默认行为。

------

## 4. 但需要明确 DST

本地 wall-clock 一定会遇到夏令时。

建议 v1 规定：

### 时间不存在

例如 DST 跳变：

```text
02:30
```

当天不存在。

行为：

> 在下一次可用时间执行一次。

### 时间重复

DST 回拨导致：

```text
01:30
```

出现两次。

行为：

> 只执行一次。

这样可以防止一次备份任务跑两遍。

具体哪个 native scheduler 如何实现，由 platform adapter 保证尽量贴合。

------

## 5. 不把“上次错过的任务全部补跑”

例如电脑关机三天：

```text
每天 02:00
```

开机后不要：

```text
补跑 3 次
```

建议引入：

```text
MissedRunPolicy
```

v1 只支持：

```text
Skip
RunOnceWhenAvailable
```

默认我建议：

```text
RunOnceWhenAvailable
```

即：

> 如果在计划时间设备不可用，恢复可执行状态后补执行一次，不累积多次 missed runs。

这对备份最符合实际。

------

## 6. OnStartup 也不要等于“操作系统刚启动立即压缩”

建议语义：

```text
OnStartup
Delay = fixed product default
```

比如启动后等待一段时间再触发。

但我建议 v1 **不要把 delay 暴露成 portable 配置**。

它属于平台调度实现策略。

否则 Plan 会迅速出现：

```text
startupDelay
idleDelay
batteryDelay
networkDelay
```

越来越像 Task Scheduler XML。

------

## 7. Schedule 不进入 Archive Fingerprints

当前 Change Detection 已经明确：

```text
Schedule
```

不改变 archive bytes。

继续保持：

```text
ScheduleIntent
❌ EntrySetFingerprint
❌ SelectionFingerprint
❌ ArchiveSpecFingerprint
```

用户：

```text
每天 2 点
→
每天 3 点
```

不能触发重压缩。

------

## 8. 但 Schedule 应进入 PlanSemanticFingerprint 吗？

我的建议：

> **进入。**

因为 PlanSemanticFingerprint 描述完整 desired configuration，而不仅是 archive bytes。

所以：

```text
Schedule 02:00 → 03:00
```

结果：

```text
PlanSemanticFingerprint changed
Archive fingerprints unchanged
```

于是系统知道：

```text
需要更新系统 scheduler
但不需要 rebuild archives
```

这是非常重要的区别。

------

## 9. 引入 ScheduleRevision / InstallationRevision

Portable Plan 可以产生：

```text
ScheduleSemanticFingerprint
```

或：

```text
ScheduleRevision
```

本机保存：

```text
InstalledScheduleFingerprint
```

然后：

```text
Desired schedule
≠
Installed schedule
```

状态：

```text
ScheduleOutOfSync
```

这比简单记录：

```text
task installed = true
```

可靠得多。

------

## 10. 本机 scheduler ID 永远不能进入 Plan

禁止：

```text
Task Scheduler GUID
launchd label
systemd unit path
cron line number
```

进入：

```text
*.backupplan
```

这些全部属于：

```text
ScheduleInstallation
```

本地状态。

这样同一 Plan 才能跨平台。

------

## 11. Schedule Intent 和 Installation 必须分开

比如 File-backed Plan 写：

```text
Daily 02:00
```

但用户还没授权系统安装定时任务。

那么：

```text
Desired Schedule = Daily 02:00
Installation = Missing
```

不要偷偷说：

```text
Plan invalid
```

Plan 本身依然合法。

应该显示：

```text
ScheduleNotInstalled
```

只有真正需要自动运行时才需要安装。

也就是说：

> Schedule Intent 是配置；Scheduler Installation 是设备状态。

------

## 12. ManualOnly 是合法的完整状态

默认建议：

```text
ManualOnly
```

不要默认创建系统任务。

用户明确开启：

```text
自动备份
```

之后才安装 native scheduler。

------

## 13. Headless execution 必须统一走 CLI/Application

系统调度器不能直接调用特殊业务逻辑。

应该：

```text
Task Scheduler / launchd / systemd
          ↓
      StowCrate CLI
          ↓
    Application Use Case
          ↓
       Run Plan
```

GUI：

```text
Run Now
```

也调用同一个 Application Use Case。

这继续保持现有架构原则。

------

## 14. Scheduler 只负责“唤醒”

一个很重要的职责边界：

系统 Scheduler 只负责：

> 到时间启动 StowCrate。

它不负责：

```text
扫描
规则解析
Secret 获取
Change Detection
归档
History
```

所以 Task Scheduler 参数里不要塞一大堆业务配置。

理想调用接近：

```text
stowcrate run --plan-id <id>
```

甚至最终通过 registration ID。

------

## 15. File-backed Plan 不能通过文件路径作为长期调度 identity

例如不要安装：

```text
stowcrate run E:\configs\code.backupplan
```

因为：

```text
文件移动
```

就破坏任务。

更合理：

```text
ScheduleInstallation
→ registration identity
→ PlanId
→ 当前 file-backed registration
→ 当前文件路径
```

因此 Scheduler 只绑定：

```text
local registration
```

而不是 portable document physical path。

------

## 16. Plan 不 Ready 时定时任务如何处理

例如：

```text
Source binding 丢失
Secret unavailable
HistoryRoot 不存在
```

Scheduler 仍然可能启动。

StowCrate 应：

```text
启动
↓
resolve Plan
↓
PlanNotReady
↓
记录失败
↓
退出非零状态
```

不能：

```text
弹 GUI
等待用户点击
```

后台任务必须非交互。

------

## 17. 并发执行

这个一定要现在规定。

假设：

```text
02:00 定时任务
```

但 01:00 的任务还没结束。

02:00 再启动：

> 不允许两个相同 Plan 并行运行。

建议：

```text
ConcurrentRunPolicy = SkipIfRunning
```

v1 固定，不做可配置。

结果：

```text
AlreadyRunning
→ 本次触发记录为 skipped
```

不要排队无限堆积。

------

## 18. Manual 与 Scheduled 同样互斥

如果 Scheduled 正在执行：

```text
GUI → Run Now
```

应提示：

> 此 Backup Plan 当前正在运行。

反过来也一样。

锁粒度：

```text
PlanId + DeviceId
```

而不是整个程序全局锁。

这样两个不同 Plan 可以同时执行——至于 v1 是否允许跨 Plan 并发，可以后续根据资源管理决定。

------

## 19. 睡眠/休眠恢复

这和 missed run 类似。

例如：

```text
计划 02:00
电脑睡眠
06:00 唤醒
```

默认：

```text
RunOnceWhenAvailable
```

触发一次即可。

不是：

```text
02/03/04/05/06 各补一次
```

------

## 20. 电池、电源、网络条件不要进 v1

先不要做：

```text
only when AC power
only when idle
only when network available
stop on battery
wake computer
```

这些都是典型平台特性。

如果直接进入 Portable Schedule：

> 很快就无法真正跨平台。

v1 目标先保证：

```text
时间触发
启动触发
missed execution
```

足够。

以后可以增加跨平台有明确语义的 Execution Conditions。

------

## 21. Schedule Intent 是否允许多个 Trigger？

例如：

```text
Daily 02:00
+
OnStartup
```

v1 我建议：

> **允许多个 Trigger。**

领域模型：

```text
ScheduleIntent
└─ Triggers[]
```

例如：

```text
Triggers:
- Daily 02:00
- OnStartup
```

比限制一个 ScheduleKind 更灵活。

但同一 trigger 不允许重复。

------

## 22. Trigger 建议模型

概念上：

```text
ScheduleTrigger
├─ DailyTrigger
│  └─ LocalTime
│
├─ WeeklyTrigger
│  ├─ DaysOfWeek
│  └─ LocalTime
│
└─ StartupTrigger
```

Schedule：

```text
ScheduleIntent
├─ Enabled
├─ Triggers
└─ MissedRunPolicy
```

不过我甚至建议：

```text
ManualOnly
```

直接表示：

```text
Enabled = false
```

而不要做一个 ManualTrigger。

------

## 23. LocalTime 的格式

Portable 文档以后应使用：

```text
HH:mm
```

24 小时制。

例如：

```text
02:30
23:45
```

不允许：

```text
2:30 PM
```

避免 locale。

内部可以：

```csharp
TimeOnly
```

------

## 24. Weekday 固定使用语义名称

以后 JSON 如果需要：

```text
monday
tuesday
...
```

不要：

```text
1
2
3
```

因为不同系统对：

```text
Sunday = 0/1/7
```

处理不一致。

当然这只是 Schema 设计原则，现在仍然不用固定字段。

------

## 25. Scheduler Adapter capability

以后 Infrastructure 应该：

```text
ISchedulerAdapter
```

能力大概：

```text
Install
Update
Remove
GetStatus
```

平台：

```text
WindowsTaskSchedulerAdapter
LaunchdSchedulerAdapter
SystemdSchedulerAdapter
CronSchedulerAdapter
```

Application 不应该直接生成：

```text
XML
plist
.timer
cron
```

------

## 26. Linux 首选 systemd timer

我建议未来实现优先级：

```text
Linux
1. systemd --user timer
2. cron fallback
```

因为 systemd timer 更容易：

```text
status
missed-run
logging
lifecycle
```

但这属于实现，不进入 portable semantics。

同一个 ScheduleIntent：

```text
Daily 02:00
```

无论被 systemd 还是 cron 实现：

> 都是同一 Backup Plan 语义。

------

## 27. Scheduler Installation 失败不应修改 Plan

例如用户选择：

```text
Daily 02:00
```

保存 Portable Plan 成功。

然后系统 Task Scheduler 创建失败。

状态应：

```text
Plan configuration = saved
Schedule installation = failed/out-of-sync
```

不要 rollback Plan 配置。

因为这两个是不同事务边界。

UI：

> 自动备份配置已保存，但系统计划任务安装失败。

------

## 28. File-backed Plan 修改 schedule 后

例如文件：

```text
02:00 → 03:00
```

StowCrate 下次加载发现：

```text
DesiredScheduleFingerprint != InstalledScheduleFingerprint
```

状态：

```text
ScheduleOutOfSync
```

然后可以：

```text
自动同步？
```

这里我建议：

### GUI 正常运行时

可以主动提示并更新。

### 无头备份执行时

**不要顺手修改系统 scheduler。**

Scheduler installation 是配置管理操作，不应该和备份执行事务混在一起。

------

## 29. Managed Plan 保存 schedule 后

GUI 显式保存时：

```text
Update Plan
↓
Commit config
↓
Attempt scheduler reconcile
```

如果 reconcile 失败：

```text
Plan 仍保存
SchedulerOutOfSync
```

符合前面设计。

------

## 30. Schedule 不进入 ExecutionSemanticSnapshot 的“归档发布阻断部分”

这一点稍微微妙。

如果备份正在运行：

```text
schedule 02:00 → 03:00
```

是否应该阻止这次 Current Publish？

我的建议：

> **不阻止。**

因为 Schedule 不影响：

```text
本次扫描
选择集合
归档 bytes
```

因此 ExecutionSemanticSnapshot 最好区分：

```text
ExecutionCriticalSemantics
NonCriticalPlanSemantics
```

当前我们已有 PlanSemanticFingerprint 整体变化就触发 `PlanChangedDuringRun` 的规则，这里值得收紧：

> 只有影响当前执行结果的语义变化才阻断发布。

否则用户运行备份时改个：

```text
schedule
history retention
UI label
```

竟然导致压缩几小时的结果全部作废，不合理。

这个点我建议顺手记为一个规范修正候选。

------

## 31. 可以正式定义 `ExecutionSemanticFingerprint`

而不是简单拿整个 `PlanSemanticFingerprint` 做发布校验。

例如：

```text
ExecutionSemanticFingerprint
包含：
Source logical config
Archive Units
Rules
LinkPolicy
External Source mapping
ArchiveSpec
SecretSlot/Revision
Change Detection mode
其他影响本次归档的配置
```

不包含：

```text
Schedule
UI metadata
History retention*
registration path
```

其中 History 是否完全不影响 publish 要看后面 History P0；暂时别提前定死。

这会比现在“PlanSemanticFingerprint 一变就阻止”更准确。

------

# 32. 推荐的 v1 默认

我建议：

```text
Schedule disabled by default

Supported triggers:
- Daily
- Weekly
- OnStartup

Time semantics:
- device local wall-clock

MissedRunPolicy:
- RunOnceWhenAvailable

ConcurrentRunPolicy:
- SkipIfRunning

No:
- arbitrary cron
- monthly
- idle
- battery
- network
- OS-specific conditions
```

这已经足够绝大多数个人备份需求。

------

# 33. 结论

这轮可以固化以下内容：

> 1. `*.backupplan` 只保存 portable Schedule Intent，不保存任何 native scheduler 配置或 task ID。
> 2. v1 支持 Daily、Weekly、OnStartup，可包含多个 trigger；未启用 schedule 即 Manual-only。
> 3. 时间默认按 executing device 的 local wall-clock 解释；DST 缺失时间延后到下一可执行时间，重复时间只执行一次。
> 4. `MissedRunPolicy` v1 支持 Skip / RunOnceWhenAvailable，默认后者，不累计补跑。
> 5. Device scheduler installation 属于 `PlanId + DeviceId` 的本机 runtime state，保存 provider、native ID 和 installed semantic fingerprint。
> 6. ScheduleIntent 进入 PlanSemanticFingerprint，但不进入 EntrySet/Selection/ArchiveSpec fingerprint，不触发 archive rebuild。
> 7. native installation 与 desired schedule 不一致时为 `ScheduleOutOfSync`；安装失败不得回滚 Portable Plan。
> 8. Scheduler 只负责启动统一 headless Application use case，不承载业务规则。
> 9. 同一 Plan v1 不允许并发运行，重复触发 `SkipIfRunning`；scheduled run 必须非交互。
> 10. arbitrary cron、OS 特定电源/idle/network/wake 条件留待后续版本。
> 11. 同时评估现有 `PlanSemanticFingerprint` 直接作为 publish stale check 是否过宽；建议引入只覆盖本次执行关键语义的 `ExecutionSemanticFingerprint` / 等价概念，使 schedule-only 变化不废弃已完成的归档。

最后这一条我尤其建议一起收敛。它不是 Schedule 的旁枝，而是现在暴露出来的一个真正架构问题：**“Plan 变化”不等于“本次归档语义变化”。**

等 Schedule P0 完成后，下一个 **History / Output portability** 会继续用到同样的区分。