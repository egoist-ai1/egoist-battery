using EgoistBattery.Core;

namespace EgoistBattery.Services;

internal interface IDeviceSource
{
    Task<IReadOnlyList<ProviderResult>> ScanAsync(CancellationToken token);
}
