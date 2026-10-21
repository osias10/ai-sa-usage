using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public partial class UsageStatisticsView : UserControl
{
    private AppController? _controller;
    private bool _updating;
    private bool _subscribed;
    private bool _saving;
    private bool _refreshQueued;
    private readonly HashSet<AccountViewModel> _observedAccounts = [];
    private readonly DispatcherTimer _timer;

    public UsageStatisticsView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += (_, _) => { if (IsVisible) RefreshView(); };
        Loaded += (_, _) => { Subscribe(); RefreshView(); if (IsVisible) _timer.Start(); };
        Unloaded += (_, _) => { _timer.Stop(); Unsubscribe(); };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { RefreshView(); if (IsLoaded) _timer.Start(); }
            else _timer.Stop();
        };
    }

    internal void Initialize(AppController controller)
    {
        if (ReferenceEquals(_controller, controller)) return;
        Unsubscribe();
        _controller = controller;
        if (IsLoaded) Subscribe();
    }

    private void Subscribe()
    {
        if (_controller is null || _subscribed) return;
        _subscribed = true;
        _controller.UsageUpdated += ContextChanged;
        _controller.PreferencesUpdated += ContextChanged;
        _controller.ViewModel.Accounts.CollectionChanged += AccountsChanged;
        ObserveAccounts();
    }

    private void Unsubscribe()
    {
        if (_controller is not null && _subscribed)
        {
            _controller.UsageUpdated -= ContextChanged;
            _controller.PreferencesUpdated -= ContextChanged;
            _controller.ViewModel.Accounts.CollectionChanged -= AccountsChanged;
        }
        foreach (var account in _observedAccounts) account.PropertyChanged -= AccountChanged;
        _observedAccounts.Clear();
        _subscribed = false;
    }

    private void ObserveAccounts()
    {
        foreach (var account in _observedAccounts) account.PropertyChanged -= AccountChanged;
        _observedAccounts.Clear();
        if (_controller is null) return;
        foreach (var account in _controller.ViewModel.Accounts)
        {
            _observedAccounts.Add(account);
            account.PropertyChanged += AccountChanged;
        }
    }

    private void ContextChanged(object? sender, EventArgs e) => QueueRefresh();
    private void AccountsChanged(object? sender, NotifyCollectionChangedEventArgs e) { ObserveAccounts(); QueueRefresh(); }
    private void AccountChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountViewModel.Alias) or nameof(AccountViewModel.Identity) or nameof(AccountViewModel.HasWindows)) QueueRefresh();
    }
    private void QueueRefresh()
    {
        if (_refreshQueued || !IsVisible || !IsLoaded || Dispatcher.HasShutdownStarted) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _refreshQueued = false;
            if (IsLoaded && IsVisible) RefreshView();
        }));
    }
    private void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && IsLoaded) RefreshView();
    }

    internal void RefreshView()
    {
        if (_controller is null || _updating) return;
        _updating = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var history = _controller.GetUsageStatisticsHistory(now);
            StatisticsRecordingChoice.IsChecked = _controller.Settings.SaveUsageStatistics;
            StatisticsRecordingChoice.IsEnabled = !_saving;
            StatisticsStatusLabel.Text = _controller.Settings.SaveUsageStatistics
                ? "갱신할 때마다 저장 · 최근 24시간 보관"
                : "기록 저장 꺼짐 · 남아 있는 최근 24시간 기록을 표시합니다.";
            if (_controller.IsDemo) StatisticsStatusLabel.Text = "데모 · " + StatisticsStatusLabel.Text;
            SetChoices(StatisticsAccountChoice, _controller.ViewModel.Accounts
                .Select(account => new StatisticsOption(account.Id, $"{account.ProviderName} · {account.Alias}")).ToList());
            var accountVm = _controller.ViewModel.Accounts.FirstOrDefault(account => account.Id == StatisticsAccountChoice.SelectedValue as string);
            var account = accountVm?.Settings;
            var accountSamples = account is null ? [] : history.Samples.Where(sample => sample.AccountId == account.Id &&
                sample.Provider == account.Provider && sample.IdentityKey == account.IdentityKey).ToList();
            var windows = (accountVm?.Windows.Select(window => new StatisticsOption(window.Source.Id, window.Source.Name)) ?? [])
                .Concat(accountSamples.OrderByDescending(sample => sample.FetchedAt).Select(sample => new StatisticsOption(sample.WindowId, sample.WindowName)))
                .DistinctBy(option => option.Id).ToList();
            SetChoices(StatisticsWindowChoice, windows);
            var windowId = StatisticsWindowChoice.SelectedValue as string;
            var hours = StatisticsRangeChoice.SelectedIndex switch { 0 => 1, 1 => 6, _ => 24 };
            var report = account is not null && windowId is not null
                ? UsageStatisticsReportBuilder.Build(accountSamples, account, windowId, now, TimeSpan.FromHours(hours)) : null;
            StatisticsChart.Report = report;
            StatisticsChart.InvalidateVisual();
            StatisticsLatestLabel.Text = Percent(report?.LatestPercent);
            StatisticsPeakLabel.Text = Percent(report?.PeakPercent);
            StatisticsIncreaseLabel.Text = report?.ObservedIncreasePercent is { } increase ? $"{increase:0.#} %p" : "—";
            StatisticsCountLabel.Text = $"{report?.SampleCount ?? 0:N0}건";
            StatisticsLastObservedLabel.Text = report?.LastObservedAt is { } last
                ? $"마지막 기록 {last.ToLocalTime():MM.dd HH:mm:ss} · 점에 마우스를 올리거나 ← → 키로 확인"
                : "조회된 기록만 표시합니다.";
            var hasSamples = report?.SampleCount > 0;
            StatisticsEmptyPanel.Visibility = hasSamples ? Visibility.Collapsed : Visibility.Visible;
            StatisticsEmptyLabel.Text = account is null ? "연결된 계정이 없습니다.\n계정 탭에서 계정을 추가해 주세요."
                : accountSamples.Any(sample => sample.WindowId == windowId) ? "선택한 기간에 기록이 없습니다.\n기간을 넓혀 확인해 주세요."
                : !_controller.Settings.SaveUsageStatistics ? "저장된 기록이 없습니다.\n기록 저장을 켜면 다음 갱신부터 모읍니다."
                : "아직 저장된 기록이 없습니다.\n다음 갱신부터 사용량 추이를 확인할 수 있습니다.";
            StatisticsRecords.ItemsSource = report?.Samples.Reverse().Take(50).Select(sample => new StatisticsRecordRow(
                sample.FetchedAt.ToLocalTime().ToString("MM.dd HH:mm:ss", CultureInfo.CurrentCulture), Percent(sample.UsedPercent),
                sample.ResetsAt?.ToLocalTime().ToString("MM.dd HH:mm:ss", CultureInfo.CurrentCulture) ?? "—")).ToArray() ?? [];
            StatisticsRecordsHint.Text = hasSamples ? "선택한 기간의 최신 기록 최대 50개" : "표시할 기록이 없습니다.";
        }
        finally { _updating = false; }
    }

    private async void RecordingChanged(object sender, RoutedEventArgs e)
    {
        if (_updating || _saving || !IsLoaded || _controller is null) return;
        _saving = true;
        StatisticsRecordingChoice.IsEnabled = false;
        try { await _controller.SetUsageStatisticsEnabledAsync(StatisticsRecordingChoice.IsChecked == true); }
        catch (Exception error) { _controller.ShowError(error); }
        finally { _saving = false; if (IsLoaded) RefreshView(); }
    }

    private static string Percent(double? value) => value is { } number ? $"{number:0.#}%" : "—";
    private static void SetChoices(ComboBox control, List<StatisticsOption> options)
    {
        var selected = control.SelectedValue as string;
        if (control.ItemsSource is not List<StatisticsOption> existing || !existing.SequenceEqual(options)) control.ItemsSource = options;
        control.SelectedValue = options.Any(option => option.Id == selected) ? selected : options.FirstOrDefault()?.Id;
        control.IsEnabled = options.Count > 0;
    }

    private sealed record StatisticsOption(string Id, string Name);
    private sealed record StatisticsRecordRow(string FetchedAt, string UsedPercent, string ResetsAt);
}
