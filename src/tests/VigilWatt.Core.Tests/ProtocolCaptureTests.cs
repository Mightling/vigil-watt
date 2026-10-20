using System.Buffers.Binary;
using System.Text.Json;
using VigilWatt.ProtocolCapture;
using Xunit;

namespace VigilWatt.Core.Tests;
/// <summary>
/// 功 能： 协议采集记录的解析与写出测试。
/// 2026/10/11 12:08:46 xliu
/// </summary>

public sealed class ProtocolCaptureTests
{
    [Fact]
    public void CaptureRecord_JsonLineOmitsMissingFieldsAndKeepsRawHex()
    {
        var line = new CaptureRecord(DateTimeOffset.UnixEpoch, "webhid", "inputreport", DataHex: "0102FF").ToJsonLine();
        using var json = JsonDocument.Parse(line);
        Assert.Equal("webhid", json.RootElement.GetProperty("source").GetString());
        Assert.Equal("0102FF", json.RootElement.GetProperty("dataHex").GetString());
        Assert.False(json.RootElement.TryGetProperty("deviceName", out _));
    }

    [Fact]
    public void CaptureRecordFactory_MapsWebHidAndBleSamples()
    {
        var webHid = CaptureRecordFactory.FromWebHidJson(
            "{\"operation\":\"sendReport\",\"vendorId\":16612,\"productId\":8193,\"productName\":\"Test dongle\",\"reportId\":19,\"dataHex\":\"130501\"}",
            DateTimeOffset.UnixEpoch);
        Assert.Equal("out", webHid.Direction);
        Assert.Equal(19, webHid.ReportId);
        Assert.Equal("130501", webHid.DataHex);
        Assert.Equal("Test dongle", webHid.DeviceName);

        var ble = CaptureRecordFactory.FromBleAdvertisement(DateTimeOffset.UnixEpoch, "AABBCCDDEEFF", "Test BLE", -42,
            [new BleAdvertisementSection(0xFF, "34120102"), new BleAdvertisementSection(0x09, "44")]);
        Assert.Equal("ble-passive", ble.Source);
        Assert.Equal(-42, ble.SignalStrength);
        Assert.Equal("3412010244", ble.DataHex);
        Assert.Contains("FF:34120102", ble.DeviceMetadata);
    }

    [Fact]
    public void UsbCaptureReader_ReadsUsbPacketAndFiltersByDeviceAddress()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vw-capture-{Guid.NewGuid():N}.pcapng");
        try
        {
            File.WriteAllBytes(path, BuildPcapNg());
            var records = UsbCaptureReader.Read(path).ToArray();
            var record = Assert.Single(records);
            Assert.Equal("usb", record.Source);
            Assert.Equal("in", record.Direction);
            Assert.Equal("bus:1/device:7/endpoint:0x81", record.DeviceId);
            Assert.Equal("112233", record.DataHex);
            Assert.Equal(1_234_567, (record.Timestamp - DateTimeOffset.UnixEpoch).Ticks / 10);
            Assert.Empty(UsbCaptureReader.Read(path, "8"));
            Assert.Single(UsbCaptureReader.Read(path, "7"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UsbCaptureReader_RejectsInvalidBlockLength()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vw-capture-{Guid.NewGuid():N}.pcapng");
        try
        {
            File.WriteAllBytes(path, [0x0A, 0x0D, 0x0D, 0x0A, 0xFF, 0xFF, 0xFF, 0x7F]);
            Assert.Throws<InvalidDataException>(() => UsbCaptureReader.Read(path).ToArray());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UsbCaptureReader_ReadsClassicUsbPcapOutput()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vw-capture-{Guid.NewGuid():N}.pcap");
        try
        {
            File.WriteAllBytes(path, BuildClassicPcap());
            var record = Assert.Single(UsbCaptureReader.Read(path));
            Assert.Equal("out", record.Direction);
            Assert.Equal("A1B2C3", record.DataHex);
            Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(2).AddMilliseconds(500), record.Timestamp);
        }
        finally { File.Delete(path); }
    }

    private static byte[] BuildPcapNg()
    {
        using var output = new MemoryStream();
        using (var block = new MemoryStream())
        {
            WriteUInt32(block, 0x1A2B3C4D);
            WriteUInt16(block, 1);
            WriteUInt16(block, 0);
            WriteUInt64(block, ulong.MaxValue);
            WriteBlock(output, 0x0A0D0D0A, block.ToArray());
        }
        using (var block = new MemoryStream())
        {
            WriteUInt16(block, 249);
            WriteUInt16(block, 0);
            WriteUInt32(block, 65535);
            WriteUInt16(block, 9);
            WriteUInt16(block, 1);
            block.WriteByte(6);
            block.Write([0, 0, 0]);
            WriteUInt16(block, 0);
            WriteUInt16(block, 0);
            WriteBlock(output, 1, block.ToArray());
        }
        using (var block = new MemoryStream())
        {
            WriteUInt32(block, 0);
            WriteUInt32(block, 0);
            WriteUInt32(block, 1_234_567);
            WriteUInt32(block, 30);
            WriteUInt32(block, 30);
            var packet = BuildUsbPacket(1, 7, 0x81, [0x11, 0x22, 0x33]);
            block.Write(packet);
            block.Write([0, 0]);
            WriteBlock(output, 6, block.ToArray());
        }
        return output.ToArray();
    }

    private static byte[] BuildClassicPcap()
    {
        using var output = new MemoryStream();
        output.Write([0xD4, 0xC3, 0xB2, 0xA1]);
        WriteUInt16(output, 2);
        WriteUInt16(output, 4);
        WriteUInt32(output, 0);
        WriteUInt32(output, 0);
        WriteUInt32(output, 65535);
        WriteUInt32(output, 249);
        WriteUInt32(output, 2);
        WriteUInt32(output, 500_000);
        var packet = BuildUsbPacket(1, 7, 0x01, [0xA1, 0xB2, 0xC3]);
        WriteUInt32(output, (uint)packet.Length);
        WriteUInt32(output, (uint)packet.Length);
        output.Write(packet);
        return output.ToArray();
    }

    private static byte[] BuildUsbPacket(ushort bus, ushort device, byte endpoint, byte[] data)
    {
        var packet = new byte[27 + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 27);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(10), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(17), bus);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(19), device);
        packet[21] = endpoint;
        packet[22] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(23), (uint)data.Length);
        data.CopyTo(packet, 27);
        return packet;
    }

    private static void WriteBlock(Stream output, uint type, byte[] body)
    {
        var totalLength = checked((uint)(body.Length + 12));
        WriteUInt32(output, type);
        WriteUInt32(output, totalLength);
        output.Write(body);
        WriteUInt32(output, totalLength);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> data = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(data, value);
        stream.Write(data);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> data = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, value);
        stream.Write(data);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> data = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(data, value);
        stream.Write(data);
    }
}
