using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using EgoistBattery.ViewModels;

namespace EgoistBattery;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(420d));
    public double CardWidth { get => (double)GetValue(CardWidthProperty); private set => SetValue(CardWidthProperty, value); }
    public static readonly DependencyProperty CompactLayoutProperty = DependencyProperty.Register(nameof(CompactLayout), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public bool CompactLayout { get => (bool)GetValue(CompactLayoutProperty); private set => SetValue(CompactLayoutProperty, value); }
    internal bool PermitClose { get; set; }
    internal MainWindow(MainViewModel viewModel)
    {
        InitializeComponent(); DataContext = viewModel;
        IsVisibleChanged += (_, _) => viewModel.SetWindowVisible(IsVisible && WindowState != WindowState.Minimized);
        StateChanged += (_, _) => { viewModel.SetWindowVisible(IsVisible && WindowState != WindowState.Minimized); if (PermitClose && WindowState == WindowState.Minimized) Close(); };
        SizeChanged += (_, _) => CompactLayout = ActualHeight < 680;
        SourceInitialized += (_, _) =>
        {
            if (!SystemParameters.HighContrast) { var enabled = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int)); }
            FitWorkingArea();
        };
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
    private void DeviceViewportSizeChanged(object sender, SizeChangedEventArgs e) => UpdateCardWidth();
    private void DeviceViewportScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    { if (e.ViewportWidthChange != 0) UpdateCardWidth(); }
    private void UpdateCardWidth()
    {
        if (DeviceViewport is null) return;
        var width = DeviceViewport.ViewportWidth > 0 ? DeviceViewport.ViewportWidth : DeviceViewport.ActualWidth - 20;
        if (width < 100) return;
        var columns = Math.Clamp((int)(width / 385), 1, 3);
        var target = Math.Max(280, Math.Floor(width / columns) - 16);
        if (Math.Abs(CardWidth - target) > 0.5) CardWidth = target;
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
