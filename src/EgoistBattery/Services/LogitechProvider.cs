using System.Runtime.InteropServices;
using System.Text;
using EgoistBattery.Core;
using Microsoft.Win32.SafeHandles;

namespace EgoistBattery.Services;

internal static class LogitechProvider
{
    private static readonly Dictionary<string, (string Name, string Kind)> metadata = [];
    public static async Task<ProviderResult> ScanAsync(IReadOnlyList<HidDevice> hidDevices, CancellationToken token)
    {
        var devices = new List<DeviceSnapshot>();
        var interfaces = hidDevices.Where(x => x.Vendor == 0x046D && x.UsagePage == 0xFF00 && x.ReportLength >= 20 && x.OutputLength >= 20)
            .GroupBy(x => x.Container ?? x.Path).Select(x => x.First()).ToArray();
        foreach (var device in interfaces)
        {
            token.ThrowIfCancellationRequested();
            using var handle = CreateFile(device.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid) continue;
            using var stream = new FileStream(handle, FileAccess.ReadWrite, 1, true);
            var receiver = device.Product is >= 0xC500 and <= 0xC5FF;
            var slots = receiver ? new byte[] { 1, 2, 3, 4, 5, 6 } : new byte[] { 255 };
            try
            {
                foreach (var slot in slots)
                {
                    token.ThrowIfCancellationRequested();
                    var reachable = false;
                    BatteryReading? reading = null;
                    foreach (ushort feature in new ushort[] { 0x1004, 0x1000 })
                    {
                        var root = await RequestAsync(stream, device, slot, 0, 0, [(byte)(feature >> 8), (byte)feature], token);
                        if (root is null) break;
                        reachable = true;
                        if (root[0] == 0) continue;
                        var payload = await RequestAsync(stream, device, slot, root[0], feature == 0x1004 ? (byte)1 : (byte)0, [], token);
                        if (payload is null) continue;
                        reading = LogitechParser.Parse(payload, feature);
                        if (reading.HasData || reading.State == ChargeState.Error) break;
                    }
                    if (!reachable) continue;
                    var id = $"logitech:{device.Container ?? device.Path}:{slot}";
                    if (!metadata.TryGetValue(id, out var info))
                    {
                        info = await ReadMetadataAsync(stream, device, slot, token);
                        metadata[id] = info;
                    }
                    devices.Add(new(id, info.Name, info.Kind, receiver ? "Радиоканал / USB" : "USB", true,
                        reading ?? BatteryReading.Missing("Logitech · HID++ 2.0", "Устройство отвечает, но процент батареи недоступен."),
                        receiver ? null : device.Container, id));
                }
            }
            catch (IOException) { }
        }
        return new("Logitech HID++", devices);
    }

    private static async Task<(string Name, string Kind)> ReadMetadataAsync(FileStream stream, HidDevice device, byte slot, CancellationToken token)
    {
        var fallback = ($"Logitech · устройство {slot}", "Устройство");
        var root = await RequestAsync(stream, device, slot, 0, 0, [0, 5], token);
        if (root is null || root[0] == 0) return fallback;
        var type = await RequestAsync(stream, device, slot, root[0], 2, [], token);
        var kind = type is not { Length: > 0 } ? "Устройство" : type[0] switch
        {
            0 or 2 => "Клавиатура", 1 => "Пульт", 3 => "Мышь", 4 => "Сенсорная панель",
            5 => "Трекбол", 6 => "Презентер", 7 => "Приёмник", _ => "Устройство"
        };
        var info = await RequestAsync(stream, device, slot, root[0], 0, [], token);
        if (info is null || info[0] is 0 or > 80) return (fallback.Item1, kind);
        var name = new List<byte>();
        while (name.Count < info[0])
        {
            var part = await RequestAsync(stream, device, slot, root[0], 1, [(byte)name.Count], token);
            if (part is null || part.Length == 0) return (fallback.Item1, kind);
            name.AddRange(part.Take(Math.Min(part.Length, info[0] - name.Count)));
        }
        return (Encoding.UTF8.GetString(name.ToArray()).TrimEnd('\0'), kind);
    }

    private static async Task<byte[]?> RequestAsync(FileStream stream, HidDevice device, byte slot, byte feature, byte function, byte[] parameters, CancellationToken token)
    {
        // Белый список GET: root.getFeature и чтение имени/батареи. SET здесь отсутствует.
        var functionByte = (byte)((function << 4) | 9);
        var request = new byte[device.OutputLength]; request[0] = 0x11; request[1] = slot; request[2] = feature; request[3] = functionByte;
        parameters.CopyTo(request, 4);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(350));
        try
        {
            await stream.WriteAsync(request, deadline.Token).ConfigureAwait(false);
            var buffer = new byte[device.ReportLength];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                if (count == 0) return null;
                if (LogitechParser.IsResponse(buffer.AsSpan(0, count), slot, feature, functionByte)) return buffer[4..count];
                if (count >= 6 && buffer[1] == slot && buffer[2] is 0x8F or 0xFF && buffer[3] == feature && buffer[4] == functionByte) return null;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
