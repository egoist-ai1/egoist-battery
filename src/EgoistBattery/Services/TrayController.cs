using System.Drawing;
using System.Runtime.InteropServices;
using EgoistBattery.Core;
using Microsoft.Win32;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace EgoistBattery.Services;

internal sealed class TrayController : IDisposable
{
    private sealed class Entry(Forms.NotifyIcon notify) : IDisposable
    {
        public Forms.NotifyIcon Notify { get; } = notify;
        public Icon? Icon { get; set; }
        public string? RenderKey { get; set; }
        public void Dispose() { Notify.Visible = false; Notify.ContextMenuStrip?.Dispose(); Notify.Dispose(); Icon?.Dispose(); }
    }
    private readonly Dictionary<string, Entry> entries = [];
    private readonly Dictionary<string, DateTimeOffset> notified = [];
    private readonly Action show, refresh, exit;
    private readonly bool silent;
    private Entry? fallback;
    public int DeviceIconCount => entries.Count;
    public TrayController(Action show, Action refresh, Action exit, bool silent = false)
    { this.show = show; this.refresh = refresh; this.exit = exit; this.silent = silent; }

    public void Update(IReadOnlyList<DeviceSnapshot> devices, IReadOnlyList<string> hidden, bool notifications, int threshold)
    {
        var visible = TrayPolicy.VisibleDevices(devices, hidden);
        var currentIds = visible.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in entries.Keys.Where(x => !currentIds.Contains(x)).ToArray())
        { entries[id].Dispose(); entries.Remove(id); notified.Remove(id); }
        foreach (var device in visible)
        {
            if (!entries.TryGetValue(device.Id, out var entry)) { entry = CreateEntry(); entries.Add(device.Id, entry); }
            var value = device.Reading.HasLevel ? $"{device.Reading.Minimum}{(device.Reading.Minimum == device.Reading.Maximum ? "" : "+")}" : "?";
            Render(entry, value, device.Reading.Percent, device.Reading.State);
            var state = device.Reading.State switch { ChargeState.Charging => "Заряжается", ChargeState.Full => "Заряжено", ChargeState.Discharging => "Питание от батареи", ChargeState.Error => "Ошибка зарядки", _ => device.Reading.Quality == ReadingQuality.WindowsCache ? "Значение Windows; время измерения неизвестно" : "Состояние зарядки неизвестно" };
            var tooltip = $"{device.Name}: {device.Reading.Label}\n{state} · {device.Transport}\nПрочитано: {device.Reading.ObservedAt.ToLocalTime():HH:mm:ss}";
            entry.Notify.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
            entry.Notify.Visible = !silent;
            if (!silent && notifications && device.Reading.Quality == ReadingQuality.Live && device.Reading.IsLow(threshold)
                && (!notified.TryGetValue(device.Id, out var last) || DateTimeOffset.Now - last >= TimeSpan.FromMinutes(30)))
            {
                notified[device.Id] = DateTimeOffset.Now;
                entry.Notify.ShowBalloonTip(7000, "Низкий заряд", $"{device.Name}: {device.Reading.Label}. Подключите питание.", Forms.ToolTipIcon.Warning);
            }
        }
        if (visible.Count > 0) { fallback?.Dispose(); fallback = null; }
        else
        {
            fallback ??= CreateEntry(); Render(fallback, "battery", null, ChargeState.Unknown);
            fallback.Notify.Text = "Egoist Battery · подключённых батарей нет\nДвойной щелчок: настройки";
            fallback.Notify.Visible = !silent;
        }
    }
    private Entry CreateEntry()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть настройки и устройства", null, (_, _) => show());
        menu.Items.Add("Обновить заряд", null, (_, _) => refresh());
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("Выйти", null, (_, _) => exit());
        var notify = new Forms.NotifyIcon { ContextMenuStrip = menu };
        notify.DoubleClick += (_, _) => show();
        return new Entry(notify);
    }
    private static void Render(Entry entry, string value, int? percent, ChargeState state)
    {
        var darkText = IsLightTaskbar();
        var key = $"{value}:{percent}:{state}:{darkText}:{TrayPixels()}";
        if (entry.RenderKey == key) return;
        var next = DrawIcon(value, percent, state, darkText);
        entry.Notify.Icon = next; entry.Icon?.Dispose(); entry.Icon = next; entry.RenderKey = key;
    }
    internal static Icon DrawIcon(string value, int? percent, ChargeState state, bool darkText = false, int? pixelSize = null)
    {
        // ICO содержит отдельную отрисовку векторных контуров для каждого DPI.
        // Windows получает нужный размер, а не увеличенную картинку 16×16.
        var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var frames = sizes.Select(size => DrawFrame(value, percent, state, darkText, size)).ToArray();
        using var ico = new MemoryStream();
        using (var writer = new BinaryWriter(ico, System.Text.Encoding.UTF8, true))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)frames.Length);
            var offset = 6 + 16 * frames.Length;
            for (var i = 0; i < frames.Length; i++)
            {
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
            }
            foreach (var frame in frames) writer.Write(frame);
        }
        ico.Position = 0;
        using var icon = new Icon(ico, new System.Drawing.Size(pixelSize ?? TrayPixels(), pixelSize ?? TrayPixels()));
        return (Icon)icon.Clone();
    }
    private static byte[] DrawFrame(string value, int? percent, ChargeState state, bool dark, int size)
    {
        // GDI+ рисует контуры, не загружая графический стек WPF и драйвер GPU.
        using var bitmap = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = Drawing2D.CompositingQuality.HighQuality;
        var factor = size / 64f;
        var fillColor = dark ? Drawing.Color.FromArgb(23, 32, 36) : Drawing.Color.FromArgb(245, 250, 248);
        using var fill = new Drawing.SolidBrush(fillColor);
        if (value == "battery")
        {
            using var outline = RoundedPath(7 * factor, 17 * factor, 44 * factor, 30 * factor, 6 * factor);
            using var pen = new Drawing.Pen(fillColor, Math.Max(1, 4 * factor));
            graphics.DrawPath(pen, outline);
            graphics.FillRectangle(fill, 53 * factor, 25 * factor, 5 * factor, 14 * factor);
        }
        else
        {
            using var family = new Drawing.FontFamily("Segoe UI");
            using var path = new Drawing2D.GraphicsPath();
            path.AddString(value, family, (int)Drawing.FontStyle.Bold, 46, new Drawing.PointF(0, 0), Drawing.StringFormat.GenericTypographic);
            var bounds = path.GetBounds();
            var scale = Math.Min(55 / Math.Max(bounds.Width, 1), 43 / Math.Max(bounds.Height, 1));
            using var transform = new Drawing2D.Matrix();
            transform.Translate(-bounds.Left, -bounds.Top, Drawing2D.MatrixOrder.Append);
            transform.Scale(scale, scale, Drawing2D.MatrixOrder.Append);
            transform.Translate((64 - bounds.Width * scale) / 2, 5 + (43 - bounds.Height * scale) / 2, Drawing2D.MatrixOrder.Append);
            transform.Scale(factor, factor, Drawing2D.MatrixOrder.Append);
            path.Transform(transform);
            var glow = dark ? Drawing.Color.FromArgb(22, 111, 79) : Drawing.Color.FromArgb(148, 246, 203);
            foreach (var (width, alpha) in new (float, int)[] { (5, 8), (3, 15), (1.5f, 25) })
            {
                using var pen = new Drawing.Pen(Drawing.Color.FromArgb(alpha, glow), width * factor);
                graphics.DrawPath(pen, path);
            }
            graphics.FillPath(fill, path);
            if (percent.HasValue)
            {
                var color = state is ChargeState.Charging or ChargeState.Full ? Drawing.Color.FromArgb(133, 235, 188)
                    : percent <= 20 ? Drawing.Color.FromArgb(243, 194, 120) : dark ? Drawing.Color.FromArgb(30, 102, 77) : Drawing.Color.FromArgb(175, 225, 199);
                var height = Math.Max(1, (int)Math.Round(size * 0.045));
                using var pen = new Drawing.Pen(color, height) { StartCap = Drawing2D.LineCap.Round, EndCap = Drawing2D.LineCap.Round };
                graphics.DrawLine(pen, 7 * factor, MathF.Round(58 * factor), 57 * factor, MathF.Round(58 * factor));
            }
        }
        using var stream = new MemoryStream(); bitmap.Save(stream, Drawing.Imaging.ImageFormat.Png); return stream.ToArray();
    }
    private static Drawing2D.GraphicsPath RoundedPath(float x, float y, float width, float height, float radius)
    {
        var path = new Drawing2D.GraphicsPath(); var diameter = radius * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90); path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90); path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }
    private static int TrayPixels()
    {
        var window = FindWindow("Shell_TrayWnd", null);
        var dpi = window == IntPtr.Zero ? 96 : GetDpiForWindow(window);
        return Math.Clamp((int)Math.Round(16 * (dpi == 0 ? 96 : dpi) / 96d), 16, 64);
    }
    private static bool IsLightTaskbar()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"); return key?.GetValue("SystemUsesLightTheme") is int value && value == 1; }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }
    public void Dispose() { foreach (var entry in entries.Values) entry.Dispose(); entries.Clear(); fallback?.Dispose(); fallback = null; }
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
}
