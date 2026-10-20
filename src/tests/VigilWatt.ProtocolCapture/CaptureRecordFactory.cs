using System.Text.Json;

namespace VigilWatt.ProtocolCapture;

public sealed record BleAdvertisementSection(byte DataType, string DataHex);

/// <summary>
/// 功 能： 把各通道的原始数据收成采集记录。
/// 2026/10/11 12:08:46 xliu
/// </summary>

public static class CaptureRecordFactory
{
    public static CaptureRecord FromWebHidJson(string payload, DateTimeOffset timestamp)
    {
        using var data = JsonDocument.Parse(payload);
        var value = data.RootElement;
        string? Str(string field) => value.TryGetProperty(field, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
        int? Int(string field) => value.TryGetProperty(field, out var x) && x.TryGetInt32(out var n) ? n : null;
        var operation = Str("operation") ?? "unknown";
        var metadata = string.Join(";", new[] { Int("vendorId") is { } vid ? $"vid={vid:X4}" : null,
            Int("productId") is { } pid ? $"pid={pid:X4}" : null,
            Int("usagePage") is { } page ? $"usagePage=0x{page:X4}" : null,
            Int("usage") is { } usage ? $"usage=0x{usage:X4}" : null }.Where(x => x is not null));
        var direction = operation switch
        {
            "sendReport" or "sendFeatureReport" => "out",
            "inputreport" or "receiveFeatureReport" => "in",
            _ => null,
        };
        return new CaptureRecord(timestamp, "webhid", operation, direction,
            DeviceId: $"{Int("vendorId") ?? 0:X4}:{Int("productId") ?? 0:X4}",
            DeviceName: Str("productName"), DeviceMetadata: metadata,
            ReportId: Int("reportId"), DataHex: Str("dataHex"));
    }

    public static CaptureRecord FromBleAdvertisement(
        DateTimeOffset timestamp,
        string address,
        string? name,
        int signalStrength,
        IReadOnlyList<BleAdvertisementSection> sections)
    {
        var metadata = string.Join(";", sections.Select(section => $"{section.DataType:X2}:{section.DataHex}"));
        var payload = string.Concat(sections.Select(section => section.DataHex));
        return new CaptureRecord(timestamp, "ble-passive", "advertisement", DeviceId: address,
            DeviceName: name, DeviceMetadata: metadata, DataType: "advertisement", DataHex: payload,
            SignalStrength: signalStrength);
    }
}
