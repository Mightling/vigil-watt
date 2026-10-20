using Windows.Devices.Bluetooth.Advertisement;
using Windows.Foundation;
using Windows.Storage.Streams;
using System.Collections.Concurrent;

namespace VigilWatt.ProtocolCapture;
/// <summary>
/// 功 能： 被动采集 BLE 广播并写成采集记录。
/// 2026/10/11 12:08:46 xliu
/// </summary>

public static class BleAdvertisementCapture
{
    public static async Task RunAsync(int seconds, string path, CancellationToken cancellationToken = default)
    {
        await using var writer = new JsonLinesWriter(path);
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new ConcurrentBag<Task>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
        TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs> handler = (_, args) =>
            pending.Add(RecordAsync(args, writer, stopped.Token));
        watcher.Received += handler;
        try
        {
            watcher.Start();
            Console.WriteLine($"BLE 被动广告扫描 {seconds} 秒，不连接设备；输出 {Path.GetFullPath(path)}");
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
        }
        finally
        {
            watcher.Stop();
            watcher.Received -= handler;
            stopped.Cancel();
            await Task.WhenAll(pending.ToArray());
        }
    }

    private static async Task RecordAsync(BluetoothLEAdvertisementReceivedEventArgs args, JsonLinesWriter writer, CancellationToken cancellationToken)
    {
        try
        {
            var sections = args.Advertisement.DataSections.Select(section =>
            {
                using var reader = DataReader.FromBuffer(section.Data);
                var bytes = new byte[reader.UnconsumedBufferLength];
                reader.ReadBytes(bytes);
                return new BleAdvertisementSection(section.DataType, Convert.ToHexString(bytes));
            }).ToArray();
            var record = CaptureRecordFactory.FromBleAdvertisement(DateTimeOffset.Now,
                args.BluetoothAddress.ToString("X12"), args.Advertisement.LocalName,
                args.RawSignalStrengthInDBm, sections);
            await writer.WriteAsync(record, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.Error.WriteLine($"BLE 记录失败：{ex.Message}"); }
    }
}
