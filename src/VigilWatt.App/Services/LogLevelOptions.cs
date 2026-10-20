using VigilWatt.Core.Utils;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 设置页「日志级别」下拉框的展示与说明。
/// 2026/10/09 10:13:25 xliu
/// 取值定义在 Core 的 <see cref="LogLevelNames"/> （**单一来源**），这里只负责"取值 → 中文标签 / 一句话说明"。
/// </summary>
public static class LogLevelOptions
{
    /// <summary>下拉框档位，顺序与展示一致：关闭 → 详细 → 信息 → 警告 → 错误。</summary>
    public static IReadOnlyList<(string Value, string Label)> Choices { get; } = new[]
    {
        (LogLevelNames.Off, "关闭"),
        (LogLevelNames.Debug, "详细"),
        (LogLevelNames.Info, "信息"),
        (LogLevelNames.Warn, "警告"),
        (LogLevelNames.Error, "错误"),
    };

    /// <summary>取值 → 中文标签。</summary>
    public static string Label(string? value)
    {
        var v = LogLevelNames.Normalize(value);
        foreach (var (val, label) in Choices)
            if (val == v) return label;
        return "信息";
    }

    /// <summary>设置页副标题：说明当前档位意味着什么（统一带"需重启生效"）。</summary>
    public static string Describe(string? value) => LogLevelNames.Normalize(value) switch
    {
        LogLevelNames.Off => "不记录任何日志（含错误）。改后需重启应用生效",
        LogLevelNames.Debug => "记录全部细节，含逐帧转储与接口清单，日志量很大。改后需重启应用生效",
        LogLevelNames.Warn => "只记录警告与错误。改后需重启应用生效",
        LogLevelNames.Error => "只记录错误。改后需重启应用生效",
        _ => "默认档：记录流程里程碑与协议解析结果。改后需重启应用生效",
    };
}
