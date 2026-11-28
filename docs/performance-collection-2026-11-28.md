# C 阶段诊断证据（恢复与统计子任务）

测量日期：2026-11-28。冻结产物：`artifacts/repair-baseline-v2`。所有统计来自真实采集或固定 100 应用回放；没有修改用户实例、用户设置或用户数据库。此文件只覆盖本子任务，UI 35 轮与 100 次资源生命周期实验由另一子任务单独提供。

## 采集器开销

每组 5 轮，每轮 60 个真实采集帧。CPU 以整机 6 个逻辑处理器归一化，统一采用第 11 帧到第 60 帧的 49 个区间。下表为五轮中位数，IQR 为四分位距；内存为每轮最后一帧的 Private Bytes。每轮独立空图标缓存；CPU 测量排除了最初十帧。存在实际用户后台负载，不属于封闭实验环境。

| 模式 | trace | CPU 中位数 | CPU IQR（百分点） | Private MiB 中位数 | Private MiB IQR |
|---|---:|---:|---:|---:|---:|
| 普通 | 关闭 | 2.0733% | 0.2553 | 90.80 | 11.33 |
| 增强 | 关闭 | 2.3110% | 0.2494 | 111.36 | 7.97 |
| 普通 | 开启 | 3.0665% | 0.3671 | 79.73 | 22.88 |
| 增强 | 开启 | 3.4536% | 0.0607 | 113.39 | 15.67 |

追踪本身产生显著开销，不能将 trace 开启后的 CPU 当成日常开销，也不能以不同进程/时间段的内存差值推断追踪节省了内存。普通正式无追踪组采用原 1–4 轮及补测第 1 轮。原第 5 轮在 12:32:45–12:33:10 UTC 被一次工作区递归搜索干扰，保留原始证据但排除；排除记录位于 `collector-ordinary-v2-no-trace/exclusions.json`。

普通无追踪组第 11 帧到第 60 帧的句柄变化为 −4、−11、−1、−11、+9；增强为 −7、−4、−3、−10、−5。短期 Private Bytes 仍会增长，不能据此宣布不存在长期内存泄漏。

采集器追踪暖机阶段（每轮跳过头 10 个阶段记录，共 50 个样本/阶段）：

| 阶段 | 普通：各轮中位数范围 ms | 增强：各轮中位数范围 ms |
|---|---:|---:|
| sample 整体 | 39.01–45.45 | 44.63–53.86 |
| processesIncludingIdentity | 25.17–26.96 | 30.10–33.16 |
| pdh | 9.98–11.59 | 9.92–15.01 |
| identity（嵌套于进程阶段） | 0.95–1.11 | 1.09–1.26 |
| network | 1.57–1.75 | 1.55–1.72 |
| accounting | 0.49–0.60 | 0.86–1.06 |
| serialize | 3.11–3.58 | 5.25–5.72 |

阶段为 wall time，存在嵌套，不能相加后当作 CPU time。普通每轮 50 次 sample 线程分配约 156.9–157.8 MB，serialize 72.1–80.3 MB；增强分别 205.1–206.8 MB 与 130.5–142.7 MB（十进制 MB）。分配量不是存活内存。

## UI 追踪

Baseline/NoCharts/NoMotion 各约 60 秒；Details 约 65 秒，实际端点查询 22 次。下面统一按原始阶段事件计算线性插值分位数，避免原 reader 的离散分位数定义混用。

| 场景/阶段 | N | 中位数 ms | p95 ms | 最大 ms | 已测线程分配 |
|---|---:|---:|---:|---:|---:|
| Baseline render | 60 | 15.5145 | 109.1532 | 134.7151 | 16,987,928 B |
| Baseline acceptGate | 60 | 0.1361 | 0.3318 | 22.0903 | 未测 |
| Baseline acceptPrepare | 60 | 0.1523 | 0.3740 | 8.1331 | 未测 |
| NoCharts render | 57 | 12.7550 | 95.3371 | 121.9400 | 15,004,736 B |
| NoMotion render | 61 | 16.9634 | 62.0171 | 87.0463 | 10,234,928 B |
| Details render | 65 | 15.5382 | 100.8925 | 120.7204 | 21,579,544 B |
| Details endpointQuery | 22 | 2.5680 | 4.5864 | 5.1661 | 2,103,728 B |

`app.render` 只覆盖显式 UI 更新代码，不包含之后异步布局/合成的完整耗时。无动效场景在这一次追踪中 p95 与已测分配较低，但总 CPU 的五轮结果尚待另一子任务完成，不能由单轮推导收益比例。

四场景 GCAllocationTick 采样估计总分配依次 158.2、159.3、164.1、170.7 MB；采样类型主要为 String、MetricAccounting、统计分配相关委托/闭包。这只是分配采样线索，不能当作精确按类型分配或存活量。

托管 CPU 栈样本只有 9、15、11、9 个；其余 External 样本可能是 native 工作或阻塞/等待。当前无法可靠归因 native UI/render/compositor 热点，不能把“非 UI 线程”称为“渲染线程”。已保留 nettrace，后续如需定位该部分，需补带 native 符号的 ETW/WPR 测量。

## 真实周期 Flush 与查询队列

5 轮均超过 90 秒且观察到 3 次真实 `periodic/finished`；共 15 次成功周期 Flush、225 次查询、450 次采样工作，工作失败事件为 0。统计页面可见、每 2 秒实际查询。

| 阶段 | N | 中位数 ms | p95 ms | 最大 ms |
|---|---:|---:|---:|---:|
| Flush 全程 | 15 | 100.2849 | 175.4551 | 195.1448 |
| transaction | 15 | 0.0655 | 9.3439 | 21.8955 |
| buckets | 15 | 10.4641 | 46.4009 | 56.2255 |
| metadata | 15 | 2.4802 | 14.3413 | 14.3716 |
| compact | 15 | 0.2136 | 2.7350 | 2.7904 |
| catalog | 15 | 0.0007 | 0.7244 | 0.8797 |
| commit | 15 | 87.3121 | 111.4990 | 130.0652 |
| cache | 15 | 0.2018 | 4.1169 | 4.3708 |
| 查询排队 | 225 | 0.0971 | 0.4283 | 1.3318 |
| 查询执行 | 225 | 1.5300 | 7.0369 | 32.5759 |
| 采样排队 | 450 | 0.0439 | 0.1540 | 1.0078 |

Flush 原始全程毫秒值（每行一轮）：

```text
117.2436, 47.6749, 84.4632
195.1448, 77.4443, 64.6538
153.0270, 100.2849, 100.1870
167.0166, 70.1828, 143.4598
151.6470, 113.3402, 92.7007
```

15 次 Flush 的 p95 只能作为该样本的描述量，不能视为稳定尾延迟保障。此次查询定时没有形成明显 flush 阻塞队列；不能据此保证任意查询与提交碰撞时排队也小于 1 ms。SQLite 仍使用 WAL + synchronous=FULL。

## ETW 覆盖

增强 trace/noTrace 均各采到 300 帧 EtwQuality：每轮首帧 WarmingUp，后续 Ready；网络/磁盘能力也从 WarmingUp 进入 Ready。两个增强组累计 LostEvents 都为 0。存在少量未归属事件，已如实保留：

| 模式 | 未归属事件 | 下载 B | 上传 B | 磁盘读取 B | 磁盘写入 B |
|---|---:|---:|---:|---:|---:|
| 增强 trace | 58 | 3,749 | 710 | 807,424 | 110,592 |
| 增强 noTrace | 35 | 17,520 | 6,016 | 0 | 0 |

普通模式在补测轮记录了 60 帧质量，状态 PermissionRequired；之前普通轮未记录 EtwQuality 字段，不能用空记录声称普通模式 ETW 零丢失。增强测量沿用已有用户增强采集实例并行运行，未停止该实例；这也属于环境限制。

## 复现与证据

- 原始目录：`trace-ui-v2`、`flush-v2`、`collector-{ordinary,enhanced}-v2{,-no-trace}`，均在 artifacts 下；普通补测为 `collector-ordinary-v2-no-trace-replacement`。
- 轻量机器可读原始汇总：`artifacts/c-recovery-measurements.json`（含完整排除轮，体积较大）。
- 可复现脚本：`scripts/measure-flush-diagnostics.ps1`、`scripts/trace-ui-diagnostics.ps1`、`scripts/measure-real-collector.ps1`、`scripts/summarize-recovery-measurements.ps1`。
- 工具源码已纳入 `scripts/tools/diagnostic-reader` 与 `scripts/tools/diagnostic-live`，TraceEvent 3.1.21，编译 0 错误 0 警告。保留了既有原始 parser JSON；汇总脚本重新从阶段原始数组计算规范分位数。
- `allocatedBytes=0` 在 acceptGate/acceptPrepare/多数子阶段表示没有采该指标；明确测得线程分配的只有 app.render、app.endpointQuery、collector.sample、collector.serialize。
- 已完成回归：Statistics 36/36、App 154 检查；本轮只搬迁/修整工具和汇总脚本，未修改冻结产品。

