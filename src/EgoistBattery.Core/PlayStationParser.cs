using System.Buffers.Binary;

namespace EgoistBattery.Core;

public static class PlayStationParser
{
    public static BatteryReading Parse(ReadOnlySpan<byte> report, bool bluetooth, bool dualSense)
    {
        var source = $"HID · {(bluetooth ? "Bluetooth" : "USB")}";
        if (report.IsEmpty) return BatteryReading.Missing(source, "Отчёт устройства ещё не получен.");
        if (bluetooth && report[0] == 1)
            return BatteryReading.Missing(source, "Базовый Bluetooth-режим передаёт кнопки, но не батарею. Полный режим намеренно не включается: он может мешать играм.");
        var expected = bluetooth ? (dualSense ? 0x31 : 0x11) : 1;
        var length = bluetooth ? 78 : 64;
        if (report[0] != expected || report.Length < length)
            return BatteryReading.Missing(source, "Неполный или неизвестный отчёт устройства.");
        if (bluetooth && !HasValidBluetoothCrc(report[..78]))
            return BatteryReading.Missing(source, "Отчёт Bluetooth не прошёл проверку целостности.");
        var offset = dualSense ? (bluetooth ? 54 : 53) : (bluetooth ? 32 : 30);
        var value = report[offset];
        return dualSense ? ParseDualSenseStatus(value, source) : ParseDualShockStatus(value, source);
    }

    public static BatteryReading ParseDualSenseStatus(byte value, string source)
    {
        var raw = value & 15;
        var mode = value >> 4;
        if (mode is 10 or 11 or 15)
            return new(null, null, ChargeState.Error, ReadingQuality.Live, source, mode == 15 ? "Контроллер сообщает об ошибке зарядки." : "Контроллер сообщает об ошибке напряжения или температуры.", DateTimeOffset.Now);
        if (mode > 2 || raw > 10) return BatteryReading.Missing(source, "Контроллер передал неизвестное состояние батареи.");
        if (mode == 2) return new(100, 100, ChargeState.Full, ReadingQuality.Live, source, "Контроллер сообщает о завершении зарядки.", DateTimeOffset.Now);
        var min = raw * 10;
        return new(min, Math.Min(min + 9, 100), mode == 1 ? ChargeState.Charging : ChargeState.Discharging,
            ReadingQuality.Live, source, "DualSense передаёт уровень ступенями по 10%. Показан весь интервал, а не вымышленный точный процент.", DateTimeOffset.Now);
    }

    public static BatteryReading ParseDualShockStatus(byte value, string source)
    {
        var raw = value & 15;
        var plugged = (value & 16) != 0;
        if (plugged && raw == 11) return new(100, 100, ChargeState.Full, ReadingQuality.Live, source, "Зарядка завершена.", DateTimeOffset.Now);
        if (raw > 10) return BatteryReading.Missing(source, "Неизвестное состояние батареи DualShock 4.");
        if (plugged && raw == 10) return new(100, 100, ChargeState.Charging, ReadingQuality.Live, source, "Контроллер сообщает 100%; завершение зарядки ещё не подтверждено.", DateTimeOffset.Now);
        var low = raw * 10;
        return new(low, Math.Min(low + 9, 100), plugged ? ChargeState.Charging : ChargeState.Discharging,
            ReadingQuality.Live, source, "Контроллер передаёт уровень ступенями по 10%.", DateTimeOffset.Now);
    }

    public static bool HasValidBluetoothCrc(ReadOnlySpan<byte> report)
    {
        if (report.Length != 78) return false;
        var crc = CrcStep(uint.MaxValue, 0xA1);
        foreach (var b in report[..74]) crc = CrcStep(crc, b);
        return ~crc == BinaryPrimitives.ReadUInt32LittleEndian(report[74..]);
    }

    public static uint BluetoothCrc(ReadOnlySpan<byte> payload)
    {
        var crc = CrcStep(uint.MaxValue, 0xA1);
        foreach (var b in payload) crc = CrcStep(crc, b);
        return ~crc;
    }

    private static uint CrcStep(uint crc, byte b)
    {
        crc ^= b;
        for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        return crc;
    }
}
