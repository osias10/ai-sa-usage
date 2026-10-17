using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using AIUsage.Core;
using Forms = System.Windows.Forms;

namespace AIUsage.App;

internal sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _tray;
    private Icon _icon;
    private readonly AppController _controller;
    private readonly Popup _popup;
    private readonly TraySummary _summary;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _notificationTimer;
    private readonly Queue<IReadOnlyList<UsageAlert>> _pendingAlerts = [];
    private readonly Forms.ToolStripMenuItem _readingItem;
    internal Forms.ToolStripMenuItem ClickThroughItem { get; }
    private System.Drawing.Point _hoverPoint;
    private DateTimeOffset _hoverStarted;
    private string? _iconKey;
    private bool _disposed;
    private ReleaseInfo? _pendingUpdate;
    private bool _balloonShowsUpdate;
    internal TrayUsageReading CurrentReading { get; private set; } = null!;
    public TrayService(AppController controller, Action exit)
    {
        _controller = controller;
        _icon = CreateIcon("—", false);
        _summary = new TraySummary(controller.ViewModel);
        _popup = new Popup { Child = _summary, Placement = PlacementMode.MousePoint, AllowsTransparency = true, StaysOpen = true, PopupAnimation = PopupAnimation.None };
        var menu = new Forms.ContextMenuStrip();
        _readingItem = new Forms.ToolStripMenuItem("AI SA") { Enabled = false };
        menu.Items.Add(_readingItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("위젯 열기", null, (_, _) => Application.Current.Dispatcher.Invoke(controller.ShowWidget));
        ClickThroughItem = new Forms.ToolStripMenuItem("클릭 통과") { ShowShortcutKeys = true };
        ClickThroughItem.Click += async (_, _) =>
        {
            try
            {
                await Application.Current.Dispatcher.InvokeAsync(() => controller.SetClickThroughAsync(!controller.Settings.ClickThrough)).Task.Unwrap();
            }
            catch (Exception error) { Application.Current.Dispatcher.Invoke(() => controller.ShowError(error)); }
        };
        menu.Items.Add(ClickThroughItem);
        menu.Items.Add("새로고침", null, async (_, _) =>
        {
            try { await controller.RefreshAllAsync(); } catch (Exception error) { Application.Current.Dispatcher.Invoke(() => controller.ShowError(error)); }
        });
        menu.Items.Add("설정", null, (_, _) => Application.Current.Dispatcher.Invoke(controller.ShowSettings));
        menu.Items.Add("업데이트", null, (_, _) => Application.Current.Dispatcher.Invoke(controller.ShowUpdateSettings));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Application.Current.Dispatcher.Invoke(exit));
        // A scrollable hover panel replaces the native 127-character tooltip.
        _tray = new Forms.NotifyIcon { Icon = _icon, Text = "", ContextMenuStrip = menu, Visible = true };
        _tray.MouseMove += (_, _) => Application.Current.Dispatcher.Invoke(BeginHover);
        _tray.MouseClick += (_, e) => Application.Current.Dispatcher.Invoke(() =>
        {
            HideSummary();
            if (e.Button == Forms.MouseButtons.Left) controller.ShowWidget();
        });
        _tray.BalloonTipClicked += (_, _) => Application.Current.Dispatcher.Invoke(() =>
        {
            if (_balloonShowsUpdate) controller.ShowUpdateSettings();
            else controller.ShowSettings();
        });
        menu.Opening += (_, _) => { HideSummary(); Refresh(); };
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => Refresh(), Application.Current.Dispatcher);
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hoverTimer.Tick += (_, _) => UpdateHover();
        _notificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _notificationTimer.Tick += (_, _) => ShowNextAlert();
        controller.PreferencesUpdated += OnUpdated;
        controller.UsageUpdated += OnUpdated;
        controller.UsageAlertsRaised += EnqueueAlerts;
        if (controller.Updates is not null) controller.Updates.UpdateAvailable += EnqueueUpdate;
        Refresh();
    }
    private void OnUpdated(object? sender, EventArgs e) => Refresh();
    internal void Refresh()
    {
        if (_disposed) return;
        ClickThroughItem.Checked = _controller.Settings.ClickThrough;
        ClickThroughItem.ShortcutKeyDisplayString = _controller.Window?.ClickThroughHotkeyAvailable == true ? "Ctrl+Alt+W" : "";
        CurrentReading = TrayUsageFormatting.GetReading(_controller.Settings, _controller.GetStatuses(), DateTimeOffset.UtcNow);
        _summary.SetMode(_controller.Settings.ShowRemaining);
        _readingItem.Text = string.Join("\n", CurrentReading.Tooltip.Split('\n').Take(2));
        _readingItem.ToolTipText = CurrentReading.Tooltip;
        var key = $"{CurrentReading.IconText}:{CurrentReading.IsWarning}";
        if (_iconKey == key) return;
        var replacement = CreateIcon(CurrentReading.IconText, CurrentReading.IsWarning);
        var previous = _icon;
        _tray.Icon = replacement;
        _icon = replacement;
        _iconKey = key;
        previous.Dispose();
    }
    private void BeginHover()
    {
        if (_disposed || _tray.ContextMenuStrip?.Visible == true) return;
        _hoverPoint = Forms.Cursor.Position;
        if (_hoverTimer.IsEnabled) return;
        _hoverStarted = DateTimeOffset.UtcNow;
        _hoverTimer.Start();
    }
    private void UpdateHover()
    {
        if (_disposed) return;
        var cursor = Forms.Cursor.Position;
        if (_popup.IsOpen && _summary.IsMouseOver) return;
        if (Math.Abs(cursor.X - _hoverPoint.X) > 14 || Math.Abs(cursor.Y - _hoverPoint.Y) > 14)
        {
            HideSummary();
            return;
        }
        if (!_popup.IsOpen && DateTimeOffset.UtcNow - _hoverStarted >= TimeSpan.FromMilliseconds(450)) ShowSummary();
    }
    internal void ShowSummary()
    {
        Refresh();
        _popup.IsOpen = true;
    }
    internal void HideSummary()
    {
        _popup.IsOpen = false;
        _hoverTimer?.Stop();
    }
    internal bool IsSummaryOpen => _popup.IsOpen;
    private void EnqueueAlerts(IReadOnlyList<UsageAlert> alerts)
    {
        if (_disposed || _controller.IsDemo || alerts.Count == 0) return;
        // Reset confirmations use an informational balloon independently of threshold warnings.
        foreach (var group in alerts.GroupBy(alert => (alert.AccountId, alert.IdentityKey, alert.Kind)))
            _pendingAlerts.Enqueue(group.ToArray());
        if (!_notificationTimer.IsEnabled) { ShowNextAlert(); _notificationTimer.Start(); }
    }
    private void EnqueueUpdate(ReleaseInfo release) => Application.Current.Dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        _pendingUpdate = release;
        if (!_notificationTimer.IsEnabled) { ShowNextAlert(); _notificationTimer.Start(); }
    });
    private void ShowNextAlert()
    {
        while (_pendingAlerts.TryDequeue(out var pending))
        {
            var first = pending[0];
            var account = _controller.Settings.Accounts.FirstOrDefault(a => a.Id == first.AccountId && a.IdentityKey == first.IdentityKey);
            if (account is null) continue;
            var status = _controller.GetStatuses().FirstOrDefault(s => s.AccountId == account.Id);
            // Other account alerts may have been queued for several seconds. Recheck the latest
            // successful reading and enabled options before presenting a queued notification.
            var notification = UsageAlertFormatting.Format(account, status, pending, DateTimeOffset.UtcNow);
            if (notification is null) continue;
            _balloonShowsUpdate = false;
            _tray.ShowBalloonTip(10000, notification.Title, notification.Message,
                notification.IsWarning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
            return;
        }
        if (_pendingUpdate is { } release)
        {
            _pendingUpdate = null;
            // A channel change may have invalidated an update queued behind usage alerts.
            if (_controller.Updates?.State is { Action: not UpdateAction.None } state && state.Release == release)
            {
                var returning = state.Action == UpdateAction.ReturnToStable;
                _balloonShowsUpdate = true;
                var title = returning ? "AI SA · 정식 버전 복귀" : release.IsPrerelease ? "AI SA · 프리릴리스" : "AI SA · 새 버전";
                var message = returning ? $"정식 {release.Version} 버전으로 돌아갈 수 있습니다. 눌러서 확인하세요."
                    : $"{release.Version} 버전이 있습니다. 눌러서 업데이트를 확인하세요.";
                _tray.ShowBalloonTip(10000, title, message, Forms.ToolTipIcon.Info);
                return;
            }
        }
        _notificationTimer.Stop();
    }
    private static Icon CreateIcon(string number, bool warning)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);
        using var background = new SolidBrush(warning ? Color.FromArgb(146, 92, 29) : Color.FromArgb(47, 86, 151));
        using var tile = new GraphicsPath();
        tile.AddArc(1, 1, 13, 13, 180, 90);
        tile.AddArc(18, 1, 13, 13, 270, 90);
        tile.AddArc(18, 18, 13, 13, 0, 90);
        tile.AddArc(1, 18, 13, 13, 90, 90);
        tile.CloseFigure();
        graphics.FillPath(background, tile);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI", number.Length >= 3 ? 17 : 21, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.White);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(number, font, ink, new RectangleF(0, 0, 32, 31), format);
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller.PreferencesUpdated -= OnUpdated;
        _controller.UsageUpdated -= OnUpdated;
        _controller.UsageAlertsRaised -= EnqueueAlerts;
        if (_controller.Updates is not null) _controller.Updates.UpdateAvailable -= EnqueueUpdate;
        _refreshTimer.Stop(); _hoverTimer.Stop(); _notificationTimer.Stop();
        _pendingAlerts.Clear();
        _popup.IsOpen = false;
        _popup.Child = null;
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); _icon.Dispose();
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
}
