# Statistics 修复与性能复核（2026-11-25）

本轮先修复 Reset 后目录与图标残留，再记录优化前基准，最后实施有界缓存与减少重复工作等调整并运行相同负载。功能测试 **35/35 通过**。本次两组运行的稳态 CPU、查询 p95 和累计托管分配下降，但保存耗时没有一致改善，300 软件组的私有内存增加；不能据此承诺整体性能改善或“降低 50%”。

## 确定性修复

- Reset 在同一个 SQLite 事务中清除所选范围及不被任何剩余桶、当前运行软件引用的 application / coverage 目录项。其他会话、其他日期以及仍运行软件共享的元数据保留。
- 事务提交后同步内存目录并清理自有 StatisticsIcons 缓存。只清理顶层、64 位十六进制命名的 PNG；不遍历重解析目录，不删除原始采集图标或无关文件。
- 文件被占用等删除失败在下次成功 Flush 重试；初始化扫描未引用图标，能发现上次退出后留下、已经没有数据库目录项的孤儿。
- 运行引用消失后安排下一次 Flush 清理，无需等待原来的每日清理时间。

相关文件：`src/WinGlance.Statistics/StatisticsStorage.cs`、`StatisticsService.cs`；新增回归位于 `tests/WinGlance.Statistics.Tests/CatalogCleanupTests.cs`。

修复后、性能调整前，原 25 项加 6 项清理回归共 **31/31 通过**。新增覆盖立即删除、跨会话共享引用、跨日期及跨会话 Today 重置、运行引用、真实文件共享锁失败及重启孤儿重试。

## 性能调整与正确性边界

- 对没有稳定文件身份的宿主，按 collector run + PID + 创建时间缓存 AppId，上限 4096；把已经计算的分组 ID 传入 Remember。PID 复用或采集器重启仍是不同身份。
- 缓存 StatisticMetric 枚举数组，避免逐软件与逐查询桶反复创建数组。
- coverage 只保存发生变化的水位；成功提交后才清除脏标记，失败保留以便重试。
- 已提交桶缓存上限 4096，支持活动日桶与趋势桶。事务期间构建独立的新 totals，提交后才替换缓存；压缩或过期删除的桶不重新放入缓存。Reset 清空相应缓存边界。
- 保留 Ready=0 的积分、有效秒数、覆盖率及峰值。没有采用跳过完整 GPU 零样本的做法，也没有重写捕获回调分发算法。

性能调整后 **35/35 通过**。新增 4 项回归验证宿主身份/PID 复用、SQLite 已写桶后由 coverage 触发器中途 ABORT 的回滚与重试、空 Flush 不重写 coverage、GPU 0%→100% 的均值为 50% 且 Unknown 保留缺口。既有跨午夜、大于 2^53 的整数守恒、异步子进程区间、缺失子进程、持久化去重、重置屏障和实际 SQLite 写锁失败用例继续通过。

构建命令与测试命令：

```powershell
.\.tools\dotnet\dotnet.exe build tests/WinGlance.Statistics.Tests/WinGlance.Statistics.Tests.csproj -c Release --no-restore
.\.tools\dotnet\dotnet.exe tests/WinGlance.Statistics.Tests/bin/Release/net10.0/WinGlance.Statistics.Tests.dll
```

构建为 0 警告、0 错误；`git diff --check` 没有空白错误。另一代理只读复核了提交后缓存、压缩与重置失效、目录/图标引用及重试路径，没有提出需要修改的问题。

## 同负载运行结果

设备为 6 个逻辑处理器。每次 65 秒、1 Hz 合成帧，分别为 300 软件×1 子进程、100 软件×3 子进程；每个软件有全部 9 项指标，系统合成指标为 CPU、下载、内存 3 项。每秒分别查询 Today 与 CurrentSession，30/60 秒调用 FlushAsync，基准专用时钟禁用自动定时器以免重复保存。运行串行；已与其他代理协调，后测窗口不运行 UI 基准、构建或其他测试。

| 测量 | 300×1 优化前 | 300×1 优化后 | 100×3 优化前 | 100×3 优化后 |
| --- | ---: | ---: | ---: | ---: |
| 全程 CPU，占整机 % | 0.785 | 0.645 | 0.925 | 0.669 |
| 稳态 CPU，占整机 % | 0.643 | 0.485 | 0.746 | 0.505 |
| 65 秒累计托管分配，MiB | 438.210 | 428.442 | 219.487 | 216.179 |
| GC 后私有内存增量，MiB | 15.82 | 24.36 | 16.66 | 14.73 |
| 每秒采样私有内存峰值增量，MiB | 32.06 | 34.36 | 24.30 | 23.45 |
| GC 后保留托管内存增量，MiB | 3.357 | 3.926 | 1.271 | 1.449 |
| 独立范围查询 p95，ms | 6.769 | 3.946 | 5.614 | 1.633 |
| 第 30 秒 Flush，ms | 366.97 | 463.62 | 328.10 | 193.55 |
| 第 60 秒 Flush，ms | 172.09 | 237.47 | 96.90 | 121.53 |
| 接受帧数 | 65/65 | 65/65 | 65/65 | 65/65 |

“稳态”从第 15 次样本后计算，包含两次保存；CPU 是整个基准进程，包含合成帧、查询、SQLite、JIT/运行时，不能当作统计方法自身的隔离 CPU。私有内存峰值是每秒观测值，GC 后内存不代表自然峰值；Dispose 的最后一次 Flush 位于计时报告之后。

原始 JSON：

- 优化前 300×1：`artifacts/statistics-benchmark/review-fix-baseline-300/statistics-benchmark.json`
- 优化前 100×3：`artifacts/statistics-benchmark/review-fix-baseline-100/statistics-benchmark.json`
- 优化后 300×1：`artifacts/statistics-benchmark/review-optimized-300/statistics-benchmark.json`
- 优化后 100×3：`artifacts/statistics-benchmark/review-optimized-100/statistics-benchmark.json`

相同负载的执行方式（替换输出目录即可保留多轮记录）：

```powershell
.\.tools\dotnet\dotnet.exe tests/WinGlance.Statistics.Tests/bin/Release/net10.0/WinGlance.Statistics.Tests.dll --benchmark --apps 300 --children 1 --output artifacts/statistics-benchmark/reproduce-review-300
.\.tools\dotnet\dotnet.exe tests/WinGlance.Statistics.Tests/bin/Release/net10.0/WinGlance.Statistics.Tests.dll --benchmark --apps 100 --children 3 --output artifacts/statistics-benchmark/reproduce-review-100
```

## 结果解释与限制

本轮累计分配分别减少约 9.77 MiB 和 3.31 MiB；活动桶缓存也保留更多托管对象（分别增加约 0.57 MiB、0.18 MiB）。300 软件组的两次 Flush 都更慢且私有内存更高，100 软件组第二次 Flush 也更慢，不能声称保存延迟已经改善。此前的 0.5 整机 CPU 百分点目标，本次 300 软件组通过，100 软件组为 0.505，略超目标；不应笼统宣布全部达标。

每个条件只有本轮一次运行，Windows 调度、运行时分层编译、GC 与缓存状态会影响时间和私有内存，早先报告也有明显波动。没有采集方法级 CPU 或分配调用栈，也没有重复交错试验和统计显著性分析。因此表中时间变化是这次观测，不能归因到单项修改或转为通用收益承诺。

前后测量期间其他代理在并行开发，Core 的 SystemSample 新增了设备列表属性；基准不填充这些列表，统计后台也不读取它们，但这仍不是严格固定所有依赖二进制的实验。基准软件身份为稳定文件路径，因此这些数字不能证明 `process:` 实例缓存的单独收益；所有活跃指标每帧更新，也不能体现退出或不变 coverage 条目较多时的收益。后续若继续削减分配，先使用固定依赖、独立副本与分配/CPU 剖析验证热点，再做逐项 A/B。

本次没有数据库 schema 变更，也没有承诺 50% 或任何固定百分比改善。
