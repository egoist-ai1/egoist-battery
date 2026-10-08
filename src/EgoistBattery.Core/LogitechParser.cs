namespace EgoistBattery.Core;

public static class LogitechParser
{
    public static bool IsResponse(ReadOnlySpan<byte> report, byte slot, byte feature, byte functionByte)
        => report.Length >= 7 && report[0] is 0x10 or 0x11 && report[1] == slot && report[2] == feature && report[3] == functionByte;

    public static BatteryReading Parse(ReadOnlySpan<byte> payload, ushort feature)
    {
        const string source = "Logitech · HID++ 2.0";
        if (payload.Length < 3 || feature is not (0x1000 or 0x1004)) return BatteryReading.Missing(source, "Неизвестный или неполный ответ батареи.");
        var state = payload[2] switch { 0 => ChargeState.Discharging, 1 or 2 or 4 => ChargeState.Charging, 3 => ChargeState.Full, 5 or 6 => ChargeState.Error, _ => ChargeState.Unknown };
        if (state == ChargeState.Error) return new(null, null, state, ReadingQuality.Live, source, "Мышь сообщает об ошибке батареи или температуры.", DateTimeOffset.Now);
        if (payload[0] is > 0 and <= 100) return BatteryReading.Percentage(payload[0], source, ReadingQuality.Live, state);
        if (payload[0] > 100) return BatteryReading.Missing(source, "Недопустимое значение батареи Logitech.");
        var band = feature == 0x1004 ? payload[1] switch { 1 => BatteryBand.Critical, 2 => BatteryBand.Low, 4 => BatteryBand.Medium, 8 => BatteryBand.High, _ => BatteryBand.Unknown } : BatteryBand.Unknown;
        return new(null, null, state, band == BatteryBand.Unknown ? ReadingQuality.Unavailable : ReadingQuality.Live, source,
            band == BatteryBand.Unknown ? "Устройство не сообщило процент. Нулевое поле HID++ не выдаётся за 0% заряда." : "Logitech передаёт категорию заряда вместо процента. Процент не подменяется оценкой.", DateTimeOffset.Now, band);
    }
}
