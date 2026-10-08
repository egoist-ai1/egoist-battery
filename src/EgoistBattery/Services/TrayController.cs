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

/// <summary>
/// Единственный значок в трее. Показывает DualSense, а если его нет — устройство с наименьшим зарядом (или закреплённое владельцем);
/// при наведении открывается панель со всеми подключёнными устройствами.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly Dictionary<string, DateTimeOffset> notified = [];
    private readonly Action show, refresh, exit;
    private readonly bool silent;
    private Forms.NotifyIcon? notify;
    private Icon? icon;
    private string? renderKey, tooltip;
    private string? iconDeviceId;
    private IReadOnlyList<DeviceSnapshot> latest = [];
    private int latestThreshold = 20;

    // Панель по наведению.
    private readonly Forms.Timer hoverTimer = new() { Interval = 80 };
    private TrayFlyout? flyout;
    private bool hovering;
    private Rectangle seen;
    private long hoverSince, lastSeen, lastInside;
    private bool flyoutBroken, menuOpen;

    /// <summary>1, если значок показывает устройство; 0 — значок-заглушка или значка нет.</summary>
    public int DeviceIconCount => iconDeviceId is null ? 0 : 1;
    /// <summary>Устройство, которое сейчас показывает значок.</summary>
    internal string? IconDeviceId => iconDeviceId;
    /// <summary>Положение курсора; самопроверка подменяет его, чтобы не двигать мышь владельца.</summary>
    internal Func<Point> CursorSource { get; set; } = () => Forms.Cursor.Position;
    internal bool FlyoutVisible => flyout is { Visible: true };
    public TrayController(Action show, Action refresh, Action exit, bool silent = false)
    {
        this.show = show; this.refresh = refresh; this.exit = exit; this.silent = silent;
        hoverTimer.Tick += (_, _) => OnHoverTick();
    }

    public void Update(IReadOnlyList<DeviceSnapshot> devices, string? pinned, bool notifications, int threshold)
    {
        latest = devices; latestThreshold = threshold;
        var visible = TrayPolicy.VisibleDevices(devices);
        var device = TrayPolicy.IconDevice(devices, pinned);
        var trayIcon = notify ??= CreateNotifyIcon();
        iconDeviceId = device?.Id;
        if (device is null)
        {
            Render("battery", null, false, "battery");
            SetTooltip("Egoist Battery · подключённых батарей нет\nДвойной щелчок: настройки");
        }
        else
        {
            var level = BatterySpectrum.LevelOf(device.Reading);
            var tone = DevicePresentation.ToneOf(device.Reading, threshold);
            Render(DevicePresentation.TrayValue(device.Reading), level, tone == BatteryTone.Charging, $"{DevicePresentation.TrayValue(device.Reading)}:{device.Reading.Minimum}");
            var state = device.Reading.State switch { ChargeState.Charging => "Заряжается", ChargeState.Full => "Заряжено", ChargeState.Discharging => "Питание от батареи", ChargeState.Error => "Ошибка зарядки", _ => device.Reading.Quality == ReadingQuality.WindowsCache ? "Значение Windows; время измерения неизвестно" : "Состояние зарядки неизвестно" };
            var text = $"{device.Name}: {device.Reading.Label}\n{state} · {device.Transport}" + (visible.Count > 1 ? $"\nВсего устройств: {visible.Count}" : "");
            SetTooltip(text.Length > 127 ? text[..127] : text);
        }
        trayIcon.Visible = !silent;
        if (!silent && notifications)
        {
            foreach (var low in visible.Where(x => x.Reading.Quality == ReadingQuality.Live && x.Reading.IsLow(threshold)))
            {
                if (notified.TryGetValue(low.Id, out var last) && DateTimeOffset.Now - last < TimeSpan.FromMinutes(30)) continue;
                notified[low.Id] = DateTimeOffset.Now;
                trayIcon.ShowBalloonTip(7000, "Низкий заряд", $"{low.Name}: {low.Reading.Label}. Подключите питание.", Forms.ToolTipIcon.Warning);
            }
            foreach (var id in notified.Keys.Where(x => visible.All(v => !string.Equals(v.Id, x, StringComparison.OrdinalIgnoreCase))).ToArray()) notified.Remove(id);
        }
        if (flyout is not null) flyout.UpdateContent(FlyoutModel.From(latest, latestThreshold, iconDeviceId), iconDeviceId);
    }

    private void SetTooltip(string text)
    {
        tooltip = text;
        // Пока открыта панель, системная подсказка отключена: два окна с одним содержимым не нужны.
        if (notify is not null && flyout is null) notify.Text = text;
    }

    private Forms.NotifyIcon CreateNotifyIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть настройки и устройства", null, (_, _) => show());
        menu.Items.Add("Обновить заряд", null, (_, _) => refresh());
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("Выйти", null, (_, _) => exit());
        menu.Opening += (_, _) => { menuOpen = true; HideFlyout(); };
        menu.Closed += (_, _) => menuOpen = false;
        var created = new Forms.NotifyIcon { ContextMenuStrip = menu };
        created.DoubleClick += (_, _) => { HideFlyout(); show(); };
        if (!silent) created.MouseMove += (_, _) => OnIconMove();
        return created;
    }

    // ---- Панель по наведению -------------------------------------------------------------------------------------

    private void OnIconMove()
    {
        if (flyoutBroken || menuOpen || iconDeviceId is null) return;
        var now = Environment.TickCount64; var cursor = CursorSource();
        var spot = new Rectangle(cursor.X - 4, cursor.Y - 4, 8, 8);
        if (!hovering || now - lastSeen > 1500) { hoverSince = now; seen = spot; }
        else seen = Rectangle.Union(seen, spot);
        hovering = true; lastSeen = lastInside = now;
        hoverTimer.Start();
    }

    private void OnHoverTick()
    {
        if (!hovering || menuOpen || notify is null) { hoverTimer.Stop(); return; }
        var now = Environment.TickCount64; var cursor = CursorSource();
        var iconRect = LocateIcon(notify) ?? seen;
        var overIcon = !iconRect.IsEmpty && Rectangle.Inflate(iconRect, 2, 2).Contains(cursor);
        var overFlyout = flyout is not null && Rectangle.Inflate(flyout.Bounds, 8, 8).Contains(cursor);
        if (overIcon || overFlyout) lastInside = now;
        if (flyout is null)
        {
            if (overIcon && now - hoverSince >= 220) ShowFlyout(iconRect, cursor);
            else if (!overIcon && now - lastInside > 250) { hovering = false; hoverTimer.Stop(); }
        }
        else if (now - lastInside > 450) HideFlyout();
    }

    private void ShowFlyout(Rectangle iconRect, Point cursor)
    {
        if (flyoutBroken || notify is null) return;
        try
        {
            notify.Text = "";
            flyout = new TrayFlyout(FlyoutModel.From(latest, latestThreshold, iconDeviceId), iconDeviceId, TrayFlyout.ScaleAt(cursor));
            flyout.ShowAt(iconRect.IsEmpty ? new Rectangle(cursor.X - 12, cursor.Y - 12, 24, 24) : iconRect);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or ExternalException or ArgumentException)
        {
            // Панель — дополнение: при сбое остаётся системная подсказка.
            flyoutBroken = true; HideFlyout();
        }
    }

    private void HideFlyout()
    {
        hoverTimer.Stop(); hovering = false;
        var closing = flyout; flyout = null;
        if (closing is not null) { closing.Close(); closing.Dispose(); }
        if (notify is not null && tooltip is not null) notify.Text = tooltip;
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
    internal Bitmap RenderFlyoutPreview(float scale, string? highlight = null) => TrayFlyout.RenderBitmap(FlyoutModel.From(latest, latestThreshold, iconDeviceId), scale, highlight);

    /// <summary>Самопроверка: положение значка и имитация наведения сообщением, которое Windows шлёт при движении мыши над значком.</summary>
    internal Rectangle? IconRect() => notify is null ? null : LocateIcon(notify);
    internal bool PostHover()
    {
        if (notify is null || WindowField?.GetValue(notify) is not Forms.NativeWindow window || IdField?.GetValue(notify) is not { } iconId) return false;
        return PostMessage(window.Handle, 0x800, (IntPtr)Convert.ToInt64(iconId), (IntPtr)0x200);
    }

    /// <summary>Самопроверка: невидимая панель создаётся, показывается рядом со значком и помещается на экран.</summary>
    internal void CheckFlyout(string? highlight)
    {
        using var form = new TrayFlyout(FlyoutModel.From(latest, latestThreshold, iconDeviceId), highlight, 1f, invisible: true);
        var screen = Forms.Screen.PrimaryScreen!.Bounds;
        form.ShowAt(new Rectangle(screen.Right - 220, screen.Bottom - 40, 24, 24));
        var fits = form.Visible && screen.Contains(form.Bounds);
        form.Close();
        if (!fits) throw new InvalidOperationException("Панель трея не создаётся или не помещается на экран.");
    }

    // ---- Значок --------------------------------------------------------------------------------------------------

    private void Render(string value, double? level, bool charging, string stateKey)
    {
        var light = IsLightTaskbar();
        var key = $"{stateKey}:{(level is null ? "-" : Math.Round(level.Value * 100))}:{charging}:{light}:{TrayPixels()}";
        if (renderKey == key) return;
        var next = DrawIcon(value, level, charging, light);
        notify!.Icon = next; icon?.Dispose(); icon = next; renderKey = key;
    }

    /// <summary>
    /// Значок: яркая капсула цвета уровня (красный → жёлтый → лайм) с крупным числом. Без уровня — серая капсула.
    /// Зарядка отмечена контрастной обводкой. ICO содержит отдельную векторную отрисовку для каждого DPI.
    /// </summary>
    internal static Icon DrawIcon(string value, double? level, bool charging, bool lightTaskbar = false, int? pixelSize = null)
    {
        var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var frames = sizes.Select(size => DrawFrame(value, level, charging, lightTaskbar, size)).ToArray();
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
        using var created = new Icon(ico, new System.Drawing.Size(pixelSize ?? TrayPixels(), pixelSize ?? TrayPixels()));
        return (Icon)created.Clone();
    }

    private static readonly Lazy<string> DigitFamily = new(() =>
    {
        using var installed = new Drawing.Text.InstalledFontCollection();
        return installed.Families.Any(x => string.Equals(x.Name, "Segoe UI Black", StringComparison.OrdinalIgnoreCase)) ? "Segoe UI Black" : "Segoe UI";
    });

    private static byte[] DrawFrame(string value, double? level, bool charging, bool light, int size)
    {
        // GDI+ рисует контуры, не загружая графический стек WPF и драйвер GPU.
        using var bitmap = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = Drawing2D.CompositingQuality.HighQuality;
        var f = size / 64f;
        var ink = Color.FromArgb(14, 17, 19);
        var edge = light ? Color.FromArgb(24, 32, 36) : Color.White;
        if (value == "battery")
        {
            using var outline = RoundedPath(7 * f, 17 * f, 44 * f, 30 * f, 6 * f);
            using var pen = new Drawing.Pen(light ? ink : Color.FromArgb(237, 239, 238), Math.Max(1, 4 * f));
            using var cap = new Drawing.SolidBrush(light ? ink : Color.FromArgb(237, 239, 238));
            graphics.DrawPath(pen, outline);
            graphics.FillRectangle(cap, 53 * f, 25 * f, 5 * f, 14 * f);
        }
        else
        {
            var known = level.HasValue;
            Color body; Color text;
            if (known) { var (r, g, b) = BatterySpectrum.At(level!.Value); body = Color.FromArgb(r, g, b); text = ink; }
            else if (light) { body = Color.FromArgb(205, 211, 214); text = ink; }
            else { body = Color.FromArgb(78, 89, 95); text = Color.White; }

            // Капсула почти во весь квадрат: чем крупнее площадь, тем крупнее число.
            var box = new RectangleF(1 * f, 6 * f, 62 * f, 52 * f);
            using (var capsule = RoundedPath(box.X, box.Y, box.Width, box.Height, 16 * f))
            using (var bodyBrush = new Drawing.SolidBrush(body)) graphics.FillPath(bodyBrush, capsule);
            if (charging)
            {
                var ringWidth = Math.Max(1.2f, 4.2f * f);
                using var ring = RoundedPath(box.X + ringWidth / 2, box.Y + ringWidth / 2, box.Width - ringWidth, box.Height - ringWidth, 14.5f * f);
                using var ringPen = new Drawing.Pen(edge, ringWidth);
                graphics.DrawPath(ringPen, ring);
            }

            using var family = new Drawing.FontFamily(DigitFamily.Value);
            using var path = new Drawing2D.GraphicsPath();
            path.AddString(value, family, (int)Drawing.FontStyle.Bold, 46, new Drawing.PointF(0, 0), Drawing.StringFormat.GenericTypographic);
            var bounds = path.GetBounds();
            // Цифры занимают всю высоту капсулы; три знака («100») сжимаются по ширине не больше чем до 72% нормы.
            var innerWidth = charging ? 52f : 56f; var innerHeight = charging ? 36f : 40f;
            var sy = innerHeight / Math.Max(bounds.Height, 1);
            var sx = Math.Min(sy, innerWidth / Math.Max(bounds.Width, 1));
            if (sx < sy * 0.72f) { sy = Math.Min(sy, innerWidth / (Math.Max(bounds.Width, 1) * 0.72f)); sx = sy * 0.72f; }
            using var transform = new Drawing2D.Matrix();
            transform.Translate(-bounds.Left, -bounds.Top, Drawing2D.MatrixOrder.Append);
            transform.Scale(sx, sy, Drawing2D.MatrixOrder.Append);
            transform.Translate(box.X / f + (box.Width / f - bounds.Width * sx) / 2, box.Y / f + (box.Height / f - bounds.Height * sy) / 2, Drawing2D.MatrixOrder.Append);
            transform.Scale(f, f, Drawing2D.MatrixOrder.Append);
            path.Transform(transform);
            using var textBrush = new Drawing.SolidBrush(text);
            graphics.FillPath(textBrush, path);
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
        if (notify is not null) { notify.Visible = false; notify.ContextMenuStrip?.Dispose(); notify.Dispose(); notify = null; }
        icon?.Dispose(); icon = null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NotifyIconIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid Item; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out NativeRect rect);
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
