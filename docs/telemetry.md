# 采集实现、统计口径与验证边界

本说明对应当前源代码；它记录实际实现，不代表所有 Windows 版本、驱动或管理员路径已完成实机验收。默认约每秒发布一份快照，速率使用 `Stopwatch` 单调时钟的真实间隔。缺失数据以 `null` 表示，界面显示 `—`，不得以零代替不支持或无权限。

## 指标来源

| 指标 | 实现 | 口径和边界 |
| --- | --- | --- |
| 进程 CPU | `Process.TotalProcessorTime` 差分；每个进程独立记录读取时刻 | 除以全部逻辑处理器数量，100% 表示整机 CPU。以 PID＋UTC 创建时间防止复用。首样本、无创建时间或权限不足为缺失。 |
| 系统 CPU | `GetSystemTimes` 差分 | kernel 包含 idle，计算时扣除 idle。超过一个处理器组的系统仍需专门验证；当前已测机器只有一个处理器组。 |
| 进程内存 | 优先 PDH `Process V2(*)\Process ID` 与 `Working Set - Private` | 专用工作集，即驻留内存；不是 Private Bytes/提交量。V2 的实例名称包含 PID。读取同一 query 的已收集数据并按唯一实例名连接。 |
| 旧系统内存回退 | PDH `Process(*)\ID Process` 与 `Working Set - Private` | **只连接唯一实例名称。** 同名实例在 formatted array 中会重复，不能假定两个数组顺序相同，歧义项显示缺失。因此旧系统 Chrome 等多进程软件可能没有完整内存合计。 |
| 系统内存 | `GlobalMemoryStatusEx` | 总物理内存和可用物理内存。 |
| 进程上下行 | 内核 ETW TCP/UDP Send/Recv，IPv4＋IPv6 | 应用传输层事件字节累计，不等同于物理网卡线速。未另外把 retransmit 事件加一次；协议重传、回环、VPN等会影响与网卡计数的比较。 |
| 系统上下行 | .NET `NetworkInterface.GetIPStatistics` | 选择单个活动接口，优先 Ethernet/Wi-Fi 并过滤常见虚拟网卡描述；存在切换迟滞。该物理接口判别是启发式，未实现 IP Helper 硬件标志读取或手动选择。不会把 VPN 与底层网卡全部相加。 |
| 进程磁盘 | ETW DiskIORead/DiskIOWrite 完成事件 | TraceEvent 以发起线程和进程/线程事件映射归属。缓存命中不产生物理磁盘事件，延迟写回可能归属 System；未使用 `GetProcessIoCounters` 冒充磁盘吞吐。 |
| 系统磁盘 | PDH `PhysicalDisk(_Total)\Disk Read/Write Bytes/sec` | 物理磁盘设备计数器总吞吐；不可保证与可见进程行的合计相等。 |
| GPU 利用率 | PDH `GPU Engine(*)\Utilization Percentage` | 实例键去除 `pid_` 前缀，保留 adapter LUID、phys、engine、type。软件聚合先按同引擎求和，再取最忙引擎。不同引擎百分比不能直接相加。大于100%的异常全局引擎样本标为 Error，未夹成100掩盖异常。 |
| GPU 显存 | PDH `GPU Process Memory(*)\Dedicated Usage` / `Shared Usage` | 专用/共享进程记账值，多个进程可能映射同一内存，因此软件合计可能重复；不据此计算全局已用显存。没有使用 DXGI 查询任意目标PID，也没有依赖NVML。 |

PDH 使用 `PdhAddEnglishCounterW` 以避免系统语言改变路径名称。缓冲区有16 MiB上限，实例变化造成 `PDH_MORE_DATA` 时最多再读一次。每个数组独立记录调用是否成功以及各实例的 CStatus，不能由 `PdhCollectQueryData` 成功推断所有数组有效。GPU 首次采样为 WarmingUp；数组成功读取且没有某个进程实例时，软件聚合将它视为空闲。显存也仅在该专用/共享数组成功时，才将缺席PID记为0，使浏览器空闲子进程不污染整组显存合计。数组读取失败、计数器缺失、对应PID任一实例无效时保留缺失；部分GPU引擎实例无效时全局GPU无法完整计算。GPU利用率与显存分别提供 `gpu` / `gpuMemory` capability。

## ETW 权限、生命周期与质量

普通模式不启动内核 ETW，会将进程网络/磁盘能力标为 `PermissionRequired`。增强模式由独立提升的采集器创建 `WinGlance-{PID}-{GUID}` 会话，指定 `NoRestartOnCreate`；退出只释放自身会话，绝不停止系统或其他程序的会话。会话使用8 MiB目标缓冲和64 KiB缓冲块。

网络事件使用 TraceEvent 3.1.21 解析后的 `ProcessID`。已经核对该版本官方源码：TCP/UDP类的 `FixupData` 从事件载荷修正 PID；它不是原始 ETW Header PID。磁盘事件也使用该库对发起线程归属的修正，并订阅 Process/Thread 事件维持映射。

当前生命周期防护：

1. 每秒可读进程快照建立 `PID + 创建时间` 基线。
2. 订阅 ProcessStart/ProcessStop。启动事件只打开一次 limited-information 句柄查询精确创建 FILETIME；若PID已经指向更晚的新进程，拒绝将其当作原启动事件的身份。
3. 有界生命周期表按事件时间查询，各代由创建/停止边界隔开；保留每PID最多4代和15秒历史。`UniqueProcessKey` 用于区分晚到的旧停止事件。
4. 创建时间未知的启动事件形成隔离边界，后续事件先计入未归属。旧快照不得复活已停止的代际。丢事件后清空身份映射，等待快照重新建立基线。
5. 发现新一代启动时，尚未发布但可能属于旧PID映射的累计桶保守转为未归属。事件桶与PID表均最多32768项。启动前的短命进程、时间异常、权限不足或超过保留范围均可能无法归属。
6. 每窗口与整个会话的未归属事件数/字节数通过 capability message 暴露；ETW `EventsLost` 增长时整个窗口速率显示不可用。

这些措施降低PID复用串账风险，但不是完整事件溯源数据库。跨ETW缓冲严重乱序、系统时钟跳变、启动事件在上一窗口发布之后才到达等极端情况仍需增强模式压力测试。采样只保留当前进程；已退出短命进程不会在应用列表长期出现。当前不保证进程行之和等于全部事件或网卡总量，也不回填历史已发布快照。

## 已执行验证

2026-11-25，在当前Windows开发机以**普通权限**执行：

- Windows项目编译通过，无警告和错误。
- 连续4份真实快照：约305～306个进程；Process V2 下305～306个进程工作集可读，约150个进程CPU可读，约31个进程有GPU引擎实例；系统CPU、内存、单网卡、磁盘、GPU返回实际数值。
- 首样本CPU/GPU等待基线，后续GPU Ready；ETW网络/磁盘始终明确 PermissionRequired，没有弹出UAC。
- 使用简单身份解析器时，热采样约66～81 ms。这不是完整应用资源占用基准，也未覆盖慢磁盘/大量进程等环境。
- 显存缺席实例修复后再次连续4次采样：300个进程均返回专用/共享显存记账值（其中无实例的进程为0）；Firefox的14个进程均无缺失，合计专用显存约3.2 GB、共享约24 MB。该合计仍可能含重复映射，不代表物理显存独占量。未进行PDH错误码注入测试。
- `tests/WinGlance.Telemetry.Tests` 验证纯生命周期逻辑：旧事件延迟、旧Stop迟到、未知代际隔离、陈旧快照、PID已被替换、事件丢失清理和历史过期。该测试不需要管理员权限，也不模拟内核ETW端到端行为。

未执行：管理员ETW端到端流量/磁盘基准、UDP/QUIC压力与丢事件测试、跨驱动/Windows10兼容矩阵、多处理器组、休眠恢复、时钟调整、长时间PID复用压力、驱动重启后的GPU计数器恢复。因此不得将这些路径描述为已验收。

## 官方依据与待核验资料

以下链接用于核对接口语义；不是宣称所有页面都已在本机逐页验收。TraceEvent版本源码已下载并读取，Process V2计数器路径已通过本机PDH实际枚举及采样验证。

- TraceEvent 3.1.21 内核解析器（TCP/UDP `FixupData`、DiskIO、ProcessStart/Stop）：https://github.com/microsoft/perfview/blob/v3.1.21/src/TraceEvent/Parsers/KernelTraceEventParser.cs
- TraceEvent 3.1.21 会话实现：https://github.com/microsoft/perfview/blob/v3.1.21/src/TraceEvent/TraceEventSession.cs
- Microsoft，`PdhAddEnglishCounterW`：https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhaddenglishcounterw
- Microsoft，`PdhGetFormattedCounterArrayW`：https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhgetformattedcounterarrayw
- Microsoft，`EVENT_TRACE_PROPERTIES` / 丢事件与缓冲统计：https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-event_trace_properties
- Microsoft，任务管理器GPU引擎及显存记账：https://devblogs.microsoft.com/directx/gpus-in-the-task-manager/
- Microsoft，`GetSystemTimes`：https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes
- Microsoft，`GlobalMemoryStatusEx`：https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex
