# 日志设计

> **状态：已实施（2026-10-09 起；2026-10-10 改为按日分文件）。** 本文描述当前形态；
> 实施过程与取舍见 §10 变更历史（原《日志改造方案》已归档到那里）。

## 1. 概述

全部日志走**自研的极简静态类** `src/VigilWatt.Core/Utils/Log.cs`（**没有**引入 Serilog / NLog / `ILogger`）：

- 输出位置：`%LOCALAPPDATA%\VigilWatt\logs\app-yyyy-MM-dd.log`（`AppPaths.RootDir` + `logs`）——**按本地日期每天一个文件**
- 行格式：`2026-10-09 21:15:21.808 [INFO] 雷柏 状态帧: ...`
- 分级：`Debug < Info < Warn < Error`，外加 `Off`（全关哨兵），默认 `Info`
- 落盘：常驻一个 `StreamWriter`（`AutoFlush = true`，UTF-8 **无 BOM**）；写入时比对日期，**跨天自动切换**到新文件（长驻应用无需重启）
- 保留：默认保留最近 **14 天**（`AppSettings.LogRetentionDays`，**重启生效**），启动与跨天时清理超期文件；旧版 `app.log` / `app.N.log` 已废弃，一并清掉
- 写失败不静默：连续 3 次失败写兜底文件 `logs/log-error.txt`（带节流与上限）
- `Log.RedirectTo(logDir)` 供单测重定向目录（不重定向会污染真实日志目录）

相关文件：

| 文件 | 职责 |
|---|---|
| `src/VigilWatt.Core/Utils/LogLevel.cs` | 级别枚举 + 名称解析（未知值回落 `Info`） |
| `src/VigilWatt.Core/Utils/Log.cs` | 日志实现（分级 / 按日分文件 / 保留清理 / 兜底 / 退避 / 降噪入口） |
| `src/VigilWatt.App/Services/LogLevelOptions.cs` | 设置页下拉框档位（取值 → 中文标签 / 说明） |
| `src/VigilWatt.App/Views/SettingsPage.xaml(.cs)` | 「日志」分区：级别下拉框 + 保留天数 + 打开目录 + 清空日志 |
| `src/tests/VigilWatt.Core.Tests/LogTests.cs` | 单测（级别过滤 / 按日命名 / 保留清理 / 兜底 / 多行前缀 / 转义 / 降噪） |

## 2. 分级与标签

```
Debug < Info < Warn < Error          （Off = 全关哨兵）
```

| 标签 | 级别 | 语义 | 说明 |
| --- | --- | --- | --- |
| `DBG ` | Debug | 逐帧报文、探测明细、"读了但没解析出东西"的中间帧 | **默认不可见**，调协议时才切 |
| `INFO` | Info | 流程里程碑 + 协议解析**结果**（行尾带 `原始=<HEX>`） | 默认档 |
| `WARN` | Warn | 异常但可继续 | 不变 |
| `ERR ` | Error | 功能失败或未处理异常 | 标签不变，堆栈改多行前缀 |

标签宽度统一 **6 字符**，`[DBG ]` 与现有三个对齐，列不错位。**标签保持 ASCII、不得中文化。**

> ⚠️ **不要沿用 .NET 的 `[Conditional("DEBUG")]` 语义。**
> `System.Diagnostics.Debug.WriteLine` 带 `[Conditional("DEBUG")]`，Release 编译时整个调用会被编译器删掉。
> 若照搬，发布版里 Debug 日志会凭空消失，用户在 `settings.json` 里配 `LogLevel=Debug` 也没用（调用点根本不存在）。
> **本项目的 Debug / Info 只是运行期过滤档位，发布版同样编译进去**，开销靠 `Log.IsEnabled()` 提前短路控制。

### 2.1 什么时候用哪一级（新增日志前先问）

- 逐帧 / 中间帧 / 接口清单 / 探测明细 → **`Debug`**
- 状态变化、协议解析结果、里程碑 → **`Info`**
- 异常但可继续 → `Warn`；功能失败 → `Error`
- 热路径先 `Log.IsEnabled(level)` 短路，省掉 `$"..."` 插值开销。

## 3. 配置

- `AppSettings.LogLevel`（`string`，默认 `"Info"`），沿用项目"未知取值一律按默认处理"惯例
  （与 `WidgetDisplayMode` / `WindowBackdrop` 写法一致）。
- 启动时 `Log.MinLevel = LogLevelNames.Parse(Services.Settings.LogLevel)`；**重启生效**（不追求热更新，
  避免跨线程读静态字段引入 `volatile`）。
- `AppSettings.LogRetentionDays`（`int`，默认 **14**）：按日日志的保留天数，`0` 表示不清理；
  启动时 `Log.RetentionDays = Settings.LogRetentionDays`，**重启生效**。
- 设置页「日志」分区：
  - **级别下拉框**（5 档：关闭 / 详细 / 信息 / 警告 / 错误，见 `LogLevelOptions`）
  - **保留天数**（`NumberBox`，1~365，默认 14）
  - 「打开日志目录」按钮
  - **「清空日志」按钮**（删 `app*.log`——含按日文件与旧版单文件——+ `log-error.txt`）
  - 级别与保留天数均**明确标注"改后需重启应用生效"**——避免重蹈"点了没反应"的老坑
- `log-error.txt` 自身**带上限（64 KB）与节流（30 s）**，否则兜底通道也会涨爆。

## 4. 格式契约

**改动会破坏 docs/ 里全部 grep 考古命令，实施时不得破坏：**

| 不变量 | 说明 |
| --- | --- |
| 行首是 `yyyy-MM-dd HH:mm:ss.fff` | `grep -a "^2026-10"` 这类时间过滤依赖它。**是本地时间**（见 §6.5） |
| 级别标签宽度 6 字符 | `[DBG ]` / `[INFO]` / `[WARN]` / `[ERR ]`，保持 ASCII |
| **一个逻辑事件 = 一行** | message 里的换行/控制符写入前转义（`\n` / `\xNN`）；异常堆栈后续行统一加缩进前缀 `       \| ` |
| 协议解析结果行尾带 `原始=<HEX>` | 这是默认 `Info` 下仍能排障的前提 |
| 编码 = UTF-8 **无 BOM** | 写入用 `UTF8Encoding(encoderShouldEmitUTF8Identifier: false)`；别改成 `Encoding.UTF8`（会带 BOM） |

## 5. 降噪

轮询型代码（每 15 秒 / 每分钟重跑）经常在同一状态上重复打日志，把日志文件冲爆。
去重逻辑**收拢到 `Log` 的公共入口**，不要在调用点自己攒字段：

| 入口 | 语义 |
| --- | --- |
| `Log.InfoOnChange(key, state, message)` | 状态与上次不同才打一条，**返回"是否变化"**（可用来决定是否顺带 dump 一批 Debug 明细） |
| `Log.InfoOnChange(key, message)` | message 自身即状态 |
| `Log.InfoThrottled(key, message, interval)` | 同一 key 在 `interval` 内最多打一条 |
| `Log.Once(level, key, message)` / `Log.ResetOnce(key)` | 同一 key 只记一条（按 `level` 判启用），直到显式复位；**返回"本次是否真的记了"**。用于"信息量只有一次、但会随节拍无限重复"的事件（如设备睡着导致探测反复失败） |

- 内部用 `ConcurrentDictionary` 维护状态；并发下最坏情况是同一状态多打一条——日志去重不是正确性关键，不值得上锁。
- 当前级别高于 `Info` 时**不更新状态**，这样用户切回 `Info` 档还能看到最新状态（不丢信息）。
- 迁移范围**克制**：只把"同一 key 重复打"的降噪收进公共入口，不在调用点自己攒字段。
  2026-10-10 起，原来散在插件里的探测记忆三件套（`MchosePlugin.LogOnceByKey` / `ShouldLogOnce` / `ResetLogOnce`）
  已收拢为 `Log.Once` / `Log.ResetOnce`——插件只保留 key 的拼装（`MchosePlugin.LogKey(handle, 用途)`），
  状态表归 `Log` 统一维护。

## 6. 稳定性与管控

### 6.1 按日分文件与保留

按**本地日期**每天一个文件：`app-yyyy-MM-dd.log`。写入前比对日期，跨天自动切换（长驻应用不重启也能切）。
**不再按大小滚动**——一天就是一个文件（Debug 档单日文件可能较大，靠保留天数控制总量）。

```
app-2026-10-09.log
app-2026-10-10.log   ← 当天
```

保留最近 `Log.RetentionDays` 天（默认 **14**）：启动与跨天打开新文件时清理超期文件
（文件名日期早于 `今天 - 保留天数`）；`0` = 不清理。旧版单文件 `app.log` / `app.N.log` 已不再写入，
清理时一并删除——留着只会让人以为"日志没更新"。

> ⚠️ docs 里 `grep ... app.log` 的考古命令请改成 `grep ... app-*.log`（覆盖全部日期），
> 或 `grep ... app-$(date +%F).log`（只看当天）。

### 6.2 写失败兜底与退避

- 写失败后置 **30 s 退避**：期间不再重试，避免每行日志都重跑一遍系统调用（N2 重试风暴）。
- 连续失败达 **3 次**写 `logs/log-error.txt`（30 s 节流 + 64 KB 上限）。
- 兜底通道**自身失败就彻底放弃**（`_disabled`），不递归、不再刷。

### 6.3 并发

`FileShare.ReadWrite` 打开当天日志文件 —— **仅保证并发不互相阻塞，不保证不交错**。
单实例 `Mutex` 才是主防线（§6.4）。

### 6.4 退出收尾

`Log.Shutdown()`（`Flush` + 释放句柄）挂在**真实退出路径**上：

- `TrayService.Exit()` → `App.ShutdownLogging()`（`ReleaseMutex` + `Dispose` + `Log.Shutdown`，**幂等**）
- 单实例检测到重复启动时的退出路径同样走它
- `AppDomain.CurrentDomain.ProcessExit` 兜底（覆盖非托盘退出路径）

> ⚠️ **不要挂 `AppServices.Dispose`**：全仓无人调用它（死代码），钩子挂那儿永远不会触发（N1）。

### 6.5 时间基准（与 history.db 对齐）

- 日志行首是**本地时间**（人读友好、grep 习惯不变）；`history.db` 的 `ts` 是 **UTC 毫秒**。
- 每个日志文件**新建时**在首行写一条"时间基准"声明：

```
2026-10-09 10:23:15.001 [INFO] 时间基准: 本地时间 China Standard Time (UTC+08:00)；history.db 的 ts 为 UTC 毫秒
```

该行带完整时间戳前缀，不破坏 `grep -a "^20"`；仅在文件为空时出现一次。
拿到日志的人据此即可把日志时间与数据库时间对齐。

## 7. 测试

`src/tests/VigilWatt.Core.Tests/LogTests.cs`（`RedirectTo` 到临时目录，每个用例独立目录）：

- 级别过滤（低于阈值不写 / `Off` 连 Error 也不写）
- 按当天日期命名（写入落到 `app-yyyy-MM-dd.log`）
- 超过保留天数的旧日志被清理、保留天数内的不清理、旧版 `app.log` 被清理
- 多行异常每行都有前缀
- 换行与控制符转义（不产生假行）
- 文件首行写时间基准声明
- 写失败不抛、降级写兜底文件
- `InfoOnChange` 状态相同只记一次、`InfoThrottled` 间隔内只记一次

`src/tests/VigilWatt.Core.Tests/AssemblyInfo.cs` 有
`[assembly: CollectionBehavior(DisableTestParallelization = true)]` —— `Log` 是全局静态，
`RedirectTo` 改的是全局状态，**必须禁并行**，否则跨测试类污染（N6）。

## 8. 默认 `Info` 的边界（实测依据）

对某次真实的日志文件（当时仍是单文件 `app.log`，2 天，11516 行）按类别统计：

| 类别 | 2 天行数 | 占比 | 归属 |
| --- | --- | --- | --- |
| 接口清单（`  <设备名> [VID_..] usage=..`） | 3075 | 26.7% | **Debug** |
| HID 元数据（`HID 元数据 24AE/..`） | 3065 | 26.6% | **Debug** |
| 收到托盘回调（`收到托盘回调: 0x…`） | 1728 | 15.0% | **Debug** |
| 逐帧原始报文（`北通/雷柏 收到报文: <HEX>`） | 1132 | 9.8% | **Debug** |
| 其余流程 + WARN（其中 WARN 223） | 1637 | 14.2% | **Info** |
| 协议解析结果（`雷柏 状态帧` / `迈从 ... 解析`） | 879 | 7.6% | **Info** |

**噪音合计 9000 行（78%）**。默认 `Info` 后预期 2 天日志量 **~11500 → ~2500 行**。

> 注：上表数字来自某次真实日志文件，**仓库内无该文件，无法自动验证**。复现：取
> `%LOCALAPPDATA%\VigilWatt\logs\` 下的按日日志（`app-*.log`），按上表各簇 `grep -c` 统计。
> 行示例修正：实际是 `收到托盘回调: 0x{mouseMessage:X4}`（`TrayService.cs`），非 `NxN`。

**补充归类（原表遗漏，均归 Debug）**：Mchose 逐帧/握手簇（`MchosePlugin.cs`）、`雷柏 电量越界丢弃`（`RapooPlugin.cs`）。

**关键：排障能力零损失。** 两条协议解析日志的**行尾本来就带着原始 hex**：

```
[INFO] 雷柏 状态帧: 连接=2.4G 充电标志=1 电量=95% RF=0xCF(bit7连接=True RSSI=79) 原始=07 10 04 80 0C 80 0C 01 5F 00 03 01 00 12 46 01 02 02 CF
```

所以下沉掉的只是"读了但没解析出东西"的中间帧，`grep "状态帧"` 能拿到的信息一个不少。

**什么时候需要切 `Debug`**：调新协议、看探测矩阵、需要完整接口清单时 —— 即
[hardware/safety.md](../hardware/safety.md) 规则 6 里"先被动监听看日志"那一步。

## 9. 设计决策与理由

| 决策 | 理由 |
| --- | --- |
| **不引第三方框架** | 自研类约 300 行可控；Serilog/NLog 会撑大发布体积并给 Core 加依赖 |
| **合并 Trace 与 Debug，只留一个 `Debug`** | Debug 本就是最低监测档，不再往下细分 |
| **`Off` = 关掉全部输出（含 Error）** | 它是"全关哨兵"，不是"只留 Error" |
| **格式向后兼容** | `[INFO]` / `[WARN]` / `[ERR ]` 一个都不改，只新增 `DBG `；docs/ 里所有 grep 考古命令继续可用 |
| **日志按天分文件**（2026-10-10 起，**推翻** 2026-10-09 的"不做按天分文件"） | 一天一个文件、跨天自动切：定位某天的日志更快，历史也不会被大小滚动冲掉。代价是 docs 里 `grep app.log` 要改成 `app-*.log` |
| **按天保留天数可配**（默认 14 天） | 个人工具，日志总量可控；`LogRetentionDays=0` 可关掉自动清理 |
| **不做结构化 / JSON 日志** | grep 考古是本项目主力手段，纯文本格式价值高于可解析性 |
| **不做日志上报 / 遥测** | 个人自用工具，不引入任何外发通道 |
| **不做设备名 / MAC 脱敏** | 个人本机自用，设备名/路径进日志是**有意为之**（脱敏收益低） |
| **`FileShare.ReadWrite`** | 仅保证并发不互相阻塞，不保证不交错；单实例 Mutex 才是主防线 |
| **退出收尾挂真实路径** | `AppServices.Dispose` 是死代码，挂那儿永不触发 |
| **`LogConfig` 收敛未做** | 纯内部搬迁无可感知收益，徒增回归面（个人工具，克制） |

## 10. 变更历史

### 2026-10-08 初稿

记录现状问题（S1–S7 稳定性、M1–M7 可维护性）与目标设计。

### 2026-10-09 补强评审（软件工坊团队）

修正 3 处事实错误（M4 文件数 12→17；M3 行号；§4 的 `AppServices.Dispose` 前提不成立），
把 S1/S3/S4 提前到阶段 1，补齐"管控"半边，新增 7 项遗漏 N1–N7，补文档同步清单与回滚方案。

### 2026-10-09 实施（三阶段全部落地）

- **阶段 1**：级别体系 + 配置 + P0 稳定性（滚动 / 兜底 / 退避 / 按字节计量 / `FileShare.ReadWrite` / `Shutdown`）+ LogTests。
- **阶段 2**：N3 控制符清洗、N4 时间基准、N6 测试隔离、N7 Mutex 释放。
- **阶段 3**：`Log.InfoOnChange` / `Log.InfoThrottled` 泛化 + 迁移纯日志用途调用点 + 文档同步。
  `LogConfig` 收敛按方案标注**砍掉**（可选）。

### 2026-10-10 改为按日分文件（推翻 §9 的"不做按天分文件"）

- **诉求**：日志不要全挤在一个文件里，按自然日输出。
- **改动**：文件名 `app.log` → `app-yyyy-MM-dd.log`（本地日期）；写入前比对日期，跨天自动切换；
  移除按大小滚动（`MaxBytes` / `MaxRollFiles` / `Roll` / `RotateFiles`）。
- **新增**：`AppSettings.LogRetentionDays`（默认 14，重启生效）→ `Log.RetentionDays`；
  启动与跨天打开新文件时清理超期文件；旧版 `app.log` / `app.N.log` 一并清掉。
- **UI**：设置页「日志」分区新增"日志保留天数"；「清空日志」文案与匹配改为 `app*.log`。
- **格式契约不变**（行首时间戳 / 标签宽 6 / 一行一事件 / UTF-8 无 BOM / 首行时间基准），
  只是**文件名**变了 → docs 里 `grep ... app.log` 全部改为 `grep ... app-*.log`。

### 回滚

- 格式**向后兼容**：只新增 `[DBG ]`，`[INFO]` / `[WARN]` 不变 → 回滚 = `git revert` 该提交，docs/ 的 grep 命令无需回改。
- **唯一破坏性变更**：`[ERR ]` 异常由单行改多行前缀（§4）。若确有依赖旧单行格式的脚本，回滚需一并还原该改动。
- 2026-10-10 的按日改名**会**影响旧命令：回滚该轮即恢复 `app.log`，docs 里改成 `app-*.log` 的命令需一并回改。
- `log-error.txt` / 时间基准行均为**新增**，回滚后自然消失。
