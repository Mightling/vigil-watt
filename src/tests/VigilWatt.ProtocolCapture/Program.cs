using System.Diagnostics;
using System.Text.RegularExpressions;

namespace VigilWatt.ProtocolCapture;
/// <summary>
/// 功 能： 协议采集工具的命令行入口。
/// 2026/10/11 12:08:46 xliu
/// </summary>

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                PrintHelp();
                return 0;
            }

            if (args[0] == "sources")
            {
                PrintSources();
                return 0;
            }

            if (args[0] == "usb" && args.Skip(1).FirstOrDefault() == "roots")
            {
                var roots = UsbPcapDevices.ListRootHubs();
                if (roots.Count == 0) Console.WriteLine("未发现 USBPcap Root Hub；确认 USBPcap 已安装并重启过系统。");
                else foreach (var root in roots) Console.WriteLine(root);
                return 0;
            }

            if (args[0] == "webhid" && args.Skip(1).FirstOrDefault() == "targets")
            {
                var port = int.Parse(Option(args, "--port") ?? "9222");
                var targets = await WebHidCapture.ListTargetsAsync(port, cancellation.Token);
                foreach (var item in targets) Console.WriteLine(item);
                return 0;
            }

            return args[0] switch
            {
                "usb" => await RunUsbAsync(args[1..], cancellation.Token),
                "webhid" => await RunWebHidAsync(args[1..], cancellation.Token),
                "ble" => await RunBleAsync(args[1..], cancellation.Token),
                _ => throw new ArgumentException($"未知命令：{args[0]}")
            };
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误：{ex.Message}");
            return 1;
        }
        finally { Console.CancelKeyPress -= cancelHandler; }
    }

    private static async Task<int> RunUsbAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0) throw new ArgumentException("用法：usb capture|export [选项]");
        if (args[0] == "export")
        {
            var input = RequiredOption(args, "--in");
            var output = RequiredOption(args, "--out");
            var deviceFilter = Option(args, "--device");
            await using var writer = new JsonLinesWriter(output);
            var count = 0;
            foreach (var record in UsbCaptureReader.Read(input, deviceFilter))
            {
                await writer.WriteAsync(record);
                count++;
            }
            Console.WriteLine($"已导出 {count} 条 USB 记录：{Path.GetFullPath(output)}");
            return 0;
        }

        if (args[0] != "capture") throw new ArgumentException("用法：usb capture --root-hub \\\\.\\USBPcap1 --out capture.pcap");
        var rootHub = RequiredOption(args, "--root-hub");
        var outputPath = Path.GetFullPath(RequiredOption(args, "--out"));
        if (!Regex.IsMatch(rootHub, @"^\\\\\.\\USBPcap\d+$", RegexOptions.IgnoreCase))
            throw new ArgumentException("Root Hub 格式应为 \\\\.\\USBPcap1 这样的路径。请从 Wireshark 的 USBPcap 接口列表确认名称。");

        var executable = FindUsbPcapCommand() ?? throw new FileNotFoundException("未找到 USBPcapCMD.exe。请安装 USBPcap（工具不会代为安装）。");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = false };
        start.ArgumentList.Add("-d");
        start.ArgumentList.Add(rootHub);
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add(outputPath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("USBPcapCMD.exe 启动失败。");
        Console.WriteLine($"USBPcap 正在采集到 {outputPath}。按 Ctrl+C 停止。");
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
                Console.WriteLine("USBPcap 未能正常停止；pcap 文件可能不完整，请先用 Wireshark 检查。");
                return 130;
            }
            Console.WriteLine("USBPcap 采集已停止。");
        }
        if (!cancellationToken.IsCancellationRequested && process.ExitCode != 0)
            throw new InvalidOperationException($"USBPcapCMD.exe 退出码 {process.ExitCode}。若提示权限不足，请按 USBPcap 自身提示授权。");
        return 0;
    }

    private static async Task<int> RunWebHidAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] != "capture") throw new ArgumentException("用法：webhid capture --port 9222 --target <id> --out capture.jsonl");
        var port = int.Parse(Option(args, "--port") ?? "9222");
        var target = RequiredOption(args, "--target");
        var output = RequiredOption(args, "--out");
        await WebHidCapture.RunAsync(port, target, output, cancellationToken);
        return 0;
    }

    private static async Task<int> RunBleAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] != "capture") throw new ArgumentException("用法：ble capture --seconds 60 --out capture.jsonl");
        var seconds = int.Parse(Option(args, "--seconds") ?? "60");
        if (seconds is < 1 or > 3600) throw new ArgumentOutOfRangeException("--seconds", "采集时长须在 1 到 3600 秒之间。");
        await BleAdvertisementCapture.RunAsync(seconds, RequiredOption(args, "--out"), cancellationToken);
        return 0;
    }

    private static void PrintSources()
    {
        Console.WriteLine("USB       已安装 USBPcap 时，可按 Root Hub 抓取 pcap 并导出 JSONL（支持 pcapng 输入）");
        Console.WriteLine($"          USBPcapCMD: {FindUsbPcapCommand() ?? "未找到"}");
        Console.WriteLine("WebHID    Edge/Chrome 开启远程调试后旁路记录页面 WebHID API");
        Console.WriteLine("BLE       被动 BLE 广告扫描，不连接设备或访问 GATT");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("VigilWatt.ProtocolCapture");
        Console.WriteLine("  sources");
        Console.WriteLine("  usb roots");
        Console.WriteLine("  usb capture --root-hub \\\\.\\USBPcap1 --out capture.pcap");
        Console.WriteLine("  usb export --in capture.pcap|capture.pcapng --out capture.jsonl [--device <device-number>]");
        Console.WriteLine("  webhid targets --port 9222");
        Console.WriteLine("  webhid capture --port 9222 --target <target-id> --out capture.jsonl");
        Console.WriteLine("  ble capture --seconds 60 --out capture.jsonl");
    }

    private static string RequiredOption(string[] args, string name) =>
        Option(args, name) ?? throw new ArgumentException($"缺少必需选项 {name}");

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static string? FindUsbPcapCommand()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBPcap", "USBPcapCMD.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Wireshark", "extcap", "USBPcapCMD.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "USBPcap", "USBPcapCMD.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
