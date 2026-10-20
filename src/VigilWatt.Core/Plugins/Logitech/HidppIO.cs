using VigilWatt.Core.Hid;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins.Logitech;

/// <summary>
/// 功 能： HID++ 底层 I/O：通过 vendor 接口写 output report 发请求、读 input report 匹配响应（报文格式对照 Solaar base.py）。
/// 2026/10/06 15:53:36 xliu
/// 详见 docs/hardware/protocols.md §3。
/// </summary>
internal sealed class HidppIO : IDisposable
{
    public const byte SwId = 0x0B; // Solaar 约定，避开 G HUB(0x0D)/OpenRGB(0x07)/LGSTray(0x0A)

    private readonly HidDeviceHandle _handle;
    private readonly byte _reportId;
    private readonly bool _longMessage;
    private readonly object _ioGate = new();

    /// <summary>底层句柄（供探测工具使用）。</summary>
    public HidDeviceHandle Handle => _handle;

    /// <summary>本接口实际使用的 report ID（0x10 或 0x11）。</summary>
    public byte ReportId => _reportId;

    /// <summary>设备自发通知：参数为 (devIdx, data)，data 从 feature 字节起（与 <see cref="Request"/> 返回值同布局）。
    /// 在请求等待与空闲 Pump 时都会触发；订阅方必须快速返回、不重入 Request。</summary>
    public Action<byte, byte[]>? NotificationReceived;

    /// <summary>
    /// 写入许可（安全闸门）：返回 false 时 <see cref="Request"/> **一帧都不发**、直接返回 null。
    /// 这是罗技插件唯一的写入收口（safety.md §3 规则 1/2），由插件按用户开关刷新。
    /// </summary>
    public Func<bool>? WritesAllowed;

    public HidppIO(HidDeviceHandle handle)
    {
        _handle = handle;
        _reportId = HidppCodec.SelectReportId(handle.Info.OutputReportLength, handle.Info.OutputReportId);
        _longMessage = HidppCodec.IsLongMessage(_reportId);
    }

    /// <summary>发送一条 HID++ 请求并等待匹配的响应（返回响应 data 部分，不含 report id 和 devIdx）；等待期间穿插的设备通知经 <see cref="NotificationReceived"/> 抛出。</summary>
    public byte[]? Request(byte devIndex, ushort featureIdAndFunc, ReadOnlySpan<byte> args,
        bool patchSw = true, TimeSpan? timeout = null)
    {
        ushort requestId = patchSw
            ? (ushort)((featureIdAndFunc & 0xFFF0) | SwId)   // (feature<<8)|(func<<4)|sw
            : featureIdAndFunc;
        int dataLen = _longMessage ? 18 : 5;
        var data = new byte[dataLen];
        data[0] = (byte)(requestId >> 8);
        data[1] = (byte)requestId;
        for (int i = 0; i < args.Length && 2 + i < dataLen; i++)
            data[2 + i] = args[i];

        var packet = new byte[dataLen + 2];
        packet[0] = _reportId;
        packet[1] = devIndex;
        Array.Copy(data, 0, packet, 2, dataLen);

        if (packet.Length < _handle.Info.OutputReportLength)
            Array.Resize(ref packet, _handle.Info.OutputReportLength);

        var wait = timeout ?? TimeSpan.FromSeconds(3);

        lock (_ioGate)
        {
            // 写入闸门（安全规则 R1）：这是本插件唯一的写点，许可关闭时连一次 WriteOutput 都不发。
            if (WritesAllowed != null && !WritesAllowed())
            {
                Log.Debug($"HID++ 写入被开关挡住 dev={devIndex} req=0x{requestId:X4}");
                return null;
            }

            if (!_handle.WriteOutput(packet, TimeSpan.FromMilliseconds(500)))
            {
                Log.Warn($"HID++ 写失败 dev={devIndex} req=0x{requestId:X4} report=0x{_reportId:X2}");
                return null;
            }

            return ReadMatching(devIndex, data, wait);
        }
    }

    /// <summary>空闲泵：只读不写，把超时窗口内到达的通知交给 <see cref="NotificationReceived"/>；与 <see cref="Request"/> 互斥（同一把锁），零写入，供后台线程持续监听插拔电等事件。</summary>
    public void PumpNotifications(TimeSpan wait)
    {
        if (wait <= TimeSpan.Zero) return;
        lock (_ioGate)
        {
            var deadline = DateTime.UtcNow + wait;
            while (DateTime.UtcNow < deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                var input = _handle.ReadInput(remaining > TimeSpan.FromMilliseconds(50)
                    ? remaining
                    : TimeSpan.FromMilliseconds(50));
                if (input == null) break;
                TryDispatchNotification(input);
            }
        }
    }

    /// <summary>读输入流直到出现前两字节与请求匹配的响应；通知报文分发给订阅方后继续等。</summary>
    private byte[]? ReadMatching(byte devIndex, byte[] requestData, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var input = _handle.ReadInput(remaining > TimeSpan.FromMilliseconds(100)
                ? remaining
                : TimeSpan.FromMilliseconds(100));
            if (input == null) break;

            byte reportId = input[0];
            if (reportId != HidppCodec.ShortReportId && reportId != HidppCodec.LongReportId) continue;

            // 设备自发通知（sw=0）：交给订阅方后继续等本请求的应答
            if (TryDispatchNotification(input))
                continue;

            byte replyDev = input[1];
            bool devMatch = replyDev == devIndex || replyDev == (byte)~devIndex;
            if (!devMatch) continue;

            int dataStart = 2;
            if (input.Length < dataStart + 2) continue;
            if (input[dataStart] != requestData[0] || input[dataStart + 1] != requestData[1]) continue;

            int dataLen = input.Length - dataStart;
            var reply = new byte[dataLen];
            Array.Copy(input, dataStart, reply, 0, dataLen);

            if (reply[0] == 0x8F || reply[0] == 0xFF)
                return null;
            return reply;
        }
        Log.Warn($"HID++ 响应超时 dev={devIndex} req=0x{requestData[0]:X2}{requestData[1]:X2} report=0x{_reportId:X2}");
        return null;
    }

    /// <summary>若是设备自发通知则分发并返回 true。HID++ 2.0 通知的 software id（低 4 位）为 0；请求应答带 SwId=0x0B。</summary>
    private bool TryDispatchNotification(byte[] input)
    {
        if (input.Length < 4) return false;
        byte reportId = input[0];
        if (reportId != HidppCodec.ShortReportId && reportId != HidppCodec.LongReportId) return false;
        byte funcSw = input[3];
        if ((funcSw & 0x0F) != 0) return false; // 带 SwId 的是应答，不是通知

        byte devIdx = input[1];
        int dataLen = input.Length - 2;
        var data = new byte[dataLen];
        Array.Copy(input, 2, data, 0, dataLen);
        try
        {
            NotificationReceived?.Invoke(devIdx, data);
        }
        catch (Exception ex)
        {
            Log.Warn($"HID++ 通知处理异常: {ex.Message}");
        }
        return true;
    }

    public void Dispose() => _handle.Dispose();
}
