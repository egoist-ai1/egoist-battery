using System.Runtime.InteropServices;
using System.Text;
using EgoistBattery.Core;

namespace EgoistBattery.Services;

internal static class PnpBatteryProvider
{
    public static Task<ProviderResult> ScanAsync(CancellationToken token) => Task.Run(() => Scan(token), token);
    private static ProviderResult Scan(CancellationToken token)
    {
        if (CM_Get_Device_ID_List_Size(out var size, null, 0x100) != 0 || size is 0 or > 2_000_000) return new("Батареи PnP", []);
        var buffer = new char[size];
        if (CM_Get_Device_ID_List(null, buffer, size, 0x100) != 0) return new("Батареи PnP", [], "Список батарей Windows временно недоступен.");
        var devices = new List<DeviceSnapshot>();
        foreach (var id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            token.ThrowIfCancellationRequested();
            if (CM_Locate_DevNode(out var node, id, 0) != 0) continue;
            var battery = Get(node, new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
            if (battery is not { Length: > 0 } || battery[0] > 100) continue;
            var name = ReadText(Get(node, new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14))
                ?? ReadText(Get(node, new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 2)) ?? "Устройство с батареей";
            var containerData = Get(node, new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
            var container = containerData?.Length == 16 ? new Guid(containerData).ToString() : null;
            var connectedData = Get(node, new Guid("83DA6326-97A6-4088-9453-A1923F573B29"), 15);
            var connected = connectedData is { Length: > 0 } && connectedData[0] != 0;
            var reading = BatteryReading.Percentage(battery[0], "Windows · свойство батареи PnP", ReadingQuality.WindowsCache);
            var transport = id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) ? "Bluetooth" : "USB / Windows";
            devices.Add(new("pnp:" + id, name, "Устройство", transport, connected, reading, container));
        }
        return new("Батареи PnP", devices);
    }
    private static byte[]? Get(uint node, Guid guid, uint id)
    {
        var key = new PropertyKey { Format = guid, Id = id };
        var data = new byte[2048]; uint size = (uint)data.Length;
        return CM_Get_DevNode_Property(node, ref key, out _, data, ref size, 0) == 0 ? data[..(int)size] : null;
    }
    private static string? ReadText(byte[]? bytes) => bytes is { Length: > 1 } ? Encoding.Unicode.GetString(bytes).TrimEnd('\0') : null;
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID_List_Size(out uint size, string? filter, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID_List(string? filter, [Out] char[] buffer, uint length, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)] private static extern uint CM_Locate_DevNode(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW", CharSet = CharSet.Unicode)] private static extern uint CM_Get_DevNode_Property(uint node, ref PropertyKey key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);
}
