using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EgoistBattery.Core;

namespace EgoistBattery.Controls;

/// <summary>
/// Шкала заряда в виде линейки с делениями. Сплошные деления — гарантированный уровень,
/// штриховка — остаток интервала (устройство сообщило ступень, а не точный процент).
/// Деления заполняются по одному, как механический счётчик.
/// </summary>
internal sealed class LevelRuler : FrameworkElement
{
    public static readonly DependencyProperty SolidProperty = DependencyProperty.Register(nameof(Solid), typeof(int), typeof(LevelRuler), new PropertyMetadata(0, OnTargetChanged));
    public static readonly DependencyProperty IntervalProperty = DependencyProperty.Register(nameof(Interval), typeof(int), typeof(LevelRuler), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CellsProperty = DependencyProperty.Register(nameof(Cells), typeof(int), typeof(LevelRuler), new FrameworkPropertyMetadata(DevicePresentation.RulerCells, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(LevelRuler), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(LevelRuler), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SpectrumProperty = DependencyProperty.Register(nameof(Spectrum), typeof(bool), typeof(LevelRuler), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DelayProperty = DependencyProperty.Register(nameof(Delay), typeof(int), typeof(LevelRuler), new PropertyMetadata(0));
    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register("Shown", typeof(double), typeof(LevelRuler), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private bool revealed;

    public int Solid { get => (int)GetValue(SolidProperty); set => SetValue(SolidProperty, value); }
    public int Interval { get => (int)GetValue(IntervalProperty); set => SetValue(IntervalProperty, value); }
    public int Cells { get => (int)GetValue(CellsProperty); set => SetValue(CellsProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    /// <summary>Каждое деление окрашено по своему месту на шкале: красный → жёлтый → лайм.</summary>
    public bool Spectrum { get => (bool)GetValue(SpectrumProperty); set => SetValue(SpectrumProperty, value); }
    public int Delay { get => (int)GetValue(DelayProperty); set => SetValue(DelayProperty, value); }

    public LevelRuler()
    {
        SnapsToDevicePixels = true; Focusable = false; IsHitTestVisible = false;
        Loaded += (_, _) => { if (!revealed) { revealed = true; Animate(true); } };
    }

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ruler = (LevelRuler)d;
        if (ruler.revealed) ruler.Animate(false);
    }

    private void Animate(bool first)
    {
        var target = (double)Solid;
        if (!Motion.Enabled || !IsLoaded) { BeginAnimation(ShownProperty, null); SetValue(ShownProperty, target); return; }
        var from = first ? 0 : (double)GetValue(ShownProperty);
        var time = TimeSpan.FromMilliseconds(Math.Clamp(Math.Abs(target - from) * 34, 240, 1100));
        var animation = new DoubleAnimation(from, target, time) { EasingFunction = Motion.OutExpo, FillBehavior = FillBehavior.HoldEnd };
        if (first) { animation.BeginTime = TimeSpan.FromMilliseconds(Delay + 120); SetValue(ShownProperty, 0d); }
        BeginAnimation(ShownProperty, animation);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width, double.IsNaN(Height) ? 20 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        var count = Math.Max(1, Cells);
        var ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var gap = Math.Max(1, Math.Round((count <= 6 ? 6 : 2.5) * ppd)) / ppd;
        var width = ActualWidth; var height = ActualHeight;
        var cell = Math.Max(1 / ppd, (width - gap * (count - 1)) / count);
        var shown = (int)Math.Floor((double)GetValue(ShownProperty) + 0.5);
        var solid = Solid;
        var minorHeight = Math.Round(height * 0.56 * ppd) / ppd;
        for (var i = 0; i < count; i++)
        {
            var x = Math.Round(i * (cell + gap) * ppd) / ppd;
            var right = Math.Round((i * (cell + gap) + cell) * ppd) / ppd;
            var filled = i < shown;
            var interval = !filled && i >= solid && i < solid + Interval && shown >= solid;
            var h = filled || interval ? height : minorHeight;
            var color = Spectrum ? CellBrush(i, count) : Fill;
            dc.DrawRectangle(filled ? color : interval ? Hatch(color) : Track, null, new Rect(x, height - h, Math.Max(1 / ppd, right - x), h));
        }
    }

    private static readonly Dictionary<(int, int), SolidColorBrush> CellBrushes = [];
    private static SolidColorBrush CellBrush(int index, int count)
    {
        if (CellBrushes.TryGetValue((count, index), out var cached)) return cached;
        var (r, g, b) = BatterySpectrum.At((index + 0.5) / count);
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze();
        return CellBrushes[(count, index)] = brush;
    }

    private readonly Dictionary<Brush, Brush> hatches = [];
    private Brush Hatch(Brush fill)
    {
        if (fill is not SolidColorBrush solid) return fill;
        if (hatches.TryGetValue(fill, out var cached)) return cached;
        var tile = new DrawingGroup();
        tile.Children.Add(new GeometryDrawing(Track, null, new RectangleGeometry(new Rect(0, 0, 4, 4))));
        tile.Children.Add(new GeometryDrawing(null, new System.Windows.Media.Pen(solid, 1.2), Geometry.Parse("M -1,5 L 5,-1 M -1,1 L 1,-1 M 3,5 L 5,3")));
        var brush = new DrawingBrush(tile) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 4, 4), ViewportUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 4, 4), ViewboxUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return hatches[fill] = brush;
    }
}
