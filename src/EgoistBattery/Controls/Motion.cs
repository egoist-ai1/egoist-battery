using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EgoistBattery.Controls;

/// <summary>
/// Движение окна: появление блоков и общий выключатель. Если в Windows отключены эффекты анимации
/// («Параметры → Специальные возможности → Визуальные эффекты»), все блоки сразу в итоговом виде.
/// </summary>
internal static class Motion
{
    /// <summary>Самопроверка снимает кадры итогового вида: на время съёмки движение выключено.</summary>
    public static bool Suspended { get; set; }

    public static bool Enabled => !Suspended && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    public static readonly IEasingFunction OutExpo = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
    public static readonly IEasingFunction OutCubic = new CubicEase { EasingMode = EasingMode.EaseOut };

    public static readonly DependencyProperty RevealProperty = DependencyProperty.RegisterAttached("Reveal", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnRevealChanged));
    public static bool GetReveal(DependencyObject d) => (bool)d.GetValue(RevealProperty);
    public static void SetReveal(DependencyObject d, bool value) => d.SetValue(RevealProperty, value);

    /// <summary>Задержка появления в миллисекундах: ступенчатый вход строк списка.</summary>
    public static readonly DependencyProperty DelayProperty = DependencyProperty.RegisterAttached("Delay", typeof(int), typeof(Motion), new PropertyMetadata(0));
    public static int GetDelay(DependencyObject d) => (int)d.GetValue(DelayProperty);
    public static void SetDelay(DependencyObject d, int value) => d.SetValue(DelayProperty, value);

    private static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached("Shown", typeof(bool), typeof(Motion), new PropertyMetadata(false));

    private static void OnRevealChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.Loaded -= OnLoaded; element.IsVisibleChanged -= OnVisibleChanged;
        if (!(bool)e.NewValue) return;
        element.Loaded += OnLoaded; element.IsVisibleChanged += OnVisibleChanged;
        if (element.IsLoaded) Play(element);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Play((FrameworkElement)sender);

    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (!element.IsVisible)
        {
            // Перестановка строк (Move) кратко скрывает и возвращает элемент: сбрасываем признак только если он так и остался скрытым.
            element.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { if (!element.IsVisible) element.SetValue(ShownProperty, false); });
        }
        else if (element.IsLoaded) Play(element);
    }

    private static void Play(FrameworkElement element)
    {
        if ((bool)element.GetValue(ShownProperty)) return;
        element.SetValue(ShownProperty, true);
        if (!Enabled) return;
        var delay = TimeSpan.FromMilliseconds(GetDelay(element));
        var shift = new TranslateTransform(0, 10);
        element.RenderTransform = shift;
        // Ждущий своей очереди блок скрыт: иначе он мигнёт итоговым видом до начала анимации.
        element.Opacity = 0;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)) { BeginTime = delay, EasingFunction = OutCubic, FillBehavior = FillBehavior.HoldEnd };
        fade.Completed += (_, _) => { if (element.RenderTransform != shift) return; element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = 1; };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        var slide = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(520)) { BeginTime = delay, EasingFunction = OutExpo, FillBehavior = FillBehavior.HoldEnd };
        slide.Completed += (_, _) => { if (element.RenderTransform == shift) element.RenderTransform = Transform.Identity; };
        shift.BeginAnimation(TranslateTransform.YProperty, slide);
    }
}
