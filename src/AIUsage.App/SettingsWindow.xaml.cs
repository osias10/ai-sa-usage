using System.Globalization;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public partial class SettingsWindow : Window
{
    private readonly AppController _controller;
    private bool _loaded;
    private bool _updatingTrayChoice;
    private bool _updatingTrayWindowChoice;
    private string? _trayWindowAccountId;
    private readonly DispatcherTimer _transparencySaveTimer;
    private bool _transparencySavePending;
    private Task _transparencySave = Task.CompletedTask;
    private Exception? _transparencySaveError;
    private bool _flushingPendingEdits;
    private readonly HashSet<TextBox> _editedTextBoxes = [];
    private bool _closingForTransparencySave;
    private bool _closeAfterTransparencySave;
    private bool _windowClosed;
    private readonly HashSet<AccountViewModel> _observedTrayAccounts = [];
    private readonly DispatcherTimer _updateRetryTimer;
    private readonly Version _currentVersion;
    private bool _refreshingUpdateUi;
    private bool _updateActionRunning;
    private bool _installingUpdate;
    private bool _savingAutoCheck;
    private string? _localUpdateMessage;
    private bool _localUpdateIsError;
    private int _updateRefreshQueued;
    public SettingsWindow(AppController controller)
    {
        _controller = controller;
        _transparencySaveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _transparencySaveTimer.Tick += TransparencySaveTimerTick;
        _updateRetryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _updateRetryTimer.Tick += UpdateRetryTimerTick;
        InitializeComponent();
        AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(TrackTextEdit));
        DataContext = controller.ViewModel;
        AddAccountExpander.IsExpanded = controller.ViewModel.IsEmpty;
        _currentVersion = controller.Updates?.CurrentVersion ?? Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        VersionLabel.Text = $"현재 버전 {_currentVersion.Major}.{_currentVersion.Minor}.{Math.Max(0, _currentVersion.Build)}";
        if (controller.IsDemo) SaveStatus.Text = "데모 · 저장되지 않음";
        TopmostChoice.IsChecked = controller.Settings.AlwaysOnTop;
        LockChoice.IsChecked = controller.Settings.PositionLocked;
        StartupChoice.IsChecked = controller.Settings.StartWithWindows;
        ThemeChoice.SelectedIndex = controller.Settings.Theme switch { "Dark" => 1, "Light" => 2, _ => 0 };
        WidgetTransparencySlider.Value = Math.Clamp(controller.Settings.WidgetTransparencyPercent, 0, 100);
        LastCheckedChoice.IsChecked = controller.Settings.ShowLastChecked;
        DisplayModeChoice.SelectedIndex = controller.Settings.ShowRemaining ? 1 : 0;
        controller.ViewModel.Accounts.CollectionChanged += TrayAccountsChanged;
        controller.UsageUpdated += TrayContextChanged;
        controller.PreferencesUpdated += TrayContextChanged;
        if (controller.Updates is { } updates) updates.Changed += UpdateServiceChanged;
        ObserveTrayAccounts();
        RefreshUpdateInfo();
        _loaded = true;
        KeyDown += (_, e) => { if (!e.Handled && e.Key == Key.Escape) Close(); };
        Closing += SettingsClosing;
        Closed += (_, _) =>
        {
            _windowClosed = true;
            _transparencySaveTimer.Stop();
            _transparencySaveTimer.Tick -= TransparencySaveTimerTick;
            _updateRetryTimer.Stop();
            _updateRetryTimer.Tick -= UpdateRetryTimerTick;
            if (controller.Updates is { } closedUpdates) closedUpdates.Changed -= UpdateServiceChanged;
            controller.ViewModel.Accounts.CollectionChanged -= TrayAccountsChanged;
            controller.UsageUpdated -= TrayContextChanged;
            controller.PreferencesUpdated -= TrayContextChanged;
            foreach (var account in _observedTrayAccounts) account.PropertyChanged -= TrayAccountChanged;
            _observedTrayAccounts.Clear();
            _editedTextBoxes.Clear();
        };
    }
    private void WidgetTransparencyChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded || _closingForTransparencySave) return;
        _controller.SetWidgetTransparency(Math.Clamp((int)Math.Round(e.NewValue), 0, 100));
        _transparencySavePending = true;
        _transparencySaveTimer.Stop();
        _transparencySaveTimer.Start();
    }
    private void TransparencySaveTimerTick(object? sender, EventArgs e)
    {
        _transparencySaveTimer.Stop();
        if (_transparencySavePending) QueueTransparencySave();
    }
    private void QueueTransparencySave()
    {
        _transparencySavePending = false;
        _transparencySave = SaveTransparencyAsync(_transparencySave);
    }
    private async Task SaveTransparencyAsync(Task previousSave)
    {
        try
        {
            await previousSave;
            _transparencySaveError = null;
            await _controller.SaveSettingsAsync();
            SaveStatus.Text = _controller.IsDemo ? "데모 · 저장되지 않음" : "자동 저장";
        }
        catch (Exception ex)
        {
            _transparencySaveError = ex;
            SaveStatus.Text = "설정을 저장하지 못했습니다.";
            if (!_flushingPendingEdits) _controller.ShowError(ex);
        }
    }
    private void TrackTextEdit(object sender, TextChangedEventArgs e)
    {
        if (e.OriginalSource is TextBox editor && editor.IsKeyboardFocusWithin && editor.GetBindingExpression(TextBox.TextProperty) is not null)
            _editedTextBoxes.Add(editor);
    }
    public async Task FlushPendingEditsAsync()
    {
        _flushingPendingEdits = true;
        try
        {
            // Remember edited controls across tabs, including invalid values that could not update their source.
            var editors = TextEditors(this).Concat(_editedTextBoxes).Distinct().Where(editor => editor.DataContext switch
            {
                AccountViewModel account => _controller.ViewModel.Accounts.Contains(account),
                UsageAlertViewModel alert => _controller.ViewModel.Accounts.Any(account => account.UsageAlerts.Contains(alert)),
                _ => true
            }).ToArray();
            foreach (var editor in editors)
            {
                editor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                if (Validation.GetHasError(editor))
                    throw new InvalidOperationException("입력한 설정값을 확인한 뒤 다시 설치해 주세요.");
            }
            _transparencySaveTimer.Stop();
            if (_transparencySavePending || _transparencySaveError is not null) QueueTransparencySave();
            await _transparencySave;
            if (_transparencySaveError is { } error)
                throw new IOException("설정을 저장하지 못했습니다. 저장 후 다시 설치해 주세요.", error);
            await _controller.SaveSettingsAsync();
            _editedTextBoxes.Clear();
        }
        finally { _flushingPendingEdits = false; }
    }
    private static IEnumerable<TextBox> TextEditors(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TextBox editor) yield return editor;
            foreach (var descendant in TextEditors(child)) yield return descendant;
        }
    }
    private async void SettingsClosing(object? sender, CancelEventArgs e)
    {
        if (_closeAfterTransparencySave) return;
        if (Keyboard.FocusedElement is TextBox editor && IsAncestorOf(editor))
            editor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        _transparencySaveTimer.Stop();
        if (_transparencySavePending) QueueTransparencySave();
        if (_transparencySave.IsCompleted) return;
        e.Cancel = true;
        if (_closingForTransparencySave) return;
        _closingForTransparencySave = true;
        IsEnabled = false;
        await _transparencySave;
        _closeAfterTransparencySave = true;
        // Defer the second Close so a synchronously completed save cannot reenter Closing.
        if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(new Action(() => { if (!_windowClosed) Close(); }));
    }
    private async void AddAccount(object sender, RoutedEventArgs e)
    {
        if (_controller.IsDemo) { SaveStatus.Text = "데모에서는 계정을 연결할 수 없습니다."; return; }
        AddButton.IsEnabled = false;
        try
        {
            var provider = ProviderChoice.SelectedIndex switch { 0 => ProviderKind.Claude, 1 => ProviderKind.Codex, _ => ProviderKind.Antigravity };
            await _controller.AddAccountAsync(provider, string.IsNullOrWhiteSpace(NewAlias.Text) ? "개인" : NewAlias.Text.Trim());
        }
        catch (Exception ex) { _controller.ShowError(ex); }
        finally { AddButton.IsEnabled = true; }
    }
    private async void PreferencesChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _updatingTrayChoice) return;
        try
        {
            await _controller.SetPreferencesAsync(TopmostChoice.IsChecked == true, LockChoice.IsChecked == true,
                StartupChoice.IsChecked == true, ThemeChoice.SelectedIndex switch { 1 => "Dark", 2 => "Light", _ => "System" },
                DisplayModeChoice.SelectedIndex == 1, (TrayAccountChoice.SelectedItem as TrayAccountOption)?.Id, LastCheckedChoice.IsChecked == true);
            SaveStatus.Text = _controller.IsDemo ? "데모 · 저장되지 않음" : "자동 저장";
        }
        catch (Exception ex)
        {
            SaveStatus.Text = "설정을 저장하지 못했습니다.";
            _controller.ShowError(ex);
            _loaded = false;
            StartupChoice.IsChecked = _controller.Settings.StartWithWindows;
            DisplayModeChoice.SelectedIndex = _controller.Settings.ShowRemaining ? 1 : 0;
            LastCheckedChoice.IsChecked = _controller.Settings.ShowLastChecked;
            RefreshTrayAccounts();
            _loaded = true;
        }
    }
    private async void TrayWindowChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _updatingTrayWindowChoice || _updatingTrayChoice ||
            _trayWindowAccountId is not { } accountId || TrayWindowChoice.SelectedItem is not TrayWindowOption choice) return;
        var selection = choice.Id is { } id ? new WidgetWindowSelection(id, choice.Name) : null;
        try
        {
            await _controller.SetTrayWindowAsync(accountId, selection);
            if (!_windowClosed) SaveStatus.Text = _controller.IsDemo ? "데모 · 저장되지 않음" : "자동 저장";
        }
        catch (Exception ex)
        {
            _controller.ShowError(ex);
            if (_windowClosed) return;
            SaveStatus.Text = "설정을 저장하지 못했습니다.";
            RefreshTrayWindows();
        }
    }
    private void TrayContextChanged(object? sender, EventArgs e)
    {
        if (!_windowClosed) RefreshTrayAccounts();
    }
    private void TrayAccountsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ObserveTrayAccounts();
    private void ObserveTrayAccounts()
    {
        foreach (var account in _observedTrayAccounts) account.PropertyChanged -= TrayAccountChanged;
        _observedTrayAccounts.Clear();
        foreach (var account in _controller.ViewModel.Accounts)
        {
            _observedTrayAccounts.Add(account);
            account.PropertyChanged += TrayAccountChanged;
        }
        RefreshTrayAccounts();
    }
    private void TrayAccountChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountViewModel.Alias) or nameof(AccountViewModel.Identity)) RefreshTrayAccounts();
        else if (e.PropertyName is nameof(AccountViewModel.Pinned) or nameof(AccountViewModel.HasWindows) or nameof(AccountViewModel.CompactWindows)) RefreshTrayWindows();
    }
    private void RefreshTrayAccounts()
    {
        var choices = new List<TrayAccountOption> { new(null, "자동 · 첫 위젯 계정", "위젯에 표시한 첫 계정을 사용합니다.") };
        choices.AddRange(_controller.ViewModel.Accounts.Select(account => new TrayAccountOption(account.Id,
            $"{account.ProviderName} · {account.Alias}", $"{account.ProviderName} · {account.Alias}\n{account.Identity}")));
        var selectedIndex = Math.Max(0, choices.FindIndex(choice => choice.Id == _controller.Settings.TrayAccountId));
        if (TrayAccountChoice.ItemsSource is not List<TrayAccountOption> existing || !existing.SequenceEqual(choices) || TrayAccountChoice.SelectedIndex != selectedIndex)
        {
            _updatingTrayChoice = true;
            try
            {
                TrayAccountChoice.ItemsSource = choices;
                TrayAccountChoice.SelectedIndex = selectedIndex;
            }
            finally { _updatingTrayChoice = false; }
        }
        RefreshTrayWindows();
    }
    private void RefreshTrayWindows()
    {
        var ordered = _controller.ViewModel.Accounts.OrderBy(account => account.Settings.Order).ToArray();
        var account = ordered.FirstOrDefault(account => account.Id == _controller.Settings.TrayAccountId)
            ?? ordered.FirstOrDefault(account => account.Pinned) ?? ordered.FirstOrDefault();
        var choices = new List<TrayWindowOption> { new(null, "위젯 첫 항목", "이 계정의 위젯에 표시되는 첫 항목을 따릅니다.") };
        if (account is not null)
        {
            var windows = account.Windows.Where(window => !string.IsNullOrWhiteSpace(window.Id) && window.Id != "compact:unavailable")
                .DistinctBy(window => window.Id);
            if (account.Settings.Provider == ProviderKind.Claude)
                windows = windows.OrderBy(window => window.Id.StartsWith("session::", StringComparison.Ordinal) ? 0
                    : window.Id.StartsWith("weekly_all::", StringComparison.Ordinal) ? 1 : 2);
            choices.AddRange(windows.Select(window => new TrayWindowOption(window.Id, window.Name, window.Name)));
            if (account.Settings.TrayWindow is { } saved && !choices.Any(choice => choice.Id == saved.Id))
                choices.Add(new(saved.Id, saved.Name, $"{saved.Name}\n현재 응답에 없는 항목입니다. 사용량이 없으면 —로 표시합니다."));
        }
        var selectedIndex = Math.Max(0, choices.FindIndex(choice => choice.Id == account?.Settings.TrayWindow?.Id));
        var enabled = account is not null && choices.Count > 1;
        if (_trayWindowAccountId == account?.Id && TrayWindowChoice.ItemsSource is List<TrayWindowOption> existing &&
            existing.SequenceEqual(choices) && TrayWindowChoice.SelectedIndex == selectedIndex && TrayWindowChoice.IsEnabled == enabled) return;
        _updatingTrayWindowChoice = true;
        try
        {
            _trayWindowAccountId = account?.Id;
            TrayWindowChoice.ItemsSource = choices;
            TrayWindowChoice.SelectedIndex = selectedIndex;
            TrayWindowChoice.IsEnabled = enabled;
        }
        finally { _updatingTrayWindowChoice = false; }
    }
    private sealed record TrayAccountOption(string? Id, string Name, string Detail);
    private sealed record TrayWindowOption(string? Id, string Name, string Detail);
    private void CloseSettings(object sender, RoutedEventArgs e) { Keyboard.ClearFocus(); Close(); }
    private void OpenAccountMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
        e.Handled = true;
    }
    private void OpenWidget(object sender, RoutedEventArgs e) => _controller.ShowWidget();
    private void FitWidget(object sender, RoutedEventArgs e) { _controller.ShowWidget(); _controller.Window?.FitContent(); }
    private void OpenGemini(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://gemini.google.com/") { UseShellExecute = true }); }
        catch (Exception ex) { _controller.ShowError(ex); }
    }
    private async void CheckUpdate(object sender, RoutedEventArgs e)
    {
        if (_controller.Updates is not { } updates || _controller.IsDemo) { RefreshUpdateInfo(); return; }
        await RunUpdateActionAsync(() => updates.CheckAsync(manual: true), "업데이트를 확인하지 못했습니다.");
    }
    private async void DownloadUpdate(object sender, RoutedEventArgs e)
    {
        if (_controller.Updates is not { } updates || _controller.IsDemo) { RefreshUpdateInfo(); return; }
        // The application owns the download; closing this window only detaches its observer.
        await RunUpdateActionAsync(() => updates.DownloadAsync(), "업데이트를 다운로드하지 못했습니다.");
    }
    private async Task RunUpdateActionAsync(Func<Task> action, string failureMessage)
    {
        if (_updateActionRunning || _installingUpdate) return;
        _updateActionRunning = true;
        _localUpdateMessage = null;
        _localUpdateIsError = false;
        RefreshUpdateInfo();
        try
        {
            await action();
        }
        catch (OperationCanceledException) { _localUpdateMessage = "작업을 취소했습니다."; }
        catch (Exception ex) { SetUpdateError(failureMessage, ex); }
        finally { _updateActionRunning = false; RefreshUpdateInfo(); }
    }
    private void CancelUpdateDownload(object sender, RoutedEventArgs e)
    {
        _controller.Updates?.CancelDownload();
    }
    private async void InstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_controller.Updates is null || _controller.IsDemo || _installingUpdate || _updateActionRunning) return;
        _installingUpdate = true;
        _localUpdateMessage = "설치를 준비하고 있습니다…";
        _localUpdateIsError = false;
        RefreshUpdateInfo();
        try { await _controller.InstallUpdateAsync(); }
        catch (Exception ex) { SetUpdateError("설치를 시작하지 못했습니다.", ex); }
        finally { _installingUpdate = false; RefreshUpdateInfo(); }
    }
    private async void AutoCheckUpdatesChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _refreshingUpdateUi || _savingAutoCheck || _controller.IsDemo || _controller.Updates is null) return;
        _savingAutoCheck = true;
        var enabled = AutoCheckUpdatesChoice.IsChecked == true;
        _localUpdateMessage = null;
        _localUpdateIsError = false;
        RefreshUpdateInfo();
        try { await _controller.SetAutoCheckUpdatesAsync(enabled); }
        catch (Exception ex) { SetUpdateError("자동 확인 설정을 저장하지 못했습니다.", ex); }
        finally { _savingAutoCheck = false; RefreshUpdateInfo(); }
    }
    private void OpenReleaseNotes(object sender, RoutedEventArgs e)
    {
        if (_controller.Updates?.State.Release?.PageUrl is not { } uri) return;
        try
        {
            if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidDataException("배포 페이지 주소를 확인할 수 없습니다.");
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) { SetUpdateError("배포 페이지를 열지 못했습니다.", ex); RefreshUpdateInfo(); }
    }
    public void ShowUpdateInfo()
    {
        SettingsTabs.SelectedIndex = 2;
        RefreshUpdateInfo();
    }
    private void SetUpdateError(string message, Exception error)
    {
        _localUpdateMessage = $"{message}\n{error.Message}";
        _localUpdateIsError = true;
    }
    private void UpdateServiceChanged()
    {
        if (_windowClosed || Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _updateRefreshQueued, 1) != 0) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            Interlocked.Exchange(ref _updateRefreshQueued, 0);
            if (_windowClosed) return;
            _localUpdateMessage = null;
            _localUpdateIsError = false;
            RefreshUpdateInfo();
        }));
    }
    private void UpdateRetryTimerTick(object? sender, EventArgs e) => RefreshUpdateInfo();
    private void RefreshUpdateInfo()
    {
        if (_windowClosed) return;
        _refreshingUpdateUi = true;
        try
        {
            AutoCheckUpdatesChoice.IsChecked = _controller.Updates?.AutoCheckEnabled ?? _controller.Settings.AutoCheckUpdates;
            AutoCheckUpdatesChoice.IsEnabled = _controller.Updates is not null && !_controller.IsDemo && !_savingAutoCheck && !_installingUpdate;
            if (_controller.Updates is { } updates) { RenderUpdateState(updates.State); return; }
            UpdateButton.IsEnabled = false;
            UpdateButton.Content = "업데이트 확인";
            LatestVersionLabel.Text = "확인할 수 없음";
            LastUpdateCheckLabel.Text = "아직 확인하지 않음";
            UpdateStatus.Text = _localUpdateMessage ?? (_controller.IsDemo
                ? "데모에서는 업데이트를 확인하거나 설치할 수 없습니다."
                : "업데이트 주소가 등록되지 않았습니다.");
            UpdateStatus.SetResourceReference(TextBlock.ForegroundProperty, _localUpdateIsError ? "WarningBrush" : "MutedBrush");
            UpdateProgressPanel.Visibility = DownloadUpdateButton.Visibility = InstallUpdateButton.Visibility =
                ReleaseNotesButton.Visibility = InstallUpdateHint.Visibility = UpdateRetryLabel.Visibility = Visibility.Collapsed;
            _updateRetryTimer.Stop();
        }
        finally { _refreshingUpdateUi = false; }
    }
    internal void RenderUpdateState(AppUpdateState state)
    {
        var usable = _controller.Updates is not null && !_controller.IsDemo;
        var busy = _updateActionRunning || _installingUpdate || state.Phase is AppUpdatePhase.Checking or AppUpdatePhase.Downloading;
        var delayed = state.RetryAfterUtc is { } retry && retry > DateTimeOffset.UtcNow;
        var newer = state.Release is { } release && Version.TryParse(release.Version, out var version) && version > _currentVersion;
        LatestVersionLabel.Text = state.Release?.Version ?? (state.Phase == AppUpdatePhase.UpToDate ? "공개된 릴리스 없음" : "아직 확인하지 않음");
        LastUpdateCheckLabel.Text = state.LastCheckedAt?.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "아직 확인하지 않음";
        UpdateButton.Content = state.Phase == AppUpdatePhase.Checking ? "확인 중…" : "업데이트 확인";
        UpdateButton.IsEnabled = usable && !busy && !delayed;
        UpdateStatus.Text = _localUpdateMessage ?? state.Message ?? (state.Phase switch
        {
            AppUpdatePhase.Checking => "새 버전을 확인하고 있습니다.",
            AppUpdatePhase.Available when state.Release?.Asset is null => "설치 파일 준비 중",
            AppUpdatePhase.Available when state.Release?.CanDownload == false => "설치 파일 검증 정보가 준비되지 않았습니다.",
            AppUpdatePhase.Available => "새 버전을 다운로드할 수 있습니다.",
            AppUpdatePhase.UpToDate when state.Release is null => "공개된 릴리스가 없습니다.",
            AppUpdatePhase.UpToDate => "최신 버전을 사용하고 있습니다.",
            AppUpdatePhase.Downloading => "업데이트를 다운로드하고 있습니다.",
            AppUpdatePhase.ReadyToInstall => "검증을 마쳤습니다. 설치를 실행할 수 있습니다.",
            AppUpdatePhase.Error => "업데이트 작업을 완료하지 못했습니다.",
            _ => "업데이트 확인 전"
        });
        UpdateStatus.SetResourceReference(TextBlock.ForegroundProperty, _localUpdateIsError || state.Phase == AppUpdatePhase.Error ? "WarningBrush" : "MutedBrush");
        UpdateRetryLabel.Visibility = delayed ? Visibility.Visible : Visibility.Collapsed;
        UpdateRetryLabel.Text = delayed ? $"다시 확인 가능: {state.RetryAfterUtc!.Value.ToLocalTime():MM/dd HH:mm:ss}" : "";
        if (delayed && usable) _updateRetryTimer.Start(); else _updateRetryTimer.Stop();
        var downloading = state.Phase == AppUpdatePhase.Downloading;
        var percent = state.DownloadPercent is { } value && double.IsFinite(value) ? Math.Clamp(value, 0, 100) : (double?)null;
        UpdateProgressPanel.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.IsIndeterminate = !percent.HasValue;
        UpdateProgress.Value = percent ?? 0;
        UpdateProgressLabel.Text = percent.HasValue ? $"{percent:0}% 다운로드" : "다운로드 중…";
        CancelUpdateDownloadButton.IsEnabled = usable && downloading && !_installingUpdate;
        DownloadUpdateButton.Visibility = newer && state.Release?.CanDownload == true && (state.Phase is AppUpdatePhase.Available or AppUpdatePhase.Error)
            ? Visibility.Visible : Visibility.Collapsed;
        DownloadUpdateButton.IsEnabled = usable && !busy && !delayed;
        var readyToInstall = state.Phase == AppUpdatePhase.ReadyToInstall && !string.IsNullOrWhiteSpace(state.InstallerPath);
        InstallUpdateButton.Visibility = InstallUpdateHint.Visibility = readyToInstall ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.IsEnabled = usable && readyToInstall && !busy;
        ReleaseNotesButton.Visibility = state.Release is not null ? Visibility.Visible : Visibility.Collapsed;
        ReleaseNotesButton.Content = state.Release?.CanDownload == false ? "배포 페이지 열기" : "변경 사항";
        ReleaseNotesButton.IsEnabled = usable && !_installingUpdate;
    }
}

public sealed class RefreshMinutesRule : ValidationRule
{
    public override ValidationResult Validate(object value, CultureInfo cultureInfo) =>
        int.TryParse(value?.ToString(), out var minutes) && minutes is >= 1 and <= 1440
            ? ValidationResult.ValidResult : new ValidationResult(false, "1~1440분 사이로 입력하세요.");
}

public sealed class AlertThresholdRule : ValidationRule
{
    public override ValidationResult Validate(object value, CultureInfo cultureInfo) =>
        int.TryParse(value?.ToString(), out var percent) && percent is >= 1 and <= 100
            ? ValidationResult.ValidResult : new ValidationResult(false, "1~100% 사이로 입력하세요.");
}
