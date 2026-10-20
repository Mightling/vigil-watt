# 全仓代码与架构审查（2026-10-06）

> 基线：`main` / `ddd3b39`（与 `origin/main` 同步，审查开始时工作树干净）。
> 范围：`VigilWatt.App`（WinUI 3）、`VigilWatt.Core`（HID / 蓝牙 / 插件 / 存储）、设置页与自动化测试。
> 性质：**只读静态审查**——未启动应用、未向任何 HID 设备发帧、未强杀进程、未发布；"代码路径已核实"不等于"后果已在真机复现"。
> 本报告不修改业务源码，结论与项目文档冲突时以代码为准。

## 0. 验证边界

| 命令 | 结果 |
|---|---|
| `dotnet test src/tests/VigilWatt.Core.Tests -c Debug --no-restore` | **67 通过 / 0 失败 / 0 跳过** |
| `dotnet build VigilWatt.sln -c Debug --no-restore` | **0 错误、3 警告** |

- 3 个警告均为既有：`BluetoothClassicWatcher.cs:134` 的 CS8604（可空性），以及 System.Drawing.Common 9.0.1 引出的两个 MSB3277（`System.Collections.Immutable` / `System.Reflection.Metadata` 版本冲突）。仅确认构建成功，运行影响未评估。
- 现有测试以帧编解码、读数过滤、SQLite 为主；**67/67 通过不能证明下文跨模块路径安全**。

## 1. 硬件写入安全（P0，先修）

### 1.1 北通写入许可关闭后，首次探测仍会发送 HID 输出报告

- 位置：`src/VigilWatt.Core/Plugins/Betop/BetopPlugin.cs:58-62,94-99,133-146`；`src/VigilWatt.Core/Plugins/BatteryPluginBase.cs:99-131,318-329`；`src/VigilWatt.Core/Settings/AppSettings.cs:41-43`
- 触发：`BetopProbingEnabled` 只经 `WritesEnabled` / `CanWrite(dev)` 约束**已认领设备**的 `PollDevice`；新接口走 `Probe → Query → handle.WriteOutput`，探测入口与 `Query` 均无许可检查。品牌插件保持启用、把查询许可设为 false 后，首次扫描仍可能写一帧。
- 影响：与设置项注释"关掉即完全静默"相反，用户关闭写入后仍触碰 dongle。这是安全边界错误，**不能据此推断已造成硬件损坏**。
- 修法：探测与轮询共用同一写入闸门；许可关闭时只允许真正的被动监听或直接跳过，不发送任何输出报告。用伪 HID Writer 断言关闭许可时 Attach/Probe/轮询的写入次数为 0。

### 1.2 迈从候选匹配过宽：写入许可开启时可能向非迈从设备发协议帧

- 位置：`src/VigilWatt.Core/Plugins/Mchose/MchosePlugin.cs:38-50,56-85,220-242,317-323`；对照 `docs/hardware/safety.md:25-33`
- 触发：`IsCandidate` 仅排除罗技 / 雷柏 / 北通三个 VID，其余只要 vendor usage 且 I/O 长度为 20/20 或 64/64 就进入 `TryQhw`（0x4D 握手）、`TryKeyboardV1`（0x13 会话帧）。
- 影响：违反"只对有出处的帧、向明确目标接口写入"的硬件安全规则。品牌插件与 `MchoseProbingEnabled` 默认均为关闭，属**用户启用后的条件风险**。
- 修法：为已实测/明确记录的 VID/PID 与集合特征建立主动写入白名单；未知设备只被动读取或走独立、知情的诊断流程；不要把熔断退避当成白名单的替代品。

### 1.3 Elan 默认观察模式仍在正常执行路径枚举 Feature ID（P2）

- 位置：`src/VigilWatt.Core/Settings/AppSettings.cs:31-35`；`src/VigilWatt.Core/Plugins/Elan/ElanHeadsetPlugin.cs:20-45,49-65`；`src/VigilWatt.Core/Hid/HidWriteProbe.cs:69-80`
- 说明：Elan 默认启用，但 `ClaimEnabled=false`、永不提供电量；其 `Probe` 仍执行 `FeatureSweep(1..16)` 后再做一轮 `GetFeature(1..16)` 候选搜索。这些是 **GetFeature 读取，不是 SetFeature/WriteOutput 写入**，但仍是留在常规路径的枚举式诊断，与 safety.md 第 7 条"诊断/扫描代码不得留在正常执行路径"不一致。
- 修法：默认关闭未验证的观察插件，或把 ID 枚举迁至独立诊断入口；正式路径只读已确认的 report ID。

## 2. 设备状态与可靠性（P1）

### 2.1 来源与管理器共享可变 `DeviceStatus`，变更比较读到的是新值

- 位置：`src/VigilWatt.Core/Devices/DeviceManager.cs:283-314,319-343`；`src/VigilWatt.Core/Plugins/BatteryPluginBase.cs:275-284`；`src/VigilWatt.Core/Ble/BluetoothClassicWatcher.cs:141-149,280-286`；`src/VigilWatt.Core/Models/DeviceStatus.cs:18-46`
- 触发：首次 `_devices.GetOrAdd(id, _ => incoming)` 直接保存来源对象；此后插件 / Watcher 先原地改该对象的 `BatteryPercent` / `IsOnline` 再调用 `Upsert`，`status` 与 `incoming` 常指向同一实例 → `changed` 恒为假、`previousPercent` 已是新值。
- 影响：① "电量变化立即写历史"漏写（定时采样仍会补点，故曲线不空但变点不全）；② 低电量骤降暂缓失去旧基准。注意：基类 `BatteryReadingFilter` 是另一层过滤，**不受此缺陷影响**。
- 修法：Manager 保存独立快照 / 不可变读数事件，先比较原值再赋新值；统一处理新建、变化、离线恢复与采样。以假插件覆盖 80→50→18、休眠→恢复、数据库变点、低电量事件。

### 2.2 BLE 稀疏更新、显式断开与移除存在三条相反路径

- 位置：`src/VigilWatt.Core/Ble/BleBatteryWatcher.cs:67-70,75-103,119-136,215-219`
- 问题：
  1. `Updated` 只带变化属性：缺 `IsConnected` 时默认 `false`，无电量的更新会把在线设备误判离线；
  2. 显式 `IsConnected=false` 且事件仍带缓存电量时，越过"无电量且未连接"分支，在第 119 行重新标在线；
  3. `Removed` 只从 watcher 字典删除，不向 DeviceManager 发布离线，管理器旧引用继续在线。
- 修法：连接状态用 `bool?` 表达缺失，显式断开优先于缓存电量；`Removed` 先发离线事件再移除。可参考 `BluetoothClassicWatcher.cs:79-80,99-109,231-240` 的可空连接判定。用合成 Added/Updated/Removed 序列覆盖三种情况。

### 2.3 品牌插件关闭后，管理器仍保留此前在线的设备

- 位置：`src/VigilWatt.Core/Devices/DeviceManager.cs:217-226,267-275`；`src/VigilWatt.Core/Plugins/BatteryPluginBase.cs:310-315`；`src/VigilWatt.Core/Plugins/Logitech/LogitechPlugin.cs:348-353`
- 触发：设置保存并禁用品牌后，下一轮只 `plugin.Detach()`（释放句柄与认领），Manager 的 `_devices` 未按插件归属下线/移除；`SafeSample` 会继续按分钟写历史，托盘与小组件也继续显示。
- 相关：设备物理拔出时基类同样只原地改 `IsOnline`，不把状态返回上层，缺少确定的离线变更事件。
- 修法：显式记录设备→插件归属，禁用/拔插时发一次确定的下线事件并停止采样；不要靠共享对象被动变化。

### 2.4 历史保留天数只在"保存设置"时清理（P2）

- 位置：`src/VigilWatt.App/Views/SettingsPage.xaml:110-117`；`SettingsPage.xaml.cs:114-115`；`DeviceManager.cs:253-265`；`HistoryStore.cs:179-188`
- 界面写"超过天数的历史记录会被自动清理（天）"，但 `CleanupHistoryNow()` 全仓唯一调用点是点击"保存设置"。长期运行且从不重新保存时，超期记录不会自行过期。
- 修法：启动后与低频定时器执行保留策略，避免每轮 VACUUM；补空库与跨阈值测试。

## 3. 体验、生命周期与测试（P2/P3）

### 3.1 设置页只有"品牌开关"，缺少真正的主动写入许可控制

- 位置：`src/VigilWatt.App/Views/SettingsPage.xaml.cs:19-35,84-100`；`AppSettings.cs:28-43`；`MchosePlugin.cs:317-320`
- 用户在 UI 打开"迈从"只改 `PluginEnabled`；独立的 `MchoseProbingEnabled`（默认 false）没有控件，G87 的主动探测直接返回，界面显得"开了但没用"。
- 修法：明确区分"启用识别插件"与"允许向硬件发送已验证查询帧"，后者需风险说明与显式操作。**不要**为修复可用性而把危险许可默认改为 true。

### 3.2 罗技打开/发现失败后，同一路径可能永远不重试

- 位置：`src/VigilWatt.Core/Plugins/Logitech/LogitechPlugin.cs:71-99,118-139`
- 首次打开失败置 `OpenFailed=true`，后续 Attach 不再重开；初次打开成功但 `Devices.Count == 0` 时，后续 Attach 也不再调用 `DiscoverDevices`。设备唤醒或竞争程序释放接口后仍可能不被发现。
- 修法：分离"打开句柄失败"与"暂未发现设备"，设置有上限的冷却重试，并限制 HID++ 写入频率。该插件尚未真机验证。

### 3.3 桌面小组件关窗后匿名事件订阅未解绑

- 位置：`src/VigilWatt.App/WidgetWindow.xaml.cs:58-63`；`src/VigilWatt.App/Views/SettingsPage.xaml.cs:102-112`
- 每次创建 WidgetWindow 都向 Manager 注册两个捕获 `this` 的匿名 lambda；关闭只 `Close()` 并把 `Services.Widget` 置空，旧窗口仍被事件持有 → 反复开关会累积回调与无效 UI 刷新。
- 修法：改为具名处理器，Closed 时解绑（`DevicesPage` / `StatsPage` 已有可作参考的 `Unloaded` 写法）。

### 3.4 关键编排链路无自动测试，少量文档已漂移（P3）

- 位置：`src/tests/VigilWatt.Core.Tests/VigilWatt.Core.Tests.csproj:22-45`；`README.md:34-36,61,69-72`；`docs/project/architecture.md:19,84-86`
- 测试项目只链接帧编解码、读数过滤、名称识别、DeviceKind、SQLite 等纯逻辑文件，未覆盖 Manager 状态汇聚、插件探测许可、BLE 生命周期与设置页。
- 文档漂移：README 仍写雷柏"实验性、需抓包校准"、测试数 65（实际 67）；`architecture.md` 仍称 `HidWriteProbe` 已退役（Elan 仍在调用）、轮询为 `SafePoll` + 2 个 Timer + `Interlocked`（现为 3 个 Timer + Task/信号量）。
- 修法：优先补不接触硬件的假插件 / 状态汇聚回归测试，再同步权威文档。

## 4. 建议实施顺序

| 阶段 | 交付标准 | 验收方式 |
|---|---|---|
| 1 · 关闭写入边界 | 北通禁写时 Probe/Poll 均零输出；迈从主动写入仅命中明确白名单，未知设备只被动探测 | 伪 HID Writer 计数测试；真机需用户知情后另行验证 |
| 2 · 可信状态模型 | Manager 持有独立快照，电量变点立即入历史，阈值防护有旧基准；插件禁用/拔插准确下线 | 假插件序列 + 临时 SQLite 库；托盘/小组件同步断言 |
| 3 · 蓝牙与生命周期 | BLE 稀疏更新/显式断开/Removed 语义统一；小组件解绑；罗技有界重试 | 合成 watcher 事件测试，不触碰真实蓝牙设备 |
| 4 · 体验与文档 | 设置页双层许可、保留期自动执行、Elan 移出常规枚举；更新 README / 架构文档 | 构建、单测、人工 UI 审核；最后安排知情真机验收 |

## 5. 已知取舍（不当作新缺陷）

- 北通 T9 其它候选接口每轮白等约 4~6 秒：`docs/project/handoff.md:399-403` 已记录为既有性能待办。
- 托盘"退出"直接 `Environment.Exit(0)`、不做 HID 解绑：`code-flow.md:153-155` 与 `TrayService.cs:292-297` 已记录为已知行为，与"会话被占死约 2 分钟"的现象相关，建议后续优化（先停定时器、等 pass 结束再释句柄，**不要凭猜测发"关闭会话"帧**），但不算本次新发现的回归。
- 历史删除走 SQL 参数绑定 + 二次确认 + VACUUM 串行化，存储层未发现注入风险。
