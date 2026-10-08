namespace EgoistBattery.Core;

/// <summary>
/// Цвет уровня заряда: красный у пустого, оранжевый и жёлтый посередине, лайм у полного.
/// Один источник для шкалы, числа, значка в трее и панели.
/// </summary>
public static class BatterySpectrum
{
    private static readonly (double At, byte R, byte G, byte B)[] Stops =
        [(0.00, 255, 90, 79), (0.25, 255, 165, 61), (0.50, 246, 216, 74), (1.00, 198, 242, 78)];

    /// <summary>Цвет для доли заряда 0…1.</summary>
    public static (byte R, byte G, byte B) At(double level)
    {
        level = Math.Clamp(level, 0, 1);
        for (var i = 1; i < Stops.Length; i++)
        {
            if (level > Stops[i].At) continue;
            var (a, b) = (Stops[i - 1], Stops[i]);
            var t = (level - a.At) / (b.At - a.At);
            byte Mix(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
            return (Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B));
        }
        var last = Stops[^1];
        return (last.R, last.G, last.B);
    }

    /// <summary>Доля заряда для окраски: процент устройства или середина интервала; для категории — середина ступени.</summary>
    public static double? LevelOf(BatteryReading reading) =>
        reading.HasLevel ? reading.Percent / 100d
        : reading.Band switch { BatteryBand.Critical => 0.125, BatteryBand.Low => 0.375, BatteryBand.Medium => 0.625, BatteryBand.High => 0.875, _ => null };
}
