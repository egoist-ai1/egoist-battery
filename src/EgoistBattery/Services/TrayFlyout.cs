using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using EgoistBattery.Core;
using Forms = System.Windows.Forms;

namespace EgoistBattery.Services;

// Глобальные псевдонимы проекта указывают на типы WPF; здесь нужны типы GDI+.
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

/// <summary>Строка панели: всё, что нужно для рисования, без ссылок на устройства.</summary>
internal sealed record FlyoutRow(string Id, string Name, string Meta, string Glyph, BatteryTone Tone, string Numeral, int Cells, int Solid, int Interval, bool Measured, bool Charging);

internal sealed record FlyoutModel(IReadOnlyList<FlyoutRow> Rows, string Time)
{
    public static FlyoutModel From(IEnumerable<DeviceSnapshot> devices, int threshold)
    {
        var connected = DevicePresentation.Order(devices.Where(x => x.Connected)).ToArray();
        var rows = connected.Select(x =>
        {
            var tone = DevicePresentation.ToneOf(x.Reading, threshold);
            var measured = DevicePresentation.GroupOf(x) == DeviceGroup.Measured;
            var state = x.Reading.State switch { ChargeState.Charging => "Заряжается", ChargeState.Full => "Зарядка завершена", ChargeState.Discharging => "Питание от батареи", ChargeState.Wired => "Проводное питание", ChargeState.Error => "Ошибка зарядки", _ => measured ? "Состояние зарядки неизвестно" : "Заряд не передаётся" };
            var (solid, interval) = measured ? DevicePresentation.Cells(x.Reading) : (0, 0);
            return new FlyoutRow(x.Id, x.Name, $"{state} · {x.Transport}", GlyphOf(x.Kind), measured || tone == BatteryTone.Error ? tone : BatteryTone.Unknown,
                measured ? x.Reading.Label : "—", DevicePresentation.CellCount(x.Reading), solid, interval, measured, measured && tone == BatteryTone.Charging);
        }).ToArray();
        var time = connected.Length == 0 ? DateTimeOffset.Now : connected.Max(x => x.Reading.ObservedAt);
        return new(rows, time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
    }
    private static string GlyphOf(string kind) => kind switch { "Мышь" => "\uE962", "Клавиатура" => "\uE765", "Наушники" => "\uE7F6", "Контроллер" => "\uE7FC", "Компьютер" => "\uE7F8", "Bluetooth" => "\uE702", _ => "\uE772" };
}

/// <summary>
/// Панель по наведению на значок трея. Рисуется GDI+ без WPF и GPU, поэтому фоновый процесс остаётся лёгким;
/// форма создаётся при наведении и уничтожается при уходе курсора.
/// </summary>
internal sealed class TrayFlyout : Forms.Form
{
    private const int LogicalWidth = 396;
    private const int WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExTopmost = 0x8, CsDropShadow = 0x20000;
    private FlyoutModel model;
    private string? highlightId;
    private float scale;
    private readonly bool animate;
    private readonly Forms.Timer? fade;
    private Rectangle iconRect;

    public TrayFlyout(FlyoutModel model, string? highlightId, float scale, bool invisible = false)
    {
        this.model = model; this.highlightId = highlightId; this.scale = scale;
        FormBorderStyle = Forms.FormBorderStyle.None; StartPosition = Forms.FormStartPosition.Manual; ShowInTaskbar = false; TopMost = true;
        DoubleBuffered = true; BackColor = Palette.Surface; Text = "Egoist Battery";
        ResizeRedraw = true;
        ApplySize();
        animate = System.Windows.SystemParameters.ClientAreaAnimation && !System.Windows.SystemParameters.HighContrast && !invisible;
        if (invisible) Opacity = 0;
        if (animate)
        {
            Opacity = 0;
            fade = new Forms.Timer { Interval = 15 };
            fade.Tick += (_, _) => { Opacity = Math.Min(1, Opacity + 0.2); if (Opacity >= 1) fade!.Stop(); };
        }
    }

    protected override bool ShowWithoutActivation => true;
    protected override Forms.CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= WsExToolWindow | WsExNoActivate | WsExTopmost; cp.ClassStyle |= CsDropShadow; return cp; }
    }

    public void UpdateContent(FlyoutModel next, string? highlight)
    {
        model = next; highlightId = highlight;
        var before = Size; ApplySize();
        if (before != Size && Visible) Bounds = new Rectangle(FlyoutAnchor.Place(iconRect, Size), Size);
        Invalidate();
    }

    public void ShowAt(Rectangle icon)
    {
        iconRect = icon;
        Bounds = new Rectangle(FlyoutAnchor.Place(icon, Size), Size);
        Show();
        fade?.Start();
    }

    private void ApplySize() => Size = FlyoutLayout.Measure(model, scale, MaxHeight()).Size;

    /// <summary>Предел высоты: рабочая область монитора со значком без запаса на края.</summary>
    private int MaxHeight() => iconRect.IsEmpty ? 0 : Forms.Screen.FromRectangle(iconRect).WorkingArea.Height - 20;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Скругление и тёмная рамка Windows 11; в Windows 10 вызовы безвредны.
        var dark = 1; var round = 2; var border = Palette.Rgb(Palette.LineStrong);
        DwmSetWindowAttribute(Handle, 20, ref dark, 4); DwmSetWindowAttribute(Handle, 33, ref round, 4); DwmSetWindowAttribute(Handle, 34, ref border, 4);
    }

    protected override void OnPaint(Forms.PaintEventArgs e) => Render(e.Graphics, model, scale, highlightId, MaxHeight());
    protected override void Dispose(bool disposing) { if (disposing) fade?.Dispose(); base.Dispose(disposing); }

    /// <summary>Рисует панель в растр: самопроверка и предпросмотр используют тот же код, что и живая панель.</summary>
    public static Bitmap RenderBitmap(FlyoutModel model, float scale, string? highlightId = null)
    {
        var size = FlyoutLayout.Measure(model, scale).Size;
        var bitmap = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        Render(graphics, model, scale, highlightId);
        return bitmap;
    }

    internal static void Render(Graphics g, FlyoutModel model, float scale, string? highlightId, int maxHeight = 0)
    {
        var layout = FlyoutLayout.Measure(model, scale, maxHeight);
        g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Palette.Surface);
        using var fonts = new Fonts(scale);
        using var wrap = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisWord, FormatFlags = 0 };
        using var right = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far };
        using var textBrush = new SolidBrush(Palette.Text); using var mutedBrush = new SolidBrush(Palette.Muted); using var faintBrush = new SolidBrush(Palette.Faint);
        using var linePen = new Pen(Palette.Line, Math.Max(1, scale));

        g.DrawString("Заряд устройств", fonts.Title, textBrush, layout.Pad * scale, layout.Pad * scale - 1, StringFormat.GenericTypographic);
        g.DrawString(model.Time, fonts.Data12, faintBrush, new RectangleF(0, layout.Pad * scale + 2 * scale, layout.Width * scale - layout.Pad * scale, 20 * scale), right);

        if (model.Rows.Count == 0)
        {
            g.DrawString("Подключённых устройств нет", fonts.Body, mutedBrush, layout.Pad * scale, layout.HeaderHeight * scale + 6 * scale, StringFormat.GenericTypographic);
            return;
        }
        foreach (var item in layout.Items)
        {
            var r = item.Bounds;
            if (item.Kind == FlyoutLayout.ItemKind.Caption)
            {
                g.DrawString(item.Caption!, fonts.Caps, faintBrush, r.X + 2 * scale, r.Y + 2 * scale, StringFormat.GenericTypographic);
                continue;
            }
            var row = item.Row!;
            if (row.Id == highlightId) { using var path = Rounded(Rectangle.Round(new RectangleF(r.X - 6 * scale, r.Y - 2 * scale, r.Width + 12 * scale, r.Height + 4 * scale)), 8 * scale); using var hi = new SolidBrush(Palette.Raised); g.FillPath(hi, path); }
            else if (item.Separator) g.DrawLine(linePen, r.X, r.Y - 3 * scale, r.Right, r.Y - 3 * scale);
            var tone = Palette.Tone(row.Tone);
            using var toneBrush = new SolidBrush(tone);
            g.DrawString(row.Glyph, fonts.Icon, row.Measured ? toneBrush : faintBrush, r.X, r.Y + 3 * scale, StringFormat.GenericTypographic);
            var nameBrush = row.Measured ? textBrush : mutedBrush;
            g.DrawString(row.Name, fonts.Name, nameBrush, new RectangleF(item.NameRect.X, item.NameRect.Y, item.NameRect.Width, item.NameRect.Height), wrap);
            if (row.Measured)
            {
                g.DrawString(row.Meta, fonts.Meta, mutedBrush, new RectangleF(item.MetaRect.X, item.MetaRect.Y, item.MetaRect.Width, item.MetaRect.Height), wrap);
                if (row.Charging)
                {
                    var width = g.MeasureString(row.Numeral, row.Numeral.Length > 5 ? fonts.NumeralSmall : fonts.Numeral, 400, StringFormat.GenericTypographic).Width;
                    g.DrawString("\uE945", fonts.Icon, toneBrush, item.NumeralRect.Right - width - 26 * scale, item.NumeralRect.Y + 4 * scale, StringFormat.GenericTypographic);
                }
                g.DrawString(row.Numeral, row.Numeral.Length > 5 ? fonts.NumeralSmall : fonts.Numeral, toneBrush, new RectangleF(item.NumeralRect.X, item.NumeralRect.Y, item.NumeralRect.Width, item.NumeralRect.Height), right);
                DrawRuler(g, item.RulerRect, row, tone, scale);
            }
            else
            {
                g.DrawString("нет данных", fonts.Meta, faintBrush, new RectangleF(item.NumeralRect.X, item.NumeralRect.Y, item.NumeralRect.Width, item.NumeralRect.Height), right);
            }
        }
        g.DrawLine(linePen, layout.Pad * scale, layout.FooterY * scale, (layout.Width - layout.Pad) * scale, layout.FooterY * scale);
        g.DrawString("Двойной щелчок по значку открывает окно", fonts.Meta, faintBrush, layout.Pad * scale, layout.FooterY * scale + 9 * scale, StringFormat.GenericTypographic);
    }

    private static void DrawRuler(Graphics g, RectangleF area, FlyoutRow row, Color tone, float scale)
    {
        var cells = row.Cells;
        var gap = Math.Max(1, (float)Math.Round((cells <= 6 ? 6 : 2) * scale));
        var cell = (area.Width - gap * (cells - 1)) / cells;
        using var fill = new SolidBrush(tone); using var track = new SolidBrush(Palette.Track);
        using var hatch = new HatchBrush(HatchStyle.WideUpwardDiagonal, tone, Palette.Track);
        var prior = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None;
        for (var i = 0; i < cells; i++)
        {
            var x = (float)Math.Round(area.X + i * (cell + gap));
            var w = Math.Max(1, (float)Math.Round(area.X + i * (cell + gap) + cell) - x);
            var filled = i < row.Solid; var interval = !filled && i >= row.Solid && i < row.Solid + row.Interval;
            var h = filled || interval ? area.Height : (float)Math.Round(area.Height * 0.56f);
            g.FillRectangle(filled ? fill : interval ? hatch : track, x, area.Bottom - h, w, h);
        }
        g.SmoothingMode = prior;
    }

    internal static GraphicsPath Rounded(Rectangle r, float radius)
    {
        var d = radius * 2; var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90); path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure(); return path;
    }

    /// <summary>Цвета панели: те же роли, что в окне.</summary>
    internal static class Palette
    {
        public static readonly Color Surface = Color.FromArgb(20, 24, 27), Raised = Color.FromArgb(31, 38, 42), Line = Color.FromArgb(38, 46, 51), LineStrong = Color.FromArgb(49, 58, 64);
        public static readonly Color Text = Color.FromArgb(237, 239, 238), Muted = Color.FromArgb(142, 154, 159), Faint = Color.FromArgb(122, 135, 140), Track = Color.FromArgb(40, 48, 53);
        public static int Rgb(Color c) => c.R | c.G << 8 | c.B << 16;
        public static Color Tone(BatteryTone tone) => tone switch
        {
            BatteryTone.Charging => Color.FromArgb(198, 242, 78), BatteryTone.Low => Color.FromArgb(246, 200, 121),
            BatteryTone.Critical or BatteryTone.Error => Color.FromArgb(255, 143, 134), BatteryTone.Unknown => Faint, _ => Text
        };
    }

    private sealed class Fonts : IDisposable
    {
        // Список установленных шрифтов читается один раз: каждое обращение к FontFamily.Families создаёт объекты GDI+ на каждое семейство.
        private static readonly Lazy<(string Ui, string Display, string Data, string Icons)> Families = new(() =>
        {
            using var installed = new System.Drawing.Text.InstalledFontCollection();
            var names = installed.Families.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string Pick(params string[] candidates) => candidates.FirstOrDefault(names.Contains) ?? "Segoe UI";
            return (Pick("Segoe UI Variable Text", "Segoe UI"), Pick("Segoe UI Variable Display", "Segoe UI"), Pick("Bahnschrift", "Segoe UI"), Pick("Segoe Fluent Icons", "Segoe MDL2 Assets"));
        });
        public readonly Font Title, Name, Body, Meta, Caps, Data12, Numeral, NumeralSmall, Icon;
        public Fonts(float s)
        {
            var (ui, display, data, icons) = Families.Value;
            Font F(string family, float px, FontStyle style = FontStyle.Regular) => new(family, px * s, style, GraphicsUnit.Pixel);
            Title = F(display, 16, FontStyle.Bold); Name = F(ui, 13.5f, FontStyle.Bold); Body = F(ui, 13.5f); Meta = F(ui, 12); Caps = F(data, 11); Data12 = F(data, 12);
            Numeral = F(data, 24, FontStyle.Bold); NumeralSmall = F(data, 18, FontStyle.Bold); Icon = F(icons, 20);
        }
        public void Dispose() { foreach (var f in new[] { Title, Name, Body, Meta, Caps, Data12, Numeral, NumeralSmall, Icon }) f.Dispose(); }
    }

    /// <summary>Раскладка панели: единственное место, где считаются размеры, чтобы рисование и размер формы совпадали.</summary>
    internal sealed class FlyoutLayout
    {
        public enum ItemKind { Caption, Row }
        public sealed record Item(ItemKind Kind, string? Caption, FlyoutRow? Row, RectangleF Bounds, RectangleF NameRect, RectangleF MetaRect, RectangleF NumeralRect, RectangleF RulerRect, bool Separator);
        public int Width => LogicalWidth;
        public int Pad => 18;
        public int HeaderHeight => 40;
        public List<Item> Items { get; } = [];
        public float FooterY { get; private set; }
        public Size Size { get; private set; }

        public static FlyoutLayout Measure(FlyoutModel model, float scale, int maxHeight = 0)
        {
            var layout = new FlyoutLayout();
            using var probe = new Bitmap(1, 1); using var g = Graphics.FromImage(probe);
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using var fonts = new Fonts(scale);
            using var wrap = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisWord, FormatFlags = 0 };
            var y = layout.HeaderHeight * scale; var pad = layout.Pad * scale;
            var numeralWidth = 108 * scale; var textX = pad + 38 * scale; var textRight = LogicalWidth * scale - pad - numeralWidth;
            DeviceGroup? last = null; var first = true;
            for (var index = 0; index < model.Rows.Count; index++)
            {
                var row = model.Rows[index];
                // Очень много устройств: панель не выходит за экран, остальное показывает окно.
                // Предел высоты: подвал (42) и строка «ещё N» (28) должны поместиться; без предела — 620 DIP.
                var limit = maxHeight > 0 ? maxHeight - 70 * scale - 8 * scale : 620 * scale;
                if (y + 60 * scale > limit && index > 0 || y > limit)
                {
                    layout.Items.Add(new(ItemKind.Caption, $"ещё {model.Rows.Count - index} — в окне", null, new RectangleF(pad, y + 6 * scale, 300 * scale, 18 * scale), default, default, default, default, false));
                    y += 28 * scale; break;
                }
                var group = row.Measured ? DeviceGroup.Measured : DeviceGroup.Silent;
                if (group != last && group == DeviceGroup.Silent && model.Rows.Any(x => x.Measured))
                { layout.Items.Add(new(ItemKind.Caption, DevicePresentation.GroupTitle(group).ToUpper(CultureInfo.GetCultureInfo("ru-RU")), null, new RectangleF(pad, y + 8 * scale, 300 * scale, 18 * scale), default, default, default, default, false)); y += 30 * scale; }
                last = group;
                var nameH = g.MeasureString(row.Name, fonts.Name, (int)(textRight - textX), wrap).Height;
                nameH = Math.Min(nameH, fonts.Name.Height * 2 + 2);
                if (row.Measured)
                {
                    var metaH = g.MeasureString(row.Meta, fonts.Meta, (int)(textRight - textX), wrap).Height;
                    var height = 10 * scale + nameH + 2 * scale + metaH + 10 * scale + 8 * scale + 12 * scale;
                    var bounds = new RectangleF(pad, y, LogicalWidth * scale - 2 * pad, height);
                    layout.Items.Add(new(ItemKind.Row, null, row, bounds,
                        new RectangleF(textX, y + 10 * scale, textRight - textX, nameH), new RectangleF(textX, y + 10 * scale + nameH + 2 * scale, textRight - textX, metaH),
                        new RectangleF(bounds.Right - numeralWidth, y + 10 * scale, numeralWidth, 32 * scale), new RectangleF(textX, y + height - 8 * scale - 12 * scale, bounds.Right - textX, 8 * scale), !first && last == DeviceGroup.Measured));
                    y += height + 4 * scale;
                }
                else
                {
                    var height = Math.Max(nameH, 18 * scale) + 16 * scale;
                    var bounds = new RectangleF(pad, y, LogicalWidth * scale - 2 * pad, height);
                    layout.Items.Add(new(ItemKind.Row, null, row, bounds, new RectangleF(textX, y + 8 * scale, textRight - textX, nameH), default,
                        new RectangleF(bounds.Right - numeralWidth, y + 9 * scale, numeralWidth, 18 * scale), default, false));
                    y += height;
                }
                first = false;
            }
            if (model.Rows.Count == 0) y += 36 * scale;
            layout.FooterY = (float)((y + 8 * scale) / scale);
            layout.Size = new Size((int)Math.Ceiling(LogicalWidth * scale), (int)Math.Ceiling(y + 8 * scale + 34 * scale));
            return layout;
        }
    }

    /// <summary>Положение панели рядом со значком: над панелью задач, под ней или сбоку — в зависимости от её стороны.</summary>
    internal static class FlyoutAnchor
    {
        public static Point Place(Rectangle icon, Size size)
        {
            // Сторона панели задач определяется по ближайшему краю монитора: у скрытой панели рабочая область равна всему экрану.
            var screen = Forms.Screen.FromRectangle(icon); var bounds = screen.Bounds; var area = screen.WorkingArea; const int gap = 10;
            int cx = icon.Left + icon.Width / 2, cy = icon.Top + icon.Height / 2;
            var toBottom = bounds.Bottom - cy; var toTop = cy - bounds.Top; var toLeft = cx - bounds.Left; var toRight = bounds.Right - cx;
            var nearest = Math.Min(Math.Min(toBottom, toTop), Math.Min(toLeft, toRight));
            int x, y;
            if (nearest == toBottom) { x = cx - size.Width / 2; y = icon.Top - size.Height - gap; }
            else if (nearest == toTop) { x = cx - size.Width / 2; y = icon.Bottom + gap; }
            else if (nearest == toLeft) { x = icon.Right + gap; y = cy - size.Height / 2; }
            else { x = icon.Left - size.Width - gap; y = cy - size.Height / 2; }
            return new Point(Math.Clamp(x, area.Left + 8, Math.Max(area.Left + 8, area.Right - size.Width - 8)), Math.Clamp(y, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - size.Height - 8)));
        }
    }

    /// <summary>Масштаб монитора под курсором: панель не опирается на DPI процесса.</summary>
    public static float ScaleAt(Point point)
    {
        try
        {
            var monitor = MonitorFromPoint(new POINT { X = point.X, Y = point.Y }, 2);
            return GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96f : 1f;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return 1f; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, ref int value, int size);
}
