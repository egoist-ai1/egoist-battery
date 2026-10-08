namespace EgoistBattery.Core;

/// <summary>Смысловой тон показания: от него зависят цвет шкалы, значка в трее и текста.</summary>
public enum BatteryTone { Unknown, Normal, Charging, Low, Critical, Error }

/// <summary>Раздел списка: устройства с зарядом, без заряда, не подключённые.</summary>
public enum DeviceGroup { Measured, Silent, Offline }

/// <summary>Общие правила показа для окна и панели трея: порядок, группы, тон и деления шкалы.</summary>
public static class DevicePresentation
{
    /// <summary>Число делений шкалы уровня в строке окна и панели.</summary>
    public const int RulerCells = 30;
    /// <summary>Число ступеней шкалы для категории заряда (Logitech передаёт «критический, низкий, средний, высокий»).</summary>
    public const int CategoryCells = 4;

    /// <summary>Сколько делений рисовать: тонкая линейка для процента, четыре крупные ступени для категории.</summary>
    public static int CellCount(BatteryReading reading) => reading.HasLevel ? RulerCells : CategoryCells;

    public static BatteryTone ToneOf(BatteryReading reading, int lowThreshold = 20)
    {
        if (reading.State == ChargeState.Error) return BatteryTone.Error;
        if (!reading.HasData) return BatteryTone.Unknown;
        if (reading.State is ChargeState.Charging or ChargeState.Full) return BatteryTone.Charging;
        var critical = Math.Max(5, lowThreshold / 2);
        if (reading.HasLevel) return reading.Maximum <= critical ? BatteryTone.Critical : reading.Maximum <= lowThreshold ? BatteryTone.Low : BatteryTone.Normal;
        return reading.Band switch { BatteryBand.Critical => BatteryTone.Critical, BatteryBand.Low => BatteryTone.Low, _ => BatteryTone.Normal };
    }

    public static DeviceGroup GroupOf(DeviceSnapshot device) =>
        !device.Connected ? DeviceGroup.Offline : device.Reading.HasData ? DeviceGroup.Measured : DeviceGroup.Silent;

    public static string GroupTitle(DeviceGroup group) => group switch
    {
        DeviceGroup.Measured => "Заряд",
        DeviceGroup.Silent => "Заряд не передаётся",
        _ => "Не подключены"
    };

    /// <summary>Сначала подключённые с зарядом (меньше заряда выше), затем без заряда, затем отключённые.</summary>
    public static IReadOnlyList<DeviceSnapshot> Order(IEnumerable<DeviceSnapshot> devices) => devices
        .OrderBy(GroupOf)
        .ThenBy(x => GroupOf(x) == DeviceGroup.Measured ? Urgency(x.Reading) : 0)
        .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    // Категории Logitech не переводятся в проценты: они стоят рядом с уровнями по ступеням 1–4 из 4.
    private static int Urgency(BatteryReading reading) => reading.HasLevel ? reading.Maximum!.Value
        : reading.Band switch { BatteryBand.Critical => 5, BatteryBand.Low => 15, BatteryBand.Medium => 50, _ => 90 };

    /// <summary>
    /// Деления шкалы: сплошные — гарантированный уровень, штриховка — остаток интервала
    /// (DualSense передаёт ступень 10%, а не точный процент). Для категории — ступень 1–4 из четырёх.
    /// </summary>
    public static (int Solid, int Interval) Cells(BatteryReading reading) => Cells(reading, CellCount(reading));

    public static (int Solid, int Interval) Cells(BatteryReading reading, int cells)
    {
        if (reading.HasLevel)
        {
            var solid = (int)Math.Floor(reading.Minimum!.Value / 100d * cells);
            var top = (int)Math.Ceiling(reading.Maximum!.Value / 100d * cells);
            if (reading.Minimum == reading.Maximum) return (Math.Clamp((int)Math.Round(reading.Minimum!.Value / 100d * cells), reading.Minimum > 0 ? 1 : 0, cells), 0);
            return (Math.Clamp(solid, 0, cells), Math.Clamp(top - solid, 0, cells - solid));
        }
        var step = reading.Band switch { BatteryBand.Critical => 1, BatteryBand.Low => 2, BatteryBand.Medium => 3, BatteryBand.High => 4, _ => 0 };
        return (step * cells / CategoryCells, 0);
    }

    /// <summary>Текст в значке трея: процент или нижняя граница со знаком «+»; «?» — уровень неизвестен.</summary>
    public static string TrayValue(BatteryReading reading) =>
        reading.HasLevel ? $"{reading.Minimum}{(reading.Minimum == reading.Maximum ? "" : "+")}" : "?";
}
