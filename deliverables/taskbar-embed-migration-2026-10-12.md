# 任务栏电量贴片 →「真嵌入」迁移方案（评估稿 · 第 2 版）

> 时间：2026-10-12。状态：**已落地**（用户 2026-10-12 拍板"直接走方案 A"，同日实现并验证完毕）。
> **本文保留为"决策当时的评估稿"**——结论、证据链与风险清单都在这里；
> **实现结果与最新口径以 `docs/dev/runtime-pitfalls.md` §16 与 `docs/project/changelog.md`（2026-10-12 那一节）为准**。
> 落地内容：新建 `src/VigilWatt.App/Services/TaskbarPillWindow.cs`（原生分层窗口）+ `TaskbarPillRenderer.cs`，
> 删除 `TaskbarBatteryWindow.xaml(.cs)` 与 `Services/TaskbarChipFactory.cs`。**WinUI 3 窗口不可 reparent**（§2.4 结论成立），
> 故整条走原生。验证：build 0 警告 0 错误、测试 292/292、`GetParent(贴片) == Shell_TrayWnd`。
> 本文只出方案与证据，**当时不含任何代码改动**（遵循 `AGENTS.md` §3：UI 改版先出方案）。
> 第 2 版新增：TrafficMonitor 与 TrayStats 的**成熟配方**（用户 2026-10-12 提供的线索），
> 结论由第 1 版的"大概率走不通"修订为"**可行，但不能用 WinUI 3 窗口去 reparent**"。
> 落点建议：定稿后结论进 `docs/dev/runtime-pitfalls.md` §16（现状文档漏了本文 §2 这条路线），
> 未决项进 `docs/reviews/BACKLOG.md`。

## 1. 目标与非目标

**目标**：让贴片具备"真嵌入"的三个特征——① 任务栏移动/自动隐藏时由**系统**带着它走；
② Z 序处在任务栏**内部**（开始菜单打开也不被遮挡）；③ 从而**删掉 16ms 抬 Z 序轮询**。

**非目标**：不改贴片显示内容/配色/设置项语义；不碰硬件层；不改变"本机电量恒定排除"等既有决定。

## 2. 技术前提（查证结论）

### 2.1 「真嵌入」的实际做法 = 跨进程 `SetParent`

不是 deskband。是把自己建的一个窗口挂成任务栏的子窗口：

```cpp
SetParent(自己的窗口, Shell_TrayWnd);   // 副屏是 Shell_SecondaryTrayWnd
```

公开实现的原文：

- **ChissaQAQ/TaskbarLyrics**：README「原理」——"用 `SetParent` 把窗口挂进任务栏（主屏 `Shell_TrayWnd`，副屏 `Shell_SecondaryTrayWnd`）"。
- **Jimmy-Sue473/FunLyrics**：README「关键实现细节」——"**任务栏嵌入**：`SetParent` 成为任务栏子窗口，随任务栏波段提升，开始菜单打开也不被遮挡"。
- **nope-e/taskbar_obs_mute 的 `ARCHITECT.taskbar.md`**——"**本质上是一个 Win32 子窗口 reparent 方案**"，含 5 步流程与完整风险点。

### 2.2 正面范例：TrafficMonitor（用户 2026-10-12 提供）

TrafficMonitor **正是**"真嵌入"，且是这条路上最成熟的实现：

- 结构：抽象基类 `CTaskBarDlg` 定义统一嵌入契约 + Win11 子类 `Win11TaskbarDlg`。
- `Win11TaskbarDlg.cpp` 的 `GetParentHwnd()` **直接返回任务栏句柄** → 走 `SetParent`；
  定位用 `MoveWindow(m_rect)`，而 `m_rect` 的坐标是**相对任务栏**的
  （源码里 `m_rcStart.MoveToXY(m_rcStart.left - m_rcTaskbar.left, ...)`）→ 确证它是**子窗口**，不是覆盖窗。
- 它甚至要处理：任务栏左对齐/居中、小组件按钮避让、副屏无 `TrayNotifyWnd` 时用固定 88px、
  build 22621 触屏设备垂直位置修正——**全是"真嵌进任务栏"才会有的问题**。
- 社区描述："Windows 10 及更早版本的任务栏内部存在可承载子窗口的区域，程序可以把自身窗口真正'插入'任务栏；
  而 Windows 11 重写了任务栏"（`opendeep.wiki` 对 TrafficMonitor 的机制分析）。

### 2.3 **关键配方：Win11 任务栏的「三道锁」**

`PCCCQ/TrayStats` 的 README 把 Win11 的难点与解法讲得最清楚（**原文引用**）：

> **实现说明（Win11 任务栏的三道锁）**
> Win11 任务栏是 XAML 架构，Win10 时代 TrafficMonitor 的窗口嵌入法已失效。本程序的做法：
> 1. 把状态条窗口 `SetParent` 为任务栏 `Shell_TrayWnd` 的**子窗口**（随任务栏移动/隐藏）；
> 2. 加 `WS_EX_LAYERED` 分层样式——普通子窗口会被任务栏的 XAML 合成层盖住，
>    **分层子窗口有自己的 DWM 合成面，能显示在 XAML 之上**；
> 3. 内容不走 `WM_PAINT`，而是每秒渲染成 32 位 ARGB 位图（透明底、预乘 alpha），
>    经 `CreateDIBSection` + `UpdateLayeredWindow` 提交（同时完成定位）。

**这条对我们是决定性的**：它同时解掉了我第 1 版里标为高风险的 R6（Win11 XAML 层会盖住子窗口）
和"WinUI 3 内容岛在子窗口里会断"的问题——**答案是根本不要用 XAML / 内容岛承载内容，
内容走 `UpdateLayeredWindow` 的 ARGB 位图**。

### 2.4 但**不能用 WinUI 3 窗口去 reparent**（第 1 版结论，仍然成立）

| 实现 | 技术栈 | 依据 |
|---|---|---|
| TrafficMonitor | C++ 原生 Win32 | 源码 `TaskBarDlg.cpp` / `Win11TaskbarDlg.cpp` |
| TrayStats | **C#**（.NET Framework 4.8，单文件） | README「从源码构建」 |
| ChissaQAQ/TaskbarLyrics | WPF / .NET 10 | README |
| Jimmy-Sue473/FunLyrics | WPF / .NET 10 | README |
| ANYNC/TaskbarLyrics | **WPF** + WebView2 | csproj `UseWPF=true`，无 `Microsoft.WindowsAppSDK` |

**没有一个用 WinUI 3。** 且 `melody0709/zencrop` 的 `doc/WinUI3_Reparenting_Fix.md` 记录了原因：

- DWM 对子窗口**立即停止合成** Mica/Acrylic；
- 带 `WS_CAPTION` 时 User32 会画"蓝幽灵"子窗口标题栏；
- 对 `DesktopChildSiteBridge` 架构（= 纯 WinUI 3）：**reparent 顶层窗口 + 去掉 `WS_CAPTION`
  → 整个 DWM 合成链断裂，得到一大块纯灰/空白背景，没有内容**；只加 `WS_CHILD` 不去 caption
  → 应用内部把客户区**下移**给隐藏的回退标题栏让位，坐标全错。

> 我们的 `TransparentWindowSurface.ApplyBorderlessChrome` 走的正是"去 caption"那一支。
> 所以：**贴片那个 WinUI 3 窗口不能拿去 reparent**——要真嵌入，得**新建一个原生分层窗口**来承载内容。

### 2.5 官方文档的硬约束

- 跨进程 `SetParent` 会**强制重置子窗口进程的 DPI 感知**（Win10 1703+）。
- `SetParent` **不会自动改 `WS_CHILD` / `WS_POPUP`**，必须自己同步。
- Raymond Chen：`SetParent` 文档**曾有"不支持跨进程"的严厉警告**（后被删），他见过客户这么做后**间歇性不稳定**；
  跨进程父子窗口还会**把消息循环强制串联成一个队列**（`walterlv` 那篇讲得最透）——
  **explorer 卡 = 贴片卡，贴片卡 = explorer 卡**。

## 3. 三条可选路线

### 路线 A：新建原生分层窗口 + `SetParent`（**推荐**）

**做法**（照 §2.3 的三道锁）：

1. 贴片内容**不再由 WinUI 3 承载**。新建一个原生 Win32 窗口：
   `WS_POPUP | WS_CHILD`，扩展样式 `WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`。
2. `SetParent(hwnd, Shell_TrayWnd)`（副屏 `Shell_SecondaryTrayWnd`）。
3. 内容用 **GDI / GDI+ 画进 32 位预乘 ARGB 位图**，`CreateDIBSection` + `UpdateLayeredWindow` 提交
   （**顺带完成定位**，不需要 `MoveWindow` + `WM_PAINT` 两条路）。
4. 定位沿用 `TaskbarLayout` 的数学，但坐标从"屏幕绝对"改成"**相对任务栏客户区**"
   （TrafficMonitor 就是这么做的：`m_rcStart.left - m_rcTaskbar.left`）。

- **改动面**：
  - 新增一个原生分层窗口类（替代 `TaskbarBatteryWindow` 的窗口部分）；
  - `Services/TaskbarChipFactory`：从"产 XAML 元素"改成"往位图上画"（文字/图形/分隔线用 GDI/GDI+ 重做）；
  - `Services/TransparentWindowSurface`：**整体作废**（原生分层窗口不需要"清两层背景"那套，§17 整节不再是贴片的路径）；
  - `Services/TaskbarLayout`：数学**可复用**，坐标系改写；
  - 新增：`TaskbarCreated` 广播监听（Explorer 重启重挂）。
- **收益**：三个"真嵌入"特征全部拿到；删掉 16ms 定时器；不再抢 Z 序；"点任务栏闪"根因消失。
- **代价**：贴片 UI 要重写一遍（文字渲染从 XAML 降到 GDI/GDI+，字体/DPI/主题色要重做）。
- **信心来源**：`TrayStats` 是**同语言（C#）+ 同功能（任务栏状态条）+ 单文件**的现成先例，MIT 许可，可直接参照 API 用法。

### 路线 B：先做最小探针（**建议与 A 合并，作为 A 的第一步**）

第 1 版把探针定位为"验证 WinUI 3 能否 reparent"——**这个问题现在已经有答案了（不能）**。
探针的用途改为：**在本机（Win10 19045）验证"原生分层窗口 + SetParent"这条配方能否正常显示**。

- **做法**：写一个最小原生分层窗口（几十行），`SetParent` 到 `Shell_TrayWnd`，画一块纯色/一行文字，
  截图确认：① 显示正常不灰屏；② 任务栏自动隐藏时跟着藏；③ 开始菜单打开时仍可见。
- **成本**：很小。**收益**：把配方在本机跑通再动贴片主体，避免改完才发现要返工。
- **注意**：跨进程父子窗口会串联消息循环，探针期间别与 dongle 相关操作并行
  （项目已知坑：强杀实例会粘住迈从 dongle 会话约 2 分钟）。

### 路线 C：不迁移，只治现有覆盖窗的三个痛点

- **16ms 抬 Z 序轮询 → 事件驱动**：改用 `SetWinEventHook`（`EVENT_OBJECT_LOCATIONCHANGE` /
  `EVENT_OBJECT_SHOW` / `EVENT_OBJECT_HIDE`），只在任务栏真正变化后补一次 `SetWindowPos`。
- **自动隐藏判定**：从"矩形厚度 ≤ 4px"改成监听任务栏 SHOW/HIDE 事件。
- **"点任务栏闪"**：根因（同为 topmost 被抢 Z 序）**无法消除**，只能由事件驱动缩短补抬延迟。
- **收益**：小改动、零新风险，顺带解决此前排查的"后台干扰"。
- **代价**：**不解决"不是真任务栏方案"这件事本身**。

## 4. 建议

**推荐：路线 B（探针，作为第一步）→ 路线 A（正式迁移）。路线 C 作为兜底。**

理由：

- §2.2 / §2.3 已经给出**成熟配方**（TrafficMonitor 验证了机制、TrayStats 验证了 C# 实现路径），
  路线 A 从第 1 版的"高风险重写"降级为"**有现成参照的重写**"；
- **本机是 Win10 19045**，而 Win10 的任务栏恰恰是"内部存在可承载子窗口的区域"的**经典形态**
  （TrafficMonitor 的经典路径），**比 Win11 更容易做成**——本机就能验收；
- 探针成本极低，且能一次性回答"配方在本机是否可用"，值得先跑。

**什么时候选 C**：如果探针发现本机也无法稳定显示，或者评估后认为"为一个已经调好的 UI 重写渲染"
不划算，那就退回 C，把现状的性能与判定问题治掉，形态维持覆盖窗。

## 5. 风险清单（走 A）

| # | 风险 | 说明 |
|---|---|---|
| R1 | **消息循环串联** | 跨进程父子窗口强制串联消息队列：贴片卡 → explorer 卡。常驻自用小工具引入"拖死整条任务栏"的可能 |
| R2 | **官方未支持** | Raymond Chen：文档曾有 "not supported" 警告；见过间歇性不稳定 |
| R3 | **DPI 感知被重置** | 跨进程 `SetParent` 强制重置子窗口进程 DPI 感知（官方文档），现有 DPI 计算需重做 |
| R4 | **Explorer 重启** | 旧 HWND 失效，必须监听 `TaskbarCreated` 重挂 |
| R5 | **类名非稳定 API** | `Shell_TrayWnd` / `TrayNotifyWnd` / `Start` 均非正式 API，系统更新可能失效 |
| R6 | **Win11 重叠/避让是长期战场** | 分层子窗口能画在 XAML 之上（§2.3 锁 2），但**重叠问题连 TrafficMonitor 都没消停**：issue #1925（任务栏标签占满时叠加覆盖）、#2199（25H2 与任务栏程序重叠）。本机是 Win10，风险低；将来上 Win11 要重验 |
| R7 | **未验证路径** | 自动隐藏 / 竖向任务栏 / 多显示器（现状也未真机验证，迁移后要重新验一遍） |
| R8 | **项目已知坑** | 强杀实例会粘住迈从 dongle 会话；探针与验证要避开 |
| R9 | **交互回归** | 拖动+位置记忆、右键菜单（`AppPopupMenu` 的 `TrackPopupMenuEx` **需要前台窗口**，子窗口下行为要重验）、双击打开主窗口、主题切换、多设备 `+N` |

## 6. 回退路径

- **探针阶段**：`SetParent(hwnd, NULL)` 恢复；探针是独立小程序/独立开关，**默认关**。
- **正式迁移**：**不删**现有 `TaskbarBatteryWindow` 实现，新实现与它**并排**；设置项加"实验性真嵌入"开关，
  出问题一键切回旧实现。
- **全程**：不碰 `dist/` 与发布流程；不执行任何 git 写操作。

## 7. 验收口径

- **形态**：任务栏自动隐藏时贴片跟着藏；开始菜单打开时贴片仍可见；点任务栏不闪。
- **性能**：16ms 定时器消失（可用日志或计数器确认）。
- **功能回归**：拖动 + 位置记忆、右键菜单能关、双击打开主窗口、主题切换、多设备 / `+N`、空态文案。
- **像素级**：贴片背景与紧邻任务栏像素逐字节相同（沿用 `runtime-pitfalls.md` §17 的判据）。

## 8. 待你定

1. **先跑探针 B 吗？**（成本极小，把配方在本机跑通再动贴片主体）
2. 若探针通过：直接走**路线 A**（新建原生分层窗口重写贴片）？还是先落**路线 C** 的改进、A 留到后面？
3. 本次查证的结论（我们的文档漏了 `SetParent` 这条路，`runtime-pitfalls.md` §16 的"deskband 是唯一真方案"不准确）
   要不要补进 §16 + `BACKLOG.md`？

## 附：证据清单

- **`zhongyang219/TrafficMonitor`**：`TaskBarDlg.cpp`（经典路径）、`Win11TaskbarDlg.cpp`
  （`GetParentHwnd()` 返回任务栏、`MoveWindow` 用相对任务栏坐标 → 子窗口）；社区机制分析
  "Win10 可真正插入任务栏 / Win11 重写了任务栏"
- **`PCCCQ/TrayStats`** README：**「Win11 任务栏的三道锁」**（`SetParent` + `WS_EX_LAYERED` +
  `CreateDIBSection`/`UpdateLayeredWindow`）；C# 单文件实现，与我们的功能同型
- `ChissaQAQ/TaskbarLyrics`、`Jimmy-Sue473/FunLyrics`、`ANYNC/TaskbarLyrics`（csproj `UseWPF=true`）
- `nope-e/taskbar_obs_mute` `ARCHITECT.taskbar.md`（机制抽象 + 风险点 + Win10/Win11 策略差异）
- `melody0709/zencrop` `doc/WinUI3_Reparenting_Fix.md`（**WinUI 3 被 reparent 的实测失败模式**）
- Microsoft Learn `SetParent`「注解」（DPI 强制重置；`WS_CHILD`/`WS_POPUP` 不自动改）
- `walterlv`《使用 SetParent 跨进程设置父子窗口时的一些问题（小心卡死）》（消息循环串联）
