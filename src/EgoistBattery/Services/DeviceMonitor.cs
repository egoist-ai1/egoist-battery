using System.Runtime.InteropServices;
using EgoistBattery.Core;

namespace EgoistBattery.Services;

internal sealed class DeviceMonitor : IDeviceSource
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<IReadOnlyList<ProviderResult>> ScanAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Отказ одного источника не убирает устройства остальных источников.
            var hid = ProtectAsync("USB / HID", () => Task.Run(() => ScanHidAsync(token), token));
            var bt = ProtectAsync("Bluetooth / GATT", () => BluetoothProvider.ScanAsync(token));
            var power = ProtectAsync("Батарея компьютера / XInput", () => Task.Run(ScanPower, token));
            var pnp = ProtectAsync("Батареи PnP", () => PnpBatteryProvider.ScanAsync(token));
            return await Task.WhenAll(hid, bt, power, pnp).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private static async Task<ProviderResult> ProtectAsync(string source, Func<Task<ProviderResult>> scan)
    {
        try { return await scan().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return new(source, [], $"{source}: временно недоступен ({e.GetType().Name})."); }
    }

    private static async Task<ProviderResult> ScanHidAsync(CancellationToken token)
    {
        var devices = new List<DeviceSnapshot>();
        var entries = HidDevices.Enumerate();
        foreach (var device in entries)
        {
            token.ThrowIfCancellationRequested();
            if (device.UsagePage != 1) continue;
            // Наличие USB-приёмника не доказывает связь с мышью. Её показываем
            // отдельным устройством лишь после ответа конкретного слота HID++.
            if (device.Vendor == 0x046D && device.Product is >= 0xC500 and <= 0xC5FF) continue;
            var sony = device.Vendor == 0x054C && device.Product is 0x0CE6 or 0x0DF2 or 0x05C4 or 0x09CC;
            var reading = sony ? await HidDevices.ReadPlayStationAsync(device, token).ConfigureAwait(false)
                : BatteryReading.Missing("USB / HID", "В стандартном HID-интерфейсе этого устройства батарея не объявлена. Через радиоприёмник может требоваться протокол производителя.");
            var name = sony ? device.Product switch { 0x0CE6 => "DualSense", 0x0DF2 => "DualSense Edge", _ => "DualShock 4" } : device.Name;
            var kind = device.Vendor == 0x046D && device.Product is >= 0xC500 and <= 0xC5FF ? "Приёмник" : device.Usage switch { 2 => "Мышь", 6 => "Клавиатура", _ => "Контроллер" };
            var serial = BluetoothProvider.NormalizeAddress(device.Serial);
            devices.Add(new(device.Path, name, kind, device.Bluetooth ? "Bluetooth" : "USB", true, reading, device.Container, serial));
        }
        var logitech = await LogitechProvider.ScanAsync(entries, token).ConfigureAwait(false);
        devices.AddRange(logitech.Devices);
        return new("USB / HID", devices);
    }

    private static Task<ProviderResult> ScanPower()
    {
        var devices = new List<DeviceSnapshot>();
        if (GetSystemPowerStatus(out var power) && (power.BatteryFlag & 128) == 0 && power.BatteryFlag != 255)
        {
            var state = (power.BatteryFlag & 8) != 0 ? ChargeState.Charging : power.ACLineStatus == 0 ? ChargeState.Discharging : ChargeState.Unknown;
            var reading = BatteryReading.Percentage(power.BatteryPercent, "Windows · питание компьютера", ReadingQuality.Live, state);
            devices.Add(new("system-power", "Батарея компьютера", "Компьютер", "Система", true, reading));
        }
        for (uint index = 0; index < 4; index++)
        {
            if (XInputGetBatteryInformation(index, 0, out var battery) != 0) continue;
            var reading = battery.Type == 1 ? new BatteryReading(null, null, ChargeState.Wired, ReadingQuality.Live, "XInput", "Контроллер сообщает проводное питание; процент батареи не передаётся.", DateTimeOffset.Now)
                : battery.Type == 255 ? BatteryReading.Missing("XInput", "Батарея неизвестна. Виртуальные контроллеры могут не сообщать батарею.")
                : battery.Level switch
                {
                    0 => new(0, 0, ChargeState.Discharging, ReadingQuality.Live, "XInput", "XInput сообщает: батарея разряжена.", DateTimeOffset.Now),
                    _ => new(null, null, ChargeState.Discharging, ReadingQuality.Live, "XInput", "XInput передаёт категорию; точный процент неизвестен.", DateTimeOffset.Now,
                        battery.Level switch { 1 => BatteryBand.Low, 2 => BatteryBand.Medium, 3 => BatteryBand.High, _ => BatteryBand.Unknown })
                };
            devices.Add(new($"xinput-{index}", $"Контроллер XInput {index + 1}", "Контроллер", "XInput", true, reading));
        }
        return Task.FromResult(new ProviderResult("Батарея компьютера / XInput", devices));
    }

    [StructLayout(LayoutKind.Sequential)] private struct SystemPowerStatus { public byte ACLineStatus, BatteryFlag, BatteryPercent, SystemStatus; public uint BatteryLifeTime, BatteryFullLifeTime; }
    [StructLayout(LayoutKind.Sequential)] private struct XInputBattery { public byte Type, Level; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetBatteryInformation(uint index, byte deviceType, out XInputBattery battery);
}
