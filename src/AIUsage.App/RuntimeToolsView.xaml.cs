using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public partial class RuntimeToolsView : UserControl, IDisposable
{
    private readonly ObservableCollection<RuntimeToolRowModel> _rows = [];
    private readonly HashSet<ProviderKind> _actions = [];
    private IRuntimeToolService? _service;
    private bool _checking;
    private bool _disposed;
    private bool _checkOnLoad;
    private int _refreshQueued;

    public RuntimeToolsView()
    {
        InitializeComponent();
        RuntimeToolsItems.ItemsSource = _rows;
        Loaded += ViewLoaded;
    }

    internal void Initialize(AppController controller) => InitializeService(controller.RuntimeTools, checkOnLoad: true);

    // The same view can be exercised with an isolated in-memory service by UI smoke checks.
    internal void InitializeService(IRuntimeToolService service, bool checkOnLoad = false)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RuntimeToolsView));
        if (_service is not null) _service.Changed -= ServiceChanged;
        _service = service;
        _checkOnLoad = checkOnLoad;
        _service.Changed += ServiceChanged;
        SetError(null);
        RefreshView();
    }

    private async void ViewLoaded(object sender, RoutedEventArgs e)
    {
        RefreshView();
        if (!_checkOnLoad) return;
        _checkOnLoad = false;
        await CheckToolsAsync();
    }

    private void ServiceChanged(object? sender, EventArgs e)
    {
        if (_disposed || Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (!_disposed) RefreshView();
        }));
    }

    private void RefreshView()
    {
        if (_disposed || _service is null) return;
        try
        {
            var statuses = _service.GetStatuses().OrderBy(status => status.Provider).ToArray();
            foreach (var old in _rows.Where(row => statuses.All(status => status.Provider != row.Provider)).ToArray()) _rows.Remove(old);
            foreach (var status in statuses)
            {
                var row = _rows.FirstOrDefault(row => row.Provider == status.Provider);
                if (row is null)
                {
                    row = new RuntimeToolRowModel(status);
                    _rows.Insert(statuses.TakeWhile(item => item.Provider != status.Provider).Count(), row);
                }
                row.Apply(status, _actions.Contains(status.Provider));
            }
            CheckRuntimeToolsButton.IsEnabled = !_checking && !statuses.Any(status => status.Phase == RuntimeToolPhase.Checking);
            CheckRuntimeToolsButton.Content = _checking || statuses.Any(status => status.Phase == RuntimeToolPhase.Checking) ? "확인 중" : "버전 확인";
        }
        catch (Exception error)
        {
            SetError($"도구 상태를 확인하지 못했습니다. {error.Message}");
        }
    }

    private async void CheckTools(object sender, RoutedEventArgs e) => await CheckToolsAsync();

    private async Task CheckToolsAsync()
    {
        if (_disposed || _service is null || _checking) return;
        _checking = true;
        SetError(null);
        RefreshView();
        try
        {
            // The service owns the operation. Closing Settings only detaches this view.
            await _service.CheckAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { SetError($"도구 상태를 확인하지 못했습니다. {error.Message}"); }
        finally
        {
            _checking = false;
            RefreshView();
        }
    }

    private async void UpdateTool(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RuntimeToolRowModel row } && row.CanUpdate) await UpdateToolAsync(row, false);
    }

    private async void ReinstallTool(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RuntimeToolRowModel row } && row.CanReinstall) await UpdateToolAsync(row, true);
    }

    private async Task UpdateToolAsync(RuntimeToolRowModel row, bool reinstall)
    {
        if (_disposed || _service is null || !_actions.Add(row.Provider)) return;
        row.LocalError = null;
        RefreshView();
        try { await _service.UpdateAsync(row.Provider, reinstall, CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_disposed) row.LocalError = $"도구를 준비하지 못했습니다. {error.Message}";
        }
        finally
        {
            _actions.Remove(row.Provider);
            RefreshView();
        }
    }

    private void CancelTool(object sender, RoutedEventArgs e)
    {
        if (_disposed || _service is null || sender is not Button { DataContext: RuntimeToolRowModel row } || !row.CanCancel) return;
        try { _service.Cancel(row.Provider); }
        catch (Exception error) { row.LocalError = $"취소하지 못했습니다. {error.Message}"; }
    }

    private void SetError(string? message)
    {
        if (_disposed) return;
        RuntimeToolsError.Text = message ?? "";
        RuntimeToolsError.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= ViewLoaded;
        if (_service is not null) _service.Changed -= ServiceChanged;
        // Downloads belong to the app and continue when this settings window closes.
    }
}

internal sealed class RuntimeToolRowModel(RuntimeToolStatus status) : ObservableModel
{
    private RuntimeToolStatus _status = status;
    private bool _actionPending;
    private string? _localError;
    public ProviderKind Provider => _status.Provider;
    public string Name => Provider switch { ProviderKind.Claude => "Claude Code", ProviderKind.Codex => "Codex", _ => "Antigravity" };
    public string CurrentVersionLabel => string.IsNullOrWhiteSpace(_status.InstalledVersion)
        ? _status.IsInstalled ? "확인 필요" : "미설치" : _status.InstalledVersion;
    public string SupportedVersionLabel => _status.TargetVersion;
    public string StateLabel => _localError is not null ? "준비 실패" : _status.Phase switch
    {
        RuntimeToolPhase.NotInstalled => "미설치", RuntimeToolPhase.Ready => "준비됨",
        RuntimeToolPhase.UpdateAvailable => "업데이트 가능", RuntimeToolPhase.Checking => "확인 중",
        RuntimeToolPhase.Downloading => "다운로드 중", RuntimeToolPhase.Verifying => "검증 중",
        RuntimeToolPhase.WaitingForIdle => "사용 종료 대기", RuntimeToolPhase.Applying => "적용 중",
        RuntimeToolPhase.Failed => "준비 실패", RuntimeToolPhase.Cancelled => "취소됨", _ => "확인 필요"
    };
    public bool IsBusy => _status.IsBusy || _actionPending;
    public bool CanUpdate => !_actionPending && _status.CanUpdate;
    public bool CanReinstall => !_actionPending && _status.CanReinstall;
    public bool ShowUpdate => !IsBusy && (!_status.IsInstalled || _status.InstalledVersion != _status.TargetVersion);
    public bool ShowReinstall => !IsBusy && _status.IsInstalled;
    public bool CanCancel => _status.IsBusy && (_status.Phase != RuntimeToolPhase.Checking || _actionPending);
    public string UpdateLabel => _status.IsInstalled ? "업데이트" : "설치";
    public string UpdateAccessibilityLabel => $"{Name} {UpdateLabel}";
    public string ReinstallAccessibilityLabel => $"{Name} 다시 설치";
    public string CancelAccessibilityLabel => $"{Name} 준비 취소";
    public string ProgressAccessibilityLabel => $"{Name} 준비 진행률";
    public double ProgressPercent => _status.Percent is { } percent && double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0;
    public bool IsIndeterminate => _status.Percent is null || !double.IsFinite(_status.Percent.Value);
    public string ProgressLabel => IsIndeterminate ? StateLabel : $"{StateLabel} · {ProgressPercent:0}%";
    public string LastCheckedLabel => _status.LastCheckedAt is { } checkedAt ? $"확인 {checkedAt.ToLocalTime():MM/dd HH:mm}" : "아직 확인하지 않음";
    public string LastCheckedDetail => _status.LastCheckedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "아직 확인하지 않음";
    public string? Message => _localError ?? _status.Message;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool IsError => _localError is not null || _status.Phase == RuntimeToolPhase.Failed;
    public string? LocalError
    {
        get => _localError;
        set { if (Set(ref _localError, value)) { Changed(nameof(Message)); Changed(nameof(HasMessage)); Changed(nameof(StateLabel)); Changed(nameof(IsError)); } }
    }

    public void Apply(RuntimeToolStatus value, bool actionPending)
    {
        // Service recovery or a later operation supersedes a local exception from an earlier action.
        if (_status != value) _localError = null;
        _status = value;
        _actionPending = actionPending;
        Changed(string.Empty);
    }
}
