# VigilWatt 上线前全检报告

**日期**：2026-10-10  
**场景**：上线前全检（代码审查 + 安全审计 + QA 测试 + 可维护性专项）  
**参与成员**：🛡️ 安全卫士（gstack-security-officer）· ✅ 质量门神（gstack-qa-lead）· 🔍 产品官（gstack-product-reviewer）· 🔧 排障手（gstack-investigator）  
**基线**：commit `bdee6e4`「feat: 重构文档信息」（工作树仅 `AGENTS.md`/`docs/*` 有未提交文档改动，**`src/` 无改动**）  
**边界声明**：全程**只读审查**——未修改任何源码、未做任何 git 写操作、**未启动应用、未向任何设备发帧**。所有结论均为**代码推断**，非真机实测。QA 部分实际执行了构建与单测（见 §1）。

---

## 📌 TL;DR（执行摘要）

- **整体结论**：🟡 **有条件通过**——机械层（构建/测试）全绿，但代码库仍带 **4 条 `open` 的 P0**，其中 **CR-03（真功能 bug）** 与 **CR-09（运行时资源泄漏）** 属"不改就有可感知后果"，建议上线前必修。
- **阻塞项数量**：**4 条 P0**（CR-01 / CR-02 / CR-03 / CR-09）。
- **机械质量门**：`dotnet build` **0 警告 0 错误**；**17 个测试类 / 177 用例全绿**。
- **本次复核**：17 条旧 BACKLOG **16 条仍成立**（CR-03 定性加重、CR-09 上调 P0）、**1 条转 done**（CR-11 文档面已修）、新增 **15 条**（CR-19~CR-33）+ 10 条重构候选。
- **下一步**：先修 CR-03 + CR-09（可感知后果），CR-01/CR-02 补闸门或在 BACKLOG 写明暂缓理由（`review-standard.md` §1 允许的口径）。

---

## 🎯 核心结论卡片

| 项目              | 内容                                                      |
| --------------- | ------------------------------------------------------- |
| **Go / No-Go**  | 🟡 **条件 Go**（机械层 Go；带 4 条 P0，需处置后才可发版）                  |
| **严重度分布**（open） | 🔴 P0 **4** / 🟠 P1 **7** / 🟡 P2 **18** / ⚪ Info **2** |
| **本次复核结果**      | 仍成立 16 · 转 done 1 · 新增 15 · 重构候选 10                     |
| **关键行动项**       | 4 条（见 §5）                                               |
| **建议负责人**       | 用户（个人项目，唯一开发者）                                          |
| **改动面**         | **本次零代码改动**（纯审查）                                        |

---

## 1. 各成员核心结论

### ✅ 质量门神（QA 测试）

- **核心判断**：L0 机械质量门 **Go**。`dotnet build VigilWatt.sln -c Debug` → **0 警告 0 错误**（`TrimMui` 白名单生效：all=172/keep=6）；按类逐跑 17 个测试类 **177 用例全绿、0 失败 0 跳过**（未跑全量——本机全量会卡住）。
- **关键建议**：**发布质量门 = 条件性 No-Go**——机械层全绿，但仍有 3 条 `open` 且未写暂缓理由的 P0。另修正一条旧认知：**`BatteryPluginBase` 并非"碰 WinRT 测不了"**（已实测：它只依赖纯 P/Invoke 的 `Hid` 层，能编译进 net8.0 临时工程）；真正障碍是逻辑与真实 HID I/O 耦合。`DeviceManager` 确实碰 WinRT，说法成立。

### 🛡️ 安全卫士（OWASP + STRIDE 审计）

- **核心判断**：安全底座扎实（SQL **全参数化无注入**、日志对控制符有 `Sanitize` 防伪造、雷柏零写入），但**硬件写入闸门有 2 个 P0 破口**：SEC-01 罗技插件整体绕过基类、SEC-02 北通探测路径无闸门且开关无 UI 关不掉。
- **关键建议**：罗技插件改继承 `BatteryPluginBase`（或至少加独立写入开关并默认关闭）；`RunProbe` 增加 `CanWrite` 检查。另：基类 `WritesEnabled` **默认 true** 是 fail-open 设计隐患（CR-19）；`settings.json` **非原子写**（CR-20）；更新源可由 `settings.json` 任意指定且无签名（CR-31）。

### 🔍 产品官（代码审查 + 冗余/可抽离）

- **核心判断**：架构分层与安全机制设计良好，但存在**一条结构性 P0**——所有数据源与 `DeviceManager` 共享**同一个可变 `DeviceStatus` 实例**，使 `Upsert` 的变化判定（历史即写 + UI 单设备刷新 + 骤降保护）对"非墓碑"设备**整体失效**（CR-03 比原报告更广、更隐蔽，根因即 R3）。
- **关键建议**：把变化判定/骤降保护抽成纯逻辑并补测；给出 **10 条可抽离/去重方案**（D1~D10），最高收益是**两个 watcher 去重**（D1）与**设备状态文案格式化抽离**（D2）。

### 🔧 排障手（可维护性 + 性能）

- **核心判断**：负责的 8 条（CR-05/06/07/08/09/13/14/16）**全部仍成立、无一条已修**；CR-09 为运行时真泄漏应**上调 P0**。
- **关键建议**：BACKLOG 末尾 4 条未核实项**全部核实**（SafeSample 无去重=属实、`int?` 撕裂=部分属实、小组件无合并=属实、BLE 串行 await=属实）；新增 4 条性能/日志问题（`Log.IsEnabled` 零调用、UI 刷新无防抖、扫描开写句柄、雷柏逐帧 Info）。

---

## 2. 综合审查发现（去重合并，按严重度排序）

> 编号沿用 `docs/reviews/BACKLOG.md`（CR-01~~CR-17 为旧条目复核，CR-19~~CR-33 为本次新增）。  
> 证据一律 `文件:符号`，**不写行号**（项目铁律）。

### 2.1 🔴 P0 · 阻塞项（4 条）

| #         | 类别                   | 位置（`文件:符号`）                                                                                                                                                                                                                      | 问题                                                                                                                                                                                                | 建议                                                     | 来源   |
| --------- | -------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------ | ---- |
| **CR-03** | 状态模型/线程共享（R3）        | `Devices/DeviceManager.cs:Upsert`、`Plugins/BatteryPluginBase.cs:TryApplyReading`/`RaiseStatusPushed`、`Ble/BleBatteryWatcher.cs:HandleDevice`、`Ble/BluetoothClassicWatcher.cs:HandleDevice`、`Power/SystemBatteryWatcher.cs:Apply` | 所有数据源原地改**同一个** `DeviceStatus` 后传给 `Upsert`，`GetOrAdd(id, _ => incoming)` 存的就是该实例 → `changed` **恒 false**：历史"变化即写"不触发、`DeviceUpdated` 不触发、**骤降保护恒不成立**。（例外：设备若先由墓碑建条目则正常，故表现取决于启动时有无 24h 内历史，更隐蔽） | 快照传递（`Upsert` 内拷贝后再存/再比）或存储层独占实例；顺带改掉"同一对象的情况也成立"的失实注释 | 产品官  |
| **CR-01** | 硬件写入红线（R1+R2）        | `Plugins/Logitech/LogitechPlugin.cs`（`: IBatteryPlugin`）、`Plugins/Logitech/HidppIO.cs:Request`、`Settings/AppSettings.cs`                                                                                                         | 罗技插件直接实现 `IBatteryPlugin`，绕过基类全部安全机制；`HidppIO.Request` 唯一写入点**无条件** `WriteOutput`；默认启用、无 `LogitechProbingEnabled`；失败只标离线**永不停止写入**                                                                | 改继承 `BatteryPluginBase` 并把写入收敛到基类闸门；在此之前至少加独立开关并默认关闭   | 安全卫士 |
| **CR-02** | 硬件写入红线（R1）           | `Plugins/Betop/BetopPlugin.cs:Probe`/`:Query`、`Plugins/BatteryPluginBase.cs:RunProbe`/`CanWrite`                                                                                                                                 | 北通探测路径无写入闸门（闸门只在 `PollDevice` 首行）；`ProbeCooldown`(30s) < 轮询(60s) → 探测失败后**每轮必写一帧**（精确条件：候选接口完全静默时）                                                                                                | `RunProbe` 增加 `CanWrite` 检查，或把北通探测改为只读；关掉开关时降级纯被动      | 安全卫士 |
| **CR-09** | 资源泄漏（R4，**本次上调 P0**） | `WidgetWindow.xaml.cs` 三处订阅、`Views/SettingsPage.xaml.cs:Save_Click`                                                                                                                                                              | 小组件三个捕获 `this` 的匿名 lambda **全文件无解绑**，窗口关闭后仍被管理器强引用 → **反复开关小组件累积泄漏 + 回调叠加**（运行时真泄漏）                                                                                                               | 改命名方法 + `Closed` 里解绑，或实现 `IDisposable`                 | 排障手  |

### 2.2 🟠 P1 · 用户可感知（7 条）

| #         | 类别               | 位置                                                                                                          | 问题                                                                                                                            | 来源   |
| --------- | ---------------- | ----------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------- | ---- |
| **CR-04** | 功能错误             | `Devices/DeviceManager.cs:CleanupHistoryNow` / `StartTimers`                                                | 历史保留只在手动保存设置时执行，无定时任务，UI 文案却写"自动清理"                                                                                           | 产品官  |
| **CR-05** | 数据/性能            | `Storage/HistoryStore.cs`                                                                                   | 无 `PRAGMA`（未开 WAL/busy_timeout），读写共用一把锁；**加重**：`HistoryPage.DeleteAsync` 还在 UI 线程同步跑 `Compact()`（VACUUM）                      | 排障手  |
| **CR-06** | UI 阻塞（S5）        | `Views/HistoryPage.xaml.cs:Reload`/`:DeleteAsync`、`Views/StatsPage.xaml.cs:ReloadDeviceList`/`:RenderChart` | 历史页/统计页在 UI 线程同步跑 SQLite 全表统计，无 `Task.Run`                                                                                    | 排障手  |
| **CR-07** | 资源释放（R4）         | `Services/TrayService.cs:Exit`、`App.xaml.cs:AppServices.Dispose`                                            | 退出路径不释放资源（`AppServices.Dispose` 全仓无人调用）→ Timer/DB/HID 句柄非确定性释放。**级别经裁决维持 P1**：发生在进程终止路径、不累积，与 §1「资源泄漏=P0」字面存在张力（建议后续修订 §1 措辞） | 排障手  |
| **CR-08** | 异常静默（R5）         | `App.xaml.cs:UnhandledException`                                                                            | 全局异常一律 `e.Handled = true` 吞掉，无计数熔断 → 带病运行                                                                                     | 排障手  |
| **CR-10** | 安全规则违反（**重新定性**） | `Plugins/Elan/ElanHeadsetPlugin.cs:Probe`                                                                   | 只读 `FeatureSweep`/`FindBatteryFeature` 留在正常执行路径，违反 `safety.md` §3 规则 7。**非硬件误写**（`GetFeature` 只读），违规点是"扫描代码未剥离"               | 安全卫士 |
| **CR-12** | 安全可用性（**加重**）    | `Views/SettingsPage.xaml(.cs)`、`Settings/AppSettings.cs`                                                    | 写入开关无 UI，`BetopProbingEnabled` 默认 true → 闸门默认敞开且用户关不掉                                                                         | 安全卫士 |

### 2.3 🟡 P2 · 可维护性（18 条，摘要）

**旧条目复核（仍成立）**：CR-13（`ReadConnected` 双份，重复面更大）· CR-14（`HidWriteProbe` 写入变体死代码）· CR-15（低电量档位只降不升，逐 1% 可刷屏）· CR-16（6 个文件超 400 行：WidgetWindow 679 / LogitechPlugin 634 / BatteryPluginBase 599 / MchosePlugin 584 / DeviceManager 499 / **BatteryChartControl 405 补漏**）· CR-17（罗技会话打开失败不重试；**表述收紧**为"接口在位但打开失败"时成立）。

**本次新增**：

- **CR-19** `BatteryPluginBase.WritesEnabled => true` 默认开启写入（fail-open，与 R1 反向）— 安全
- **CR-20** `settings.json` 非原子写（崩溃可致截断 + 静默回落默认）— 安全
- **CR-21** 多处空 `catch` 无留痕（`HidDeviceInfo.ReadInput` 等）— 安全
- **CR-22** `BatteryPluginBase.Detach` 无条件打"插件已卸载" → 禁用期间每 60s 假日志 — 代码审查
- **CR-23** 低电量档位表 `_lastNotifiedPercent` 不随设备移除清理 — 代码审查
- **CR-24** 设备状态文案/配色格式化三处重复（`DeviceRowModel`/`WidgetWindow`/`TrayService`）— 代码审查
- **CR-25** Mchose 日志去重三件套与 `Log.InfoOnChange` 重复 — 代码审查
- **CR-26** `LogitechPlugin.StableHash` 无谓直通包装（S6 死代码）— 代码审查
- **CR-27** `Log.IsEnabled` 全仓 **0 调用点**，热路径插值不短路（违反 `logging.md` §2.1）— 可维护性
- **CR-28** 雷柏被动心跳逐帧 Info + `Log.Hex` → 约 2.8 万行/天 — 可维护性
- **CR-29** 小组件/设备页刷新无合并（防抖），逐事件全量重建 — 可维护性
- **CR-30** `HidDeviceScanner.Inspect` 以读写权限开句柄读元数据，约 96+ 次/分 — 可维护性
- **CR-31** 更新源可由 `settings.json` 任意指定、无签名校验 — 安全

**⚪ Info（记录备查）**：CR-32（`.vpk-github-token` 落盘但未入库）· CR-33（单实例互斥本地命名可抢占）。

---

## 3. 冗余与可抽离封装专项（用户特别点名）

> 收益降序。每条含「抽什么 / 抽到哪 / 收益 / 风险」。完整表见 `docs/reviews/BACKLOG.md` §重构候选。

| 编号       | 抽什么                                                                            | 抽到哪                                                     | 收益                                                  | 风险         | 关联    |
| -------- | ------------------------------------------------------------------------------ | ------------------------------------------------------- | --------------------------------------------------- | ---------- | ----- |
| **D1** ⭐ | 两个 watcher 的 `ReadConnected` + `HandleRemoved` + `Start()` 探测循环 + `Snapshot()` | `Ble/WatcherProps.cs` 或 `DeviceWatcherHost` 基类          | 消除 S3、堵"语义再分叉"（10-06 P0 根因）、`ReadConnected` 可单测（R8） | 低          | CR-13 |
| **D2** ⭐ | 设备状态文案/配色格式化（充电/离线文案、`--%`、主题色 vs 状态色）                                         | `Models/DeviceStatusFormatter`（Core 纯逻辑）                | S3/DRY + 可测                                         | 低-中（UI 回归） | CR-24 |
| **D3**   | `BatteryChartControl` 几何/刻度/命中（`TimeToX`/`PctToY`、X 轴档数、最近点查找）                 | `Views/ChartGeometry.cs`（纯函数）                           | S2 + S1；hover 命中可单测                                 | 低          | CR-16 |
| **D4**   | `WidgetWindow` 业务逻辑（排序、行宽估算、显示条数封顶）                                            | `ViewModels/WidgetLayoutModel`                          | S2 + S1（679 行最大超限）                                  | 低-中        | CR-16 |
| **D5**   | `SettingsPage.Save_Click` 副作用编排（autostart/小组件生命周期/backdrop/清理）                 | `ViewModels/SettingsViewModel`                          | S2（355 行）                                           | 中          | CR-16 |
| **D6**   | 插件"发帧→窗口内读→解析"骨架（Rapoo/Mchose/Betop 各写一遍）                                      | `Plugins/HidReadLoop.cs`（`static T? ReadUntil<T>(...)`） | S3、统一读窗口语义                                          | 中          | 新增    |
| **D7**   | Mchose 日志去重三件套 → `Log.InfoOnChange`                                            | —                                                       | S3/DRY                                              | 低          | CR-25 |
| **D8**   | 充电状态映射（Mchose/Betop 各一份 + "100%→Full"）                                         | `Models/ChargingStateMap.cs`（纯函数）                       | S3                                                  | 低          | 新增    |
| **D9**   | 删除 `LogitechPlugin.StableHash` 直通包装                                            | —                                                       | S6/S3                                               | 低          | CR-26 |
| **D10**  | 插件写入许可读取模式统一（基类 `ProbingFlag`）                                                 | `Plugins/BatteryPluginBase.cs`                          | 收益有限                                                | 低          | CR-12 |

**超长文件拆分建议（CR-16，复杂度视角）**：`WidgetWindow` → `WidgetIconFactory` + `WidgetChrome`；`LogitechPlugin` → `LogitechSession` + `LogitechBatteryReader`（**须先解决 CR-01 才能复用基类生命周期**）；`BatteryPluginBase` → `PassiveListener` + `OfflineCoordinator`（**是 R8 测试盲区**）；`MchosePlugin` → `MchoseQhwCodec`（纯函数，可直接补 R8 单测）；`DeviceManager` → `DeviceRegistry` + `LowBatteryNotifier` + `PollScheduler`（解锁 R8 单测）。

---

## 4. Go / No-Go 决策

### 阻塞项清单（4 条 P0）

| 优先级 | 条目        | 为什么阻塞                                              | 处置建议                               |
| --- | --------- | -------------------------------------------------- | ---------------------------------- |
| 1   | **CR-03** | 唯一"不改就有**可感知后果**"的项：历史跳变丢点 + UI 单设备刷新失效 + 骤降保护恒不成立 | **必修**，修时按 §2.1 抽纯逻辑 + 补测（R7）      |
| 2   | **CR-09** | 运行时真泄漏，反复开关小组件累积                                   | **必修**（改动小：加解绑/`IDisposable`）      |
| 3   | **CR-01** | 硬件写入红线（R2 历史事故唯一成因）；本机无 VID_046D 设备 → 现实风险低        | 补闸门，**或在 BACKLOG 写明暂缓理由**（§1 允许口径） |
| 4   | **CR-02** | 硬件写入红线（每轮必写一帧）                                     | 补 `CanWrite` 判定，或写明暂缓理由            |

### 回滚预案

- **前置**：发布前**备份上一版 `dist/` 全量**（当前已有 `VigilWattApp-1.0.1-full.nupkg` + `RELEASES`）。`RELEASES`+nupkg 即"更新源"，保留旧版即可回滚。
- **回滚方式**：① 用户侧装回旧版 `Setup.exe`；或 ② 把旧版 `RELEASES`+nupkg 放回更新源，触发降级更新。
- **数据安全**：`packId=VigilWattApp` ≠ 数据目录 `%LOCALAPPDATA%\VigilWatt` → 版本升降**不丢数据**；回滚**不要删** `%LOCALAPPDATA%\VigilWatt`。
- **崩溃应急**：卸载新版 → 装回旧版；证据取 `%LOCALAPPDATA%\VigilWatt\logs\app*.log`。
- **dongle 假死应急**（`safety.md` §4）：关掉所有会话持有者（本应用 / 浏览器 M HUB WEB / 官方 HUB）→ 仍不行则重插拔 dongle。

### 发布前检查清单（QA 提供）

1. `tasklist` 确认**无 `VigilWatt.App.exe` 实例**（占用会 `MSB3027`）；**禁止强杀实例**（会占死 dongle 会话，`safety.md` §2）。
2. `dotnet --list-sdks` 确认 .NET 8 SDK 在位。
3. `dotnet build VigilWatt.sln -c Debug` → **0 警告 0 错误**（本报告已通过）。
4. 跑相关测试类（本报告 177 用例全绿）。
5. `dotnet publish src/VigilWatt.App -c Release -p:Platform=x64 -o publish` → 校验 `publish/VigilWatt.App.exe` 存在。
6. 版本一致：csproj `<Version>`（当前 **1.0.2**）== exe FileVersion == `dist/` 包版本。
7. `publish/` 语言目录只应有 `en-us/zh-CN/zh-TW`（`TrimMui` 生效）。
8. `scripts\build-installer.ps1` → 检查 `dist/`：`Setup.exe` + `RELEASES` + `*.nupkg`。

---

## ✅ 行动清单

| # | 行动                                                       | 负责方 | 紧急度    | 期望  |
| - | -------------------------------------------------------- | --- | ------ | --- |
| 1 | 修 **CR-03**：`Upsert` 改快照传递 + 抽变化判定/骤降保护为纯逻辑 + 补测 + 改失实注释 | 开发者 | **P0** | 上线前 |


| 2 | 修 **CR-09**：`WidgetWindow` 订阅改命名方法并在 `Closed` 解绑（或实现 `IDisposable`） | 开发者 | **P0** | 上线前 |  
| 3 | 处置 **CR-01 / CR-02**：补写入闸门，或在 BACKLOG 写明暂缓理由（§1 允许） | 开发者 | **P0** | 上线前 |  
| 4 | 按 **D1/D2** 执行去重（两个 watcher 合并 + 设备状态格式化抽离），顺带补 R8 单测 | 开发者 | P2 | 下一轮 |  
| 5 | 补 5 处纯逻辑测试：`PluginUtils.StableHash` / `DeviceClaimRegistry` / `LogLevelNames` / `AppSettings.Load`（成本最低） | 开发者 | P2 | 下一轮 |  
| 6 | 修 **CR-05/CR-06**（WAL + UI 线程异步查询）与 **CR-04**（历史清理定时任务） | 开发者 | P1 | 最近一轮 |  
| 7 | 修 **CR-27/CR-28**（热路径 `IsEnabled` 短路 + 雷柏逐帧日志降级），降低日志噪声 | 开发者 | P2 | 最近一轮 |

---

## ⚠️ 待完善 / 已知局限

- **本次零代码改动**：报告只给结论与方案，未修任何问题；修复需另行确认。
- **未实测**（代码推断，非真机）：CR-02"每轮必写一帧"依赖"接口完全静默"前提；CR-15/CR-17 的真机影响面；`history.db` 日增速与 UI 卡顿程度。
- **未做**：依赖 CVE 库离线比对（仅做版本新旧判断）；CR-30 更新攻击链未构造 PoC（仅静态链路）；BLE/经典 watcher 并发细节、`Native.cs` P/Invoke 逐个核对超出本次范围。
- **R8 表述建议修正**：`review-standard.md` 把"测不了"归因于 WinRT，实测 `BatteryPluginBase` 并不碰 WinRT（只依赖纯 P/Invoke 的 `Hid` 层）——真正障碍是逻辑与真实 HID I/O 耦合，正确做法是把纯决策抽成自由函数。
- **`review-standard.md` §1 措辞张力**：§1 把"资源泄漏"一律列 P0，但"退出路径未释放"（CR-07）与"运行时累积泄漏"（CR-09）后果不同，建议区分。

---

## 📚 成员产出索引

- **gstack-qa-lead（质量门神）**：构建 0/0、17 类 177 用例全绿矩阵、5 处覆盖缺口、发布检查清单 + 回滚预案、8 条静态冒烟清单、Go/No-Go。
- **gstack-security-officer（安全卫士）**：SEC-01~SEC-14（2 个 P0 写入破口）、OWASP（适配桌面）+ STRIDE 补充层、全仓 R1/R2/R4/R5 红线核对。
- **gstack-product-reviewer（产品官）**：CR-03 加重定性（波及所有数据源）、D1~D10 去重/抽离清单、架构契约分析、CR-04/13/15/16/17 复核。
- **gstack-investigator（排障手）**：CR-05~~CR-16 复核 + NF-01~~NF-04 新增、BACKLOG 末尾 4 条未核实项核实、CR-16 复杂度拆分建议、性能与健康检查小结。
- **跟踪载体**：所有条目已落 `docs/reviews/BACKLOG.md`（先 BACKLOG 后报告，符合 `review-standard.md` §7）。

---



> 本报告由软件工坊 AI 协作生成，关键决策请由工程负责人复核。
