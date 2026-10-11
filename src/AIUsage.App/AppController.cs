using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using AIUsage.Core;
using AIUsage.Infrastructure;
using AIUsage.Providers;
using Microsoft.Win32;

namespace AIUsage.App;

public sealed class AppController : IAsyncDisposable
{
    private readonly JsonSettingsStore _store;
    private readonly UsageCoordinator _coordinator;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DispatcherTimer _clock;
    private readonly IDisposable? _runtime;
    private readonly UsageAlertEngine _alerts;
    private readonly Dictionary<string, UsageSnapshot?> _observedSnapshots = [];
    private SettingsWindow? _settingsWindow;
    private bool _disposed;
    private readonly Dictionary<string, CancellationTokenSource> _connections = [];
    private readonly Dictionary<string, TaskCompletionSource> _connectionCompletions = [];
    public AppSettings Settings { get; }
    public MainViewModel ViewModel { get; }
    public bool IsDemo { get; }
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
        IReadOnlyList<UsageSnapshot> snapshots, bool demo, IDisposable? runtime = null, UsageAlertState? alertState = null)
    {
        Settings = settings;
        _store = store;
        IsDemo = demo;
        _runtime = runtime;
        _alerts = new UsageAlertEngine(alertState);
        _coordinator = new UsageCoordinator(providers, settings.Accounts, snapshots);
        foreach (var account in settings.Accounts) _observedSnapshots[account.Id] = _coordinator.GetStatus(account.Id).Snapshot;
        ViewModel = new MainViewModel
        {
            IsDemo = demo,
            RefreshAllCommand = new AsyncCommand(RefreshAllAsync, ShowError),
            SettingsCommand = new RelayCommand(ShowSettings),
            HideCommand = new RelayCommand(() => Window?.Hide())
        };
        foreach (var account in settings.Accounts.OrderBy(x => x.Order)) ViewModel.Accounts.Add(CreateAccount(account));
        ViewModel.RefreshCollections();
        _coordinator.Changed += CoordinatorChanged;
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => ViewModel.Tick(), Application.Current.Dispatcher);
        if (!demo)
        {
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.TimeChanged += TimeChanged;
            SystemEvents.UserPreferenceChanged += UserPreferenceChanged;
            NetworkChange.NetworkAvailabilityChanged += NetworkChanged;
        }
    }

    public static async Task<AppController> CreateAsync(bool demo, bool mini, bool light)
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
            return new AppController(settings, store, Enum.GetValues<ProviderKind>().Select(kind => new DemoProvider(kind, snapshots)), snapshots, true);
        }
        var loaded = await store.LoadSettingsAsync();
        // Uninstall keeps preferences but removes startup registration. Reflect the
        // current executable's registration instead of silently re-enabling it.
        loaded.StartWithWindows = StartupManager.IsEnabled();
        var cached = await store.LoadSnapshotsAsync();
        var alertState = await store.LoadAlertStateAsync();
        var runtime = new RuntimeManager(paths);
        var runner = new ProcessRunner();
        var login = new DesktopLoginInteraction();
        IUsageProvider[] providers = [new ClaudeUsageProvider(paths, runtime, runner, login), new CodexUsageProvider(paths, runtime, runner, login), new AntigravityUsageProvider(paths, runtime, runner, login)];
        var controller = new AppController(loaded, store, providers, cached, false, runtime, alertState);
        if (store.LastWarning is not null) controller.ViewModel.Error = store.LastWarning;
        return controller;
    }
    private AccountViewModel CreateAccount(AccountSettings account)
    {
        var vm = new AccountViewModel(account, RefreshAccountAsync, ConnectAccountAsync, RemoveAccountAsync, AccountSettingsChanged, Reorder, CancelConnection, ShowError);
        vm.ShowRemaining = Settings.ShowRemaining;
        vm.ShowLastChecked = Settings.ShowLastChecked;
        vm.Apply(_coordinator.GetStatus(account.Id));
        return vm;
    }
    public void Start() { ThemeManager.Apply(Settings.Theme); if (!IsDemo) _coordinator.Start(); }
    public void ShowSettings()
    {
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
    public void ShowWidget()
    {
        if (Window is null) return;
        Window.Show();
        Window.WindowState = WindowState.Normal;
        Window.Activate();
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
    public Task RefreshAllAsync() { ViewModel.Error = null; return _coordinator.RefreshAllAsync(_shutdown.Token); }
    private async Task RemoveAccountAsync(AccountViewModel account)
    {
        if (IsDemo) return;
        var message = $"{account.ProviderName} · {account.Alias} 계정을 위젯에서 삭제할까요?\n다른 계정과 사용 중인 공식 앱은 유지됩니다.";
        if (MessageBox.Show(_settingsWindow ?? (Window)Window!, message, "계정 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await _coordinator.RemoveAccountAsync(account.Id, _shutdown.Token);
        Settings.Accounts.Remove(account.Settings);
        if (Settings.TrayAccountId == account.Id) Settings.TrayAccountId = null;
        _alerts.RemoveAccount(account.Id);
        _observedSnapshots.Remove(account.Id);
        ViewModel.Accounts.Remove(account);
        ViewModel.RefreshCollections();
        UsageUpdated?.Invoke(this, EventArgs.Empty);
        await SaveSettingsAsync();
        await SaveSnapshotsAsync();
        await SaveAlertStateAsync();
    }
    private async void AccountSettingsChanged(AccountViewModel account)
    {
        try { _coordinator.UpdateAccount(account.Settings); ViewModel.RefreshCollections(); UsageUpdated?.Invoke(this, EventArgs.Empty); await SaveSettingsAsync(); }
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
    public void SetWidgetTransparency(int value)
    {
        var percent = Math.Clamp(value, 0, 100);
        if (Settings.WidgetTransparencyPercent == percent) return;
        Settings.WidgetTransparencyPercent = percent;
        WidgetAppearanceUpdated?.Invoke(this, EventArgs.Empty);
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
            if (_disposed) return;
            ViewModel.Accounts.FirstOrDefault(x => x.Id == e.Status.AccountId)?.Apply(e.Status);
            ViewModel.Tick();
            UsageUpdated?.Invoke(this, EventArgs.Empty);
            if (!e.Status.IsRefreshing)
            {
                try
                {
                    var account = Settings.Accounts.FirstOrDefault(a => a.Id == e.Status.AccountId);
                    var snapshot = e.Status.Snapshot;
                    _observedSnapshots.TryGetValue(e.Status.AccountId, out var previous);
                    if (account is not null && e.Status.State == UsageState.Ready && snapshot is not null && !ReferenceEquals(previous, snapshot))
                    {
                        // Cached snapshots and preference-change events must never trigger alerts.
                        _observedSnapshots[account.Id] = snapshot;
                        var alerts = _alerts.Evaluate(account, snapshot, DateTimeOffset.UtcNow, isFresh: true);
                        if (alerts.Count > 0 && !IsDemo)
                        {
                            // Persist the deduplication ledger before notifying, including across restarts.
                            await SaveAlertStateAsync();
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
        _clock.Stop();
        SystemEvents.PowerModeChanged -= PowerChanged;
        SystemEvents.TimeChanged -= TimeChanged;
        SystemEvents.UserPreferenceChanged -= UserPreferenceChanged;
        NetworkChange.NetworkAvailabilityChanged -= NetworkChanged;
        _coordinator.Changed -= CoordinatorChanged;
        _settingsWindow?.Close();
        await _shutdown.CancelAsync();
        await Task.WhenAll(_connectionCompletions.Values.Select(x => x.Task).ToArray());
        await _coordinator.DisposeAsync();
        await SaveSettingsAsync();
        await SaveSnapshotsAsync();
        await SaveAlertStateAsync();
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
