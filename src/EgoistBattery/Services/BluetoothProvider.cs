using EgoistBattery.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace EgoistBattery.Services;

internal static class BluetoothProvider
{
    private static readonly string[] Properties = ["System.Devices.Aep.IsConnected", "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.ContainerId", "System.Devices.BatteryLife", "System.Devices.Aep.Bluetooth.Cod.Major"];

    public static async Task<ProviderResult> ScanAsync(CancellationToken token)
    {
        var devices = new List<DeviceSnapshot>();
        var bluetooth = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelector(), Properties).AsTask(token).ConfigureAwait(false);
        foreach (var info in bluetooth)
        {
            token.ThrowIfCancellationRequested();
            var connected = Bool(info, "System.Devices.Aep.IsConnected");
            var battery = ReadPercentage(info);
            // IsPresent у PnP для сопряжённого Bluetooth не означает активную связь.
            var identity = Text(info, "System.Devices.Aep.DeviceAddress");
            var name = string.IsNullOrWhiteSpace(info.Name) ? "Устройство Bluetooth" : info.Name;
            var kind = name.Contains("DualSense", StringComparison.OrdinalIgnoreCase) || name.Contains("Controller", StringComparison.OrdinalIgnoreCase) ? "Контроллер"
                : name.Contains("Pods", StringComparison.OrdinalIgnoreCase) || name.Contains("Head", StringComparison.OrdinalIgnoreCase) ? "Наушники" : "Bluetooth";
            devices.Add(new(info.Id, name, kind, "Bluetooth", connected, battery, Text(info, "System.Devices.Aep.ContainerId"), NormalizeAddress(identity)));
        }
        var ble = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelector(), Properties).AsTask(token).ConfigureAwait(false);
        foreach (var info in ble)
        {
            token.ThrowIfCancellationRequested();
            var connected = Bool(info, "System.Devices.Aep.IsConnected");
            var battery = ReadPercentage(info);
            if (connected)
            {
                try
                {
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                    budget.CancelAfter(TimeSpan.FromSeconds(3));
                    battery = await ReadGattAsync(info, budget.Token).ConfigureAwait(false) ?? battery;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (Exception e) when (e is not OperationCanceledException)
                { battery = battery.HasLevel ? battery : BatteryReading.Missing("Bluetooth LE", "Служба батареи недоступна. Повторим при следующем обновлении."); }
            }
            devices.Add(new(info.Id, string.IsNullOrWhiteSpace(info.Name) ? "Устройство Bluetooth LE" : info.Name, "Bluetooth LE", "Bluetooth LE", connected, battery,
                Text(info, "System.Devices.Aep.ContainerId"), NormalizeAddress(Text(info, "System.Devices.Aep.DeviceAddress"))));
        }
        return new("Bluetooth / GATT", devices);
    }

    private static async Task<BatteryReading?> ReadGattAsync(DeviceInformation info, CancellationToken token)
    {
        using var device = await BluetoothLEDevice.FromIdAsync(info.Id).AsTask(token).ConfigureAwait(false);
        if (device is null || device.ConnectionStatus != BluetoothConnectionStatus.Connected) return null;
        // Читаем стандартную службу 180F только у уже подключённых устройств.
        // MaintainConnection и запись GATT не используются.
        var result = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached).AsTask(token).ConfigureAwait(false);
        try
        {
            if (result.Status != GattCommunicationStatus.Success) return null;
            foreach (var service in result.Services)
            {
                var chars = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Cached).AsTask(token).ConfigureAwait(false);
                if (chars.Status != GattCommunicationStatus.Success) continue;
                foreach (var characteristic in chars.Characteristics)
                {
                    var value = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(token).ConfigureAwait(false);
                    if (value.Status != GattCommunicationStatus.Success || value.Value.Length != 1) continue;
                    using var reader = DataReader.FromBuffer(value.Value);
                    return BatteryReading.Percentage(reader.ReadByte(), "Bluetooth LE · Battery Service", ReadingQuality.Live);
                }
            }
        }
        finally { foreach (var service in result.Services) service.Dispose(); }
        return null;
    }

    private static BatteryReading ReadPercentage(DeviceInformation info)
    {
        if (info.Properties.TryGetValue("System.Devices.BatteryLife", out var value) && value is not null)
        {
            try { return BatteryReading.Percentage(Convert.ToInt32(value), "Windows", ReadingQuality.WindowsCache); }
            catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { }
        }
        return BatteryReading.Missing("Windows", "Windows и устройство не предоставляют уровень батареи через стандартный интерфейс.");
    }
    private static bool Bool(DeviceInformation info, string property) => info.Properties.TryGetValue(property, out var v) && v is true;
    private static string? Text(DeviceInformation info, string property) => info.Properties.TryGetValue(property, out var v) ? v?.ToString() : null;
    public static string? NormalizeAddress(string? value)
    {
        if (value is null) return null;
        var address = new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return address.Length == 12 ? address : null;
    }
}
