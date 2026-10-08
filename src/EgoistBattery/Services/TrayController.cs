using System.ComponentModel;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using EgoistBattery.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace EgoistBattery.Services;

// Глобальные псевдонимы проекта указывают на типы WPF; здесь нужны типы GDI+.
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

internal sealed class TrayController : IDisposable
{
    private sealed class Entry(string id, Forms.NotifyIcon notify) : IDisposable
    {
        public string Id { get; } = id;
        public Forms.NotifyIcon Notify { get; } = notify;
        public Icon? Icon { get; set; }
        public string? RenderKey { get; set; }
        public string Tooltip { get; set; } = "";
        public void Dispose() { Notify.Visible = false; Notify.ContextMenuStrip?.Dispose(); Notify.Dispose(); Icon?.Dispose(); }
    }
    private readonly Dictionary<string, Entry> entries = [];
    private readonly Dictionary<string, DateTimeOffset> notified = [];
    private readonly Action show, refresh, exit;
    private readonly bool silent;
    private Entry? fallback;
    private IReadOnlyList<DeviceSnapshot> latest = [];
    private int latestThreshold = 20;

    // Панель по наведению.
    private readonly Forms.Timer hoverTimer = new() { Interval = 80 };
    private TrayFlyout? flyout;
    private Entry? hovered;
    private Rectangle seen;
    private long hoverSince, lastSeen, lastInside;
    private bool flyoutBroken, menuOpen;

    public int DeviceIconCount => entries.Count;
    /// <summary>Положение курсора; самопроверка подменяет его, чтобы не двигать мышь владельца.</summary>
    internal Func<Point> CursorSource { get; set; } = () => Forms.Cursor.Position;
    internal bool FlyoutVisible => flyout is { Visible: true };
    public TrayController(Action show, Action refresh, Action exit, bool silent = false)
    {
        this.show = show; this.refresh = refresh; this.exit = exit; this.silent = silent;
        hoverTimer.Tick += (_, _) => OnHoverTick();
    }

    public void Update(IReadOnlyList<DeviceSnapshot> devices, IReadOnlyList<string> hidden, bool notifications, int threshold)
    {
        latest = devices; latestThreshold = threshold;
        var visible = TrayPolicy.VisibleDevices(devices, hidden);
        var currentIds = visible.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in entries.Keys.Where(x => !currentIds.Contains(x)).ToArray())
        { if (ReferenceEquals(hovered, entries[id])) HideFlyout(); entries[id].Dispose(); entries.Remove(id); notified.Remove(id); }
        foreach (var device in visible)
        {
            if (!entries.TryGetValue(device.Id, out var entry)) { entry = CreateEntry(device.Id); entries.Add(device.Id, entry); }
            var tone = DevicePresentation.ToneOf(device.Reading, threshold);
            Render(entry, DevicePresentation.TrayValue(device.Reading), device.Reading.Minimum, tone);
            var state = device.Reading.State switch { ChargeState.Charging => "Заряжается", ChargeState.Full => "Заряжено", ChargeState.Discharging => "Питание от батареи", ChargeState.Error => "Ошибка зарядки", _ => device.Reading.Quality == ReadingQuality.WindowsCache ? "Значение Windows; время измерения неизвестно" : "Состояние зарядки неизвестно" };
            var tooltip = $"{device.Name}: {device.Reading.Label}\n{state} · {device.Transport}\nПрочитано: {device.Reading.ObservedAt.ToLocalTime():HH:mm:ss}";
            entry.Tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
            // Пока открыта панель, системная подсказка отключена: два окна с одним содержимым не нужны.
            if (!ReferenceEquals(hovered, entry) || flyout is null) entry.Notify.Text = entry.Tooltip;
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
            fallback ??= CreateEntry("", fallback: true); Render(fallback, "battery", null, BatteryTone.Unknown);
            fallback.Notify.Text = "Egoist Battery · подключённых батарей нет\nДвойной щелчок: настройки";
            fallback.Notify.Visible = !silent;
        }
        if (flyout is not null) flyout.UpdateContent(FlyoutModel.From(latest, latestThreshold), hovered?.Id);
    }

    private Entry CreateEntry(string id, bool fallback = false)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть настройки и устройства", null, (_, _) => show());
        menu.Items.Add("Обновить заряд", null, (_, _) => refresh());
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("Выйти", null, (_, _) => exit());
        menu.Opening += (_, _) => { menuOpen = true; HideFlyout(); };
        menu.Closed += (_, _) => menuOpen = false;
        var notify = new Forms.NotifyIcon { ContextMenuStrip = menu };
        var entry = new Entry(id, notify);
        notify.DoubleClick += (_, _) => { HideFlyout(); show(); };
        if (!fallback && !silent) notify.MouseMove += (_, _) => OnIconMove(entry);
        return entry;
    }

    // ---- Панель по наведению -------------------------------------------------------------------------------------

    private void OnIconMove(Entry entry)
    {
        if (flyoutBroken || menuOpen) return;
        var now = Environment.TickCount64; var cursor = CursorSource();
        var spot = new Rectangle(cursor.X - 4, cursor.Y - 4, 8, 8);
        if (!ReferenceEquals(hovered, entry) || now - lastSeen > 1500) { hoverSince = now; seen = spot; }
        else seen = Rectangle.Union(seen, spot);
        // Курсор перешёл на соседний значок: панель остаётся и подсвечивает его устройство.
        var switched = !ReferenceEquals(hovered, entry);
        if (flyout is not null && switched) { if (hovered is not null) hovered.Notify.Text = hovered.Tooltip; entry.Notify.Text = ""; }
        hovered = entry; lastSeen = lastInside = now;
        if (flyout is not null && switched) flyout.UpdateContent(FlyoutModel.From(latest, latestThreshold), entry.Id);
        hoverTimer.Start();
    }

    private void OnHoverTick()
    {
        if (hovered is null || menuOpen) { hoverTimer.Stop(); return; }
        var now = Environment.TickCount64; var cursor = CursorSource();
        var icon = LocateIcon(hovered.Notify) ?? seen;
        var overIcon = !icon.IsEmpty && Rectangle.Inflate(icon, 2, 2).Contains(cursor);
        var overFlyout = flyout is not null && Rectangle.Inflate(flyout.Bounds, 8, 8).Contains(cursor);
        if (overIcon || overFlyout) lastInside = now;
        if (flyout is null)
        {
            if (overIcon && now - hoverSince >= 220) ShowFlyout(icon, cursor);
            else if (!overIcon && now - lastInside > 250) { hovered = null; hoverTimer.Stop(); }
        }
        else if (now - lastInside > 450) HideFlyout();
    }

    private void ShowFlyout(Rectangle icon, Point cursor)
    {
        if (flyoutBroken || hovered is null) return;
        try
        {
            hovered.Notify.Text = "";
            flyout = new TrayFlyout(FlyoutModel.From(latest, latestThreshold), hovered.Id, TrayFlyout.ScaleAt(cursor));
            flyout.ShowAt(icon.IsEmpty ? new Rectangle(cursor.X - 12, cursor.Y - 12, 24, 24) : icon);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or ExternalException or ArgumentException)
        {
            // Панель — дополнение: при сбое остаётся системная подсказка.
            flyoutBroken = true; HideFlyout();
        }
    }

    private void HideFlyout()
    {
        hoverTimer.Stop();
        var closing = flyout; flyout = null;
        if (closing is not null) { closing.Close(); closing.Dispose(); }
        if (hovered is not null) hovered.Notify.Text = hovered.Tooltip;
        hovered = null;
    }

    /// <summary>Прямоугольник значка на экране. Берётся у оболочки Windows; если закрытые поля NotifyIcon недоступны, остаётся запасной способ.</summary>
    private static Rectangle? LocateIcon(Forms.NotifyIcon icon)
    {
        try
        {
            if (WindowField is null || IdField is null) return null;
            if (WindowField.GetValue(icon) is not Forms.NativeWindow window || window.Handle == IntPtr.Zero || IdField.GetValue(icon) is not { } id) return null;
            // В .NET 8 идентификатор значка — uint, в .NET Framework — int.
            var target = new NotifyIconIdentifier { Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = window.Handle, Id = Convert.ToUInt32(id) };
            return Shell_NotifyIconGetRect(ref target, out var rect) == 0 ? Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom) : null;
        }
        catch (Exception e) when (e is TargetException or InvalidCastException or DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) { return null; }
    }
    private static readonly FieldInfo? WindowField = typeof(Forms.NotifyIcon).GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance) ?? typeof(Forms.NotifyIcon).GetField("window", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? IdField = typeof(Forms.NotifyIcon).GetField("_id", BindingFlags.NonPublic | BindingFlags.Instance) ?? typeof(Forms.NotifyIcon).GetField("id", BindingFlags.NonPublic | BindingFlags.Instance);
    /// <summary>Нужные закрытые поля NotifyIcon найдены (проверка самотестом после обновления .NET).</summary>
    internal static bool CanLocateIcons => WindowField is not null && IdField is not null;

    /// <summary>Панель для самопроверки и предпросмотра: тот же рисунок, что при наведении.</summary>
    internal Bitmap RenderFlyoutPreview(float scale, string? highlight = null) => TrayFlyout.RenderBitmap(FlyoutModel.From(latest, latestThreshold), scale, highlight);

    /// <summary>Самопроверка: положение значка первого устройства и имитация наведения сообщением, которое Windows шлёт при движении мыши над значком.</summary>
    internal (string Id, Rectangle Icon)? FirstIcon()
    {
        var entry = entries.Values.FirstOrDefault();
        return entry is not null && LocateIcon(entry.Notify) is { } rect ? (entry.Id, rect) : null;
    }
    internal bool PostHover(string id)
    {
        if (!entries.TryGetValue(id, out var entry) || WindowField?.GetValue(entry.Notify) is not Forms.NativeWindow window || IdField?.GetValue(entry.Notify) is not { } iconId) return false;
        return PostMessage(window.Handle, 0x800, (IntPtr)Convert.ToInt64(iconId), (IntPtr)0x200);
    }

    /// <summary>Самопроверка: невидимая панель создаётся, показывается рядом со значком и помещается на экран.</summary>
    internal void CheckFlyout(string? highlight)
    {
        using var form = new TrayFlyout(FlyoutModel.From(latest, latestThreshold), highlight, 1f, invisible: true);
        var screen = Forms.Screen.PrimaryScreen!.Bounds;
        form.ShowAt(new Rectangle(screen.Right - 220, screen.Bottom - 40, 24, 24));
        var fits = form.Visible && screen.Contains(form.Bounds);
        form.Close();
        if (!fits) throw new InvalidOperationException("Панель трея не создаётся или не помещается на экран.");
    }

    // ---- Значок --------------------------------------------------------------------------------------------------

    private static void Render(Entry entry, string value, int? percent, BatteryTone tone)
    {
        var light = IsLightTaskbar();
        var key = $"{value}:{percent}:{tone}:{light}:{TrayPixels()}";
        if (entry.RenderKey == key) return;
        var next = DrawIcon(value, percent, tone, light);
        entry.Notify.Icon = next; entry.Icon?.Dispose(); entry.Icon = next; entry.RenderKey = key;
    }

    internal static Icon DrawIcon(string value, int? percent, BatteryTone tone, bool lightTaskbar = false, int? pixelSize = null)
    {
        // ICO содержит отдельную отрисовку векторных контуров для каждого DPI.
        // Windows получает нужный размер, а не увеличенную картинку 16×16.
        var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var frames = sizes.Select(size => DrawFrame(value, percent, tone, lightTaskbar, size)).ToArray();
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

    /// <summary>Цвета капсулы: заливка уровня, текст поверх заливки и поверх пустой части.</summary>
    private readonly record struct Look(Color Track, Color Outline, Color Fill, Color OnFill, Color OnTrack, bool HasFill);
    private static Look LookOf(BatteryTone tone, bool light)
    {
        if (light)
        {
            var track = Color.FromArgb(225, 230, 232); var outline = Color.FromArgb(140, 152, 158); var onTrack = Color.FromArgb(24, 32, 36);
            return tone switch
            {
                BatteryTone.Charging => new(track, outline, Color.FromArgb(84, 150, 16), Color.White, onTrack, true),
                BatteryTone.Low => new(track, outline, Color.FromArgb(222, 150, 20), Color.FromArgb(30, 22, 4), onTrack, true),
                BatteryTone.Critical or BatteryTone.Error => new(track, outline, Color.FromArgb(200, 52, 46), Color.White, onTrack, true),
                BatteryTone.Unknown => new(track, outline, track, onTrack, onTrack, false),
                _ => new(track, outline, Color.FromArgb(36, 44, 48), Color.FromArgb(245, 248, 247), onTrack, true)
            };
        }
        var darkTrack = Color.FromArgb(45, 53, 58); var darkOutline = Color.FromArgb(96, 108, 114); var onDark = Color.FromArgb(14, 17, 19); var white = Color.FromArgb(237, 239, 238);
        return tone switch
        {
            BatteryTone.Charging => new(darkTrack, darkOutline, Color.FromArgb(198, 242, 78), onDark, white, true),
            BatteryTone.Low => new(darkTrack, darkOutline, Color.FromArgb(246, 200, 121), onDark, white, true),
            BatteryTone.Critical or BatteryTone.Error => new(darkTrack, darkOutline, Color.FromArgb(255, 143, 134), onDark, white, true),
            BatteryTone.Unknown => new(darkTrack, darkOutline, darkTrack, white, Color.FromArgb(200, 208, 211), false),
            _ => new(darkTrack, darkOutline, white, onDark, white, true)
        };
    }

    private static byte[] DrawFrame(string value, int? percent, BatteryTone tone, bool light, int size)
    {
        // GDI+ рисует контуры, не загружая графический стек WPF и драйвер GPU.
        using var bitmap = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = Drawing2D.CompositingQuality.HighQuality;
        var f = size / 64f;
        var look = LookOf(tone, light);
        if (value == "battery")
        {
            using var outline = RoundedPath(7 * f, 17 * f, 44 * f, 30 * f, 6 * f);
            using var pen = new Drawing.Pen(look.OnTrack, Math.Max(1, 4 * f));
            using var cap = new Drawing.SolidBrush(look.OnTrack);
            graphics.DrawPath(pen, outline);
            graphics.FillRectangle(cap, 53 * f, 25 * f, 5 * f, 14 * f);
        }
        else
        {
            // Капсула: пустая часть тёмная, заполненная — цвета состояния; число читается на обеих частях.
            var box = new RectangleF(2 * f, 11 * f, 60 * f, 42 * f);
            using var capsule = RoundedPath(box.X, box.Y, box.Width, box.Height, 13 * f);
            using (var trackBrush = new Drawing.SolidBrush(look.Track)) graphics.FillPath(trackBrush, capsule);
            var fillWidth = 0f;
            if (look.HasFill && percent.HasValue)
            {
                fillWidth = box.Width * Math.Clamp(percent.Value, 0, 100) / 100f;
                if (percent.Value > 0) fillWidth = Math.Max(fillWidth, 7 * f);
                graphics.SetClip(capsule);
                using var fillBrush = new Drawing.SolidBrush(look.Fill);
                graphics.FillRectangle(fillBrush, box.X, box.Y, fillWidth, box.Height);
                graphics.ResetClip();
            }
            using (var outlinePen = new Drawing.Pen(look.Outline, Math.Max(1, 2 * f))) graphics.DrawPath(outlinePen, capsule);

            using var family = new Drawing.FontFamily("Segoe UI");
            using var path = new Drawing2D.GraphicsPath();
            path.AddString(value, family, (int)Drawing.FontStyle.Bold, 46, new Drawing.PointF(0, 0), Drawing.StringFormat.GenericTypographic);
            var bounds = path.GetBounds();
            // Цифры вытягиваются по высоте; три знака («100») сжимаются по ширине, но не больше чем до 72% от нормы.
            var sy = Math.Min(28 / Math.Max(bounds.Height, 1), 1f);
            var sx = Math.Min(sy, 50 / Math.Max(bounds.Width, 1));
            if (sx < sy * 0.72f) { sy = Math.Min(sy, 50 / (Math.Max(bounds.Width, 1) * 0.72f)); sx = sy * 0.72f; }
            using var transform = new Drawing2D.Matrix();
            transform.Translate(-bounds.Left, -bounds.Top, Drawing2D.MatrixOrder.Append);
            transform.Scale(sx, sy, Drawing2D.MatrixOrder.Append);
            transform.Translate(box.X / f + (box.Width / f - bounds.Width * sx) / 2, box.Y / f + (box.Height / f - bounds.Height * sy) / 2, Drawing2D.MatrixOrder.Append);
            transform.Scale(f, f, Drawing2D.MatrixOrder.Append);
            path.Transform(transform);
            using var onFill = new Drawing.SolidBrush(look.OnFill); using var onTrack = new Drawing.SolidBrush(look.OnTrack);
            var split = box.X + fillWidth;
            graphics.SetClip(new RectangleF(0, 0, split, size)); graphics.FillPath(onFill, path);
            graphics.SetClip(new RectangleF(split, 0, size, size)); graphics.FillPath(onTrack, path);
            graphics.ResetClip();
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
    public void Dispose()
    {
        hoverTimer.Stop(); hoverTimer.Dispose(); HideFlyout();
        foreach (var entry in entries.Values) entry.Dispose(); entries.Clear(); fallback?.Dispose(); fallback = null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NotifyIconIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid Item; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out NativeRect rect);
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
