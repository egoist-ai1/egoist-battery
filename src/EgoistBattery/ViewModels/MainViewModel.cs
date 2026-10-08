using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgoistBattery.Core;
using EgoistBattery.Services;

namespace EgoistBattery.ViewModels;

internal sealed class DeviceCard(DeviceSnapshot device, bool hiddenInTray = false)
{
    public DeviceSnapshot Device { get; } = device;
    public string Id => Device.Id;
    public bool CanShowTrayIcon => TrayPolicy.VisibleDevices([Device]).Count != 0;
    public string TrayGlyph => !CanShowTrayIcon ? "—" : hiddenInTray ? "+" : "✓";
    public string TrayHint => !CanShowTrayIcon ? "Значок доступен для подключённого устройства с батареей" : hiddenInTray ? "Показать значок этого устройства в трее" : "Скрыть значок этого устройства из трея";
    public string Name => Device.Name;
    public string Kind => Device.Kind;
    public string Transport => Device.Transport;
    public string Glyph => Kind switch { "Мышь" => "\uE962", "Клавиатура" => "\uE765", "Наушники" => "\uE7F6", "Контроллер" => "\uE7FC", "Компьютер" => "\uE7F8", _ => "\uE702" };
    public string Connection => Device.Connected ? "На связи" : "Не подключено";
    public string Level => !Device.Connected ? "—" : Device.Reading.Label;
    public double Progress => Device.Connected ? Device.Reading.Percent ?? 0 : 0;
    public string Accent => !Device.Connected || !Device.Reading.HasLevel ? "#98A3AB" : Device.Reading.State == ChargeState.Error ? "#FF8F86" : Device.Reading.Percent <= 20 ? "#F6C879" : "#B7F5CC";
    public string State => !Device.Connected ? "Уровень появится после подключения" : Device.Reading.State switch
    {
        ChargeState.Charging => "Заряжается", ChargeState.Full => "Зарядка завершена", ChargeState.Discharging => "Питание от батареи",
        ChargeState.Wired => "Проводное питание", ChargeState.Error => "Ошибка зарядки", _ => Device.Reading.HasLevel ? "Состояние зарядки неизвестно" : "Заряд не передаётся"
    };
    public string Quality => Device.Reading.Quality switch { ReadingQuality.Live => "Данные устройства", ReadingQuality.WindowsCache => "Последнее значение Windows", _ => "Процент недоступен" };
    public string Detail => Device.Reading.Detail;
    public string Source => Device.Reading.Source;
    public string Time => $"Прочитано в {Device.Reading.ObservedAt.ToLocalTime():HH:mm:ss}";
}

internal sealed partial class MainViewModel : ObservableObject
{
    private readonly IDeviceSource monitor;
    private readonly SettingsStore store;
    private readonly Settings settings;
    private readonly CancellationToken token;
    private IReadOnlyList<DeviceSnapshot> snapshots = [];
    private bool windowVisible;
    private string? savedSnapshotKey;
    private DateTimeOffset savedSnapshotAt;
    private bool loadingSettings;
    public ObservableCollection<DeviceCard> Cards { get; } = [];
    public event Action<IReadOnlyList<DeviceSnapshot>>? Updated;
    public event Action? PollingIntervalChanged;
    public event Action? SettingsChanged;
    [ObservableProperty] private string query = "";
    [ObservableProperty] private string filter = "connected";
    [ObservableProperty] private string page = "devices";
    [ObservableProperty] private string status = "Ищем подключённые устройства…";
    [ObservableProperty] private string notice = "";
    [ObservableProperty] private int connectedCount;
    [ObservableProperty] private int measuredCount;
    [ObservableProperty] private int unknownCount;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private int refreshSeconds;
    [ObservableProperty] private int lowThreshold;
    [ObservableProperty] private bool notifications;
    [ObservableProperty] private bool ecoMode;
    public bool DevicesPage => Page == "devices";
    public bool SettingsPage => Page == "settings";
    public string PageTitle => SettingsPage ? "Настройки" : "Заряд устройств";
    public string PageSubtitle => SettingsPage ? "Обновление, уведомления и значки в трее" : "Что подключено и сколько энергии осталось";
    public bool Empty => Cards.Count == 0 && !IsBusy;
    public string EmptyTitle => Query.Length > 0 ? "Ничего не найдено" : Filter == "battery" ? "Данных батареи пока нет" : "Устройств в этом списке пока нет";
    public IReadOnlyList<string> HiddenTrayDevices => settings.HiddenTrayDevices;
    public IReadOnlyList<DeviceSnapshot> Snapshots => snapshots;
    public int EffectiveRefreshSeconds => EcoMode && !windowVisible ? Math.Max(60, RefreshSeconds) : RefreshSeconds;

    public void SetWindowVisible(bool visible)
    {
        if (windowVisible == visible) return;
        windowVisible = visible;
        if (visible) ApplyFilter();
        PollingIntervalChanged?.Invoke();
    }

    public MainViewModel(IDeviceSource monitor, SettingsStore store, CancellationToken token)
    {
        this.monitor = monitor; this.store = store; this.token = token;
        settings = store.Load();
        refreshSeconds = settings.RefreshSeconds; lowThreshold = settings.LowThreshold; notifications = settings.Notifications; ecoMode = settings.EcoMode;
        notice = store.LoadWarning ?? "";
    }
    public void ForceSnapshotSave() => savedSnapshotKey = null;
    public void ReloadSettings()
    {
        var latest = store.Load();
        loadingSettings = true;
        try
        {
            RefreshSeconds = latest.RefreshSeconds; LowThreshold = latest.LowThreshold;
            Notifications = latest.Notifications; EcoMode = latest.EcoMode;
            settings.HiddenTrayDevices.Clear(); settings.HiddenTrayDevices.AddRange(latest.HiddenTrayDevices.Distinct());
        }
        finally { loadingSettings = false; }
        ApplyFilter(); Updated?.Invoke(snapshots); PollingIntervalChanged?.Invoke();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy || token.IsCancellationRequested) return;
        IsBusy = true;
        OnPropertyChanged(nameof(Empty));
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromSeconds(20));
            var results = await monitor.ScanAsync(budget.Token);
            var errors = results.Where(x => x.Error is not null).Select(x => x.Error).ToArray();
            snapshots = SnapshotMerger.Merge(results.SelectMany(x => x.Devices));
            ConnectedCount = snapshots.Count(x => x.Connected);
            MeasuredCount = snapshots.Count(x => x.Connected && x.Reading.HasData);
            UnknownCount = ConnectedCount - MeasuredCount;
            Notice = string.Join(" ", errors!);
            var observed = monitor is SnapshotDeviceSource && snapshots.Count > 0 ? snapshots.Max(x => x.Reading.ObservedAt).ToLocalTime() : DateTimeOffset.Now;
            Status = $"Обновлено в {observed:HH:mm:ss} · следующее чтение через {EffectiveRefreshSeconds} с";
            ApplyFilter();
            var snapshotKey = string.Join('|', snapshots.Select(x => $"{x.Id}:{x.Name}:{x.Connected}:{x.Transport}:{x.Reading.Minimum}:{x.Reading.Maximum}:{x.Reading.State}:{x.Reading.Quality}:{x.Reading.Band}")) + string.Join('|', errors);
            try
            {
                // Не записываем неизменный полный снимок каждую минуту.
                if (monitor is not SnapshotDeviceSource && (snapshotKey != savedSnapshotKey || DateTimeOffset.Now - savedSnapshotAt >= TimeSpan.FromMinutes(5)))
                {
                    store.WriteJson("last-snapshot.json", new { ObservedAt = DateTimeOffset.Now, Providers = results, Devices = snapshots });
                    savedSnapshotKey = snapshotKey; savedSnapshotAt = DateTimeOffset.Now;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notice = "Не удалось сохранить снимок. Текущие показания доступны в окне."; store.Log(e); }
            Updated?.Invoke(snapshots);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // Старый снимок не выдаём за текущий после отказа общего опроса.
            snapshots = []; Cards.Clear(); ConnectedCount = MeasuredCount = UnknownCount = 0;
            Updated?.Invoke(snapshots);
            Notice = "Чтение заняло слишком долго. Повторим автоматически; старые данные скрыты.";
            Status = "Обновление не завершено";
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            snapshots = []; Cards.Clear(); ConnectedCount = MeasuredCount = UnknownCount = 0; Updated?.Invoke(snapshots);
            Notice = "Не удалось обновить устройства. Повторим автоматически; старые показания скрыты."; Status = "Ошибка чтения"; store.Log(e);
        }
        finally { IsBusy = false; OnPropertyChanged(nameof(Empty)); OnPropertyChanged(nameof(EmptyTitle)); }
    }

    [RelayCommand] private void SelectFilter(string value) { Filter = value; }
    [RelayCommand] private void SelectPage(string value) { Page = value; }
    [RelayCommand] private void ToggleTrayDevice(string id)
    {
        if (!settings.HiddenTrayDevices.Remove(id)) settings.HiddenTrayDevices.Add(id);
        SaveSettings(); ApplyFilter(); Updated?.Invoke(snapshots);
    }
    partial void OnQueryChanged(string value) => ApplyFilter();
    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnPageChanged(string value) { OnPropertyChanged(nameof(DevicesPage)); OnPropertyChanged(nameof(SettingsPage)); OnPropertyChanged(nameof(PageTitle)); OnPropertyChanged(nameof(PageSubtitle)); }
    partial void OnRefreshSecondsChanged(int value) { settings.RefreshSeconds = Math.Clamp(value, 5, 120); SaveSettings(); PollingIntervalChanged?.Invoke(); }
    partial void OnLowThresholdChanged(int value) { settings.LowThreshold = Math.Clamp(value, 5, 50); SaveSettings(); }
    partial void OnNotificationsChanged(bool value) { settings.Notifications = value; SaveSettings(); }
    partial void OnEcoModeChanged(bool value) { settings.EcoMode = value; SaveSettings(); PollingIntervalChanged?.Invoke(); }
    private void SaveSettings()
    {
        if (loadingSettings) return;
        try { store.Save(settings); SettingsChanged?.Invoke(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notice = "Не удалось сохранить настройки."; store.Log(e); }
    }

    private void ApplyFilter()
    {
        if (!windowVisible) return;
        var items = snapshots.Where(x => (Filter != "connected" || x.Connected) && (Filter != "battery" || x.Connected && x.Reading.HasLevel)
            && ($"{x.Name} {x.Kind} {x.Transport}".Contains(Query.Trim(), StringComparison.CurrentCultureIgnoreCase)));
        Cards.Clear();
        foreach (var device in items) Cards.Add(new(device, settings.HiddenTrayDevices.Contains(device.Id)));
        OnPropertyChanged(nameof(Empty)); OnPropertyChanged(nameof(EmptyTitle));
    }
}
