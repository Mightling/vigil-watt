using VigilWatt.Core.Utils;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： LogLevelNames —— settings.json 里日志级别的取值收敛（全检报告行动项 5）。
/// 2026/10/10 17:44:33 xliu
/// 手改坏的配置（大小写、空白、未知值、null）不能把日志系统搞崩，必须收敛到 Default。
/// </summary>
public class LogLevelNamesTests
{
    [Theory]
    [InlineData("Off", "Off")]
    [InlineData("Debug", "Debug")]
    [InlineData("Info", "Info")]
    [InlineData("Warn", "Warn")]
    [InlineData("Error", "Error")]
    public void L1_合法取值原样返回(string input, string expected)
    {
        Assert.Equal(expected, LogLevelNames.Normalize(input));
    }

    [Theory]
    [InlineData("debug", "Debug")]
    [InlineData("INFO", "Info")]
    [InlineData("  warn  ", "Warn")]      // 手改配置常带空白
    public void L2_大小写与空白都收敛到规范写法(string input, string expected)
    {
        Assert.Equal(expected, LogLevelNames.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Verbose")]              // 未知取值
    [InlineData("3")]
    public void L3_未知取值一律回落默认(string? input)
    {
        Assert.Equal(LogLevelNames.Default, LogLevelNames.Normalize(input));
    }

    [Theory]
    [InlineData("Off", LogLevel.Off)]
    [InlineData("Debug", LogLevel.Debug)]
    [InlineData("Info", LogLevel.Info)]
    [InlineData("Warn", LogLevel.Warn)]
    [InlineData("Error", LogLevel.Error)]
    [InlineData("bogus", LogLevel.Info)]
    [InlineData(null, LogLevel.Info)]
    public void L4_解析成枚举_未知回落Info(string? input, LogLevel expected)
    {
        Assert.Equal(expected, LogLevelNames.Parse(input));
    }

    [Fact]
    public void L5_枚举转字符串与解析互为逆运算()
    {
        foreach (var level in new[] { LogLevel.Off, LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error })
            Assert.Equal(level, LogLevelNames.Parse(LogLevelNames.ToName(level)));
    }

    [Fact]
    public void L6_All的顺序即设置页展示顺序_且与常量一致()
    {
        Assert.Equal(
            new[] { "Off", "Debug", "Info", "Warn", "Error" },
            LogLevelNames.All);
    }

    [Fact]
    public void L7_Off排在最后_使等级比较在关闭时恒为假()
    {
        // Log.Write 的判据是 level >= MinLevel；Off 若不在最大值位，关日志会关不干净
        foreach (var level in new[] { LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error })
            Assert.True(level < LogLevel.Off);
    }
}
