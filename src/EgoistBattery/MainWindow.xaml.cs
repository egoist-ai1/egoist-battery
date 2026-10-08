using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using EgoistBattery.Controls;
using EgoistBattery.Core;
using EgoistBattery.Services;
using EgoistBattery.ViewModels;

namespace EgoistBattery;

public partial class MainWindow : Window
{
    internal bool PermitClose { get; set; }
    internal MainWindow(MainViewModel viewModel)
    {
        InitializeComponent(); DataContext = viewModel;
        IsVisibleChanged += (_, _) => viewModel.SetWindowVisible(IsVisible && WindowState != WindowState.Minimized);
        StateChanged += (_, _) => { viewModel.SetWindowVisible(IsVisible && WindowState != WindowState.Minimized); if (PermitClose && WindowState == WindowState.Minimized) Close(); };
        viewModel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.IsBusy)) UpdateSpin(viewModel.IsBusy); };
        SourceInitialized += (_, _) =>
        {
            if (!SystemParameters.HighContrast) TintTitleBar();
            FitWorkingArea();
        };
        Loaded += (_, _) => BuildTrayLegend();
    }

    /// <summary>Значок обновления вращается, пока идёт чтение; при отключённых эффектах Windows остаётся неподвижным.</summary>
    private void UpdateSpin(bool busy) =>
        Spin.BeginAnimation(RotateTransform.AngleProperty, busy && Motion.Enabled ? new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever } : null);

    /// <summary>Заголовок окна красится в цвет фона: Windows 11 принимает цвета заголовка, Windows 10 игнорирует вызов.</summary>
    private void TintTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var dark = 1; DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        static int Rgb(string key) { var c = ((SolidColorBrush)Application.Current.FindResource(key)).Color; return c.R | c.G << 8 | c.B << 16; }
        var caption = Rgb("Background"); var text = Rgb("Text"); var border = Rgb("Line");
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int)); DwmSetWindowAttribute(handle, 36, ref text, sizeof(int)); DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
    }

    private void FitWorkingArea()
    {
        var bounds = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        Width = Math.Min(Width, Math.Max(MinWidth, bounds.Width / dpi.DpiScaleX - 24));
        Height = Math.Min(Height, Math.Max(MinHeight, bounds.Height / dpi.DpiScaleY - 24));
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!PermitClose) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        FitWorkingArea();
    }

    /// <summary>Образцы значков в настройках рисует настоящий отрисовщик трея, поэтому они не расходятся с реальными.</summary>
    private void BuildTrayLegend()
    {
        if (TrayLegend.Children.Count > 0) return;
        foreach (var (value, percent, tone, caption) in new (string, int?, BatteryTone, string)[]
        {
            ("90", 90, BatteryTone.Normal, "Заряд устройства"), ("10+", 10, BatteryTone.Charging, "Идёт зарядка; «+» — интервал 10–19%"),
            ("15", 15, BatteryTone.Low, "Ниже порога предупреждения"), ("?", null, BatteryTone.Unknown, "Процент недоступен")
        })
        {
            using var icon = TrayController.DrawIcon(value, percent, tone, false, 64);
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            var image = new System.Windows.Controls.Image { Source = source, Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Width = 236, Margin = new Thickness(0, 0, 8, 12) };
            panel.Children.Add(image);
            panel.Children.Add(new TextBlock { Text = caption, Style = (Style)FindResource("Meta"), TextWrapping = TextWrapping.Wrap, MaxWidth = 170, VerticalAlignment = VerticalAlignment.Center });
            TrayLegend.Children.Add(panel);
        }
    }

    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "Заряд устройств.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(((MainViewModel)DataContext).Snapshots, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(this, "Не удалось сохранить файл. Выберите доступную папку.", "Сохранение сведений", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, ref int value, int size);
}
