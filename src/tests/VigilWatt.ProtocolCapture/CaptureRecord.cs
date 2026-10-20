using System.Text.Json;
using System.Text.Json.Serialization;

namespace VigilWatt.ProtocolCapture;
/// <summary>
/// 功 能： 一条协议采集记录。
/// 2026/10/11 12:08:46 xliu
/// </summary>

public sealed record CaptureRecord(
    DateTimeOffset Timestamp,
    string Source,
    string Operation,
    string? Direction = null,
    string? DeviceId = null,
    string? DeviceName = null,
    string? DeviceMetadata = null,
    int? ReportId = null,
    string? DataType = null,
    string? DataHex = null,
    int? SignalStrength = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string ToJsonLine() => JsonSerializer.Serialize(this, JsonOptions);
}
