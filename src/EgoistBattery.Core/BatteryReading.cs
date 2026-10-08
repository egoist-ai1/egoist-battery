namespace EgoistBattery.Core;

public enum ChargeState { Unknown, Discharging, Charging, Full, Error, Wired }
public enum ReadingQuality { Live, WindowsCache, Unavailable }
public enum BatteryBand { Unknown, Critical, Low, Medium, High }

public sealed record BatteryReading(int? Minimum, int? Maximum, ChargeState State,
    ReadingQuality Quality, string Source, string Detail, DateTimeOffset ObservedAt, BatteryBand Band = BatteryBand.Unknown)
{
    public bool HasLevel => Minimum is >= 0 and <= 100 && Maximum >= Minimum && Maximum <= 100;
    public int? Percent => HasLevel ? (Minimum + Maximum) / 2 : null;
    public bool HasData => HasLevel || Band != BatteryBand.Unknown;
    public string Label => HasLevel ? Minimum == Maximum ? $"{Minimum}%" : $"{Minimum}–{Maximum}%" : Band switch
    { BatteryBand.Critical => "Критический", BatteryBand.Low => "Низкий", BatteryBand.Medium => "Средний", BatteryBand.High => "Высокий", _ => "Нет данных" };
    public bool IsLow(int threshold) => HasLevel && Maximum <= threshold && State is not (ChargeState.Charging or ChargeState.Full);
    public static BatteryReading Missing(string source, string reason) => new(null, null, ChargeState.Unknown, ReadingQuality.Unavailable, source, reason, DateTimeOffset.Now);
    public static BatteryReading Percentage(int percent, string source, ReadingQuality quality, ChargeState state = ChargeState.Unknown) => percent is < 0 or > 100
        ? Missing(source, "Устройство передало недопустимый процент.")
        : new(percent, percent, state, quality, source, quality == ReadingQuality.WindowsCache ? "Последнее значение Windows. Время измерения устройством неизвестно." : "Значение, переданное устройством; точность датчика зависит от производителя.", DateTimeOffset.Now);
}

public sealed record DeviceSnapshot(string Id, string Name, string Kind, string Transport,
    bool Connected, BatteryReading Reading, string? ContainerId = null, string? Identity = null);

public sealed record ProviderResult(string Name, IReadOnlyList<DeviceSnapshot> Devices, string? Error = null);
