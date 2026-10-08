using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Diagnostics;
using EgoistBattery.Core;
using EgoistBattery.Services;
using EgoistBattery.ViewModels;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace EgoistBattery;

public partial class App : Application
{
    private readonly CancellationTokenSource lifetime = new();
    private Mutex? instance;
    private EventWaitHandle? activateEvent;
    private EventWaitHandle? exitEvent;
    private RegisteredWaitHandle? activationWait;
    private RegisteredWaitHandle? exitWait;
    private EventWaitHandle? refreshEvent, settingsEvent;
    private RegisteredWaitHandle? refreshWait, settingsWait;
    private Process? windowHost, backgroundParent;
    private bool windowProcess;
    private MainWindow? window;
    private MainViewModel? viewModel;
    private SettingsStore? store;
    private TrayController? tray;
    private DispatcherTimer? timer, changeTimer;
    private DeviceEventWindow? deviceEvents;
    private DeviceWatcher? classicWatcher, bleWatcher;
    private bool stopping;

    public App()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("ru-RU")));
        DispatcherUnhandledException += (_, e) => { store?.Log(e.Exception); Shutdown(1); e.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, e) => { store?.Log(e.Exception); e.SetObserved(); };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var dataDirectory = Argument(e.Args, "--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EgoistBattery");
        store = new(dataDirectory);
        try
        {
            if (e.Args.Contains("--exit"))
            {
                try { using var signal = EventWaitHandle.OpenExisting("Local\\EgoistBattery-exit-v1"); signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
                Shutdown(); return;
            }
            if (e.Args.Contains("--probe") || e.Args.Contains("--self-test") || e.Args.Contains("--ui-test"))
            { await RunCheckAsync(e.Args); Shutdown(0); return; }
            if (e.Args.Contains("--window")) { await RunWindowHostAsync(e.Args); return; }
            instance = new Mutex(true, "Local\\EgoistBattery-v1", out var created);
            activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\EgoistBattery-activate-v1");
            if (!created) { if (!e.Args.Contains("--tray")) activateEvent.Set(); instance.Dispose(); instance = null; Shutdown(); return; }
            ApplyAccessibilityColors();
            viewModel = new(new DeviceMonitor(), store, lifetime.Token);
            // Фоновый старт не создаёт WPF-окно, карточки и дерево настроек.
            deviceEvents = new(ScheduleRefresh);
            tray = new(ShowWindow, () => Dispatcher.BeginInvoke(async () => await viewModel.RefreshAsync()), () => Dispatcher.BeginInvoke(ExitApplication));
            viewModel.Updated += devices =>
            {
                tray.Update(devices, viewModel.HiddenTrayDevices, viewModel.Notifications, viewModel.LowThreshold);
                try { store.WriteJson("tray-state.json", new { UpdatedAt = DateTimeOffset.Now, DeviceIconCount = tray.DeviceIconCount, WindowCreated = window is not null, BackgroundIntervalSeconds = viewModel.EffectiveRefreshSeconds, CardCount = viewModel.Cards.Count, Devices = TrayPolicy.VisibleDevices(devices, viewModel.HiddenTrayDevices).Select(x => new { x.Name, Level = x.Reading.Label }) }); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { store.Log(ex); }
            };
            activationWait = ThreadPool.RegisterWaitForSingleObject(activateEvent, (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, Timeout.Infinite, false);
            exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\EgoistBattery-exit-v1");
            exitWait = ThreadPool.RegisterWaitForSingleObject(exitEvent, (_, _) => Dispatcher.BeginInvoke(ExitApplication), null, Timeout.Infinite, false);
            refreshEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\EgoistBattery-refresh-v1");
            refreshWait = ThreadPool.RegisterWaitForSingleObject(refreshEvent, (_, _) => Dispatcher.BeginInvoke(async () =>
            { if (stopping) return; viewModel.ReloadSettings(); viewModel.ForceSnapshotSave(); await viewModel.RefreshAsync(); ResetPollingTimer(); }), null, Timeout.Infinite, false);
            settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\EgoistBattery-settings-v1");
            settingsWait = ThreadPool.RegisterWaitForSingleObject(settingsEvent, (_, _) => Dispatcher.BeginInvoke(() =>
            { if (!stopping) viewModel.ReloadSettings(); }), null, Timeout.Infinite, false);
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(viewModel.EffectiveRefreshSeconds) };
            timer.Tick += async (_, _) => { timer.Stop(); await viewModel.RefreshAsync(); ResetPollingTimer(); };
            viewModel.PollingIntervalChanged += ResetPollingTimer;
            changeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            changeTimer.Tick += async (_, _) => { changeTimer.Stop(); await viewModel.RefreshAsync(); };
            StartWatchers();
            SystemEvents.PowerModeChanged += OnPowerChanged;
            if (!e.Args.Contains("--tray")) ShowWindow();
            await viewModel.RefreshAsync();
            ResetPollingTimer();
        }
        catch (Exception ex)
        {
            store.Log(ex);
            if (e.Args.Any(x => x is "--probe" or "--self-test" or "--ui-test"))
            {
                try { store.WriteJson("check-error.json", new { Type = ex.GetType().FullName, ex.Message, ex.StackTrace }); } catch (Exception) { }
            }
            Shutdown(1);
        }
    }

    private async Task RunWindowHostAsync(string[] args)
    {
        if (store is null || !int.TryParse(Argument(args, "--background-pid"), out var parentId)) throw new InvalidOperationException("Не указан фоновый процесс.");
        windowProcess = true;
        backgroundParent = Process.GetProcessById(parentId);
        if (backgroundParent.HasExited) { Shutdown(); return; }
        instance = new Mutex(true, "Local\\EgoistBattery-window-v1", out var created);
        activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\EgoistBattery-window-activate-v1");
        if (!created) { if (!args.Contains("--window-check")) activateEvent.Set(); instance.Dispose(); instance = null; Shutdown(); return; }
        backgroundParent.EnableRaisingEvents = true;
        backgroundParent.Exited += (_, _) => { if (!stopping) Dispatcher.BeginInvoke(ExitApplication); };
        ApplyAccessibilityColors(); RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        viewModel = new(new SnapshotDeviceSource(store, () => Signal("Local\\EgoistBattery-refresh-v1")), store, lifetime.Token);
        viewModel.SettingsChanged += () => Signal("Local\\EgoistBattery-settings-v1");
        window = new(viewModel) { PermitClose = true }; MainWindow = window;
        window.Closed += (_, _) => { if (!stopping) { stopping = true; lifetime.Cancel(); Shutdown(); } };
        activationWait = ThreadPool.RegisterWaitForSingleObject(activateEvent, (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, Timeout.Infinite, false);
        exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\EgoistBattery-window-exit-v1");
        exitWait = ThreadPool.RegisterWaitForSingleObject(exitEvent, (_, _) => Dispatcher.BeginInvoke(ExitApplication), null, Timeout.Infinite, false);
        if (args.Contains("--window-check")) { window.Opacity = 0; window.ShowActivated = false; window.ShowInTaskbar = false; }
        window.Show();
        await viewModel.RefreshAsync();
        if (args.Contains("--window-check"))
        {
            if (viewModel.IsBusy || viewModel.Notice.Length > 0) throw new InvalidOperationException("Окно не получило актуальный снимок из фонового процесса.");
            store.WriteJson("window-process-check.json", new { Success = true, ParentId = parentId, WindowProcessId = Environment.ProcessId, HardwarePollingInWindow = false, DeviceCount = viewModel.Snapshots.Count, Finished = DateTimeOffset.Now });
            ExitApplication(); return;
        }
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(viewModel.EffectiveRefreshSeconds) };
        timer.Tick += async (_, _) => { timer.Stop(); await viewModel.RefreshAsync(); ResetPollingTimer(); };
        viewModel.PollingIntervalChanged += ResetPollingTimer;
        ResetPollingTimer();
    }
    private static void Signal(string name)
    {
        try { using var signal = EventWaitHandle.OpenExisting(name); signal.Set(); }
        catch (WaitHandleCannotBeOpenedException) { }
    }

    private async Task RunCheckAsync(string[] args)
    {
        if (store is null) throw new InvalidOperationException();
        var monitor = new DeviceMonitor();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var results = await monitor.ScanAsync(budget.Token);
        var devices = SnapshotMerger.Merge(results.SelectMany(x => x.Devices));
        var output = Argument(args, "--output") ?? Path.Combine(store.DirectoryPath, "check-result.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { ObservedAt = DateTimeOffset.Now, Providers = results, Devices = devices }, new JsonSerializerOptions { WriteIndented = true }));
        if (args.Contains("--probe")) return;
        ApplyAccessibilityColors();
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var vm = new MainViewModel(monitor, store, lifetime.Token);
        if (vm.EffectiveRefreshSeconds < 60 || vm.Cards.Count != 0) throw new InvalidOperationException("Фоновый режим создаёт интерфейс или слишком часто опрашивает устройства.");
        var testWindow = new MainWindow(vm) { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, PermitClose = true };
        testWindow.Show();
        try
        {
            await vm.RefreshAsync();
            if (vm.IsBusy || vm.Notice.Contains("не удалось", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Опрос не завершён успешно.");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            testWindow.UpdateLayout();
            if (args.Contains("--ui-test"))
            {
                using var trayCheck = new TrayController(() => { }, () => { }, () => { }, true);
                trayCheck.Update(devices, [], false, 20);
                if (trayCheck.DeviceIconCount != TrayPolicy.VisibleDevices(devices).Count) throw new InvalidOperationException("Не созданы отдельные значки устройств.");
                trayCheck.Update([], [], false, 20);
                if (trayCheck.DeviceIconCount != 0) throw new InvalidOperationException("Значок отключённого устройства остался в трее.");
                trayCheck.Update(devices, [], false, 20);
                if (trayCheck.DeviceIconCount != TrayPolicy.VisibleDevices(devices).Count) throw new InvalidOperationException("Значки не восстановились после подключения.");
                foreach (var value in new[] { "10", "10+", "20", "90", "100", "?", "battery" })
                {
                    using var trayIcon = TrayController.DrawIcon(value, int.TryParse(value.TrimEnd('+'), out var level) ? level : null, ChargeState.Discharging, pixelSize: 64);
                    using var trayBitmap = trayIcon.ToBitmap(); trayBitmap.Save(Path.Combine(store.DirectoryPath, $"tray-{value.Replace("?", "unknown")}.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                SaveTrayPreview(store.DirectoryPath);
                SavePreview(testWindow.RootContent, Path.Combine(store.DirectoryPath, "devices.png"));
                vm.Page = "settings";
                testWindow.UpdateLayout();
                var intervalSlider = VisualDescendants(testWindow.RootContent).OfType<Slider>().First(x => x.Maximum == 120);
                var originalInterval = vm.RefreshSeconds;
                Slider.IncreaseLarge.Execute(null, intervalSlider);
                if (vm.RefreshSeconds <= originalInterval) throw new InvalidOperationException("Ползунок интервала не изменяет настройки.");
                intervalSlider.Value = originalInterval;
                SavePreview(testWindow.RootContent, Path.Combine(store.DirectoryPath, "settings.png"));
                vm.Page = "devices"; vm.Query = "__no_such_device__"; testWindow.UpdateLayout();
                if (!vm.Empty) throw new InvalidOperationException("Не работает поиск устройств.");
                SavePreview(testWindow.RootContent, Path.Combine(store.DirectoryPath, "empty.png"));
                vm.Query = ""; vm.Filter = "all"; testWindow.Width = 890; testWindow.UpdateLayout();
                SavePreview(testWindow.RootContent, Path.Combine(store.DirectoryPath, "compact.png"));
                var layouts = new List<object>();
                testWindow.WindowStyle = WindowStyle.None;
                testWindow.ResizeMode = ResizeMode.NoResize;
                foreach (var scale in new[] { 1d, 1.25, 1.5, 2d })
                {
                    testWindow.Width = 1920 / scale; testWindow.Height = 1080 / scale;
                    vm.Filter = "connected";
                    foreach (var page in new[] { "devices", "settings" })
                    {
                        vm.Page = page;
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        testWindow.UpdateLayout();
                        AssertLayout(testWindow.RootContent);
                        SavePreview(testWindow.RootContent, Path.Combine(store.DirectoryPath, $"{page}-fullhd-{scale * 100:0}.png"), scale);
                        layouts.Add(new { Page = page, Scale = scale, Width = testWindow.RootContent.ActualWidth, Height = testWindow.RootContent.ActualHeight });
                    }
                }
                store.WriteJson("layout-check.json", new { Success = true, Cases = layouts, Checks = "Видимые тексты и кнопки: горизонтальные границы, отсутствие обрезания текста; 1920×1080 при масштабе 100/125/150/200%" });
            }
            testWindow.Hide();
            if (vm.EffectiveRefreshSeconds < 60) throw new InvalidOperationException("После скрытия окна не включился экономный режим.");
            testWindow.Show();
            if (vm.EffectiveRefreshSeconds != vm.RefreshSeconds) throw new InvalidOperationException("Не восстановлен интервал открытого окна.");
            store.WriteJson("ui-check.json", new { Success = true, DeviceCount = devices.Count, Sources = results.Select(x => new { x.Name, x.Error }), Ui = "XAML, текущие устройства, настройки, поиск, узкое окно, Full HD с масштабами 100/125/150/200%, переходы фон/окно", Tray = "Отдельные значки, исчезновение при отключении, восстановление при подключении, отрисовка 10/10+/20/90/100/?/батарея", Finished = DateTimeOffset.Now });
        }
        finally { testWindow.Close(); }
    }
    private static void AssertLayout(FrameworkElement root)
    {
        foreach (var element in VisualDescendants(root).OfType<FrameworkElement>())
        {
            if (!element.IsVisible || element.ActualWidth < 1 || element is not (TextBlock or System.Windows.Controls.Button or System.Windows.Controls.TextBox)) continue;
            var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(new Size(element.ActualWidth, element.ActualHeight)));
            // Вертикальные границы не проверяем: прокрутка намеренно скрывает нижние карточки.
            if (bounds.Left < -2 || bounds.Right > root.ActualWidth + 2)
                throw new InvalidOperationException($"Элемент выходит за ширину окна: {element.GetType().Name} {element.Name}.");
            if (element is TextBlock text && text.Text.Length > 0 && text.TextWrapping == TextWrapping.NoWrap && text.Inlines.Count <= 1)
            {
                var formatted = new FormattedText(text.Text, CultureInfo.GetCultureInfo("ru-RU"), text.FlowDirection,
                    new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground,
                    null, TextFormattingMode.Display, VisualTreeHelper.GetDpi(text).PixelsPerDip);
                if (formatted.Width > text.ActualWidth + 3)
                    throw new InvalidOperationException($"Текст обрезан: {text.Text}; нужно {formatted.Width:0.0}, доступно {text.ActualWidth:0.0}.");
            }
        }
    }
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }
    private static void SavePreview(FrameworkElement content, string path, double scale = 1)
    {
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Round(content.ActualWidth * scale)), Math.Max(1, (int)Math.Round(content.ActualHeight * scale)), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void SaveTrayPreview(string directory)
    {
        using var bitmap = new System.Drawing.Bitmap(540, 144);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.FromArgb(23, 28, 31));
        using var font = new System.Drawing.Font("Segoe UI", 12, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
        using var text = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(190, 206, 200));
        var labels = new[] { "DualSense", "G304", "100%", "нет данных" };
        var values = new[] { "10+", "90", "100", "?" };
        for (var i = 0; i < values.Length; i++)
        {
            using var icon = TrayController.DrawIcon(values[i], i == 3 ? null : int.Parse(values[i].TrimEnd('+')), i == 0 ? ChargeState.Charging : ChargeState.Discharging, pixelSize: 40);
            graphics.DrawIcon(icon, new System.Drawing.Rectangle(28 + i * 130, 18, 40, 40));
            using var smallIcon = TrayController.DrawIcon(values[i], i == 3 ? null : int.Parse(values[i].TrimEnd('+')), i == 0 ? ChargeState.Charging : ChargeState.Discharging, pixelSize: 24);
            graphics.DrawIcon(smallIcon, new System.Drawing.Rectangle(32 + i * 130, 73, 24, 24));
            graphics.DrawString(labels[i], font, text, 22 + i * 130, 112);
        }
        bitmap.Save(Path.Combine(directory, "tray-preview.png"), System.Drawing.Imaging.ImageFormat.Png);
    }
    private void StartWatchers()
    {
        try
        {
            classicWatcher = DeviceInformation.CreateWatcher(BluetoothDevice.GetDeviceSelector(), ["System.Devices.Aep.IsConnected"]);
            bleWatcher = DeviceInformation.CreateWatcher(BluetoothLEDevice.GetDeviceSelector(), ["System.Devices.Aep.IsConnected"]);
            foreach (var watcher in new[] { classicWatcher, bleWatcher })
            {
                watcher.Updated += (_, _) => ScheduleRefresh(); watcher.Added += (_, _) => ScheduleRefresh(); watcher.Removed += (_, _) => ScheduleRefresh(); watcher.Start();
            }
        }
        catch (Exception ex) { store?.Log(ex); }
    }
    private void ScheduleRefresh() => Dispatcher.BeginInvoke(() => { if (stopping) return; changeTimer?.Stop(); changeTimer?.Start(); });
    private void OnPowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) ScheduleRefresh(); }
    private void ResetPollingTimer()
    {
        if (stopping || timer is null || viewModel is null) return;
        timer.Stop(); timer.Interval = TimeSpan.FromSeconds(viewModel.EffectiveRefreshSeconds); timer.Start();
    }
    private void ShowWindow()
    {
        if (stopping || viewModel is null) return;
        if (windowProcess)
        {
            window?.Show(); if (window?.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            _ = viewModel.RefreshAsync(); return;
        }
        try
        {
            if (windowHost is { HasExited: false }) { Signal("Local\\EgoistBattery-window-activate-v1"); return; }
            windowHost?.Dispose();
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var value in new[] { "--window", "--data-dir", store!.DirectoryPath, "--background-pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture) }) start.ArgumentList.Add(value);
            windowHost = Process.Start(start);
        }
        catch (Exception ex) { store?.Log(ex); }
    }
    private void ExitApplication() { if (stopping) return; stopping = true; lifetime.Cancel(); if (window is not null) { window.PermitClose = true; window.Close(); } Shutdown(); }
    private void ApplyAccessibilityColors()
    {
        if (!SystemParameters.HighContrast) return;
        foreach (var key in new[] { "Background", "Surface", "Raised" }) Resources[key] = SystemColors.WindowBrush;
        foreach (var key in new[] { "Text", "Muted", "Accent" }) Resources[key] = SystemColors.WindowTextBrush;
        Resources["Line"] = SystemColors.WindowTextBrush;
    }
    protected override void OnExit(ExitEventArgs e)
    {
        stopping = true; lifetime.Cancel(); timer?.Stop(); changeTimer?.Stop();
        foreach (var watcher in new[] { classicWatcher, bleWatcher })
        { try { if (watcher?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop(); } catch (Exception ex) { store?.Log(ex); } }
        SystemEvents.PowerModeChanged -= OnPowerChanged;
        activationWait?.Unregister(null); activateEvent?.Dispose(); exitWait?.Unregister(null); exitEvent?.Dispose(); tray?.Dispose(); deviceEvents?.Dispose();
        refreshWait?.Unregister(null); refreshEvent?.Dispose(); settingsWait?.Unregister(null); settingsEvent?.Dispose();
        if (!windowProcess) Signal("Local\\EgoistBattery-window-exit-v1");
        windowHost?.Dispose(); backgroundParent?.Dispose();
        instance?.ReleaseMutex(); instance?.Dispose(); lifetime.Dispose();
        base.OnExit(e);
    }
    private static string? Argument(string[] args, string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
}
