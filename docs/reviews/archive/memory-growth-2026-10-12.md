# 主窗口显示后私有字节爬升 · 分析报告（2026-10-12）

对应 BACKLOG 条目：**CR-64**。本报告只承载证据与推理，条目本体在 `docs/reviews/BACKLOG.md`。

## 1. 基线与边界声明

- **基线**：2026-10-12 工作树（未提交）。`git status` 显示 11 个改动文件，其中 6 个源文件是同日"页面切换性能优化"
  （四个页面设 `NavigationCacheMode.Required`、`SettingsPage` 拆 `BuildOptions`/`SyncFromSettings`、
  `MainWindow.PrewarmSettingsPage`、`WindowBackdrop` 能力缓存）。
- **本次分析未启动应用、未向任何设备发帧**。用户明确要求：
  "别再尝试运行了，直接分析下。你再运行和我前面的运行又会有什么区别呢"。
- **数据来源**：用户本人那一次带临时埋点的 Debug 运行（读数为任务管理器"私有字节" + 应用内埋点日志）。
  临时埋点（`App.xaml.cs` 的 `--memtest`/`RunMemTestAsync`、`TrayService.MemSnapshot`、
  `TaskbarPillWindow._diagTicks`、`DevicesPage` 的两条 `[诊断]` 日志）**已全部撤销**，
  `git diff` 确认这四个文件已回到 HEAD；撤销后重新构建 **0 警告 0 错误**。
- **结论性质**：第 2 节的**读数**是实测；**机制归因**是代码推断，已逐条标注。第 4 节单列未能证实项。

## 2. 逐条发现

### CR-64 · 主窗口每次显示后进程私有字节单调爬升（P1，定级待复核）

**实测读数**

| 场景 | 私有字节 | 托管堆 | 句柄 |
|---|---|---|---|
| 隐藏 · 空闲 90s（17:55:24→17:56:54） | 122 → 120 MB（**零增长**） | 2–3 MB 恒定 | 1353 → 1368 稳定 |
| 显示/隐藏循环 90s（17:57:02→17:58:32） | 120 → **171 MB（+51）** | 1–3 MB 恒定 | 稳定 |

时间线上的三个关键点：

1. `17:57:02.072` 显示前 Priv=120 MB → `17:57:04.173` Priv=127 MB（**+7 MB / 2.1s**）；
2. `DevicesPage Unloaded`（由窗口 Hide 触发）→ `17:57:14.277` Priv=147 MB（**+20 MB / 10s**）；
3. `17:57:54.850` 显示前 Priv=164 MB → `17:57:54.909` 显示后 Priv=161 MB（**−3 MB**）。
   → **增长不在 `AppWindow.Show()` 的调用栈里**，是调用返回之后的**异步尾部**。

**证据（`文件:符号`）**

- `Services/TrayService.cs:ShowMainWindow` —— 显示入口，仅 `AppWindow.Show()` / `Activate()` / `SetForegroundWindow()`，
  无分配（对应上面第 3 点实测 −3 MB）。
- `MainWindow.xaml.cs:MainWindow` —— `AppWindow.Closing` 拦截为 `AppWindow.Hide()`（关闭到托盘），
  与 `ShowMainWindow` 构成 Hide/Show 往返。
- `MainWindow.xaml.cs:PrewarmSettingsPage` —— 启动即把设置页实例化并常驻（抬高基线，见 §3）。
- `Views/DevicesPage.xaml.cs:OnPageLoaded` / `:Refresh` —— `Loaded` 里**无条件**执行
  `DeviceList.ItemsSource = devices.Select(DeviceRowModel.From).ToList()`，**每次都换一个新的 List**。
- `Views/HistoryPage.xaml.cs:ReloadAsync` —— `DeviceList.ItemsSource = _rows`，同样无条件。
- `Views/StatsPage.xaml.cs:OnPageLoaded` / `:RenderChartAsync` —— 每次刷新 **`new BatteryChartControl()`**，
  再 `ChartHost.Children.Clear()` / `Add(chart)`。
- `Views/SettingsPage.xaml.cs:OnPageLoaded` / `:SyncFromSettings` —— 每次 `Loaded` 重读设置并写控件值。

**已排除的嫌疑（对照实验的直接结论）**

"隐藏空闲 90 秒零增长"这一条对照把下面这些**全部**排除掉了——它们在那 90 秒里都在正常运行：

- 1 Hz 任务栏贴片（`Services/TaskbarPillWindow.cs:UpdateCore` / `:Upload` / `:CopyToDib`，
  含每拍的 `FindWindowW` / `GetWindowRect` / `ScreenToClient` / `UpdateLayeredWindow`）；
- 15 s 发现节拍与 60 s 主轮询（含罗技 HID++ 超时，见 CR-62）；
- `Utils/Log.cs:Emit` 的全局锁 + `AutoFlush` 同步刷盘（见 CR-61）。

同样排除：**托管泄漏**（51 MB 若在托管侧，`GC.GetTotalMemory` 不可能是 1–3 MB 的平线）、
**句柄泄漏**（1353→1368）、**`ShowMainWindow()` 本身**（实测 −3 MB）。

**机制（代码推断）**

隐藏与显示之间**唯一的行为差异**是：窗口可见时，当前页的刷新会跑；隐藏时页面已被 `Unloaded` 解绑
（`_hooked` 标志，见 `dev/runtime-pitfalls.md` §19），刷新根本不发生。

而"刷新"在这些页面上等于**替换 `ItemsSource`** 或**重建控件实例**。替换 `ItemsSource` 会作废所有已实现的
item 容器并重新生成：一行设备约 10–12 个 XAML 元素（`ListViewItem`→`Border`→`Grid`→`FontIcon`
+`StackPanel`+3×`TextBlock`+`ProgressBar`，见 `Views/DevicesPage.xaml` 的 `ItemTemplate`），
5 台设备即每次刷新销毁再新建约 50–60 个元素。**每个 XAML 元素在合成器侧都有对应物**
（`Visual` / `SpriteVisual` / 画刷 / 字形），这些住在**原生堆**，不归托管 GC 管；丢掉托管包装不会同步释放合成资源，
回收被推迟，而合成器实践上只保**高水位**、不把内存还给 OS。这正好解释了
"私有字节爬升 + 托管堆恒定 + 句柄恒定"这一组合。

另一条同向的机制（框架侧，我们改不动）：`DevicesPage.Loaded`/`Unloaded` **在窗口 Hide/Show 时也会触发**
（实测，不只切页时），说明 WinUI 3 在 Hide 时把整棵内容树**摘下来**、Show 时**重新挂上**，
每次重挂都要重建合成侧资源。

**仓库内先例（同一个坑踩过两次）**

- `Views/StatsPage.xaml.cs:ReloadDeviceListAsync` 里对 `DeviceCombo.ItemsSource` 专门做了
  "列表内容没变就不重建"的守卫，注释写明重建会把打开中的下拉框关掉、把选择弹回。
  **当时只补了下拉框，图表和另外两个页面没补。**
- CR-29（已修）记的是同一类问题：一轮轮询 N 次 `Upsert` → N+1 次全量重建。
  那次用 `Interlocked` 合并降低的只是**重建频率**，"重建本身无条件"这件事一直还在。

## 3. 需要修正或遗漏的部分

- **纠正用户报告里的前提**："初始启动 110 MB 这也不正常"——**WinUI 3 启动 110 MB 属正常**。
  XAML 运行时 + DWrite 字体缓存 + 合成器 + .NET/WinRT 投影的基线通常在 80–130 MB。
  真正值得追的只有"每显示一次就涨一截"这一半，110 MB 基线本身不构成缺陷。
- **本轮改动自身抬高了基线**：`MainWindow.xaml.cs:PrewarmSettingsPage` 让设置页（大棵视觉树）整棵常驻进程，
  会把基线抬高若干 MB。这是"首次打开设置页不卡"的代价，**不属于逐轮增长**，但写在这里以免日后误判。
- **CR-29 的闭环并不完整**：它把"每轮 N+1 次重建"降成了"每轮 1 次重建"，
  但**重建本身仍然是无条件的**。三个页面都缺"内容没变就不重建"的守卫。CR-29 应视为只解决了一半。

## 4. 未能证实的部分

以下三项**均未验证**，不得当作已证结论使用：

1. **是高水位还是累积型泄漏**。只有一轮数据（120→171 MB）。"隐藏空闲 90 s 零增长"这一读数
   **偏向**高水位（即"用过就占着"，不是泄漏），但不足以定论。
   **判据**：连做 3 轮显示/隐藏，每轮静置后记一次私有字节——第 2 轮起持平 = 高水位；
   逐轮上台阶 = 累积型，按 `dev/review-standard.md` §1 应**升为 P0**。
2. **责任方是我们的刷新，还是框架重挂内容树**。两者预测同样的读数。
   **判据**：临时让 `Loaded` 跳过刷新，再跑 3 轮，看增长是否消失。
3. **未真机点检**：以上两项判据都需要运行应用，按用户要求**未执行**。

另：**"再跑一次不会有新信息"**——同一份 exe（日志中 `Environment.ProcessPath` 已确认同一路径）、
同一条代码路径。原计划的"脚本化 Show/Hide + `GC.Collect()`"只会**更差**：强制回收只管托管堆
（已证平），碰不到合成器/DWM 内存；且单实例互斥量决定了另一次运行只能**替换**用户那次、无法并行对比。

## 5. 结论去向

- 条目：`docs/reviews/BACKLOG.md` → **CR-64**（P1，含升 P0 的条件）。
- 报告本体：本文件。
- 指针：`docs/project/handoff.md` §4（只放指针，不抄条目）。

## 6. 2026-10-12 晚 · 真机实测与结论（关闭 CR-64）

本节补齐 §4 的三项未证实项，给出定论：**不是泄漏，属高水位**。

**取证方式**（全程只读；未改任何代码、未重启应用）：安装版 1.0.5，PID 14104，进程 20:26:48 启动。
① 用户提供 `%TEMP%\VigilWatt.App.DMP`（760 MB）；
② 自写脚本对**活进程**做 `GetProcessMemoryInfo` + `VirtualQueryEx` 分层采样（3 秒一拍，两个窗口共 6 分钟 / 120 点）；
③ 用户手动反复切页，并用腾讯电脑管家"回收内存"作判定性对照。

**读数**

| 项 | 读数 | 来源 |
|---|---|---|
| 托管堆 | 4.71 MB / 44,510 对象（`TextBlock` 仅 180） | `dotnet-dump` `dumpheap -stat` |
| 私有提交 `MEM_PRIVATE` | 99.1 MB（dump 时＝启动后 3 分 26 秒）→ 113.9→114.7 MB（20:42–20:47） | `MemoryInfoList` / 采样 |
| `MAPPED` | 256.8 → 285.5 MB（字体文件 + 驱动 WDDM 节） | 同上 |
| `IMAGE` | 441.3 → 441.4 MB（**全程不动**） | 同上 |
| 句柄 / 模块 / 线程 | 1411 / 187（无重名）/ 51 | `HandleData` / `ModuleList` / `ThreadList` |
| 工作集 | 137.2 →（回收）3.8 → 回升 79.8 MB | 采样 |

**判定性实验（§4 三项的答案）**

1. **高水位还是累积型** → **高水位**。6 分钟 120 点里 `PRIVATE` 净增 **+0.8 MB**，峰值 116.2 出现在点击瞬间后**立刻回落**；`MAPPED` 反而 −0.2 MB；`IMAGE` 完全不动。
2. **责任方是我们的刷新还是框架重挂内容树** → 已不必区分：两者产生的都是**驻留（工作集）**抬升，不是提交量泄漏。
3. **"内存回收"类工具的数字不能用来判断泄漏** → 回收瞬间工作集 **137.2 → 3.8 MB**，而 `priv`/`commit` 154.3、`PRIVATE` 113.9 **纹丝不动**。用户看到的"60 多 MB → 0.5 MB"就是这个量。**驻留页天生可回收**——能被打掉，说明它从来不是泄漏。

**必须纠正的读数口径**：`VirtualQueryEx` 的"已提交"总量（797.2 MB = IMAGE 441.3 + MAPPED 256.8 + PRIVATE 99.1）**不是**任务管理器的"提交大小"。IMAGE 与**文件后备**的 MAPPED **不计入进程提交量**——本进程实测提交只有 **144.3 MB**。dump 文件 760 MB 由此而来，**不能当内存占用看**。

**遗留（新条目）**：`MainWindow.xaml.cs:Nav_SelectionChanged` 不清 `BackStack` → BACKLOG **CR-65**（P2）。
