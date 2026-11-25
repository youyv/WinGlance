# 验证记录

记录日期：**2026-11-25**。当前交付定位为 Windows x64 的 **0.1 技术预览**。下面区分已经运行验证的结果与仅已完成实现的修改，不将未运行的测试视为通过。

本文中的 `artifacts/` 日志、截图和性能原始记录为本地验收材料，不随公开源码仓库上传；`.tools/` 中的 SDK、缓存及本机桌面自动化脚本同样排除。公开仓库保留源码、回归测试与 `scripts/` 下的构建/采集验证入口，可按 README 在 Windows 上重新生成构建产物。

## 运行统计交付（20:54）

最终发布包已包含左侧“运行统计”页面及 `WinGlance.Statistics`：本次运行/今日/近 7 天、六张整机汇总卡片、六种趋势维度、四种应用排行、搜索/排序/占比、历史详情、系统过滤和范围重置。统计在后台持续累计，SQLite 每 30 秒批量保存，正常退出排空队列；重复启动唤回已有实例。统计口径及数据缺失处理见 [statistics.md](statistics.md)。

### 构建与功能验证

| 检查 | 结果与证据 |
| --- | --- |
| 最终 Release 发布 | `artifacts/statistics-build.log`：33 项核心、13 项采集生命周期、25 项统计回归通过；采集器及 UI 发布成功，无编译警告/错误。 |
| 统计计算与存储 | 25 项回归覆盖加权平均、原始整数计数、独立指标窗口、跨午夜、重复序号、采集器重连、PID/应用身份、退出进程、同时峰值、GPU 引擎、部分子进程缺失、持久化、范围重置、7 天保留、容量变化及真实 SQLite 写锁失败后重试。 |
| Windows accounting 烟测 | `artifacts/accounting-smoke.log`：17 项通过，包括真实 CPU ticks、网卡整数差分、磁盘原始差分、普通权限降级、退出进程 CPU 尾段、元数据解析期间退出竞态、采样断档与重连身份。 |
| 最终发布包桌面交互 | `artifacts/statistics-ui.log`：真实样本、三个范围、六种趋势、四个排行视角、排序箭头、搜索/空状态、重置范围/取消、深浅主题、页面往返与单实例通过。 |
| 暂停与托盘 | `artifacts/statistics-lifecycle.log`：概览暂停及隐藏到托盘期间仍累计；恢复、共享系统过滤、历史详情与关闭详情通过。 |
| 重启与重置 | `artifacts/statistics-exit.log`、`statistics-persistence.log`、`statistics-persistence.json`：正常退出并重启后保留先前会话；今日包含先前会话；确认重置本次运行后，3 个旧会话的观测时长和 CPU ticks 均逐项不变，重置边界已持久化，新样本继续累计。 |
| 画面检查 | 已查看最终构建的 `statistics-dark-overview.png`、`statistics-light-ranking.png`、`statistics-detail.png`，卡片、排行与抽屉显示正常；其他深浅截图在同目录。 |

上述桌面复验使用最终发布版本。正常退出前的主进程为 PID 23272；重启、重置及性能测量使用 PID 23132 / 采集器 PID 6200。复验后恢复“跟随系统”、隐藏系统进程、关闭到托盘、悬浮条关闭，主窗口保留在运行统计页。

### 统计后端基准

本机 6 个逻辑处理器，两组各运行 65 秒、每秒一帧，包含全部九项指标、每秒两个不同范围查询，以及第 30/60 帧的 SQLite 提交。CPU 是**整个基准进程**的 CPU 秒除以实际墙钟秒及全部逻辑处理器，包含合成数据生成、统计处理、查询、JIT 和写库；不是 UI 的开销增量，也不是关闭统计后的 A/B 对照。稳态从第 15 帧后开始，仍包含两次提交。

| 指标 | 300 应用 × 1 进程 | 100 应用 × 3 进程 |
| --- | ---: | ---: |
| 全程 CPU，整机归一化 | 0.661% | 0.577% |
| 第 15 帧后 CPU | 0.521% | 0.429% |
| GC 后额外私有内存 | 22.91 MiB | 15.44 MiB |
| 每秒采样的额外私有内存峰值 | 31.91 MiB | 20.84 MiB |
| Accept p95 | 0.018 ms | 0.027 ms |
| 独立范围查询 p95 | 3.41 ms | 1.67 ms |
| 第 30/60 帧提交耗时 | 378.70 / 196.67 ms | 201.00 / 88.00 ms |
| 接受快照 | 65 / 65 | 65 / 65 |

**300 应用场景的稳态 CPU 仍略高于 0.5 个百分点目标；其自然采样内存峰值也略高于约 30 MiB 目标。** GC 后保留内存与自然峰值分别列出，不用前者替代后者。完整条件与重现命令见 `artifacts/statistics-benchmark/REPORT.md`，原始数据位于同目录 `measured-300-apps` / `measured-100-apps`。基准程序集与最终发布程序集相同。

### 最终应用的短时资源观测

普通权限、6 个逻辑处理器、悬浮条关闭，各阶段约 20 秒；CPU 按整机容量归一化，内存为阶段结束时点值。资源概览、暂停、托盘见 `artifacts/performance-statistics-final.json`，统计页面见 `artifacts/performance-statistics-page-final.json`。概览及暂停各 21/21 次窗口观测为前台，统计页面 20/20 次为前台；托盘阶段保持隐藏。测量与状态恢复无错误。

| 状态 | App CPU | Collector CPU | App 工作集 / 私有内存 | Collector 工作集 / 私有内存 |
| --- | ---: | ---: | ---: | ---: |
| 资源概览 | 2.185% | 1.054% | 284.4 / 234.2 MiB | 121.1 / 71.0 MiB |
| 概览暂停，继续统计 | 0.534% | 1.171% | 280.5 / 230.1 MiB | 120.7 / 70.3 MiB |
| 隐藏到托盘，继续统计 | 0.585% | 1.171% | 281.4 / 232.3 MiB | 117.3 / 66.7 MiB |
| 运行统计页面 | 0.814% | 1.072% | 305.3 / 256.4 MiB | 118.4 / 67.9 MiB |

这些是不同时间段的绝对短时观测，不能据此计算相对旧版本的性能变化。`performance-before-statistics.json` 的负载/权限/前台条件与本轮不一致，不作为增量性能结论。当前未压缩、自包含发布目录为 **227.56 MiB / 545 个文件**，不是安装器体积。

20:46 至 20:53 的应用事件检查未发现新的 WinGlance 1000/1001 事件，托管错误日志最后修改时间仍为 18:34:38，见 `artifacts/statistics-event-errors.json`。这不消除下文记录的历史 WinUI 原生退出问题。

### 本功能尚未覆盖的验收

- 尚未连续实机运行 7 天；跨日、保留与长会话依靠受控时间的回归验证。
- 尚未完成真实休眠恢复、多 GPU、Windows 10、超过 64 个逻辑处理器的兼容性验收；当前历史整机 CPU 在超过 64 个逻辑处理器时明确标记不支持。
- 尚未完成增强采集的 TCP/UDP/IPv4/IPv6/磁盘高负载、事件丢失与长时间统计压测。普通权限降级已实测，不能等同于增强路径全面验收。
- 突然断电/强制终止的故障注入尚未验收；正常退出保存、真实数据库写锁失败重试已验证。异常退出可能丢失未提交批次。
- 全部发生在轮询间隔内的短命进程及无法可靠确认创建时间的进程，仍可能缺少应用 CPU 历史。

## 设置中的开机启动（19:28）

设置新增“开机启动”开关，保存时添加/删除当前用户 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的 `WinGlance` 值；正常启动、打开设置、切换开关及取消本身不写启动项。命令为带双引号的当前 EXE 完整路径，使用 `REG_SZ`。状态直接读注册项，旧路径或异常类型显示更新提示。不会写 `StartupApproved` 或提升采集器；界面提醒用户，Windows“启动应用”中的禁用需要由用户在那里重新启用。

Release 构建发布成功（`artifacts/build-startup-option.log`），33 项核心及 13 项采集生命周期回归通过（`artifacts/startup-option-regression.log`）。对发布应用的实际 UI/注册表检查共九项通过，见 `artifacts/startup-option-verification.log`：取消不写、保存引号命令及类型、重开设置回显、重启应用后回显（PID 760 → 22684）、关闭删除、旧路径修复、非法 DWORD 注册项删除、配置文件只读时显示部分保存错误并保持对话框且进程存活、深浅主题显示。

普通配置文件保存改用可报告错误的 `TrySave`，避免拒绝访问异常导致退出。如果启动项已经写入而普通配置保存失败，会明确显示部分成功，保留对话框并还原未保存的内存偏好。截图为 `artifacts/settings-startup-{light,dark,enabled,save-error}.png`。验证完成后已恢复原状态：启动项不存在、“跟随系统”、配置文件原内容与属性。**没有重启电脑或注销登录，不将应用重启验证等同于 Windows 登录启动的实机验收。**

## 侧边栏主题修复（19:17）

资源概览按钮原先只覆盖常态背景为 `AccentFillColorSecondaryBrush`，前景与悬停/按下状态仍使用普通按钮资源，造成浅色主题下深底暗图标、悬停突然变浅。现移除局部背景覆盖，使用标准 Button 的整套主题状态资源；独立的细条标记当前页面，使用 `AccentTextFillColorPrimaryBrush` 随主题变化，不拦截鼠标。按钮补充了可访问名称与“当前页面”说明。

本次 Release 发布成功，日志为 `artifacts/build-sidebar-theme.log`。已在同一进程（PID 25096）实际切换浅色/深色，分别捕捉常态、鼠标悬停、鼠标按下共六种状态，并检查截图：浅色为浅底深图标，深色为深底浅图标，悬停不再反转明暗。对照图为 `artifacts/sidebar-theme-states.png`，采样记录为 `artifacts/sidebar-theme-verification.json`。验证后恢复原设置“跟随系统”，应用与采集器继续运行。本轮为 XAML 样式修改，未重跑下方历史采集回归及性能测量。

## 已完成的构建与测试

| 项目 | 结果与范围 |
| --- | --- |
| 真实构建发布 | 已构建并发布独立采集器和 WinUI 应用至 `artifacts/app`；发布目录包含 .NET、Windows App SDK、PRI/XBF 与图标资源。 |
| 身份、聚合和历史回归 | 33 项检查通过，涵盖应用隔离、PID 重用、系统分类、GPU 引擎聚合、未知指标、内存缺失、历史过期与断档。 |
| 采集生命周期回归 | 13 项检查通过，覆盖相应采集状态和生命周期行为；不等同于管理员 ETW 实机压测。 |
| Windows 身份烟测 | 真实系统文件签名、中文友好名称、Shell 图标、服务进程枚举、包应用身份与 AppsFolder 名称/图标通过。 |
| 元数据缓存烟测 | 删除缓存图标后恢复成功；替换测试 EXE 后，名称和图标缓存正确失效并更新。 |
| 进程操作烟测 | 创建时间不符时拒绝结束且测试子进程仍存活；系统分类和自身结束请求拒绝；使用正确身份仅结束专门启动的隐藏测试子进程。 |
| 真实采样 | 连续输出 3 帧真实快照，覆盖约 304 个进程。进程数随本机应用状态变化，不是固定容量或压力测试规模。 |
| 普通权限降级 | 进程网络、磁盘能力状态为 `PermissionRequired`，界面显示未知及原因，不以零冒充成功采集。 |
| 桌面交互 | 最终发布包已验证搜索、展开/收起、详情及显存、双向排序提示、暂停/恢复、悬浮条打开/关闭、托盘隐藏/恢复，以及采集器重连与旧采集器退出。 |

此阶段发布目录约 **225 MiB**，这是未压缩、自包含目录的文件总量，包含运行时依赖；加入运行统计后的目录大小见本文顶部。不能据此声称安装体积或运行资源占用已达到“轻量”目标。

## 承诺补齐与本轮实测

悬浮条生命周期调整后的发布构建及 **46 项基础回归**（33 项身份/聚合/历史与 13 项采集生命周期）全部通过，日志为 `artifacts/build-final-lifecycle.log`。19:06 后已对同一构建重新完成交互烟测：搜索、展开/收起、详情及聚合显存、暂停/恢复、悬浮条开关、托盘隐藏/恢复、旧采集器清理和重连均通过，见更新后的 `artifacts/final-ui-smoke.log`；主进程 PID 20124 在复验后仍存活。

### 排序提示与悬浮条材质

- 生命周期调整后，应用、CPU、GPU、内存、下载、上传、磁盘 I/O **七列的两个排序方向**均已通过两轮 UI Automation 核验：箭头、可见提示和 `HelpText` 一致。日志为 `artifacts/promises-final-lifecycle.log` 与 `artifacts/promises-final-repeat.log`。
- 同两轮测试覆盖深色/浅色及激活/失焦组合，悬浮条均报告 `Active` Acrylic；PID 20124 在两轮复验后仍存活。更新后的深浅色截图分别保存在 `artifacts/verified-Dark-acrylic.png` 与 `artifacts/verified-Light-acrylic.png`。原始状态由 `%LOCALAPPDATA%/WinGlance/overlay-backdrop.json` 记录，四种组合的快照保存在 `artifacts/overlay-{Dark,Light}-{active,inactive}.json`，包含 `Lifecycle=Window.SystemBackdrop`。
- 这些检查覆盖当前机器的主题与焦点切换，不代表透明效果关闭、高对比模式、API 不支持时的全部回退场景已经验收。

### 实现中的性能调整

`SoftwareRow` 缓存格式化文本及属性通知参数，仅在显示值发生变化时通知界面；`Sparkline` 复用折线和基线对象，集中替换点集合；列表降低自动重排频率，并减少重复构建或设置 Tooltip 的工作。这些是已落地的实现调整，不能单凭代码变化推断整机资源下降幅度。

### 最终构建的受控短时资源观测

数据来自 **2026-11-25 19:06:25（UTC+08:00）**的 `artifacts/performance-final-lifecycle.json`。条件为普通权限、6 个逻辑处理器、悬浮条关闭，Visible、Paused、Tray 各约 **20 秒**。Visible 与 Paused 阶段主窗口均置顶，各阶段全部 **21 次窗口状态观测**均为前台；Tray 阶段主窗口隐藏。CPU 按整机全部逻辑处理器容量归一化；工作集与私有内存为采样时点值。测量完成，`Errors` 与 `RestorationErrors` 均为空。

| 状态 | 进程 | CPU | 工作集 | 私有内存 |
| --- | --- | ---: | ---: | ---: |
| 主窗口运行（Visible） | WinGlance.App | 1.118% | 251.7 MiB | 199.3 MiB |
| 主窗口运行（Visible） | WinGlance.Collector | 0.819% | 95.1 MiB | 45.1 MiB |
| 主窗口暂停（Paused） | WinGlance.App | 0.104% | 247.1 MiB | 194.4 MiB |
| 主窗口暂停（Paused） | WinGlance.Collector | 0.832% | 96.3 MiB | 46.3 MiB |
| 托盘（Tray） | WinGlance.App | 0.208% | 251.6 MiB | 199.6 MiB |
| 托盘（Tray） | WinGlance.Collector | 0.936% | 96.8 MiB | 46.9 MiB |

暂停停止主窗口的周期渲染，采集与历史记录仍继续，因此采集器在暂停和托盘阶段仍有开销。该样本仅描述本机此次短时运行，**不构成长期稳定性、性能上限或“轻量”目标验收**。

旧 `artifacts/performance-before-normal.json` 与 `artifacts/performance-after-normal.json` 未控制窗口遮挡和前台状态，**不能作为优化前后的 A/B 对照，也不据此宣称性能降幅**。`after-normal` 中暂停阶段 App CPU 为 2.102%，此后两轮受控短时测量未复现，但没有确认其原因。

18:52 的 `artifacts/performance-controlled-final.json` 是生命周期调整前的历史样本，保留供追溯，不与最终构建混作 A/B 对照。

### 仍需排查的原生退出

2026-11-25 **18:34（UTC+08:00）**出现的高对比状态订阅 COM 崩溃已修复，最终结构已移除该手工订阅。其后在高频 UI Automation 排序、主题与焦点切换专项及后续运行中，Windows 事件日志记录 **18:45:36、18:56:23** 两次 WinUI 原生退出：异常代码 `0xc000027b`，内部错误 `8000ffff`，没有托管异常栈。`artifacts/native-crash-summary.txt` 仅保留第一次的原始摘要，不是两次事件的完整转储。

随后将 Acrylic 接入 `Window.SystemBackdrop` 生命周期，具体结构见下文。调整后的两轮专项、受控性能测量及完整交互烟测期间未复现原生退出，进程在复验后仍存活；**此前两次原生退出的根因仍未确认，不能将生命周期调整等同于根治或长期稳定性验收**。

## 历史短时资源观测

以下为修复 `Move` 重置并开启位移动画后的一次 **15 秒短时观测**。运行条件为普通权限、6 个逻辑处理器、约 300 个进程，测试期间本机还存在其他重负载。CPU 百分比采用整机全部逻辑处理器容量口径；内存是采样时点值。

| 进程 | CPU | 工作集 | 私有内存 |
| --- | ---: | ---: | ---: |
| WinGlance.App | 3.25% | 222.8 MiB | 173.6 MiB |
| WinGlance.Collector | 0.88% | 98.1 MiB | 43.1 MiB |

该结果为历史记录，样本早于最后一次行背景调整及本轮性能调整；当前观测见上方受控样本。它**不构成轻量目标验收、稳定性能上限或跨机器承诺，也不用于计算与本轮结果的降幅**。尚需覆盖更长时间、管理员采集和不同进程规模。

## 本轮列表闪烁修复

根因定位到 WinUI 1.8 的集合适配：`ObservableCollection.Move` 被 `BindableObservableVectorWrapper` 转换为整个集合的 `Reset`。原先每轮排序进行多次 `Move`，会反复重置列表呈现；仅增加普通重排主题动画无法消除该行为。

实现已经调整为：

- 局部 `RemoveAt` / `Insert`，复用同一个 `SoftwareRow`，避免发送 `Move` 导致整表重置。
- CPU/GPU 采用与显示一致的小数精度；相同显示值保持已有顺序，减少无意义的排名交换。
- 只对已创建的可见行容器做 **220ms Composition Translation** 位移过渡，不以整行透明度淡出/淡入模拟更新。
- 检查系统动画设置；关闭系统动画时直接更新位置，不播放过渡。回收容器时清理位移动画状态。

实现依据已经通过官方 WinUI 1.8.3 源码核对：提交 `de1b1d8fb7981fc67d984620fe70dd491fc0f32d`，`src/dxaml/xcp/dxaml/lib/BindableObservableVectorWrapper.cpp` 的 223–226 行将 `NotifyCollectionChangedAction_Move` 与 `Reset` 共同映射为 `CollectionChange_Reset`。

**阶段验证：**内存与 CPU 排序的 UI Automation 调用成功；六帧屏幕捕捉确认可见行的 220ms 滑动过渡已经生效。首次捕捉同时发现透明行移动时文字相互透叠，随后将列表与行背景改为相同的实色 secondary brush，避免透出相邻行内容。

**前一轮画面复验：**实色背景修复后的发布包曾重新捕捉六帧排序过渡，确认没有整表空白和透明文字重叠。`artifacts/final-dark.png` 与 `artifacts/motion-contact.png` 是生命周期调整前一轮的历史界面截图及帧序列；最新构建的交互烟测已重新运行，见上方记录。

## 本轮悬浮条修改

最终结构采用 `OverlayAcrylicBackdrop : SystemBackdrop`，赋给 `Window.SystemBackdrop`，由 WinUI 的 `OnTargetConnected` / `OnTargetDisconnected` 回调管理 Acrylic 目标连接与释放。实现将框架提供的默认主题、高对比配置复制到独立配置，并保持 `IsInputActive=true`，使悬浮条失焦时仍保留 Acrylic 视觉状态；不修改框架的默认配置。已移除 `AccessibilitySettings` 手工订阅及直接通过窗口 `As<>` 挂载目标的路径。

API 不支持或初始化失败时保留实色背景，悬浮条继续显示总网速与 CPU/GPU，并保持置顶。调整后已通过两轮七列排序和当前机器深浅主题、激活/失焦专项，原始诊断包含 `Lifecycle=Window.SystemBackdrop`。这次修改属于生命周期管理调整；系统透明效果关闭、全部高对比/实色回退场景和长期稳定性仍待验收。

## 尚未验收

- 管理员权限下的 ETW TCP/UDP、IPv4/IPv6 与磁盘实际负载、事件丢失和长时间运行验证。
- 真实 Windows 10、不同 DPI、多显示器、睡眠恢复、不同厂商 GPU 与多 GPU 的完整兼容性矩阵。
- 大量进程变化时的长时间连续刷新/滚动，以及系统关闭动画后的行为验证。
- 悬浮条在系统透明效果关闭、高对比及 API 不支持等条件下的实色回退视觉复测。
- 本轮重复出现的 WinUI 原生退出的根因定位与稳定性复验。
- 新版本统一条件下更长时间的 CPU、工作集、私有内存和刷新开销测量；本轮每阶段 20 秒样本仍仅为短时观测。

## 复现命令

```powershell
.\scripts\build.ps1
.\.tools\dotnet\dotnet.exe run --project tests/WinGlance.Tests -- --windows-smoke
.\.tools\dotnet\dotnet.exe run --project tests/WinGlance.Telemetry.Tests
.\.tools\dotnet\dotnet.exe run --project tests/WinGlance.Statistics.Tests -c Release
.\.tools\dotnet\dotnet.exe run --file scripts/verify-accounting.cs -c Release
.\scripts\verify.ps1
```

`build.ps1` 默认执行核心、采集生命周期、统计三组回归后发布。`--windows-smoke` 使用专门创建的隐藏测试子进程验证结束任务，不结束已有用户进程。`verify.ps1` 的三帧采样包含真实进程名称和路径，应作为本地排查材料。

身份识别与指标汇总的详细边界见 [identity.md](identity.md)，采集实现与限制见 [telemetry.md](telemetry.md)。
