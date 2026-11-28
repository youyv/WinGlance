# 连接筛选与悬浮条预设验证 · 2026-11-28

本次新增 D2「连接筛选与复制」和 D3「悬浮条内容预设」。产品沿用现有每秒采集与选中网卡/磁盘口径；筛选只处理已取得的端点，不增加连接查询。默认预设仍为标准，未知持久化枚举回退标准。

## 自动回归

App 服务回归 247 项通过，其中新增连接筛选/复制格式、来源覆盖提示、未知代际成员，以及四预设/未知值/断开状态/大数值布局用例。100%、150%、200% 是纯几何推导测试，不是实际跨屏或 DPI 切换验收。原生绘制与 WinUI 使用相同的显式分行、Consolas 12 DIP 和 20 DIP 行高，默认标准为 510 × 58 DIP。

## 实机 UI 与截图

`artifacts/features-ui-v6/report.json` 记录在 `artifacts/feature-integration-v2` 的实机验证。隔离应用正常退出并完成清理，但脚本因最后一项断言文案写错而失败，整体 `Completed=false`；不能当成全套通过。已通过项目包含：

- 暂停后按 TCP、UDP、已连接、UDP 绑定筛选，40 条来源数量与观测时间不变；组合不匹配时显示 0 条。
- 在端点文本上真实右键，UDP 远端复制不可用，本地与整行可用；实际整行复制内容正确，测试恢复之前的剪贴板格式内容。
- 四预设 × 浅深主题 × Acrylic/原生穿透两渲染器，共 16 张截图；另有两张 1023.9 TB/s、100.0% 极值截图。
- 每行用实际 GDI `GetTextExtentPoint32` 测量 Consolas 字体宽度；WinUI 同时检查 StatusText 实际 UIA 边界。所有截图均为本机实际 96 DPI，已目视浅深主题字色/底色、普通及极值文本无截断。
- 设置取消不改变持久化预设。

截图目录为 `artifacts/features-ui-v6/`；`statistics-page.png` 是 1400 × 900 默认窗口的运行统计页，导出按钮和顶部控制栏可见。`overlay-large-acrylic.png` 与 `overlay-large-native.png` 为极值证据。诊断 `large-overlay` 标记仅覆盖隔离回放的显示值，其统计记账仍为固定回放值，不能用于资源开销或统计正确性结论。

## 本轮发现与修复

实点端点文本时发现既有 `IsTextSelectionEnabled=True` 吞掉右键，显示文本自带的“全选”菜单。已关闭连接行文本的原生选择，整行统一自定义复制菜单，并保留键盘上下文菜单入口。`feature-integration-v2` 已实际通过端点文本右键和复制。

自定义 Acrylic 不透明度后显式同步深浅色 TintColor/FallbackColor；截图确认浅色黑字浅底、深色白字深底。保留窗口复用及 `AppWindow.Show(false)`，没有恢复每次 Activate 的行为。

设置 UI 测试发现诊断隔离原先只隔离文件目录，保存设置仍会通过 `StartupRegistration` 修改当前用户 Run 项。v4–v7 没有操作启动/静默开关，但保存主题和预设时会重写已有启动项路径。发现后立即暂停 GUI。主任务依据原运行 PID 18984 与桌面快捷方式的一致路径恢复为 `"D:\codex\WinGlance\artifacts\app\WinGlance.App.exe"`；启用且非静默状态有证据，测试前原始命令文本没有保存，不能声称进行了逐字节原值恢复。恢复证据见 `artifacts/diagnostic-startup-restoration.json`。

`StartupRegistration` 已为诊断模式添加进程内模拟状态，诊断 Get/Set 均不触碰真实 Run 项。测试脚本增加首尾只读检查，比较真实 Run 值的存在性、类型与内容；不会用测试后的自动覆盖掩盖隔离失败。

最终包 `artifacts/release-2026-11-28-v2` 的独立尾项 7/7 通过，证据为 `artifacts/features-ui-tail-final-v2/report.json`：`Completed=true`、`CleanupErrors=[]`、`PackageUnchanged=true`、`UserStartupUnchanged=true`。实际验证保存 All、取消 Network、诊断开机启动开关保存再重开回显，以及四种预设显示等待新数据/最后观测而不显示陈旧实时值。尾项使用 `scripts/verify-features-ui.ps1 -QuickTail`，不重复已经完成的 16+2 截图矩阵。

## 失败记录与范围

- v1：脚本没有滚动详情外层，端点行不可见。
- v2/v3：真实产品右键事件被文本自带菜单接管，上述修复已验证。
- v4：截图自动化出现空值表达式异常；未保留足以确认具体原因的定位堆栈。
- v5：截图脚本将临时 tooltip 窗口计入悬浮条；改为按所属 PID 和精确窗口标题识别。
- v6：产品显示“等待新数据 · 最后观测 …”，脚本错误要求“上次观测”；无实时数字冒充新数据。断言已修为匹配公共状态文案。
- v7：设置动画期间 ThemePicker 的 UIA 枚举短暂未就绪，失败截图/树显示控件实际存在；已加入控件就绪等待。该轮未完成，原始证据保留在 `artifacts/features-ui-v7/`。
- `features-ui-tail-final`：前两项通过后，脚本在取消动画结束前立即点击设置，被产品的对话框重入保护忽略；添加动画间隔后，`features-ui-tail-final-v2` 全部通过。首轮真实 Run 亦未改变。

上述轮次的冻结包均未改变，隔离进程清理均无错误。没有把脚本定位失败算成产品通过，也没有删除失败原始证据。实际混合 DPI 多屏、Windows 10、系统高对比度开关、Windows 辅助功能“文本大小”缩放、超窄工作区仍待专用环境验收；DPI 几何用例不能替代辅助功能文字缩放测试。本轮没有修改用户操作系统主题/DPI/高对比度/文本大小设置。
