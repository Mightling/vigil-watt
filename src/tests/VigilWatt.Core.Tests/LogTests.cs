using VigilWatt.Core.Utils;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： Log 核心行为回归测试（见 docs/dev/logging.md §7）。
/// 2026/10/09 10:13:25 xliu
/// Log 是全局静态， 本类每个用例都在独立临时目录里跑，并在 Dispose 时复位级别/保留天数。
/// </summary>
public sealed class LogTests : IDisposable
{
    private readonly string _dir;
    private readonly LogLevel _savedMinLevel;
    private readonly int _savedRetentionDays;

    public LogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "VigilWattTests", "log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _savedMinLevel = Log.MinLevel;
        _savedRetentionDays = Log.RetentionDays;
        Log.RedirectTo(_dir);
    }

    public void Dispose()
    {
        Log.Shutdown();
        Log.MinLevel = _savedMinLevel;
        Log.RetentionDays = _savedRetentionDays;
        try { Directory.Delete(_dir, recursive: true); } catch { /* 忽略 */ }
    }

    /// <summary>当天日志文件名，与 <c>Log</c> 的按日命名约定一致。</summary>
    private string AppLog => Path.Combine(_dir, $"app-{DateTime.Now:yyyy-MM-dd}.log");

    /// <summary>读取当天日志：Log 的写句柄以 FileShare.ReadWrite 打开，而 File.ReadAllText
    /// 只请求 Read 共享，会因共享模式冲突打不开——必须同样以 ReadWrite 共享来读。</summary>
    private string ReadLog()
    {
        if (!File.Exists(AppLog)) return "";
        using var fs = new FileStream(AppLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    private string[] ReadLogLines() =>
        ReadLog().Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>去掉每个日志文件首行的"时间基准"声明（N4），只留真正的日志事件行。</summary>
    private string[] ReadEventLines() =>
        ReadLogLines().Where(l => !l.Contains("时间基准")).ToArray();

    [Fact]
    public void 低于级别的调用不写入()
    {
        Log.MinLevel = LogLevel.Warn;

        Log.Debug("dbg-marker");
        Log.Info("info-marker");
        Log.Warn("warn-marker");

        var text = ReadLog();
        Assert.DoesNotContain("dbg-marker", text);
        Assert.DoesNotContain("info-marker", text);
        Assert.Contains("warn-marker", text);
    }

    [Fact]
    public void Off_连错误也不写()
    {
        Log.MinLevel = LogLevel.Off;

        Log.Error("boom");

        Assert.False(File.Exists(AppLog));
    }

    [Fact]
    public void 日志按当天日期命名()
    {
        Log.MinLevel = LogLevel.Info;

        Log.Info("today-marker");

        Assert.True(File.Exists(AppLog), "日志应写到 app-当天日期.log");
        Assert.Contains("today-marker", ReadLog());
    }

    [Fact]
    public void 超过保留天数的旧日志被清理()
    {
        Log.MinLevel = LogLevel.Info;
        Log.RetentionDays = 14;
        var old = Path.Combine(_dir, $"app-{DateTime.Now.AddDays(-20):yyyy-MM-dd}.log");
        File.WriteAllText(old, "old");

        Log.Info("触发打开当天文件");

        Assert.False(File.Exists(old), "超过保留天数的旧日志应被删除");
    }

    [Fact]
    public void 保留天数内的旧日志保留()
    {
        Log.MinLevel = LogLevel.Info;
        Log.RetentionDays = 14;
        var recent = Path.Combine(_dir, $"app-{DateTime.Now.AddDays(-3):yyyy-MM-dd}.log");
        File.WriteAllText(recent, "recent");

        Log.Info("触发打开当天文件");

        Assert.True(File.Exists(recent), "保留天数内的日志不应被删除");
    }

    [Fact]
    public void 旧版单文件日志被清理()
    {
        Log.MinLevel = LogLevel.Info;
        Log.RetentionDays = 14;
        var legacy = Path.Combine(_dir, "app.log");
        File.WriteAllText(legacy, "legacy");

        Log.Info("触发打开当天文件");

        Assert.False(File.Exists(legacy), "旧版 app.log 已不再使用，应被清理");
    }

    [Fact]
    public void 多行异常每行都带前缀()
    {
        Log.MinLevel = LogLevel.Info;

        Exception ex;
        try { throw new InvalidOperationException("内层错误"); }
        catch (Exception e) { ex = e; }
        Log.Error("出错了", ex);

        var lines = ReadEventLines();
        Assert.True(lines.Length > 1, "异常堆栈应为多行");
        Assert.StartsWith("20", lines[0]);
        Assert.All(lines.Skip(1), l => Assert.StartsWith("| ", l.TrimStart()));
        Assert.Contains(lines, l => l.Contains("内层错误"));
    }

    [Fact]
    public void 消息里的换行被转义_不产生假行()
    {
        Log.MinLevel = LogLevel.Info;

        Log.Info("设备名\n[ERR ] 伪造行");

        var lines = ReadEventLines();
        Assert.Single(lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("[ERR ]"));
    }

    [Fact]
    public void 控制符被转义_不产生假行()
    {
        Log.MinLevel = LogLevel.Info;

        // NUL / BEL 这类控制符来自外部字符串（HID 产品名/BLE 名称），必须转成可见形式
        Log.Info("设备名\u0000\u0007尾巴");

        var lines = ReadEventLines();
        Assert.Single(lines);
        Assert.Contains("\\x00", lines[0]);
        Assert.Contains("\\x07", lines[0]);
        Assert.DoesNotContain('\u0000', lines[0]);
        Assert.DoesNotContain('\u0007', lines[0]);
        // 回归：普通字符（含中文）**不得**被一并转义（曾把整条消息变成 \xNN 串）
        Assert.Contains("设备名", lines[0]);
        Assert.Contains("尾巴", lines[0]);
        Assert.DoesNotContain("\\x8B", lines[0]);
    }

    [Fact]
    public void 文件首行写时间基准声明()
    {
        Log.MinLevel = LogLevel.Info;

        Log.Info("随便一行");

        Assert.Contains(ReadLogLines(), l => l.Contains("时间基准"));
    }

    [Fact]
    public void InfoOnChange_状态相同只记一次()
    {
        Log.MinLevel = LogLevel.Info;

        Assert.True(Log.InfoOnChange("k", "same", "变了: same"), "首次应记为变化");
        Assert.False(Log.InfoOnChange("k", "same", "变了: same"), "状态未变不应再记");
        Assert.True(Log.InfoOnChange("k", "other", "变了: other"), "状态变化应再记");

        Assert.Equal(2, ReadEventLines().Count(l => l.Contains("变了: ")));
    }

    [Fact]
    public void InfoThrottled_间隔内只记一次()
    {
        Log.MinLevel = LogLevel.Info;

        Log.InfoThrottled("t", "限流消息", TimeSpan.FromMinutes(5));
        Log.InfoThrottled("t", "限流消息", TimeSpan.FromMinutes(5));

        Assert.Equal(1, ReadEventLines().Count(l => l.Contains("限流消息")));
    }

    [Fact]
    public void 写入失败不抛异常()
    {
        // 把 logs 目录指向一个"文件"，CreateDirectory 必然失败
        var blocked = Path.Combine(_dir, "blocked-file");
        File.WriteAllText(blocked, "x");
        Log.RedirectTo(blocked);
        Log.MinLevel = LogLevel.Info;

        var ex = Record.Exception(() =>
        {
            for (int i = 0; i < 20; i++) Log.Info("x");
        });

        Assert.Null(ex);
    }

    [Fact]
    public void 写入失败降级写兜底文件()
    {
        // 当天日志文件位置放一个**目录**：打开必然失败，但 logs 目录本身可写 → 兜底文件应生成
        var dir = Path.Combine(_dir, "blocked-dir");
        Directory.CreateDirectory(Path.Combine(dir, $"app-{DateTime.Now:yyyy-MM-dd}.log"));
        Log.RedirectTo(dir);
        Log.MinLevel = LogLevel.Info;

        for (int i = 0; i < 5; i++) Log.Info("x");

        Assert.True(File.Exists(Path.Combine(dir, "log-error.txt")), "连续失败应写 log-error.txt");
    }
}
