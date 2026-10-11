using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using AIUsage.Core;

namespace AIUsage.App;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name);
        return true;
    }
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncCommand(Func<Task> execute, Action<Exception> onError, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        Refresh();
        try { await execute(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { onError(ex); }
        finally { _running = false; Refresh(); }
    }
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class UsageWindowViewModel(UsageWindow source, Func<Brush> accent, Func<bool>? showRemaining = null, Func<int>? warningThreshold = null) : ObservableModel
{
    public UsageWindow Source { get; } = source;
    public string Id => Source.Id;
    public string Name => Source.Name;
    public string CompactName => UsageFormatting.CompactName(Source);
    public Brush Accent => Source.UsedPercent is { } used && used >= (warningThreshold?.Invoke() ?? 80)
        ? Application.Current?.TryFindResource("WarningBrush") as Brush ?? accent()
        : accent();
    public double Value => Source.UsedPercent is { } used ? showRemaining?.Invoke() == true ? 100 - Math.Clamp(used, 0, 100) : Math.Clamp(used, 0, 100) : 0;
    public bool HasValue => Source.UsedPercent.HasValue;
    public string Percent => HasValue ? $"{Value:0.#}%" : "—";
    public string DisplayLabel => showRemaining?.Invoke() == true ? "잔여량" : "사용량";
    public string DisplayDetail => $"{Name} · {DisplayLabel} {Percent}\n{ResetDetail}";
    public string Reset => FormatReset(Source.ResetsAt);
    public string CompactReset => UsageFormatting.CompactRemaining(Source.ResetsAt, DateTimeOffset.UtcNow);
    public string ResetDetail => Source.ResetsAt?.ToLocalTime().ToString("M월 d일 HH:mm:ss 초기화 예정", CultureInfo.GetCultureInfo("ko-KR")) ?? "서비스에서 초기화 시각을 제공하지 않습니다.";
    public void Tick() { Changed(nameof(Reset)); Changed(nameof(CompactReset)); Changed(nameof(Accent)); }
    public void RefreshDisplay()
    {
        Changed(nameof(Value)); Changed(nameof(Percent)); Changed(nameof(DisplayLabel)); Changed(nameof(DisplayDetail)); Changed(nameof(Accent));
    }
    public static string FormatReset(DateTimeOffset? reset)
    {
        if (reset is null) return "초기화 시간 미제공";
        var remaining = reset.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return "초기화 확인 중";
        if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays}일 {remaining.Hours}시간 후 초기화";
        if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours}시간 {remaining.Minutes}분 후 초기화";
        if (remaining.TotalMinutes >= 1) return $"{(int)remaining.TotalMinutes}분 {remaining.Seconds:00}초 후 초기화";
        return $"{Math.Max(1, remaining.Seconds)}초 후 초기화";
    }
}

public sealed class UsageAlertViewModel(UsageAlertRule rule, string name, Action<UsageAlertRule> onChanged) : ObservableModel
{
    private string _name = name;
    public string WindowId => rule.WindowId;
    public string Name => _name;
    public bool Enabled
    {
        get => rule.Enabled;
        set { if (rule.Enabled == value) return; rule.Enabled = value; Changed(); onChanged(rule); }
    }
    public int ThresholdPercent
    {
        get => rule.ThresholdPercent;
        set
        {
            if (value is < 1 or > 100 || rule.ThresholdPercent == value) return;
            rule.ThresholdPercent = value;
            Changed();
            onChanged(rule);
        }
    }
    internal void UpdateName(string value) => Set(ref _name, value, nameof(Name));
}

public sealed class WidgetWindowOptionViewModel(string id, Action<WidgetWindowOptionViewModel, bool> onSelectionChanged) : ObservableModel
{
    private string _name = "";
    private bool _isSelected;
    private bool _canToggle = true;
    private bool _isAvailable;
    public string Id { get; } = id;
    public string Name => _name;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            onSelectionChanged(this, value);
            Changed();
        }
    }
    public bool CanToggle => _canToggle;
    public bool IsAvailable => _isAvailable;
    public string Detail => !CanToggle ? "한 개 이상 선택해야 합니다. 다른 한도를 먼저 선택하세요."
        : IsAvailable ? Name : $"{Name}\n현재 응답에 없는 한도입니다. 사용량이 없으면 —로 표시합니다.";
    internal void Update(string name, bool isSelected, bool isAvailable, bool canToggle)
    {
        Set(ref _name, name, nameof(Name));
        Set(ref _isSelected, isSelected, nameof(IsSelected));
        Set(ref _isAvailable, isAvailable, nameof(IsAvailable));
        Set(ref _canToggle, canToggle, nameof(CanToggle));
        Changed(nameof(Detail));
    }
}

public sealed class AccountViewModel : ObservableModel
{
    private readonly Action<AccountViewModel> _onSettings;
    private UsageSnapshot? _snapshot;
    private UsageState _state = UsageState.SignInRequired;
    private string? _message;
    private bool _refreshing;
    private string? _progress;
    private bool _canCancelConnection;
    private bool _updatingWindows;
    private bool _showRemaining;
    private bool _showLastChecked = true;
    private bool _pendingWidgetSettingsChange;
    private IReadOnlyList<UsageWindowViewModel> _compactWindows = [];
    public AccountSettings Settings { get; }
    public AccountViewModel(AccountSettings settings, Func<AccountViewModel, Task> refresh, Func<AccountViewModel, Task> connect,
        Func<AccountViewModel, Task> remove, Action<AccountViewModel> onSettings, Action<AccountViewModel, int> reorder, Action<AccountViewModel> cancel, Action<Exception> onError)
    {
        Settings = settings;
        _onSettings = onSettings;
        RefreshCommand = new AsyncCommand(() => refresh(this), onError, () => !IsRefreshing);
        ConnectCommand = new AsyncCommand(() => connect(this), onError, () => !IsRefreshing);
        RemoveCommand = new AsyncCommand(() => remove(this), onError, () => !IsRefreshing);
        UpCommand = new RelayCommand(() => reorder(this, -1));
        DownCommand = new RelayCommand(() => reorder(this, 1));
        CancelCommand = new RelayCommand(() => cancel(this));
    }
    public string Id => Settings.Id;
    public string ProviderName => Settings.Provider switch { ProviderKind.Claude => "Claude", ProviderKind.Codex => "Codex", _ => "Antigravity" };
    public Geometry IconGeometry => ProviderIcons.Get(Settings.Provider);
    public Brush Accent => (Brush)Application.Current.FindResource("AccentBrush");
    public bool ShowRemaining
    {
        get => _showRemaining;
        set
        {
            if (!Set(ref _showRemaining, value)) return;
            foreach (var window in Windows.Concat(_compactWindows).Distinct()) window.RefreshDisplay();
            Changed(nameof(DisplayLabel)); Changed(nameof(DisplayDescription));
            Changed(nameof(PrimaryPercent)); Changed(nameof(PrimaryValue));
        }
    }
    public string DisplayLabel => ShowRemaining ? "잔여량" : "사용량";
    public string DisplayDescription => ShowRemaining ? "숫자와 게이지는 남은 사용량입니다." : "숫자와 게이지는 사용한 비율입니다.";
    public bool ShowLastChecked { get => _showLastChecked; set => Set(ref _showLastChecked, value); }
    public string Alias
    {
        get => Settings.Alias;
        set { if (Settings.Alias == value) return; Settings.Alias = value; Changed(); _onSettings(this); }
    }
    public bool Pinned
    {
        get => Settings.Pinned;
        set { if (Settings.Pinned == value) return; Settings.Pinned = value; Changed(); _onSettings(this); }
    }
    public bool AutoRefresh
    {
        get => Settings.AutoRefresh;
        set { if (Settings.AutoRefresh == value) return; Settings.AutoRefresh = value; Changed(); Changed(nameof(RefreshDescription)); _onSettings(this); }
    }
    public int RefreshMinutes
    {
        get => Settings.RefreshMinutes;
        set { if (value < 1 || value > 1440 || value == Settings.RefreshMinutes) return; Settings.RefreshMinutes = value; Changed(); Changed(nameof(RefreshDescription)); _onSettings(this); }
    }
    public string? SelectedWindowId
    {
        get => Settings.SelectedWindowId ?? Primary?.Id;
        set { if (_updatingWindows || Settings.SelectedWindowId == value) return; Settings.SelectedWindowId = value; Changed(); Changed(nameof(Primary)); RaisePrimary(); _onSettings(this); }
    }
    public string RefreshDescription => AutoRefresh ? $"{RefreshMinutes}분마다 자동 갱신" : "자동 갱신 꺼짐";
    public string Identity => Settings.Email ?? "미연결";
    public string Plan => _snapshot?.Plan ?? Settings.Plan ?? "구독";
    public bool IsRefreshing => _refreshing;
    public bool CanCancelConnection
    {
        get => _canCancelConnection;
        set
        {
            if (!Set(ref _canCancelConnection, value)) return;
            Changed(nameof(StateLabel)); Changed(nameof(Notice)); Changed(nameof(HasNotice));
        }
    }
    public bool HasWindows => Windows.Count > 0;
    public bool NeedsConnection => _state == UsageState.SignInRequired;
    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);
    public bool HasUsageIssue => !IsRefreshing && _state != UsageState.Ready;
    public bool HasStatusIndicator => IsRefreshing || HasUsageIssue;
    public string StatusIcon => IsRefreshing ? "\uE72C" : "\uE7BA";
    public string Notice => IsRefreshing ? _progress ?? StateLabel : _message ?? (_state switch
    {
        UsageState.SignInRequired => "로그인이 필요합니다.",
        UsageState.Offline => "오프라인 · 이전 값 표시",
        UsageState.RateLimited => "조회 제한 · 재시도 대기",
        UsageState.UpdateRequired => "연결 도구 업데이트 필요",
        UsageState.Unavailable => "사용량 미제공",
        _ => ""
    });
    public string StateLabel => IsRefreshing ? CanCancelConnection ? "연결 중" : "새로고침 중" : _state switch
    {
        UsageState.Ready => "연결됨", UsageState.SignInRequired => "로그인 필요", UsageState.Offline => "오프라인",
        UsageState.RateLimited => "잠시 대기", UsageState.UpdateRequired => "업데이트 필요", _ => "확인 필요"
    };
    public Brush StateBrush => (Brush)Application.Current.FindResource(IsRefreshing ? "AccentBrush" : HasUsageIssue ? "WarningBrush" : "MutedBrush");
    public string UpdatedLabel
    {
        get
        {
            if (_snapshot is null) return "조회 기록 없음";
            var elapsed = DateTimeOffset.UtcNow - _snapshot.FetchedAt;
            string label = elapsed.TotalSeconds < 60 ? "방금 갱신" : elapsed.TotalHours < 1 ? $"{Math.Max(1, (int)elapsed.TotalMinutes)}분 전 갱신" : $"{Math.Max(1, (int)elapsed.TotalHours)}시간 전 갱신";
            return _state == UsageState.Ready ? label : $"이전 값 · {label}";
        }
    }
    public string UpdatedDetail => _snapshot?.FetchedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "사용량 기록이 없습니다.";
    public string CompactUpdatedLabel
    {
        get
        {
            if (_snapshot is null) return "확인 기록 없음";
            var fetchedAt = _snapshot.FetchedAt.ToLocalTime();
            var now = DateTimeOffset.Now;
            var format = fetchedAt.Date == now.Date ? "HH:mm:ss" : fetchedAt.Year == now.Year ? "MM/dd HH:mm:ss" : "yyyy/MM/dd HH:mm:ss";
            var prefix = _state == UsageState.Ready ? "확인" : "이전 확인";
            return $"{prefix} {fetchedAt.ToString(format, CultureInfo.InvariantCulture)}";
        }
    }
    public string LastCheckedDetail => _snapshot is null ? "아직 사용량을 확인하지 못했습니다." : $"마지막 성공 확인: {UpdatedDetail}\n{UpdatedLabel}";
    public ObservableCollection<UsageWindowViewModel> Windows { get; } = [];
    public ObservableCollection<WidgetWindowOptionViewModel> WidgetWindowOptions { get; } = [];
    public bool HasWidgetWindowOptions => WidgetWindowOptions.Count > 0;
    public ObservableCollection<UsageAlertViewModel> UsageAlerts { get; } = [];
    public bool HasUsageAlerts => UsageAlerts.Count > 0;
    public IReadOnlyList<UsageWindowViewModel> CompactWindows => _compactWindows;
    public UsageWindowViewModel? Primary => Windows.FirstOrDefault(x => x.Id == Settings.SelectedWindowId) ?? Windows.FirstOrDefault(x => x.Source.IsPrimary) ?? Windows.FirstOrDefault();
    public string PrimaryPercent => Primary?.Percent ?? "—";
    public string PrimaryReset => Primary?.Reset ?? (_state == UsageState.SignInRequired ? "로그인 필요" : "초기화 시간 미제공");
    public string PrimaryName => Primary?.Name ?? "사용량";
    public double PrimaryValue => Primary?.Value ?? 0;
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand RemoveCommand { get; }
    public RelayCommand UpCommand { get; }
    public RelayCommand DownCommand { get; }
    public RelayCommand CancelCommand { get; }
    public void Apply(AccountUsageStatus status)
    {
        if (!_refreshing && status.IsRefreshing) _progress = null;
        _state = status.State;
        _message = status.Message;
        _refreshing = status.IsRefreshing;
        if (!ReferenceEquals(_snapshot, status.Snapshot))
        {
            _snapshot = status.Snapshot;
            _updatingWindows = true;
            try
            {
                Windows.Clear();
                if (_snapshot is not null) foreach (var window in _snapshot.Windows) Windows.Add(CreateUsageWindow(window));
            }
            finally { _updatingWindows = false; }
        }
        if (!_refreshing) _progress = null;
        RaiseAll();
    }
    public void Report(ProviderProgress value)
    {
        if (!IsRefreshing) return;
        _progress = value.Message;
        Changed(nameof(Notice)); Changed(nameof(HasNotice));
    }
    public void Tick()
    {
        foreach (var window in Windows) window.Tick();
        foreach (var window in _compactWindows) if (!Windows.Contains(window)) window.Tick();
        Changed(nameof(PrimaryReset)); Changed(nameof(UpdatedLabel)); Changed(nameof(Accent)); Changed(nameof(StateBrush));
        Changed(nameof(CompactUpdatedLabel)); Changed(nameof(LastCheckedDetail));
    }
    private void RaisePrimary()
    {
        RebuildCompactWindows();
        Changed(nameof(Primary));
        Changed(nameof(PrimaryPercent)); Changed(nameof(PrimaryReset)); Changed(nameof(PrimaryName)); Changed(nameof(PrimaryValue));
    }
    private void RebuildCompactWindows(bool persistMigration = true)
    {
        _pendingWidgetSettingsChange |= UsageFormatting.EnsureWidgetWindows(Settings, _snapshot);
        _compactWindows = UsageFormatting.SelectCompactWindows(Settings, _snapshot)
            .Select(source => Windows.FirstOrDefault(window => window.Source == source) ?? CreateUsageWindow(source)).ToArray();
        Changed(nameof(CompactWindows));
        RebuildWidgetWindowOptions();
        RebuildUsageAlerts();
        if (persistMigration && _pendingWidgetSettingsChange)
        {
            _pendingWidgetSettingsChange = false;
            _onSettings(this);
        }
    }
    private IReadOnlyList<UsageWindow> WidgetWindowCandidates()
    {
        var candidates = Windows.Select(window => window.Source).ToList();
        if (Settings.Provider == ProviderKind.Claude)
        {
            if (!candidates.Any(window => window.Id.StartsWith("session::", StringComparison.Ordinal)))
                candidates.Add(new("session::0", "현재 세션", null, null));
            if (!candidates.Any(window => window.Id.StartsWith("weekly_all::", StringComparison.Ordinal)))
                candidates.Add(new("weekly_all::0", "주간 전체", null, null));
        }
        foreach (var selected in Settings.WidgetWindows ?? [])
            if (!candidates.Any(window => window.Id == selected.Id)) candidates.Add(new(selected.Id, selected.Name, null, null));
        if (Settings.Provider == ProviderKind.Claude)
            return candidates.OrderBy(window => window.Id.StartsWith("session::", StringComparison.Ordinal) ? 0
                : window.Id.StartsWith("weekly_all::", StringComparison.Ordinal) ? 1 : 2).ToArray();
        return candidates;
    }
    private void RebuildWidgetWindowOptions()
    {
        var candidates = WidgetWindowCandidates();
        var selectedIds = (Settings.WidgetWindows ?? []).Select(window => window.Id).ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var option = WidgetWindowOptions.FirstOrDefault(existing => existing.Id == candidate.Id);
            if (option is null)
            {
                option = new WidgetWindowOptionViewModel(candidate.Id, WidgetWindowSelectionChanged);
                WidgetWindowOptions.Insert(index, option);
            }
            else
            {
                var previousIndex = WidgetWindowOptions.IndexOf(option);
                if (previousIndex != index) WidgetWindowOptions.Move(previousIndex, index);
            }
            var isSelected = selectedIds.Contains(candidate.Id);
            option.Update(candidate.Name, isSelected, Windows.Any(window => window.Id == candidate.Id), !isSelected || selectedIds.Count > 1);
        }
        while (WidgetWindowOptions.Count > candidates.Count) WidgetWindowOptions.RemoveAt(WidgetWindowOptions.Count - 1);
        Changed(nameof(HasWidgetWindowOptions));
    }
    private void WidgetWindowSelectionChanged(WidgetWindowOptionViewModel option, bool isSelected)
    {
        var selected = (Settings.WidgetWindows ?? []).ToList();
        if (isSelected)
        {
            if (selected.Any(window => window.Id == option.Id)) return;
            selected.Add(new(option.Id, option.Name));
        }
        else
        {
            if (selected.Count <= 1 || !selected.Any(window => window.Id == option.Id)) return;
            selected.RemoveAll(window => window.Id == option.Id);
        }
        Settings.WidgetWindows = selected;
        RebuildCompactWindows(persistMigration: false);
        _pendingWidgetSettingsChange = false;
        _onSettings(this);
    }
    private UsageWindowViewModel CreateUsageWindow(UsageWindow source) => new(source, () => Accent, () => ShowRemaining,
        () => Settings.UsageAlerts.FirstOrDefault(rule => rule.WindowId == source.Id && rule.Enabled)?.ThresholdPercent ?? 80);
    private void RebuildUsageAlerts()
    {
        var windows = WidgetWindowCandidates().Where(window => window.Id != "compact:unavailable").DistinctBy(window => window.Id).ToList();
        foreach (var rule in Settings.UsageAlerts)
            if (!windows.Any(window => window.Id == rule.WindowId))
                windows.Add(new(rule.WindowId, UsageAlerts.FirstOrDefault(alert => alert.WindowId == rule.WindowId)?.Name ?? "이전 한도", null, null));
        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            var existing = UsageAlerts.FirstOrDefault(alert => alert.WindowId == window.Id);
            if (existing is null)
            {
                var rule = Settings.UsageAlerts.FirstOrDefault(alert => alert.WindowId == window.Id)
                    ?? new UsageAlertRule { WindowId = window.Id };
                UsageAlerts.Insert(index, new UsageAlertViewModel(rule, window.Name, UsageAlertChanged));
            }
            else
            {
                existing.UpdateName(window.Name);
                var previousIndex = UsageAlerts.IndexOf(existing);
                if (previousIndex != index) UsageAlerts.Move(previousIndex, index);
            }
        }
        while (UsageAlerts.Count > windows.Count) UsageAlerts.RemoveAt(UsageAlerts.Count - 1);
        Changed(nameof(HasUsageAlerts));
    }
    private void UsageAlertChanged(UsageAlertRule rule)
    {
        if (!Settings.UsageAlerts.Any(existing => existing.WindowId == rule.WindowId)) Settings.UsageAlerts.Add(rule);
        foreach (var window in Windows.Concat(_compactWindows).Where(window => window.Id == rule.WindowId).Distinct()) window.RefreshDisplay();
        _onSettings(this);
    }
    public void RaiseAll()
    {
        Changed(nameof(Identity)); Changed(nameof(Plan)); Changed(nameof(IsRefreshing)); Changed(nameof(HasWindows));
        Changed(nameof(NeedsConnection)); Changed(nameof(Notice)); Changed(nameof(HasNotice)); Changed(nameof(StateLabel)); Changed(nameof(StateBrush));
        Changed(nameof(HasUsageIssue)); Changed(nameof(HasStatusIndicator)); Changed(nameof(StatusIcon));
        Changed(nameof(UpdatedLabel)); Changed(nameof(UpdatedDetail)); Changed(nameof(SelectedWindowId)); RaisePrimary();
        Changed(nameof(CompactUpdatedLabel)); Changed(nameof(LastCheckedDetail));
        RefreshCommand.Refresh(); ConnectCommand.Refresh(); RemoveCommand.Refresh();
    }
}

public sealed class MainViewModel : ObservableModel
{
    private string? _error;
    public ObservableCollection<AccountViewModel> Accounts { get; } = [];
    public ObservableCollection<AccountViewModel> PinnedAccounts { get; } = [];
    public bool IsDemo { get; init; }
    public bool IsEmpty => Accounts.Count == 0;
    public bool NoPins => PinnedAccounts.Count == 0;
    public string AccountCount => $"{Accounts.Count}개 계정";
    public string Footer => Accounts.Count == 0 ? "연결하면 여기에 사용량이 표시됩니다" : Accounts.Any(x => x.IsRefreshing) ? "사용량을 확인하고 있습니다…" : "구독 한도 기준 · 기기에만 저장";
    public string? Error { get => _error; set { Set(ref _error, value); Changed(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public required ICommand RefreshAllCommand { get; init; }
    public required ICommand SettingsCommand { get; init; }
    public required ICommand HideCommand { get; init; }
    public void RefreshCollections()
    {
        PinnedAccounts.Clear();
        foreach (var account in Accounts.Where(x => x.Pinned)) PinnedAccounts.Add(account);
        Changed(nameof(IsEmpty)); Changed(nameof(NoPins)); Changed(nameof(AccountCount)); Changed(nameof(Footer));
    }
    public void Tick() { foreach (var account in Accounts) account.Tick(); Changed(nameof(Footer)); }
}

public sealed class InverseBooleanVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is Visibility.Collapsed;
}
