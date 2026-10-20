using System.Buffers.Binary;

namespace VigilWatt.ProtocolCapture;
/// <summary>
/// 功 能： 读取 USBPcap 抓包并解析成采集记录。
/// 2026/10/11 12:08:46 xliu
/// </summary>

public static class UsbCaptureReader
{
    private const uint PcapNgMagic = 0x0A0D0D0A;
    private const uint SectionHeaderBlock = 0x0A0D0D0A;
    private const uint InterfaceDescriptionBlock = 1;
    private const uint EnhancedPacketBlock = 6;
    private const ushort UsbPcapLinkType = 249;

    public static IEnumerable<CaptureRecord> Read(string path, string? deviceFilter = null)
    {
        using var stream = File.OpenRead(path);
        var magic = ReadBytes(stream, 4);
        if (magic.Length < 4) yield break;
        stream.Position = 0;
        if (IsClassicPcap(magic))
        {
            foreach (var record in ReadClassicPcap(stream, deviceFilter)) yield return record;
            yield break;
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(magic) != PcapNgMagic)
            throw new InvalidDataException("文件不是 USBPcap classic pcap 或 pcapng 格式。");
        foreach (var record in ReadPcapNg(stream, deviceFilter)) yield return record;
    }

    private static IEnumerable<CaptureRecord> ReadPcapNg(Stream stream, string? deviceFilter)
    {
        var interfaces = new List<ushort>();
        var sectionLittleEndian = true;
        var timestampResolutions = new List<long>();
        var blockHeader = new byte[8];

        while (ReadExactlyOrEnd(stream, blockHeader))
        {
            var rawType = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader);
            uint blockType;
            bool littleEndian;
            if (rawType == SectionHeaderBlock)
            {
                var magicBytes = ReadBytes(stream, 4);
                if (magicBytes.Length != 4) throw new InvalidDataException("pcapng section header is truncated.");
                var magic = BinaryPrimitives.ReadUInt32LittleEndian(magicBytes);
                littleEndian = magic switch
                {
                    0x1A2B3C4D => true,
                    0x4D3C2B1A => false,
                    _ => throw new InvalidDataException("pcapng section byte-order magic is invalid."),
                };
                sectionLittleEndian = littleEndian;
                blockType = SectionHeaderBlock;
                var totalLength = ReadUInt32(blockHeader.AsSpan(4), littleEndian);
                if (totalLength < 28 || totalLength > stream.Length - stream.Position + 12)
                    throw new InvalidDataException("pcapng section length is invalid.");
                var body = ReadBytes(stream, checked((int)totalLength - 12));
                if (body.Length != totalLength - 12) yield break;
                if (ReadUInt32(body.AsSpan(body.Length - 4), littleEndian) != totalLength)
                    throw new InvalidDataException("pcapng section length trailer does not match.");
                interfaces.Clear();
                timestampResolutions.Clear();
                continue;
            }

            littleEndian = sectionLittleEndian;
            blockType = littleEndian ? rawType : BinaryPrimitives.ReverseEndianness(rawType);
            var length = ReadUInt32(blockHeader.AsSpan(4, 4), littleEndian);
            if (length < 12 || length > stream.Length - stream.Position + 8)
                throw new InvalidDataException("pcapng block length is invalid.");
            var payload = ReadBytes(stream, checked((int)length - 8));
            if (payload.Length != length - 8) yield break;
            if (ReadUInt32(payload.AsSpan(payload.Length - 4), littleEndian) != length)
                throw new InvalidDataException("pcapng block length trailer does not match.");

            if (blockType == InterfaceDescriptionBlock)
            {
                var linkType = ReadUInt16(payload, littleEndian);
                interfaces.Add(linkType);
                timestampResolutions.Add(ReadTimestampResolution(payload.AsSpan(8, payload.Length - 12), littleEndian) ?? 1_000_000);
            }
            else if (blockType == EnhancedPacketBlock)
            {
                if (payload.Length < 24) continue;
                var interfaceId = ReadUInt32(payload, littleEndian);
                if (interfaceId >= interfaces.Count || interfaces[(int)interfaceId] != UsbPcapLinkType) continue;
                var high = ReadUInt32(payload.AsSpan(4), littleEndian);
                var low = ReadUInt32(payload.AsSpan(8), littleEndian);
                var capturedLength = ReadUInt32(payload.AsSpan(12), littleEndian);
                if (capturedLength > payload.Length - 24) continue;
                var packet = payload.AsSpan(20, (int)capturedLength);
                var parsed = ParseUsbPcapPacket(packet);
                if (parsed is null) continue;
                var ticksPerSecond = interfaceId < timestampResolutions.Count ? timestampResolutions[(int)interfaceId] : 1_000_000;
                var timestampValue = ((ulong)high << 32) | low;
                var ticks = checked((long)((decimal)timestampValue * TimeSpan.TicksPerSecond / ticksPerSecond));
                var timestamp = DateTimeOffset.UnixEpoch.AddTicks(ticks);
                var record = parsed with { Timestamp = timestamp };
                if (deviceFilter is null || record.DeviceId?.Contains($"/device:{deviceFilter}/", StringComparison.OrdinalIgnoreCase) == true ||
                    record.DeviceId?.Equals(deviceFilter, StringComparison.OrdinalIgnoreCase) == true)
                    yield return record;
            }
        }
    }

    private static IEnumerable<CaptureRecord> ReadClassicPcap(Stream stream, string? deviceFilter)
    {
        var globalHeader = ReadBytes(stream, 24);
        if (globalHeader.Length != 24) throw new InvalidDataException("pcap global header is truncated.");
        var littleEndian = globalHeader[0] is 0xD4 or 0x4D;
        var nanosecondResolution = (globalHeader[0] == 0x4D && globalHeader[1] == 0x3C) ||
            (globalHeader[0] == 0xA1 && globalHeader[1] == 0x3C);
        var linkType = ReadUInt32(globalHeader.AsSpan(20), littleEndian);
        if (linkType != UsbPcapLinkType) throw new InvalidDataException($"pcap link type {linkType} is not USBPcap.");
        var packetHeader = new byte[16];
        while (ReadExactlyOrEnd(stream, packetHeader))
        {
            var seconds = ReadUInt32(packetHeader, littleEndian);
            var fraction = ReadUInt32(packetHeader.AsSpan(4), littleEndian);
            var capturedLength = ReadUInt32(packetHeader.AsSpan(8), littleEndian);
            if (capturedLength > stream.Length - stream.Position)
                throw new InvalidDataException("pcap packet data is truncated.");
            var packet = ReadBytes(stream, checked((int)capturedLength));
            if (packet.Length != capturedLength) throw new InvalidDataException("pcap packet data is truncated.");
            var parsed = ParseUsbPcapPacket(packet);
            if (parsed is null) continue;
            var fractionTicks = nanosecondResolution ? (long)fraction / 100 : checked((long)fraction * 10);
            var timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(fractionTicks);
            var record = parsed with { Timestamp = timestamp };
            if (deviceFilter is null || record.DeviceId?.Contains($"/device:{deviceFilter}/", StringComparison.OrdinalIgnoreCase) == true ||
                record.DeviceId?.Equals(deviceFilter, StringComparison.OrdinalIgnoreCase) == true)
                yield return record;
        }
    }

    private static bool IsClassicPcap(ReadOnlySpan<byte> magic) =>
        magic.SequenceEqual(new byte[] { 0xD4, 0xC3, 0xB2, 0xA1 }) ||
        magic.SequenceEqual(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4 }) ||
        magic.SequenceEqual(new byte[] { 0x4D, 0x3C, 0xB2, 0xA1 }) ||
        magic.SequenceEqual(new byte[] { 0xA1, 0xB2, 0x3C, 0x4D });

    private static CaptureRecord? ParseUsbPcapPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 27) return null;
        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(packet);
        if (headerLength < 27 || headerLength > packet.Length) return null;
        var status = BinaryPrimitives.ReadUInt32LittleEndian(packet[10..]);
        var function = BinaryPrimitives.ReadUInt16LittleEndian(packet[14..]);
        var info = packet[16];
        var bus = BinaryPrimitives.ReadUInt16LittleEndian(packet[17..]);
        var device = BinaryPrimitives.ReadUInt16LittleEndian(packet[19..]);
        var endpoint = packet[21];
        var transferType = packet[22];
        var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(packet[23..]);
        var dataOffset = (int)headerLength;
        var length = Math.Min((long)dataLength, packet.Length - dataOffset);
        var data = packet.Slice(dataOffset, (int)Math.Max(length, 0));
        var direction = (endpoint & 0x80) != 0 ? "in" : "out";
        return new CaptureRecord(
            DateTimeOffset.UnixEpoch,
            "usb",
            $"urb-function-0x{function:X4}-status-0x{status:X8}",
            direction,
            $"bus:{bus}/device:{device}/endpoint:0x{endpoint:X2}",
            DeviceMetadata: $"transferType={transferType};info=0x{info:X2};status=0x{status:X8}",
            DataHex: Convert.ToHexString(data));
    }

    private static long? ReadTimestampResolution(ReadOnlySpan<byte> options, bool littleEndian)
    {
        var offset = 0;
        while (offset + 4 <= options.Length)
        {
            var code = ReadUInt16(options[offset..], littleEndian);
            var length = ReadUInt16(options[(offset + 2)..], littleEndian);
            offset += 4;
            if (code == 0) return null;
            if (offset + length > options.Length) return null;
            if (code == 9 && length == 1)
            {
                var value = options[offset];
                if ((value & 0x80) == 0) return value <= 18 ? (long)Math.Pow(10, value) : null;
                var exponent = value & 0x7F;
                return exponent < 63 ? 1L << exponent : null;
            }
            offset += (length + 3) & ~3;
        }
        return null;
    }

    private static bool ReadExactlyOrEnd(Stream stream, byte[] buffer)
    {
        var first = stream.Read(buffer, 0, buffer.Length);
        if (first == 0) return false;
        var read = first;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer, read, buffer.Length - read);
            if (count == 0) throw new InvalidDataException("pcapng file ended inside a block header.");
            read += count;
        }
        return true;
    }

    private static byte[] ReadBytes(Stream stream, int count)
    {
        var bytes = new byte[count];
        var read = 0;
        while (read < count)
        {
            var current = stream.Read(bytes, read, count - read);
            if (current == 0) break;
            read += current;
        }
        return read == count ? bytes : bytes[..read];
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(data) : BinaryPrimitives.ReadUInt16BigEndian(data);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(data) : BinaryPrimitives.ReadUInt32BigEndian(data);
}
