namespace EgoistBattery.Core;

public static class SnapshotMerger
{
    public static IReadOnlyList<DeviceSnapshot> Merge(IEnumerable<DeviceSnapshot> input)
    {
        // Физический идентификатор используется прежде контейнера: USB и Bluetooth
        // могут иметь разные контейнеры, а одинаковые модели — разные устройства.
        var all = input.ToArray();
        var identityByContainer = all.Where(x => !string.IsNullOrWhiteSpace(x.ContainerId) && !string.IsNullOrWhiteSpace(x.Identity))
            .GroupBy(x => x.ContainerId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Identity!, StringComparer.OrdinalIgnoreCase);
        return all.GroupBy(x => Key(x, identityByContainer), StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var connected = group.Any(x => x.Connected);
            var ordered = group.OrderByDescending(x => x.Connected && x.Reading.Quality == ReadingQuality.Live)
                .ThenByDescending(x => x.Reading.HasLevel).ToArray();
            var best = ordered[0];
            var metadata = group.FirstOrDefault(x => x.Kind != "Устройство" && x.Kind != "Приёмник") ?? best;
            var transports = group.Where(x => x.Connected).Select(x => x.Transport).Distinct().ToArray();
            return best with { Id = group.Key, Name = metadata.Name, Kind = metadata.Kind, Connected = connected, Transport = transports.Length > 0 ? string.Join(" + ", transports) : best.Transport };
        }).OrderByDescending(x => x.Connected).ThenByDescending(x => x.Reading.HasLevel).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static string Key(DeviceSnapshot device, IReadOnlyDictionary<string, string> identities) => !string.IsNullOrWhiteSpace(device.Identity) ? "identity:" + device.Identity
        : device.ContainerId is not null && identities.TryGetValue(device.ContainerId, out var identity) ? "identity:" + identity
        : !string.IsNullOrWhiteSpace(device.ContainerId) && device.ContainerId != Guid.Empty.ToString() ? "container:" + device.ContainerId : device.Id;
}
