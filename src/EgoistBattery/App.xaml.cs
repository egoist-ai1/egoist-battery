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
        System.Windows.Forms.Application.ThreadException += (_, e) => store?.Log(e.Exception);
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
            if (e.Args.Contains("--tray-hover-test")) { await RunTrayHoverCheckAsync(e.Args); Shutdown(0); return; }
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
                try { store.WriteJson("tray-state.json", new { UpdatedAt = DateTimeOffset.Now, DeviceIconCount = tray.DeviceIconCount, WindowCreated = window is not null, BackgroundIntervalSeconds = viewModel.EffectiveRefreshSeconds, CardCount = viewModel.DeviceRowCount, Devices = TrayPolicy.VisibleDevices(devices, viewModel.HiddenTrayDevices).Select(x => new { x.Name, Level = x.Reading.Label }) }); }
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
        var fixture = Argument(args, "--fixture");
        IDeviceSource monitor = fixture is null ? new DeviceMonitor() : new FixtureDeviceSource(fixture);
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
        Controls.Motion.Suspended = true;
        if (vm.EffectiveRefreshSeconds < 60 || vm.DeviceRowCount != 0) throw new InvalidOperationException("Фоновый режим создаёт интерфейс или слишком часто опрашивает устройства.");
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
                    using var trayIcon = TrayController.DrawIcon(value, int.TryParse(value.TrimEnd('+'), out var level) ? level : null, value == "10+" ? BatteryTone.Charging : value == "10" ? BatteryTone.Critical : value == "20" ? BatteryTone.Low : value is "?" or "battery" ? BatteryTone.Unknown : BatteryTone.Normal, pixelSize: 64);
                    using var trayBitmap = trayIcon.ToBitmap(); trayBitmap.Save(Path.Combine(store.DirectoryPath, $"tray-{value.Replace("?", "unknown")}.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                SaveTrayPreview(store.DirectoryPath);
                if (!TrayController.CanLocateIcons) throw new InvalidOperationException("В этой версии .NET у NotifyIcon нет полей для определения положения значка; панель трея работает по запасному способу.");
                trayCheck.CheckFlyout(TrayPolicy.VisibleDevices(devices).FirstOrDefault()?.Id);
                foreach (var flyoutScale in new[] { 1f, 1.5f, 2f })
                {
                    using var preview = trayCheck.RenderFlyoutPreview(flyoutScale, TrayPolicy.VisibleDevices(devices).FirstOrDefault()?.Id);
                    preview.Save(Path.Combine(store.DirectoryPath, $"tray-flyout-{flyoutScale * 100:0}.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                await Settle(); SavePreview(testWindow.RootContent, Path.Combine(store.DirectoryPath, "devices.png"));
                vm.Page = "settings";
                testWindow.UpdateLayout();
                var firstRow = vm.Items.OfType<DeviceRow>().FirstOrDefault();
                if (firstRow is not null)
                {
                    firstRow.IsExpanded = true;
                    vm.ForceSnapshotSave(); await vm.RefreshAsync();
                    if (!ReferenceEquals(vm.Items.OfType<DeviceRow>().FirstOrDefault(x => x.Id == firstRow.Id), firstRow) || !firstRow.IsExpanded) throw new InvalidOperationException("Обновление списка сбросило раскрытую строку.");
                    firstRow.IsExpanded = false;
                }
                var intervalSlider = VisualDescendants(testWindow.RootContent).OfType<Slider>().First(x => x.Maximum == 120);
                var originalInterval = vm.RefreshSeconds;
                Slider.IncreaseLarge.Execute(null, intervalSlider);
                if (vm.RefreshSeconds <= originalInterval) throw new InvalidOperationException("Ползунок интервала не изменяет настройки.");
                intervalSlider.Value = originalInterval;
                await Settle(); SavePreview(testWindow.RootContent, Path.Combine(store.DirectoryPath, "settings.png"));
                vm.Page = "devices"; vm.Query = "__no_such_device__"; testWindow.UpdateLayout();
                if (!vm.Empty) throw new InvalidOperationException("Не работает поиск устройств.");
                await Settle(); SavePreview(testWindow.RootContent, Path.Combine(store.DirectoryPath, "empty.png"));
                vm.Query = ""; vm.Filter = "all"; testWindow.Width = 890; testWindow.UpdateLayout();
                await Settle(); SavePreview(testWindow.RootContent, Path.Combine(store.DirectoryPath, "compact.png"));
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
                        await Settle(); SavePreview(testWindow.RootContent, Path.Combine(store.DirectoryPath, $"{page}-fullhd-{scale * 100:0}.png"), scale);
                        layouts.Add(new { Page = page, Scale = scale, Width = testWindow.RootContent.ActualWidth, Height = testWindow.RootContent.ActualHeight });
                    }
                }
                store.WriteJson("layout-check.json", new { Success = true, Cases = layouts, Checks = "Видимые тексты и кнопки: горизонтальные границы, отсутствие обрезания текста; 1920×1080 при масштабе 100/125/150/200%" });
            }
            if (args.Contains("--ui-test"))
            {
                // Кадры движения: список появляется строка за строкой, шкала и число набегают.
                Controls.Motion.Suspended = false;
                vm.Page = "devices"; vm.Filter = "connected"; vm.Query = "__no_such_device__"; await Settle(); vm.Query = "";
                var clock = Stopwatch.StartNew();
                foreach (var ms in new[] { 40, 180, 360, 1000 })
                {
                    var wait = ms - (int)clock.ElapsedMilliseconds; if (wait > 0) await Task.Delay(wait);
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                    SavePreview(testWindow.RootContent, Path.Combine(store.DirectoryPath, $"motion-{ms}.png"));
                }
                Controls.Motion.Suspended = true;
            }
            testWindow.Hide();
            if (vm.EffectiveRefreshSeconds < 60) throw new InvalidOperationException("После скрытия окна не включился экономный режим.");
            testWindow.Show();
            if (vm.EffectiveRefreshSeconds != vm.RefreshSeconds) throw new InvalidOperationException("Не восстановлен интервал открытого окна.");
            store.WriteJson("ui-check.json", new { Success = true, DeviceCount = devices.Count, Sources = results.Select(x => new { x.Name, x.Error }), Ui = "XAML, текущие устройства, настройки, поиск, узкое окно, Full HD с масштабами 100/125/150/200%, переходы фон/окно", Tray = "Отдельные значки, исчезновение при отключении, восстановление при подключении, отрисовка 10/10+/20/90/100/?/батарея", Finished = DateTimeOffset.Now });
        }
        finally { testWindow.Close(); }
    }
    /// <summary>
    /// Проверка панели по наведению на настоящих значках трея: сообщение движения мыши отправляется значку, курсор подменяется,
    /// физическая мышь не двигается. На 2–4 секунды в трее появляются временные значки.
    /// </summary>
    private async Task RunTrayHoverCheckAsync(string[] args)
    {
        if (store is null) throw new InvalidOperationException();
        var fixture = Argument(args, "--fixture");
        IDeviceSource monitor = fixture is null ? new DeviceMonitor() : new FixtureDeviceSource(fixture);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var devices = SnapshotMerger.Merge((await monitor.ScanAsync(budget.Token)).SelectMany(x => x.Devices));
        using var trayCheck = new TrayController(() => { }, () => { }, () => { });
        trayCheck.Update(devices, [], false, 20);
        await Task.Delay(600);
        var steps = new List<string>();
        try
        {
            steps.Add($"Значков: {trayCheck.DeviceIconCount}; поля NotifyIcon найдены: {TrayController.CanLocateIcons}; устройств: {devices.Count}");
            if (trayCheck.FirstIcon() is not { } first) throw new InvalidOperationException("Оболочка Windows не вернула положение значка (значков: " + trayCheck.DeviceIconCount + ").");
            steps.Add($"Положение значка: {first.Icon}");
            trayCheck.CursorSource = () => new System.Drawing.Point(first.Icon.Left + first.Icon.Width / 2, first.Icon.Top + first.Icon.Height / 2);
            if (!trayCheck.PostHover(first.Id)) throw new InvalidOperationException("Сообщение наведения не отправлено.");
            var shown = false;
            for (var i = 0; i < 30 && !shown; i++) { await Task.Delay(100); shown = trayCheck.FlyoutVisible; }
            steps.Add($"Панель появилась: {shown}");
            if (!shown) throw new InvalidOperationException("Панель не появилась после наведения.");
            await Task.Delay(500);
            if (!trayCheck.FlyoutVisible) throw new InvalidOperationException("Панель исчезла, пока курсор над значком.");
            trayCheck.CursorSource = () => new System.Drawing.Point(5, 5);
            var hidden = false;
            for (var i = 0; i < 30 && !hidden; i++) { await Task.Delay(100); hidden = !trayCheck.FlyoutVisible; }
            steps.Add($"Панель скрылась после ухода курсора: {hidden}");
            if (!hidden) throw new InvalidOperationException("Панель не скрылась после ухода курсора.");
            store.WriteJson("tray-hover-check.json", new { Success = true, Steps = steps, Finished = DateTimeOffset.Now });
        }
        catch (Exception ex)
        {
            store.WriteJson("tray-hover-check.json", new { Success = false, Error = ex.Message, Steps = steps, Finished = DateTimeOffset.Now });
            throw;
        }
    }

    /// <summary>Даёт доиграть коротким анимациям интерфейса перед снимком кадра.</summary>
    private async Task Settle() { await Task.Delay(350); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
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
        // Две панели задач (тёмная и светлая), значки в реальных размерах 16 и 24 пикселя и в увеличении.
        var cases = new (string Value, int? Percent, BatteryTone Tone, string Caption)[]
        {
            ("90", 90, BatteryTone.Normal, "90%"), ("10+", 10, BatteryTone.Charging, "10+ заряжается"), ("15", 15, BatteryTone.Low, "15% низкий"),
            ("8", 8, BatteryTone.Critical, "8% критический"), ("100", 100, BatteryTone.Charging, "100%"), ("?", null, BatteryTone.Unknown, "нет данных")
        };
        using var bitmap = new System.Drawing.Bitmap(cases.Length * 120 + 24, 2 * 150 + 8);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        using var font = new System.Drawing.Font("Segoe UI", 11, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
        foreach (var light in new[] { false, true })
        {
            var top = light ? 154 : 0;
            graphics.FillRectangle(new System.Drawing.SolidBrush(light ? System.Drawing.Color.FromArgb(243, 243, 243) : System.Drawing.Color.FromArgb(28, 33, 36)), 0, top, bitmap.Width, 146);
            using var text = new System.Drawing.SolidBrush(light ? System.Drawing.Color.FromArgb(70, 80, 85) : System.Drawing.Color.FromArgb(170, 184, 190));
            for (var i = 0; i < cases.Length; i++)
            {
                var (value, percent, tone, caption) = cases[i];
                var x = 24 + i * 120;
                foreach (var (size, y, shown) in new[] { (16, 14, 16), (24, 14 + 16 + 12, 24) })
                {
                    using var icon = TrayController.DrawIcon(value, percent, tone, light, size);
                    graphics.DrawIcon(icon, new System.Drawing.Rectangle(x + (size == 16 ? 0 : 28), top + 12, shown, shown));
                }
                using var big = TrayController.DrawIcon(value, percent, tone, light, 64);
                graphics.DrawIcon(big, new System.Drawing.Rectangle(x, top + 44, 64, 64));
                graphics.DrawString(caption, font, text, x - 4, top + 118);
            }
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
        // В режиме высокой контрастности цвет состояния заменяют текст и форма: значения берутся из системной палитры.
        foreach (var key in new[] { "Background", "Surface", "Raised", "RaisedHover", "AmberWash", "OnAccent" }) Resources[key] = SystemColors.WindowBrush;
        foreach (var key in new[] { "Text", "Muted", "Faint", "Accent", "Amber", "Coral", "Tone.Normal", "Tone.Charging", "Tone.Low", "Tone.Critical", "Tone.Error", "Tone.Unknown" }) Resources[key] = SystemColors.WindowTextBrush;
        foreach (var key in new[] { "Line", "LineStrong", "Track" }) Resources[key] = SystemColors.GrayTextBrush;
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
