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
}
