# WinGlance

新增 **运行统计**：本次运行、今日、近 7 天，整机累计、四种应用排行、历史详情和 SQLite 本地保存。使用方式与统计口径见 [运行统计](docs/statistics.md)，实测范围见 [验证记录](docs/validation.md)。

面向 Windows 10/11 的桌面资源监控工具。采用 **C# / .NET 10 + WinUI 3**，普通权限界面通过受当前用户限制的命名管道连接独立采集器。

当前版本为 **0.1 开发预览**，提供真实采集数据，不内置演示数据。目标系统 Windows 10 22H2 / Windows 11，当前构建为 x64。其他 Windows 版本、显卡和驱动仍需兼容性验收。

## 启动

已经构建时，运行 `artifacts/app/WinGlance.App.exe`，或者：

```powershell
.\scripts\run.ps1
```

请保留 `artifacts/app` 整个目录，不能只复制 EXE。该目录包含 .NET、Windows App SDK 运行时和 XAML 资源，目标电脑不需要另外安装开发 SDK。目录是便携开发分发；尚未制作签名安装器、自动更新或开机服务。

## 从源码构建

```powershell
.\scripts\setup.ps1
.\scripts\build.ps1
.\scripts\run.ps1
```

`setup.ps1` 将固定版本 .NET SDK 下载到项目的 `.tools/dotnet` 并校验 SHA-512，不修改全局 PATH。构建需要访问 Microsoft 下载服务器和 NuGet。也可以使用支持 .NET 10 与 WinUI 的 Visual Studio 打开 `WinGlance.slnx`。

`build.ps1` 先运行回归检查，再发布采集器与 UI 到 `artifacts/app`。发布目标明确复制 `.pri`、`.xbf`，避免仅编译成功但发布后无法加载窗口资源。

## 已实现

- CPU、私有工作集、GPU 引擎、专用/共享 GPU 内存，以及系统内存、单活动网卡和物理磁盘概览。
- ETW TCP/UDP、IPv4/IPv6 进程上传/下载、磁盘完成事件归因；需要管理员采集器。
- 每秒快照、空缺/权限/不支持状态，避免把采集失败显示成 0。
- 友好名称、包应用本地化名称、Shell 高清图标与有界缓存。
- 真实签名辅助系统分类；默认隐藏已确认的 Windows 核心组件，未知身份保留显示。
- 按应用身份聚合，展开子进程；搜索、CPU/GPU/内存/网络/磁盘排序与联网/GPU筛选。
- 60 秒 CPU、GPU、上传/下载趋势抽屉。
- 运行统计：六张整机概况卡片、六种趋势维度、应用综合/网络/算力内存/磁盘排行；历史搜索、排序、详情与范围重置。
- SQLite 每 30 秒批量保存，正常退出提交；暂停/托盘继续累计，重复启动唤回已有实例。
- 打开文件位置、复制路径、文件属性、确认后结束进程/应用。
- 深色/浅色/跟随系统、Windows 11 Mica 与回退背景、托盘、置顶悬浮条。
- 设置中的开机启动开关：当前用户登录 Windows 后自动打开应用，默认关闭，保存生效。
- 外观等设置保存在 `%LOCALAPPDATA%/WinGlance/settings.json`；开机启动状态直接读取当前用户的 Windows 启动项。

## 权限和操作

首次启动使用普通权限。点击 **增强采集** 后，只提升采集器权限，Windows 会显示 UAC。普通权限下进程网络/磁盘显示 `—` 和原因，顶部网卡/物理磁盘概览仍可使用。授权取消或连接失败可点 **重新连接**；受保护进程即使提升权限也可能缺少数据。

默认关闭主窗口会保留在托盘。托盘右键可打开主窗口、切换悬浮条或退出。设置中可以改为关闭即退出。

在 **设置 → 开机启动** 中打开开关并保存，可添加当前用户的登录启动项；关闭并保存会移除。取消不提交本次更改。如果在 Windows 的“启动应用”中另行禁用过 WinGlance，需要在那里重新启用。移动应用目录后，从新位置运行并再次保存此选项即可更新路径。此功能使用普通权限，不自动提升采集器。

结束任务只针对确认时列出的进程实例；在同一进程句柄上复核创建时间，检查 Windows 关键进程状态后执行，避免 PID 重用误杀。界面不会自行提升权限结束其他程序。未保存内容仍可能丢失。

## 数据口径

- **CPU**：占整机全部逻辑处理器能力的比例，首个采样窗口为未知。
- **内存**：主列为私有工作集，不等于提交量；顶部为独立的系统物理内存统计。
- **网络**：软件行为 TCP/UDP 应用字节；顶部为一个选定活动网卡的字节计数。VPN、回环、协议层和采样窗口不同，二者不要求相等。
- **磁盘**：软件行为可归属的磁盘完成事件；缓存写回可能属于 System。不会把通用进程 I/O 计数直接冒充物理磁盘吞吐。
- **GPU**：按适配器/引擎关联，软件内先合计同一引擎，再取最忙引擎；不相加已经计算出的进程 GPU 百分比。
- **显存**：专用与共享分开，应用合计可能重复包含共享映射；不是无重复的整卡物理占用。
- **历史**：最近 60 秒、按真实时间绘图；断采样或缺失保留断点。

## 验证

```powershell
.\.tools\dotnet\dotnet.exe run --project tests/WinGlance.Tests
.\.tools\dotnet\dotnet.exe run --project tests/WinGlance.Telemetry.Tests
.\.tools\dotnet\dotnet.exe run --project tests/WinGlance.Statistics.Tests -c Release
.\.tools\dotnet\dotnet.exe run --project tests/WinGlance.Tests -- --windows-smoke
.\.tools\dotnet\dotnet.exe run --file scripts/verify-accounting.cs -c Release
.\scripts\verify.ps1
```

普通测试覆盖应用身份隔离、GPU 按引擎聚合、未知状态、进程去重和历史保留。Windows 烟测验证真实系统签名、图标与包应用身份。`verify.ps1` 最后输出三份真实采集 JSON，包含本机进程名和文件路径，适合本地排查。

运行统计回归覆盖原始计数、独立指标窗口、跨午夜拆分、持久化、重置、去重与保留策略。最终构建通过 33 项核心、13 项采集生命周期、25 项统计回归；另有 17 项 Windows accounting 烟测。桌面交互、重启及短时性能结果见 [验证记录](docs/validation.md)。

## 结构

```text
src/WinGlance.Core        指标协议、应用聚合、历史缓冲
src/WinGlance.Windows     Win32/ETW/PDH、身份/签名/图标
src/WinGlance.Collector   独立采集进程、只写快照的 IPC
src/WinGlance.App         WinUI 界面、托盘、进程操作
src/WinGlance.Statistics  历史统计、SQLite、重置与保留策略
tests/WinGlance.Tests     无外部测试框架的回归与 Windows 烟测
scripts                  安装工具、构建、启动与验证
```

## 当前限制与后续工作

- 增强 ETW 路径已实现，但尚未完成管理员权限下 TCP/UDP/IPv6/磁盘压力测试；缺失事件会报告质量状态。
- 未完成真实 Windows 10、多 DPI/多显示器、睡眠恢复以及各厂商 GPU 的兼容性矩阵。
- 此前两次原生退出的根因尚未确认；Acrylic 生命周期调整后短时未复现，不能据此认定根治。
- 同一 EXE 的常规多进程应用可聚合；共享宿主保守拆分，不猜测跨 EXE 辅助进程的归属，暂不提供用户合并/拆分规则编辑器。
- 未确定身份或受保护进程保持显示，系统隐藏开关不保证只剩第三方应用。
- 网络卡片目前自动选择活动接口，尚无手动网卡选择；悬浮条已有拖动/置顶，尚无透明度与位置持久化设置。
- 尚无签名安装包、自动更新、服务模式、长期运行性能验收和自动崩溃恢复。请将其作为可运行预览版使用。
- 运行统计仅累计 WinGlance 运行且实际观测的时间；异常退出可能丢失尚未提交的批次。近 7 天保留策略已通过合成测试，尚未完成真实连续 7 天验收。300 应用基准的稳态 CPU 为整机的 0.521%，略超 0.5% 目标，详见验证记录。

诊断日志位于 `%LOCALAPPDATA%/WinGlance`。本地采集不上传进程信息；当前版本没有遥测服务。
