namespace VigilWatt.Core.Utils;

/// <summary>
/// 功 能： 日志级别，数值越大越严重。
/// 2026/10/09 10:13:25 xliu
/// <see cref="Off"/> 是"全关"哨兵，刻意排在 <see cref="Error"/> 之后，使"level &gt;= MinLevel"在 Off 时恒为 false（连错误也一并关掉）。
/// </summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
    Off = 4,
}

/// <summary>级别取值的字符串表示与解析（**单一来源**）。settings.json 存字符串，未知取值回落 <see cref="Default"/>（与 WidgetDisplayMode / WindowBackdrop 的"未知按默认"惯例一致）。</summary>
public static class LogLevelNames
{
    public const string Off = "Off";
    public const string Debug = "Debug";
    public const string Info = "Info";
    public const string Warn = "Warn";
    public const string Error = "Error";

    public const string Default = Info;

    /// <summary>全部合法取值，顺序与设置页下拉框展示顺序一致（关闭 → 详细 → 信息 → 警告 → 错误）。</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Off, Debug, Info, Warn, Error };

    /// <summary>把任意输入（含 null / 手改坏的配置）收敛成合法取值之一；未知回落 <see cref="Default"/>。</summary>
    public static string Normalize(string? value)
    {
        if (value is not null)
        {
            var v = value.Trim();
            foreach (var name in All)
                if (string.Equals(name, v, StringComparison.OrdinalIgnoreCase))
                    return name;
        }
        return Default;
    }

    /// <summary>解析成枚举；未知取值回落 <see cref="LogLevel.Info"/>。</summary>
    public static LogLevel Parse(string? value) => Normalize(value) switch
    {
        Off => LogLevel.Off,
        Debug => LogLevel.Debug,
        Warn => LogLevel.Warn,
        Error => LogLevel.Error,
        _ => LogLevel.Info,
    };

    /// <summary>枚举 → 规范字符串。</summary>
    public static string ToName(LogLevel level) => level switch
    {
        LogLevel.Off => Off,
        LogLevel.Debug => Debug,
        LogLevel.Warn => Warn,
        LogLevel.Error => Error,
        _ => Info,
    };
}
