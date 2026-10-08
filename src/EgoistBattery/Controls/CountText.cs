using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace EgoistBattery.Controls;

/// <summary>
/// Число заряда, которое набегает до значения: при появлении с нуля, при изменении от прежнего.
/// Для устройств без точного процента показывает готовую подпись (интервал, категория, прочерк).
/// </summary>
internal sealed class CountText : TextBlock
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double?), typeof(CountText), new PropertyMetadata(null, OnValueChanged));
    public static readonly DependencyProperty FallbackProperty = DependencyProperty.Register(nameof(Fallback), typeof(string), typeof(CountText), new PropertyMetadata("", (d, _) => ((CountText)d).Refresh()));
    public static readonly DependencyProperty DelayProperty = DependencyProperty.Register(nameof(Delay), typeof(int), typeof(CountText), new PropertyMetadata(0));
    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register("Shown", typeof(double), typeof(CountText), new PropertyMetadata(0d, (d, _) => ((CountText)d).Refresh()));
    private bool revealed;

    public double? Value { get => (double?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Fallback { get => (string)GetValue(FallbackProperty); set => SetValue(FallbackProperty, value); }
    public int Delay { get => (int)GetValue(DelayProperty); set => SetValue(DelayProperty, value); }

    public CountText() => Loaded += (_, _) => { if (!revealed) { revealed = true; Animate(true); } };

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var text = (CountText)d;
        if (text.revealed) text.Animate(false); else text.Refresh();
    }

    private void Animate(bool first)
    {
        if (Value is not { } target) { BeginAnimation(ShownProperty, null); Refresh(); return; }
        if (!Motion.Enabled || !IsLoaded) { BeginAnimation(ShownProperty, null); SetValue(ShownProperty, target); Refresh(); return; }
        var from = first ? 0 : (double)GetValue(ShownProperty);
        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(Math.Clamp(Math.Abs(target - from) * 12, 260, 900))) { EasingFunction = Motion.OutExpo, FillBehavior = FillBehavior.HoldEnd };
        if (first) { animation.BeginTime = TimeSpan.FromMilliseconds(Delay + 120); SetValue(ShownProperty, 0d); }
        BeginAnimation(ShownProperty, animation);
    }

    private void Refresh() => Text = Value is null ? Fallback : $"{Math.Round((double)GetValue(ShownProperty)):0}%";
}
