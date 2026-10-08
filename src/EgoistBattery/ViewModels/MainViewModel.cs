using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgoistBattery.Core;
using EgoistBattery.Services;

namespace EgoistBattery.ViewModels;

/// <summary>Заголовок раздела списка: «Заряд», «Заряд не передаётся», «Не подключены».</summary>
internal sealed partial class GroupHeader(DeviceGroup group) : ObservableObject
{
    public DeviceGroup Group { get; } = group;
    public override string ToString() => Title;
    public string Title { get; } = DevicePresentation.GroupTitle(group).ToUpper(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
    [ObservableProperty] private int count;
}

/// <summary>
/// Строка устройства. Объект живёт, пока устройство есть в снимке: обновление меняет свойства на месте,
/// поэтому раскрытые подробности, прокрутка и анимация шкалы не сбрасываются при каждом чтении.
/// </summary>
internal sealed partial class DeviceRow : ObservableObject
{
    public string Id { get; }
    public int RevealDelay { get; }
    public DeviceRow(string id, int revealDelay) { Id = id; RevealDelay = revealDelay; }

    [ObservableProperty] private string name = "";
    [ObservableProperty] private string meta = "";
    [ObservableProperty] private string glyph = "\uE702";
    [ObservableProperty] private DeviceGroup group;
    [ObservableProperty] private bool isMeasured;
    [ObservableProperty] private bool isCharging;
    [ObservableProperty] private Brush toneBrush = Brushes.White;
    [ObservableProperty] private double? numeralValue;
    [ObservableProperty] private string numeralText = "—";
    [ObservableProperty] private double numeralSize = 26;
    [ObservableProperty] private int rulerCells = DevicePresentation.RulerCells;
    [ObservableProperty] private int rulerSolid;
    [ObservableProperty] private int rulerInterval;
    [ObservableProperty] private string detail = "";
    [ObservableProperty] private string sourceLine = "";
    [ObservableProperty] private string timeLine = "";
    [ObservableProperty] private bool canShowTrayIcon;
    [ObservableProperty] private bool trayShown;
    [ObservableProperty] private string trayHint = "";
    [ObservableProperty] private string spoken = "";
    [ObservableProperty] private bool isExpanded;
    public string ExpandName => $"Как получено показание: {Name}";
    public override string ToString() => Spoken;
    public string TrayGlyph => TrayShown ? "\uE7B3" : "\uED1A";
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(ExpandName));
    partial void OnTrayShownChanged(bool value) => OnPropertyChanged(nameof(TrayGlyph));

    public void Apply(DeviceSnapshot device, bool hiddenInTray, int threshold)
    {
        var reading = device.Reading;
        var tone = DevicePresentation.ToneOf(reading, threshold);
        Name = device.Name;
        Group = DevicePresentation.GroupOf(device);
        IsMeasured = Group == DeviceGroup.Measured;
        Glyph = device.Kind switch { "Мышь" => "\uE962", "Клавиатура" => "\uE765", "Наушники" => "\uE7F6", "Контроллер" => "\uE7FC", "Компьютер" => "\uE7F8", "Bluetooth" => "\uE702", _ => "\uE772" };
        IsCharging = IsMeasured && tone == BatteryTone.Charging;
        ToneBrush = (Brush)Application.Current.FindResource(IsMeasured || tone == BatteryTone.Error ? $"Tone.{tone}" : "Tone.Unknown");
        NumeralValue = IsMeasured && reading.HasLevel && reading.Minimum == reading.Maximum ? reading.Minimum : null;
        NumeralText = IsMeasured ? reading.Label : "—";
        NumeralSize = NumeralValue is null && NumeralText.Length > 5 ? 20 : 26;
        RulerCells = DevicePresentation.CellCount(reading);
        (RulerSolid, RulerInterval) = IsMeasured ? DevicePresentation.Cells(reading) : (0, 0);
        var state = !device.Connected ? "Не подключено" : reading.State switch
        {
            ChargeState.Charging => "Заряжается", ChargeState.Full => "Зарядка завершена", ChargeState.Discharging => "Питание от батареи",
            ChargeState.Wired => "Проводное питание", ChargeState.Error => "Ошибка зарядки", _ => reading.HasData ? "Состояние зарядки неизвестно" : "Заряд не передаётся"
        };
        var quality = reading.Quality == ReadingQuality.WindowsCache && device.Connected ? " · значение Windows" : "";
        Meta = $"{state} · {device.Transport}{quality}";
        Detail = reading.Detail;
        SourceLine = $"Источник: {reading.Source}";
        TimeLine = $"Прочитано в {reading.ObservedAt.ToLocalTime():HH:mm:ss}";
        CanShowTrayIcon = TrayPolicy.VisibleDevices([device]).Count != 0;
        TrayShown = !hiddenInTray;
        TrayHint = !CanShowTrayIcon ? "" : hiddenInTray ? "Показать значок этого устройства в трее" : "Скрыть значок этого устройства из трея";
        Spoken = $"{device.Name}: {(IsMeasured ? reading.Label : "заряд не передаётся")}. {Meta}";
    }
}

internal sealed partial class MainViewModel : ObservableObject
{
    private readonly IDeviceSource monitor;
    private readonly SettingsStore store;
    private readonly Settings settings;
    private readonly CancellationToken token;
    private readonly Dictionary<string, DeviceRow> rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<DeviceGroup, GroupHeader> headers = Enum.GetValues<DeviceGroup>().ToDictionary(x => x, x => new GroupHeader(x));
    private IReadOnlyList<DeviceSnapshot> snapshots = [];
    private bool windowVisible;
    private bool loadedOnce;
    private string? savedSnapshotKey;
    private DateTimeOffset savedSnapshotAt;
    private bool loadingSettings;
    /// <summary>Заголовки разделов и строки устройств в порядке показа.</summary>
    public ObservableCollection<object> Items { get; } = [];
    public int DeviceRowCount => Items.Count(x => x is DeviceRow);
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
    public bool FilterConnected => Filter == "connected";
    public bool FilterBattery => Filter == "battery";
    public bool FilterAll => Filter == "all";
    public string PageTitle => SettingsPage ? "Настройки" : "Заряд";
    public string PageIndex => SettingsPage ? "02 / 02" : "01 / 02";
    public string Subtitle => loadedOnce ? $"{ConnectedCount} на связи · {MeasuredCount} с зарядом" : "Ищем подключённые устройства…";
    public bool Skeleton => !loadedOnce && IsBusy;
    public bool Empty => loadedOnce && Items.Count == 0 && !IsBusy;
    public bool CanResetFilters => Query.Length > 0 || Filter != "connected";
    public string EmptyTitle => Query.Length > 0 ? "Ничего не найдено" : Filter == "battery" ? "Данных батареи пока нет" : "Подключите устройство";
    public string EmptyHint => Query.Length > 0 ? "Измените запрос или сбросьте фильтры." : Filter == "battery" ? "Ни одно из подключённых устройств не передаёт заряд. Попробуйте фильтр «Подключённые»." : "Мышь, клавиатура или геймпад появятся здесь сами: программа повторяет чтение автоматически.";
    public string VersionText => $"Версия {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";
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
        NotifyListState();
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromSeconds(20));
            var results = await monitor.ScanAsync(budget.Token);
            var errors = results.Where(x => x.Error is not null).Select(x => x.Error).ToArray();
            snapshots = SnapshotMerger.Merge(results.SelectMany(x => x.Devices));
            loadedOnce = true;
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
            ClearSnapshots();
            Updated?.Invoke(snapshots);
            Notice = "Чтение заняло слишком долго. Повторим автоматически; старые данные скрыты.";
            Status = "Обновление не завершено";
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            ClearSnapshots(); Updated?.Invoke(snapshots);
            Notice = "Не удалось обновить устройства. Повторим автоматически; старые показания скрыты."; Status = "Ошибка чтения"; store.Log(e);
        }
        finally { IsBusy = false; NotifyListState(); }
    }

    private void ClearSnapshots()
    {
        snapshots = []; loadedOnce = true; rows.Clear(); Items.Clear();
        ConnectedCount = MeasuredCount = UnknownCount = 0;
    }

    [RelayCommand] private void SelectFilter(string value) { Filter = value; }
    [RelayCommand] private void SelectPage(string value) { Page = value; }
    [RelayCommand] private void ResetFilters() { Query = ""; Filter = "connected"; }
    [RelayCommand] private void ToggleExpanded(DeviceRow row) { row.IsExpanded = !row.IsExpanded; }
    [RelayCommand] private void ToggleTrayDevice(string id)
    {
        if (!settings.HiddenTrayDevices.Remove(id)) settings.HiddenTrayDevices.Add(id);
        SaveSettings(); ApplyFilter(); Updated?.Invoke(snapshots);
    }
    partial void OnQueryChanged(string value) { ApplyFilter(); OnPropertyChanged(nameof(CanResetFilters)); }
    partial void OnFilterChanged(string value)
    {
        ApplyFilter();
        foreach (var name in new[] { nameof(FilterConnected), nameof(FilterBattery), nameof(FilterAll), nameof(CanResetFilters) }) OnPropertyChanged(name);
    }
    partial void OnPageChanged(string value) { foreach (var name in new[] { nameof(DevicesPage), nameof(SettingsPage), nameof(PageTitle), nameof(PageIndex) }) OnPropertyChanged(name); }
    partial void OnRefreshSecondsChanged(int value) { settings.RefreshSeconds = Math.Clamp(value, 5, 120); SaveSettings(); PollingIntervalChanged?.Invoke(); }
    partial void OnLowThresholdChanged(int value) { settings.LowThreshold = Math.Clamp(value, 5, 50); SaveSettings(); ApplyFilter(); }
    partial void OnNotificationsChanged(bool value) { settings.Notifications = value; SaveSettings(); }
    partial void OnEcoModeChanged(bool value) { settings.EcoMode = value; SaveSettings(); PollingIntervalChanged?.Invoke(); }
    private void SaveSettings()
    {
        if (loadingSettings) return;
        try { store.Save(settings); SettingsChanged?.Invoke(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notice = "Не удалось сохранить настройки."; store.Log(e); }
    }

    private void NotifyListState()
    {
        foreach (var name in new[] { nameof(Empty), nameof(EmptyTitle), nameof(EmptyHint), nameof(Skeleton), nameof(Subtitle), nameof(DeviceRowCount) }) OnPropertyChanged(name);
    }

    private void ApplyFilter()
    {
        if (!windowVisible) return;
        var visible = DevicePresentation.Order(snapshots.Where(x => (Filter != "connected" || x.Connected) && (Filter != "battery" || x.Connected && x.Reading.HasLevel)
            && $"{x.Name} {x.Kind} {x.Transport}".Contains(Query.Trim(), StringComparison.CurrentCultureIgnoreCase)));
        var desired = new List<object>();
        DeviceGroup? current = null;
        foreach (var device in visible)
        {
            var group = DevicePresentation.GroupOf(device);
            if (group != current)
            {
                current = group;
                headers[group].Count = visible.Count(x => DevicePresentation.GroupOf(x) == group);
                desired.Add(headers[group]);
            }
            if (!rows.TryGetValue(device.Id, out var row)) rows[device.Id] = row = new DeviceRow(device.Id, Math.Min(desired.Count, 9) * 50);
            row.Apply(device, settings.HiddenTrayDevices.Contains(device.Id, StringComparer.OrdinalIgnoreCase), LowThreshold);
            desired.Add(row);
        }
        foreach (var gone in rows.Keys.Where(id => snapshots.All(x => !string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))).ToArray()) rows.Remove(gone);
        Sync(Items, desired);
        NotifyListState();
    }

    /// <summary>Приводит список к нужному порядку, не пересоздавая неизменившиеся элементы.</summary>
    private static void Sync(ObservableCollection<object> target, IReadOnlyList<object> desired)
    {
        // Устаревшие элементы убираются до перестановок: Move соседей пересоздаёт их визуальные элементы и повторяет анимацию появления.
        var keep = new HashSet<object>(desired, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--) if (!keep.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i])) continue;
            var at = target.IndexOf(desired[i]);
            if (at >= 0) target.Move(at, i); else target.Insert(i, desired[i]);
        }
        while (target.Count > desired.Count) target.RemoveAt(target.Count - 1);
    }
}
