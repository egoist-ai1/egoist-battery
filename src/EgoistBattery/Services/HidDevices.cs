using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using EgoistBattery.Core;
using Microsoft.Win32.SafeHandles;

namespace EgoistBattery.Services;

internal sealed record HidDevice(string Path, ushort Vendor, ushort Product, ushort UsagePage,
    ushort Usage, ushort ReportLength, string Name, string Serial, string? Container, bool Bluetooth, ushort OutputLength);

internal static class HidDevices
{
    private const uint Present = 2, DeviceInterface = 16;

    public static IReadOnlyList<HidDevice> Enumerate()
    {
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, Present | DeviceInterface);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var list = new List<HidDevice>();
        try
        {
            for (uint i = 0; ; i++)
            {
                var entry = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref entry))
                {
                    if (Marshal.GetLastWin32Error() != 259) throw new Win32Exception(Marshal.GetLastWin32Error());
                    break;
                }
                SetupDiGetDeviceInterfaceDetail(set, ref entry, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required is < 8 or > 65536) continue;
                var detail = Marshal.AllocHGlobal((int)required);
                var info = Marshal.AllocHGlobal(Marshal.SizeOf<DeviceInfoData>());
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    Marshal.StructureToPtr(new DeviceInfoData { Size = Marshal.SizeOf<DeviceInfoData>() }, info, false);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref entry, detail, required, out _, info)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    using var handle = Open(path, false);
                    if (handle.IsInvalid) continue;
                    var attrs = new HidAttributes { Size = Marshal.SizeOf<HidAttributes>() };
                    if (!HidD_GetAttributes(handle, ref attrs) || !HidD_GetPreparsedData(handle, out var preparsed)) continue;
                    HidCaps caps;
                    try { if (HidP_GetCaps(preparsed, out caps) != 0x00110000) continue; }
                    finally { HidD_FreePreparsedData(preparsed); }
                    if ((caps.UsagePage != 1 || caps.Usage is not (2 or 4 or 5 or 6)) && !(attrs.Vendor == 0x046D && caps.UsagePage == 0xFF00)) continue;
                    var name = ReadString(handle, false);
                    if (string.IsNullOrWhiteSpace(name)) name = caps.Usage switch { 2 => "Мышь", 6 => "Клавиатура", _ => "Игровой контроллер" };
                    var serial = ReadString(handle, true);
                    var data = Marshal.PtrToStructure<DeviceInfoData>(info);
                    var containerKey = new PropertyKey(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
                    var containerBytes = new byte[16];
                    string? container = null;
                    if (SetupDiGetDeviceProperty(set, ref data, ref containerKey, out _, containerBytes, 16, out _, 0))
                    {
                        var value = new Guid(containerBytes);
                        if (value != Guid.Empty) container = value.ToString();
                    }
                    var bluetooth = path.Contains("vid&", StringComparison.OrdinalIgnoreCase) || path.Contains("00001124", StringComparison.OrdinalIgnoreCase) || path.Contains("bth", StringComparison.OrdinalIgnoreCase);
                    list.Add(new(path, attrs.Vendor, attrs.Product, caps.UsagePage, caps.Usage, caps.InputLength, name, serial, container, bluetooth, caps.OutputLength));
                }
                finally { Marshal.FreeHGlobal(detail); Marshal.FreeHGlobal(info); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    public static async Task<BatteryReading> ReadPlayStationAsync(HidDevice device, CancellationToken cancellationToken)
    {
        var source = $"HID · {(device.Bluetooth ? "Bluetooth" : "USB")}";
        if (device.ReportLength is < 16 or > 4096) return BatteryReading.Missing(source, "Недопустимый размер отчёта HID.");
        try
        {
            using var handle = Open(device.Path, true);
            if (handle.IsInvalid) return BatteryReading.Missing(source, "Игровой интерфейс занят или недоступен. Повторим чтение автоматически.");
            // Совместный доступ только для чтения. Output/feature reports не отправляются.
            using var stream = new FileStream(handle, FileAccess.Read, device.ReportLength, true);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromMilliseconds(650));
            var buffer = new byte[device.ReportLength];
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var count = await stream.ReadAsync(buffer, budget.Token).ConfigureAwait(false);
                if (count == 0) break;
                var dualSense = device.Product is 0x0CE6 or 0x0DF2;
                var reading = PlayStationParser.Parse(buffer.AsSpan(0, count), device.Bluetooth, dualSense);
                if (reading.Quality != ReadingQuality.Unavailable || buffer[0] == 1) return reading;
            }
            return BatteryReading.Missing(source, "Контроллер подключён, но отчёт батареи не поступил.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return BatteryReading.Missing(source, "Истёк срок чтения батареи. Следующая попытка будет автоматической."); }
        catch (IOException) { return BatteryReading.Missing(source, "Соединение изменилось во время чтения. Ожидаем новое подключение."); }
    }

    private static SafeFileHandle Open(string path, bool read) => CreateFile(path, read ? 0x80000000u : 0, 3, IntPtr.Zero, 3, read ? 0x40000000u : 0, IntPtr.Zero);
    private static string ReadString(SafeFileHandle handle, bool serial)
    {
        var buffer = new byte[512];
        var success = serial ? HidD_GetSerialNumberString(handle, buffer, buffer.Length) : HidD_GetProductString(handle, buffer, buffer.Length);
        return success ? Encoding.Unicode.GetString(buffer).TrimEnd('\0') : "";
    }

    [StructLayout(LayoutKind.Sequential)] private struct InterfaceData { public int Size; public Guid Class; public uint Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfoData { public int Size; public Guid Class; public uint DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct HidAttributes { public int Size; public ushort Vendor; public ushort Product; public ushort Version; }
    [StructLayout(LayoutKind.Sequential)] private struct HidCaps { public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 27)] public ushort[] Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey(Guid format, uint id) { public Guid Format = format; public uint Id = id; }
    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HidAttributes attrs);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);
    [DllImport("hid.dll")] private static extern bool HidD_GetProductString(SafeFileHandle handle, byte[] buffer, int length);
    [DllImport("hid.dll")] private static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, byte[] buffer, int length);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr info);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceProperty(IntPtr set, ref DeviceInfoData info, ref PropertyKey key, out uint type, byte[] data, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
