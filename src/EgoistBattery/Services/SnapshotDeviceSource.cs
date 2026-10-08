using System.Text.Json;
using EgoistBattery.Core;

namespace EgoistBattery.Services;

// Окно получает снимок фонового процесса и само не опрашивает устройства.
internal sealed class SnapshotDeviceSource(SettingsStore store, Action request) : IDeviceSource
{
    private sealed record Snapshot(IReadOnlyList<ProviderResult> Providers);
    public async Task<IReadOnlyList<ProviderResult>> ScanAsync(CancellationToken token)
    {
        var path = Path.Combine(store.DirectoryPath, "last-snapshot.json");
        var previous = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        request();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > previous)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
                    return JsonSerializer.Deserialize<Snapshot>(json)?.Providers ?? throw new JsonException("Нет источников в снимке.");
                }
                catch (IOException) { }
            }
            await Task.Delay(80, token).ConfigureAwait(false);
        }
    }
}
