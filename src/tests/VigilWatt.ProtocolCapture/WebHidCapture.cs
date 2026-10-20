using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace VigilWatt.ProtocolCapture;
/// <summary>
/// 功 能： 经浏览器 WebHID 旁路记录 HID 报文。
/// 2026/10/11 12:08:46 xliu
/// </summary>

public static class WebHidCapture
{
    private const string Instrumentation = """
        (() => {
          if (!navigator.hid || !window.__vwCapture || window.__vwHidInstalled) return;
          window.__vwHidInstalled = true;
          const emit = (operation, device, fields = {}) => {
            try { window.__vwCapture(JSON.stringify({ operation, vendorId: device?.vendorId, productId: device?.productId,
              productName: device?.productName, usagePage: device?.collections?.[0]?.usagePage,
              usage: device?.collections?.[0]?.usage, ...fields })); } catch (_) {}
          };
          const hex = value => {
            const bytes = value instanceof ArrayBuffer ? new Uint8Array(value) :
              ArrayBuffer.isView(value) ? new Uint8Array(value.buffer, value.byteOffset, value.byteLength) : new Uint8Array(value);
            return Array.from(bytes).map(x => x.toString(16).padStart(2, '0')).join('').toUpperCase();
          };
          for (const method of ['open', 'close', 'sendReport', 'sendFeatureReport', 'receiveFeatureReport']) {
            const original = HIDDevice.prototype[method];
            if (!original) continue;
            HIDDevice.prototype[method] = function(...args) {
              const fields = { reportId: args[0] };
              if (method === 'sendReport' || method === 'sendFeatureReport') fields.dataHex = hex(args[1]);
              const result = original.apply(this, args);
              return Promise.resolve(result).then(value => {
                if (method === 'receiveFeatureReport' && value) fields.dataHex = hex(value);
                emit(method, this, fields);
                if (method === 'open' && !this.__vwInputHandler) {
                  this.__vwInputHandler = event => emit('inputreport', this, { reportId: event.reportId,
                    dataHex: hex(event.data) });
                  this.addEventListener('inputreport', this.__vwInputHandler);
                }
                return value;
              }).catch(error => { emit(method, this, { ...fields, error: String(error) }); throw error; });
            };
          }
          const requestDevice = navigator.hid.requestDevice.bind(navigator.hid);
          navigator.hid.requestDevice = async function(...args) {
            const devices = await requestDevice(...args);
            for (const device of devices) emit('requestDevice', device);
            return devices;
          };
        })();
        """;

    public static async Task RunAsync(int port, string targetSelector, string path, CancellationToken cancellationToken = default)
    {
        var endpoint = new Uri($"http://127.0.0.1:{port}/json/list");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        List<CdpTarget> targets;
        try { targets = await http.GetFromJsonAsync<List<CdpTarget>>(endpoint, cancellationToken) ?? []; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException($"无法连接 Chromium 调试端口 {port}。请用 --remote-debugging-port={port} 启动 Edge/Chrome。", ex);
        }

        var target = targets.FirstOrDefault(x => x.Type == "page" &&
            (x.Id.Equals(targetSelector, StringComparison.OrdinalIgnoreCase) ||
             x.Url.Contains(targetSelector, StringComparison.OrdinalIgnoreCase) ||
             (x.Title?.Contains(targetSelector, StringComparison.OrdinalIgnoreCase) ?? false)))
            ?? throw new InvalidOperationException("找不到匹配的浏览器页面。请用 target-id、URL 片段或标题片段指定 --target。");
        if (string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl))
            throw new InvalidOperationException("页面未提供 DevTools WebSocket。请确认浏览器允许远程调试连接。");

        await using var writer = new JsonLinesWriter(path);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{port}");
        try { await socket.ConnectAsync(new Uri(target.WebSocketDebuggerUrl), cancellationToken); }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException)
        {
            throw new InvalidOperationException("连接 DevTools 页面失败。较新 Chromium 可能需要启动参数 --remote-allow-origins=*。", ex);
        }

        var session = new CdpSession(socket, writer);
        await session.CommandAsync("Runtime.enable", cancellationToken: cancellationToken);
        await session.CommandAsync("Page.enable", cancellationToken: cancellationToken);
        await session.CommandAsync("Runtime.addBinding", new { name = "__vwCapture" }, cancellationToken);
        await session.CommandAsync("Page.addScriptToEvaluateOnNewDocument", new { source = Instrumentation }, cancellationToken);
        await session.CommandAsync("Runtime.evaluate", new { expression = Instrumentation }, cancellationToken);
        Console.WriteLine($"WebHID 旁路采集页面：{target.Title} {target.Url}");
        Console.WriteLine($"输出 {Path.GetFullPath(path)}。按 Ctrl+C 停止；本工具不会发送设备报文。");
        while (socket.State == WebSocketState.Open)
            await session.ReceiveAndDispatchAsync(cancellationToken);
    }

    public static async Task<IReadOnlyList<string>> ListTargetsAsync(int port, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var targets = await http.GetFromJsonAsync<List<CdpTarget>>($"http://127.0.0.1:{port}/json/list", cancellationToken) ?? [];
            return targets.Where(x => x.Type == "page").Select(x => $"{x.Id}\t{x.Title}\t{x.Url}").ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException($"无法连接 Chromium 调试端口 {port}。请启动带 --remote-debugging-port={port} 的 Edge/Chrome。", ex);
        }
    }

    private sealed class CdpTarget
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public string Url { get; set; } = "";
        public string? Title { get; set; }
        public string? WebSocketDebuggerUrl { get; set; }
    }

    private sealed class CdpSession(ClientWebSocket socket, JsonLinesWriter writer)
    {
        private int _commandId;

        public async Task CommandAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            var id = Interlocked.Increment(ref _commandId);
            var json = JsonSerializer.Serialize(new { id, method, @params = parameters ?? new { } });
            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken);
            while (true)
            {
                using var message = await ReceiveJsonAsync(cancellationToken);
                await DispatchBindingAsync(message.RootElement, cancellationToken);
                if (message.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
                {
                    if (message.RootElement.TryGetProperty("error", out var error))
                        throw new InvalidOperationException($"DevTools {method} 失败：{error}");
                    return;
                }
            }
        }

        public async Task ReceiveAndDispatchAsync(CancellationToken cancellationToken)
        {
            using var message = await ReceiveJsonAsync(cancellationToken);
            await DispatchBindingAsync(message.RootElement, cancellationToken);
        }

        private async Task DispatchBindingAsync(JsonElement root, CancellationToken cancellationToken)
        {
            if (!root.TryGetProperty("method", out var method) || method.GetString() != "Runtime.bindingCalled" ||
                !root.TryGetProperty("params", out var parameters) ||
                !parameters.TryGetProperty("name", out var name) || name.GetString() != "__vwCapture" ||
                !parameters.TryGetProperty("payload", out var payload)) return;

            try
            {
                var record = CaptureRecordFactory.FromWebHidJson(payload.GetString() ?? "{}", DateTimeOffset.Now);
                await writer.WriteAsync(record, cancellationToken);
            }
            catch (Exception ex) { Console.Error.WriteLine($"WebHID 事件解析失败：{ex.Message}"); }
        }

        private async Task<JsonDocument> ReceiveJsonAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException("浏览器关闭了 DevTools 连接。");
                stream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            return JsonDocument.Parse(stream.ToArray());
        }
    }
}
