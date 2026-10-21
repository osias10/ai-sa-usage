using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AIUsage.Core;
using AIUsage.Infrastructure;
using AIUsage.Providers;
using Microsoft.Win32;

namespace AIUsage.App;

public sealed partial class AppController : IAsyncDisposable
{
    private readonly JsonSettingsStore _store;
    private readonly UsageCoordinator _coordinator;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DispatcherTimer _clock;
    private readonly IDisposable? _runtime;
    private readonly UsageAlertEngine _alerts;
    private readonly UsageForecastEngine _forecast;
    private readonly UsageStatistics _statistics;
    private bool _clearingUsageStatistics;
    public int UsageStatisticsCount => _statistics.Count(DateTimeOffset.UtcNow);
    public UsageStatisticsHistory GetUsageStatisticsHistory(DateTimeOffset now)
    {
        var identities = Settings.Accounts.Select(account => (account.Provider, account.Id, account.IdentityKey)).ToHashSet();
        return new()
        {
            Samples = _statistics.GetHistory(now).Samples.Where(sample => sample.FetchedAt <= now &&
                identities.Contains((sample.Provider, sample.AccountId, sample.IdentityKey))).ToList()
        };
    }
    private readonly Dictionary<string, UsageSnapshot?> _observedSnapshots = [];
    private SettingsWindow? _settingsWindow;
    private bool _disposed;
    private readonly HttpClient? _updateHttp;
    private readonly HttpClient? _downloadHttp;
    private Task _updateInitialization = Task.CompletedTask;
    private readonly UpdateInstallation _updateInstallation = new();
    private bool _preparingUpdate;
    private readonly SemaphoreSlim _clickThroughSaveGate = new(1, 1);
    private int _clickThroughRevision;
    private bool _savedClickThrough;
    private readonly Dictionary<string, CancellationTokenSource> _connections = [];
    private readonly Dictionary<string, TaskCompletionSource> _connectionCompletions = [];
    public AppSettings Settings { get; }
    public MainViewModel ViewModel { get; }
    public bool IsDemo { get; }
    public AppUpdateService? Updates { get; }
    internal Func<Task>? ExitForUpdateAsync { get; set; }
    internal bool UpdateInstallerLaunched { get; private set; }
    public MainWindow? Window { get; set; }
    public event EventHandler? PreferencesUpdated;
    public event EventHandler? WidgetAppearanceUpdated;
    public event EventHandler? UsageUpdated;
    public event Action<IReadOnlyList<UsageAlert>>? UsageAlertsRaised;
    public IReadOnlyList<AccountUsageStatus> GetStatuses()
    {
        List<AccountUsageStatus> statuses = [];
        foreach (var account in Settings.Accounts)
        {
            try { statuses.Add(_coordinator.GetStatus(account.Id)); }
            catch (KeyNotFoundException) { /* Disconnect may finish before the UI removes its row. */ }
        }
        return statuses;
    }

    private AppController(AppSettings settings, JsonSettingsStore store, IEnumerable<IUsageProvider> providers,
        IReadOnlyList<UsageSnapshot> snapshots, bool demo, IDisposable? runtime = null, UsageAlertState? alertState = null,
        UsageStatisticsHistory? statisticsHistory = null, ResetMessageState? resetMessageState = null)
    {
        Settings = settings;
        _savedClickThrough = settings.ClickThrough;
        _store = store;
        IsDemo = demo;
        _runtime = runtime;
        _alerts = new UsageAlertEngine(alertState);
        _statistics = new UsageStatistics(statisticsHistory);
        _forecast = new UsageForecastEngine(_statistics.GetForecastHistory(DateTimeOffset.UtcNow));
        _coordinator = new UsageCoordinator(providers, settings.Accounts, snapshots);
        foreach (var account in settings.Accounts)
        {
            _observedSnapshots[account.Id] = _coordinator.GetStatus(account.Id).Snapshot;
            _alerts.SynchronizeSettings(account);
            _forecast.SynchronizeAccount(account);
            _statistics.SynchronizeAccount(account);
        }
        ViewModel = new MainViewModel
        {
            IsDemo = demo,
            RefreshAllCommand = new AsyncCommand(RefreshAllAsync, ShowError),
            SettingsCommand = new RelayCommand(ShowSettings),
            HideCommand = new AsyncCommand(() => SetWidgetVisibilityAsync(false), ShowError)
        };
        InitializeResetMessages(resetMessageState);
        foreach (var account in settings.Accounts.OrderBy(x => x.Order)) ViewModel.Accounts.Add(CreateAccount(account));
        ViewModel.RefreshCollections();
        RefreshResetMessageUi();
        _coordinator.Changed += CoordinatorChanged;
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => ViewModel.Tick(), Application.Current.Dispatcher);
        if (!demo)
        {
            var paths = new AppPaths();
            var source = new GitHubReleaseSource("osias10", "ai-sa-usage");
            var channelPath = Path.Combine(AppContext.BaseDirectory, "release-channel.json");
            try
            {
                if (File.Exists(channelPath))
                    source = JsonSerializer.Deserialize<GitHubReleaseSource>(File.ReadAllText(channelPath),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? source;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                // An unreadable local channel cannot stop usage tracking. Use the public default.
            }
            _updateHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            _downloadHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            Updates = new AppUpdateService(new UpdateChecker(_updateHttp), new UpdateDownloader(_downloadHttp, paths.UpdatesDirectory),
                source, InstalledReleaseVersion, paths.UpdateStateFile);
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.TimeChanged += TimeChanged;
            SystemEvents.UserPreferenceChanged += UserPreferenceChanged;
            NetworkChange.NetworkAvailabilityChanged += NetworkChanged;
        }
    }

    internal static ReleaseVersion InstalledReleaseVersion => ReleaseVersion.Parse(
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");

    public static async Task<AppController> CreateAsync(bool demo, bool mini, bool light, bool statisticsDemo = false)
    {
        var paths = new AppPaths();
        var store = new JsonSettingsStore(paths);
        if (demo)
        {
            var settings = new AppSettings { MiniMode = mini, Theme = light ? "Light" : "Dark" };
            settings.Accounts = [
                new() { Provider = ProviderKind.Claude, Alias = "개인", Plan = "Max", Email = "personal@example.com", IdentityKey = "demo-claude", Order = 0 },
                new() { Provider = ProviderKind.Codex, Alias = "업무", Plan = "Pro", Email = "work@example.com", IdentityKey = "demo-codex", Order = 1 },
                new() { Provider = ProviderKind.Antigravity, Alias = "개인", Plan = "Google AI Pro", Email = "personal@example.com", IdentityKey = "demo-antigravity", Order = 2 }
            ];
            var now = DateTimeOffset.UtcNow;
            var snapshots = settings.Accounts.Select((a, index) => new UsageSnapshot(a.Provider, a.Id, a.IdentityKey, a.Plan, now.AddMinutes(-2),
                index switch
                {
                    0 => [new("session::0", "현재 세션", 37, now.AddHours(2).AddMinutes(18), true), new("weekly_all::0", "주간 전체", 68, now.AddDays(3).AddHours(7)), new("weekly_scoped:Fable:0", "주간 · Fable", 21, now.AddDays(3).AddHours(7))],
                    1 => [new("primary", "5시간 한도", 24, now.AddHours(3).AddMinutes(42), true), new("weekly", "주간 한도", 43, now.AddDays(5).AddHours(12))],
                    _ => [new("pro", "Gemini Pro", 16, now.AddHours(4).AddMinutes(5), true), new("flash", "Gemini Flash", 8, now.AddHours(4).AddMinutes(5))]
                })).ToArray();
            settings.SaveUsageStatistics = statisticsDemo;
            return new AppController(settings, store, Enum.GetValues<ProviderKind>().Select(kind => new DemoProvider(kind, snapshots)), snapshots, true,
                statisticsHistory: statisticsDemo ? StatisticsDemoData.Create(settings.Accounts, snapshots, now) : null);
        }
        try { new UpdateDataBackup(paths).CleanupExpired(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Locked backup files can be cleaned on the next launch; usage tracking remains available.
        }
        var loaded = await store.LoadSettingsAsync();
        // Uninstall keeps preferences but removes startup registration. Reflect the
        // current executable's registration instead of silently re-enabling it.
        loaded.StartWithWindows = StartupManager.IsEnabled();
        var cached = await store.LoadSnapshotsAsync();
        var alertState = await store.LoadAlertStateAsync();
        var statisticsHistory = await store.LoadUsageStatisticsAsync();
        var resetMessageState = await store.LoadResetMessageStateAsync();
        var runtime = new RuntimeManager(paths);
        var runner = new ProcessRunner();
        var login = new DesktopLoginInteraction();
        IUsageProvider[] providers = [new ClaudeUsageProvider(paths, runtime, runner, login), new CodexUsageProvider(paths, runtime, runner, login), new AntigravityUsageProvider(paths, runtime, runner, login)];
        var controller = new AppController(loaded, store, providers, cached, false, runtime, alertState, statisticsHistory, resetMessageState);
        if (store.LastWarning is not null) controller.ViewModel.Error = store.LastWarning;
        return controller;
    }
    private AccountViewModel CreateAccount(AccountSettings account)
    {
        var vm = new AccountViewModel(account, RefreshAccountAsync, ConnectAccountAsync, RemoveAccountAsync, AccountSettingsChanged, Reorder, CancelConnection, ShowError);
        vm.ShowRemaining = Settings.ShowRemaining;
        vm.ShowLastChecked = Settings.ShowLastChecked;
        ConfigureForecast(vm);
        vm.Apply(_coordinator.GetStatus(account.Id));
        return vm;
    }
    public void Start()
    {
        ThemeManager.Apply(Settings.Theme);
        if (!IsDemo)
        {
            _coordinator.Start();
            _resetMessageTimer.Start();
            _updateInitialization = Updates!.InitializeAsync(Settings.AutoCheckUpdates,
                Settings.IncludePrereleaseUpdates ? UpdateChannel.Preview : UpdateChannel.Stable, _shutdown.Token);
        }
    }
    public void ShowSettings()
    {
        if (_preparingUpdate) return;
        if (_settingsWindow is not null)
        {
            _settingsWindow.Show();
            _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }
    public void ShowUpdateSettings()
    {
        ShowSettings();
        _settingsWindow?.ShowUpdateInfo();
    }
    public async Task SetAutoCheckUpdatesAsync(bool enabled)
    {
        var previous = Settings.AutoCheckUpdates;
        Settings.AutoCheckUpdates = enabled;
        try { await SaveSettingsAsync(); }
        catch { Settings.AutoCheckUpdates = previous; throw; }
        Updates?.SetAutoCheckEnabled(enabled);
    }
    public async Task SetIncludePrereleaseUpdatesAsync(bool enabled)
    {
        if (_preparingUpdate) throw new InvalidOperationException("설치 준비가 끝난 뒤 변경해 주세요.");
        var previous = Settings.IncludePrereleaseUpdates;
        Settings.IncludePrereleaseUpdates = enabled;
        try { await SaveSettingsAsync(); }
        catch { Settings.IncludePrereleaseUpdates = previous; throw; }
        await _updateInitialization;
        if (Updates is { } updates)
            await updates.SetChannelAsync(enabled ? UpdateChannel.Preview : UpdateChannel.Stable, _shutdown.Token);
    }
    public async Task InstallUpdateAsync()
    {
        if (IsDemo || Updates is null) throw new InvalidOperationException("데모에서는 설치할 수 없습니다.");
        if (_preparingUpdate) return;
        if (_connections.Count > 0) throw new InvalidOperationException("계정 연결을 마친 뒤 설치를 실행해 주세요.");
        var exit = ExitForUpdateAsync ?? throw new InvalidOperationException("앱 종료를 준비하지 못했습니다.");
        var enabledWindows = new List<Window>();
        var selectedRelease = Updates.State.Release;
        var selectedChannel = Updates.Channel;
        try
        {
            await _updateInstallation.RunAsync(
            () => Updates.GetVerifiedInstallerAsync(_shutdown.Token),
            async () =>
            {
                if (_connections.Count > 0) throw new InvalidOperationException("계정 연결을 마친 뒤 설치를 실행해 주세요.");
                _preparingUpdate = true;
                foreach (var pendingReset in _resetMessageCancellations.Values.ToArray()) pendingReset.Cancel();
                await _resetMessageWork;
                foreach (Window window in Application.Current.Windows)
                    if (window.IsEnabled) { enabledWindows.Add(window); window.IsEnabled = false; }
                if (_settingsWindow is not null) await _settingsWindow.FlushPendingEditsAsync();
                Window?.CapturePlacement();
                await SaveSettingsAsync();
                await SaveSnapshotsAsync();
                await SaveAlertStateAsync();
                await SaveUsageStatisticsAsync();
                await SaveResetMessagesAsync();
                if (selectedRelease is not null && (Updates.CurrentReleaseVersion.IsPrerelease || selectedRelease.IsPrerelease))
                    await _store.BackupForVersionChangeAsync(Updates.CurrentReleaseVersion.ToString(), selectedRelease.Version, _shutdown.Token);
            },
            installer =>
            {
                if (_disposed) throw new InvalidOperationException("앱이 종료 중입니다. 다시 실행한 뒤 설치해 주세요.");
                if (Updates.Channel != selectedChannel || Updates.State.Release != selectedRelease ||
                    Updates.State.Action == UpdateAction.None || Updates.State.InstallerPath != installer)
                    throw new InvalidOperationException("업데이트 대상이 변경되었습니다. 다시 확인해 주세요.");
                var start = new ProcessStartInfo(installer) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installer)! };
                start.ArgumentList.Add("/DIR=" + AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
                start.ArgumentList.Add("/LANG=korean");
                using var process = Process.Start(start) ?? throw new InvalidOperationException("설치 프로그램을 열지 못했습니다.");
                UpdateInstallerLaunched = true;
            }, exit);
        }
        finally
        {
            _preparingUpdate = false;
            if (!_disposed)
            {
                foreach (var window in enabledWindows) window.IsEnabled = true;
                foreach (var account in ViewModel.Accounts) account.Apply(_coordinator.GetStatus(account.Id));
                ViewModel.Tick();
            }
        }
    }
    public async Task AddAccountAsync(ProviderKind provider, string alias)
    {
        if (IsDemo) return;
        var account = new AccountSettings { Provider = provider, Alias = alias, Order = Settings.Accounts.Count };
        _coordinator.AddAccount(account);
        Settings.Accounts.Add(account);
        var vm = CreateAccount(account);
        ViewModel.Accounts.Add(vm);
        ViewModel.RefreshCollections();
        await SaveSettingsAsync();
        await ConnectAccountAsync(vm);
    }
    private async Task ConnectAccountAsync(AccountViewModel account)
    {
        if (IsDemo) return;
        ViewModel.Error = null;
        if (_connections.ContainsKey(account.Id)) return;
        if (_resetMessageCancellations.TryGetValue(account.Id, out var pendingResetMessage)) pendingResetMessage.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connections.Add(account.Id, cancellation);
        _connectionCompletions.Add(account.Id, completion);
        account.CanCancelConnection = true;
        try { await _coordinator.ConnectAsync(account.Id, new Progress<ProviderProgress>(account.Report), cancellation.Token); }
        finally
        {
            _connections.Remove(account.Id);
            account.CanCancelConnection = false;
            try { await SaveSettingsAsync(); }
            finally { _connectionCompletions.Remove(account.Id); completion.TrySetResult(); }
        }
    }
    private void CancelConnection(AccountViewModel account)
    {
        if (_connections.TryGetValue(account.Id, out var cancellation)) cancellation.Cancel();
    }
    private Task RefreshAccountAsync(AccountViewModel account)
    {
        ViewModel.Error = null;
        return _coordinator.RefreshAsync(account.Id, new Progress<ProviderProgress>(account.Report), _shutdown.Token);
    }
    public Task RefreshAllAsync()
    {
        if (_preparingUpdate) return Task.CompletedTask;
        ViewModel.Error = null;
        return _coordinator.RefreshAllAsync(_shutdown.Token);
    }
    private async Task RemoveAccountAsync(AccountViewModel account)
    {
        if (IsDemo) return;
        var message = $"{account.ProviderName} · {account.Alias} 계정을 위젯에서 삭제할까요?\n다른 계정과 사용 중인 공식 앱은 유지됩니다.";
        if (MessageBox.Show(_settingsWindow ?? (Window)Window!, message, "계정 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        // Cancel before waiting for the account gate, including an in-flight authentication check.
        if (_resetMessageCancellations.TryGetValue(account.Id, out var pendingResetMessage)) pendingResetMessage.Cancel();
        await _coordinator.RemoveAccountAsync(account.Id, _shutdown.Token);
        Settings.Accounts.Remove(account.Settings);
        if (Settings.TrayAccountId == account.Id) Settings.TrayAccountId = null;
        _alerts.RemoveAccount(account.Id);
        _forecast.RemoveAccount(account.Id);
        _statistics.RemoveAccount(account.Id);
        RemoveResetMessageAccount(account.Settings);
        await SaveResetMessagesAsync();
        _observedSnapshots.Remove(account.Id);
        ViewModel.Accounts.Remove(account);
        ViewModel.RefreshCollections();
        UsageUpdated?.Invoke(this, EventArgs.Empty);
        await SaveSettingsAsync();
        await SaveSnapshotsAsync();
        await SaveAlertStateAsync();
        await SaveUsageStatisticsAsync();
    }
    private async void AccountSettingsChanged(AccountViewModel account)
    {
        try
        {
            var resetRevision = PauseResetMessagesForSettings(account.Settings);
            var requestedResetEnabled = account.Settings.ResetMessageEnabled;
            _alerts.SynchronizeSettings(account.Settings);
            _forecast.SynchronizeAccount(account.Settings);
            _statistics.SynchronizeAccount(account.Settings);
            _coordinator.UpdateAccount(account.Settings);
            ViewModel.RefreshCollections();
            UsageUpdated?.Invoke(this, EventArgs.Empty);
            await SaveSettingsAsync();
            await ApplyResetMessageSettingsAsync(account.Settings, requestedResetEnabled, resetRevision);
            await SaveAlertStateAsync();
            await SaveUsageStatisticsAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void Reorder(AccountViewModel account, int delta)
    {
        var oldIndex = ViewModel.Accounts.IndexOf(account);
        var newIndex = Math.Clamp(oldIndex + delta, 0, ViewModel.Accounts.Count - 1);
        if (newIndex == oldIndex) return;
        ViewModel.Accounts.Move(oldIndex, newIndex);
        for (int i = 0; i < ViewModel.Accounts.Count; i++) ViewModel.Accounts[i].Settings.Order = i;
        Settings.Accounts = ViewModel.Accounts.Select(x => x.Settings).ToList();
        ViewModel.RefreshCollections();
        UsageUpdated?.Invoke(this, EventArgs.Empty);
        try { await SaveSettingsAsync(); } catch (Exception ex) { ShowError(ex); }
    }
    public async Task SetPreferencesAsync(bool topmost, bool locked, bool startup, string theme, bool showRemaining, string? trayAccountId, bool showLastChecked)
    {
        if (!IsDemo && Settings.StartWithWindows != startup) StartupManager.SetEnabled(startup);
        Settings.AlwaysOnTop = topmost;
        Settings.PositionLocked = locked;
        Settings.StartWithWindows = startup;
        Settings.Theme = theme;
        Settings.ShowRemaining = showRemaining;
        Settings.ShowLastChecked = showLastChecked;
        Settings.TrayAccountId = Settings.Accounts.Any(a => a.Id == trayAccountId) ? trayAccountId : null;
        foreach (var account in ViewModel.Accounts)
        {
            account.ShowRemaining = showRemaining;
            account.ShowLastChecked = showLastChecked;
        }
        ThemeManager.Apply(theme);
        PreferencesUpdated?.Invoke(this, EventArgs.Empty);
        await SaveSettingsAsync();
    }
    public async Task SetClickThroughAsync(bool enabled)
    {
        if (_disposed || _preparingUpdate || Settings.ClickThrough == enabled) return;
        var previous = Settings.ClickThrough;
        var previousRevision = _clickThroughRevision;
        var revision = ++_clickThroughRevision;
        Settings.ClickThrough = enabled;
        try { PreferencesUpdated?.Invoke(this, EventArgs.Empty); }
        catch (Exception error)
        {
            if (revision == _clickThroughRevision)
            {
                // Applying styles is synchronous. Restore the earlier request's
                // revision too, so its pending save is still allowed to finish.
                _clickThroughRevision = previousRevision;
                error = RestoreClickThrough(previous, error);
            }
            throw new InvalidOperationException("클릭 통과를 적용하지 못했습니다.", error);
        }
        // Apply each input immediately, then serialize saves. An earlier failed save
        // must not undo a newer choice from the settings window, tray, or hotkey.
        await _clickThroughSaveGate.WaitAsync();
        try
        {
            if (revision != _clickThroughRevision) return;
            try
            {
                // The store serializes synchronously before awaiting disk access.
                var persistedValue = Settings.ClickThrough;
                await SaveSettingsAsync();
                _savedClickThrough = persistedValue;
            }
            catch (Exception error)
            {
                if (revision == _clickThroughRevision)
                    error = RestoreClickThrough(_savedClickThrough, error);
                throw new InvalidOperationException("클릭 통과 설정을 저장하지 못했습니다.", error);
            }
        }
        finally { _clickThroughSaveGate.Release(); }
    }
    private Exception RestoreClickThrough(bool enabled, Exception originalError)
    {
        Settings.ClickThrough = enabled;
        try { PreferencesUpdated?.Invoke(this, EventArgs.Empty); }
        catch (Exception restoreError) { return new AggregateException(originalError, restoreError); }
        return originalError;
    }
    public void SetWidgetTransparency(int value)
    {
        var percent = Math.Clamp(value, 0, 100);
        if (Settings.WidgetTransparencyPercent == percent) return;
        Settings.WidgetTransparencyPercent = percent;
        WidgetAppearanceUpdated?.Invoke(this, EventArgs.Empty);
    }
    public UsageForecast GetForecast(AccountSettings account, string windowId)
    {
        if (!Settings.UsageForecastEnabled) return new(UsageForecastState.Unavailable);
        try { return _forecast.GetForecast(account, _coordinator.GetStatus(account.Id), windowId, DateTimeOffset.UtcNow); }
        catch (KeyNotFoundException) { return new(UsageForecastState.Unavailable); }
    }
    private void ConfigureForecast(AccountViewModel account) => account.ConfigureForecast(
        Settings.UsageForecastEnabled, Settings.ShowUsageForecast, id => GetForecast(account.Settings, id), Settings.SaveUsageStatistics);
    public async Task SetUsageStatisticsEnabledAsync(bool enabled)
    {
        var previous = Settings.SaveUsageStatistics;
        Settings.SaveUsageStatistics = enabled;
        try { await SaveSettingsAsync(); }
        catch { Settings.SaveUsageStatistics = previous; throw; }
        foreach (var account in ViewModel.Accounts) ConfigureForecast(account);
        ViewModel.Tick();
        PreferencesUpdated?.Invoke(this, EventArgs.Empty);
    }
    public async Task ClearUsageStatisticsAsync()
    {
        if (_clearingUsageStatistics) return;
        _clearingUsageStatistics = true;
        try
        {
            _statistics.Clear();
            _forecast.Clear();
            ViewModel.Tick();
            UsageUpdated?.Invoke(this, EventArgs.Empty);
            // An empty statistics file also prevents re-importing the previous forecast cache.
            await SaveUsageStatisticsAsync();
            if (!IsDemo)
            {
                await _store.SaveForecastHistoryAsync(new());
                await _store.ClearStatisticsBackupsAsync();
            }
        }
        finally { _clearingUsageStatistics = false; }
    }
    public async Task SetForecastPreferencesAsync(bool enabled, bool showInWidget)
    {
        var previous = (Settings.UsageForecastEnabled, Settings.ShowUsageForecast);
        Settings.UsageForecastEnabled = enabled;
        Settings.ShowUsageForecast = showInWidget;
        try { await SaveSettingsAsync(); }
        catch
        {
            Settings.UsageForecastEnabled = previous.UsageForecastEnabled;
            Settings.ShowUsageForecast = previous.ShowUsageForecast;
            throw;
        }
        foreach (var account in ViewModel.Accounts) ConfigureForecast(account);
        ViewModel.Tick();
        PreferencesUpdated?.Invoke(this, EventArgs.Empty);
    }
    public async Task SetTrayWindowAsync(string accountId, WidgetWindowSelection? selection)
    {
        var account = Settings.Accounts.FirstOrDefault(candidate => candidate.Id == accountId);
        if (account is null || account.TrayWindow == selection) return;
        if (selection is not null && (string.IsNullOrWhiteSpace(selection.Id) || string.IsNullOrWhiteSpace(selection.Name)))
            throw new ArgumentException("표시할 한도를 선택해 주세요.");
        account.TrayWindow = selection;
        UsageUpdated?.Invoke(this, EventArgs.Empty);
        await SaveSettingsAsync();
    }
    private void CoordinatorChanged(object? sender, AccountUsageChangedEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            if (_disposed || _preparingUpdate) return;
            ViewModel.Accounts.FirstOrDefault(x => x.Id == e.Status.AccountId)?.Apply(e.Status);
            ViewModel.Tick();
            UsageUpdated?.Invoke(this, EventArgs.Empty);
            if (!e.Status.IsRefreshing)
            {
                try
                {
                    var account = Settings.Accounts.FirstOrDefault(a => a.Id == e.Status.AccountId);
                    var snapshot = e.Status.Snapshot;
                    if (account is not null)
                    {
                        _forecast.SynchronizeAccount(account);
                        _statistics.SynchronizeAccount(account);
                    }
                    _observedSnapshots.TryGetValue(e.Status.AccountId, out var previous);
                    if (account is not null && e.Status.State == UsageState.Ready && snapshot is not null && !ReferenceEquals(previous, snapshot))
                    {
                        // Cached snapshots and preference-change events must never trigger alerts.
                        _observedSnapshots[account.Id] = snapshot;
                        await ObserveResetMessageAsync(account, snapshot);
                        if (Settings.SaveUsageStatistics && !_clearingUsageStatistics && _statistics.Observe(account, snapshot, DateTimeOffset.UtcNow))
                        {
                            // Forecasts consume the saved series even when their display/calculation was disabled.
                            _forecast.Observe(account, snapshot, DateTimeOffset.UtcNow);
                            ViewModel.Tick();
                            UsageUpdated?.Invoke(this, EventArgs.Empty);
                            try { await SaveUsageStatisticsAsync(); }
                            catch (Exception error)
                            {
                                // Statistics storage must not interrupt usage alerts or the last-good cache.
                                ShowError(new InvalidOperationException("사용량 통계를 저장하지 못했습니다.", error));
                            }
                        }
                        var alerts = _alerts.Evaluate(account, snapshot, DateTimeOffset.UtcNow, isFresh: true);
                        // Keep the reset baseline even when this read produces no notification.
                        // Persist both observations and delivery receipts before showing alerts.
                        await SaveAlertStateAsync();
                        if (alerts.Count > 0 && !IsDemo)
                        {
                            if (!_disposed && Settings.Accounts.Any(a => a.Id == account.Id && a.IdentityKey == snapshot.IdentityKey))
                                UsageAlertsRaised?.Invoke(alerts);
                        }
                    }
                    await SaveSettingsAsync(); await SaveSnapshotsAsync();
                }
                catch (Exception ex) { ShowError(ex); }
            }
        });
    }
    public Task SaveSettingsAsync() => IsDemo ? Task.CompletedTask : _store.SaveSettingsAsync(Settings);
    private Task SaveSnapshotsAsync() => IsDemo ? Task.CompletedTask : _store.SaveSnapshotsAsync(_coordinator.GetSnapshots());
    private Task SaveAlertStateAsync() => IsDemo ? Task.CompletedTask : _store.SaveAlertStateAsync(_alerts.GetState());
    private Task SaveUsageStatisticsAsync() => IsDemo ? Task.CompletedTask : _store.SaveUsageStatisticsAsync(_statistics.GetHistory(DateTimeOffset.UtcNow));
    public void ShowError(Exception error)
    {
        if (error is OperationCanceledException) return;
        ViewModel.Error = error is ProviderException or InvalidOperationException ? error.Message : "작업을 완료하지 못했습니다. 연결 상태와 설정을 확인해 주세요.";
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) Resume(); }
    private void NetworkChanged(object? sender, NetworkAvailabilityEventArgs e) { if (e.IsAvailable) Resume(); }
    private void UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Application.Current.Dispatcher.BeginInvoke(() => ThemeManager.Apply(Settings.Theme));
    private void TimeChanged(object? sender, EventArgs e) => Application.Current.Dispatcher.BeginInvoke(async () =>
    {
        if (_disposed) return;
        try { await _coordinator.RefreshAfterClockChangeAsync(_shutdown.Token); } catch (OperationCanceledException) { }
    });
    private void Resume() => Application.Current.Dispatcher.BeginInvoke(async () =>
    {
        if (_disposed) return;
        try { await _coordinator.RefreshDueAsync(_shutdown.Token); } catch (OperationCanceledException) { }
    });
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _widgetVisibilityGate.WaitAsync();
        _widgetVisibilityGate.Release();
        _clock.Stop();
        _resetMessageTimer.Stop();
        SystemEvents.PowerModeChanged -= PowerChanged;
        SystemEvents.TimeChanged -= TimeChanged;
        SystemEvents.UserPreferenceChanged -= UserPreferenceChanged;
        NetworkChange.NetworkAvailabilityChanged -= NetworkChanged;
        _coordinator.Changed -= CoordinatorChanged;
        _settingsWindow?.Close();
        await _shutdown.CancelAsync();
        await _resetMessageWork;
        try { await _updateInitialization; } catch (OperationCanceledException) { }
        if (Updates is not null) await Updates.DisposeAsync();
        _updateHttp?.Dispose();
        _downloadHttp?.Dispose();
        await Task.WhenAll(_connectionCompletions.Values.Select(x => x.Task).ToArray());
        await _coordinator.DisposeAsync();
        // Installation already crossed the durable-save boundary before launching its wizard.
        // Do not introduce another fallible write after handing control to that process.
        if (!UpdateInstallerLaunched)
        {
            await SaveSettingsAsync();
            await SaveSnapshotsAsync();
            await SaveAlertStateAsync();
            await SaveUsageStatisticsAsync();
        }
        _runtime?.Dispose();
        _shutdown.Dispose();
    }

    private sealed class DemoProvider(ProviderKind kind, IReadOnlyList<UsageSnapshot> snapshots) : IUsageProvider
    {
        public ProviderKind Kind => kind;
        public Task<ConnectionResult> ConnectAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken) => throw new InvalidOperationException("데모 계정입니다.");
        public Task DisconnectAsync(AccountSettings account, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<UsageSnapshot> FetchAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken) => Task.FromResult(snapshots.Single(x => x.AccountId == account.Id) with { FetchedAt = DateTimeOffset.UtcNow });
    }
}

internal static class StartupManager
{
    internal static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("AIUsageWidget") is string command &&
                string.Equals(command, $"\"{Environment.ProcessPath}\" --startup", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }
    internal static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (!enabled) { key.DeleteValue("AIUsageWidget", false); return; }
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일의 경로를 확인할 수 없습니다.");
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("자동 시작은 설치된 앱에서 설정할 수 있습니다.");
        key.SetValue("AIUsageWidget", $"\"{executable}\" --startup");
    }
}
