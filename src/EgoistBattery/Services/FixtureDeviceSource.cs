using System.Text.Json;
using EgoistBattery.Core;

namespace EgoistBattery.Services;

// Источник заготовленных устройств для самопроверки интерфейса (--ui-test --fixture): состояния, которых нет на столе, воспроизводятся из файла.
internal sealed class FixtureDeviceSource(string path) : IDeviceSource
{
    public async Task<IReadOnlyList<ProviderResult>> ScanAsync(CancellationToken token)
    {
        var json = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<IReadOnlyList<ProviderResult>>(json) ?? throw new JsonException("В файле нет источников.");
    }
}
