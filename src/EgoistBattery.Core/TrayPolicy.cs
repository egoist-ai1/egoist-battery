namespace EgoistBattery.Core;

public static class TrayPolicy
{
    public static IReadOnlyList<DeviceSnapshot> VisibleDevices(IEnumerable<DeviceSnapshot> devices, IEnumerable<string>? hidden = null)
    {
        var excluded = new HashSet<string>(hidden ?? [], StringComparer.OrdinalIgnoreCase);
        return devices.Where(x => x.Connected && !excluded.Contains(x.Id) &&
            (x.Reading.HasData || x.Kind == "Наушники" || x.Kind == "Мышь" && x.Reading.Source.StartsWith("Logitech") || x.Kind == "Контроллер" && x.Reading.State != ChargeState.Wired))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToArray();
    }

    /// <summary>
    /// Устройство, которое показывает единственный значок трея: закреплённое владельцем; иначе DualSense/DualShock;
    /// иначе устройство с наименьшим зарядом; без данных — первое подходящее.
    /// </summary>
    public static DeviceSnapshot? IconDevice(IEnumerable<DeviceSnapshot> devices, string? pinned = null)
    {
        var visible = VisibleDevices(devices);
        if (pinned is not null && visible.FirstOrDefault(x => string.Equals(x.Id, pinned, StringComparison.OrdinalIgnoreCase)) is { } chosen) return chosen;
        var pool = visible.Any(x => x.Reading.HasData) ? visible.Where(x => x.Reading.HasData).ToArray() : visible;
        static bool IsPlayStation(DeviceSnapshot x) => x.Name.Contains("DualSense", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("DualShock", StringComparison.OrdinalIgnoreCase);
        return pool.FirstOrDefault(IsPlayStation)
            ?? DevicePresentation.Order(pool).FirstOrDefault();
    }
}
