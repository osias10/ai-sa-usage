using System.Globalization;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private bool _closingForTransparencySave;
    private bool _closeAfterTransparencySave;
    private bool _windowClosed;
    private readonly HashSet<AccountViewModel> _observedTrayAccounts = [];
    private Uri? _downloadUri;
    private readonly CancellationTokenSource _closed = new();
    public SettingsWindow(AppController controller)
    {
        _controller = controller;
        _transparencySaveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _transparencySaveTimer.Tick += TransparencySaveTimerTick;
        InitializeComponent();
        DataContext = controller.ViewModel;
        AddAccountExpander.IsExpanded = controller.ViewModel.IsEmpty;
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionLabel.Text = version is null ? "" : $"버전 {version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
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
        ObserveTrayAccounts();
        _loaded = true;
        KeyDown += (_, e) => { if (!e.Handled && e.Key == Key.Escape) Close(); };
        Closing += SettingsClosing;
        Closed += (_, _) =>
        {
            _windowClosed = true;
            _transparencySaveTimer.Stop();
            _transparencySaveTimer.Tick -= TransparencySaveTimerTick;
            controller.ViewModel.Accounts.CollectionChanged -= TrayAccountsChanged;
            controller.UsageUpdated -= TrayContextChanged;
            controller.PreferencesUpdated -= TrayContextChanged;
            foreach (var account in _observedTrayAccounts) account.PropertyChanged -= TrayAccountChanged;
            _observedTrayAccounts.Clear();
            _closed.Cancel();
            _closed.Dispose();
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
            await _controller.SaveSettingsAsync();
            SaveStatus.Text = _controller.IsDemo ? "데모 · 저장되지 않음" : "자동 저장";
        }
        catch (Exception ex)
        {
            SaveStatus.Text = "설정을 저장하지 못했습니다.";
            _controller.ShowError(ex);
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
        UpdateButton.IsEnabled = false;
        DownloadUpdateButton.Visibility = Visibility.Collapsed;
        UpdateStatus.Text = "확인 중…";
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "release-channel.json");
            var channel = File.Exists(path) ? JsonSerializer.Deserialize<ReleaseChannel>(await File.ReadAllTextAsync(path, _closed.Token), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) : null;
            if (string.IsNullOrWhiteSpace(channel?.FeedUrl)) { UpdateStatus.Text = "업데이트 주소가 등록되지 않았습니다."; return; }
            if (_controller.IsDemo) { UpdateStatus.Text = "데모에서는 업데이트를 확인할 수 없습니다."; return; }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);
            var result = await new UpdateChecker(http).CheckAsync(new Uri(channel.FeedUrl), current, _closed.Token);
            UpdateStatus.Text = result.IsNewer ? $"새 버전 {result.Release.Version}" : $"최신 버전 · {current.Major}.{current.Minor}.{current.Build}";
            if (result.IsNewer) { _downloadUri = result.Release.DownloadUrl; DownloadUpdateButton.Visibility = Visibility.Visible; }
        }
        catch (OperationCanceledException) { if (IsVisible) UpdateStatus.Text = "연결 시간이 초과되었습니다. 다시 확인해 주세요."; }
        catch (Exception) { UpdateStatus.Text = "업데이트를 확인하지 못했습니다. 연결과 업데이트 주소를 확인해 주세요."; }
        finally { UpdateButton.IsEnabled = true; }
    }
    private void OpenUpdate(object sender, RoutedEventArgs e)
    {
        if (_downloadUri is null) return;
        try { UpdateChecker.RequireHttps(_downloadUri); Process.Start(new ProcessStartInfo(_downloadUri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { _controller.ShowError(ex); }
    }
    private sealed record ReleaseChannel(string? FeedUrl, string? DownloadUrl);
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
