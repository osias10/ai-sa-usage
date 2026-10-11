using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AIUsage.Core;
using Forms = System.Windows.Forms;

namespace AIUsage.App;

public partial class MainWindow : Window
{
    private const string PlacementKey = "compact-v2";
    private const double DefaultWidth = 240;
    private readonly AppController _controller;
    private HwndSource? _source;
    private bool _ready;
    private bool _showLastChecked;
    private bool _layoutPending;
    private string _layoutKey = "";
    internal bool AllowClose { get; set; }

    public MainWindow(AppController controller)
    {
        _controller = controller;
        _showLastChecked = controller.Settings.ShowLastChecked;
        InitializeComponent();
        DataContext = controller.ViewModel;
        ApplyTransparency();
        controller.ViewModel.PinnedAccounts.CollectionChanged += PinnedAccountsChanged;
        controller.UsageUpdated += UsageUpdated;
        controller.WidgetAppearanceUpdated += WidgetAppearanceUpdated;
        UpdateMinimumHeight();
        controller.PreferencesUpdated += PreferencesUpdated;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            ApplySwitcherStyle(handle);
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(WindowMessage);
        };
        Loaded += (_, _) => { _ready = true; _layoutKey = LayoutKey(); ApplyPreferences(); ApplyMode(fitHeight: true); };
        Closing += ClosingWindow;
        PreviewKeyDown += WidgetKeyDown;
        Closed += (_, _) =>
        {
            controller.PreferencesUpdated -= PreferencesUpdated;
            controller.ViewModel.PinnedAccounts.CollectionChanged -= PinnedAccountsChanged;
            controller.UsageUpdated -= UsageUpdated;
            controller.WidgetAppearanceUpdated -= WidgetAppearanceUpdated;
            _source?.RemoveHook(WindowMessage);
        };
    }

    private static void ApplySwitcherStyle(IntPtr handle)
    {
        const int extendedStyleIndex = -20;
        const long toolWindow = 0x00000080;
        const long appWindow = 0x00040000;
        var current = IntPtr.Size == 8 ? GetWindowLongPtr64(handle, extendedStyleIndex) : new IntPtr(GetWindowLong32(handle, extendedStyleIndex));
        var error = Marshal.GetLastPInvokeError();
        if (current == IntPtr.Zero && error != 0) throw new Win32Exception(error, "위젯 창 스타일을 확인하지 못했습니다.");
        var style = (current.ToInt64() | toolWindow) & ~appWindow;
        if (style == current.ToInt64()) return;
        var previous = IntPtr.Size == 8 ? SetWindowLongPtr64(handle, extendedStyleIndex, new IntPtr(style))
            : new IntPtr(SetWindowLong32(handle, extendedStyleIndex, unchecked((int)style)));
        error = Marshal.GetLastPInvokeError();
        // A zero previous value is valid; SetLastError clears stale errors before each P/Invoke on .NET.
        if (previous == IntPtr.Zero && error != 0) throw new Win32Exception(error, "위젯 창 스타일을 적용하지 못했습니다.");
        const uint frameChangedWithoutMoving = 0x0020 | 0x0001 | 0x0002 | 0x0004 | 0x0010;
        if (!SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, frameChangedWithoutMoving))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "위젯 창 스타일을 갱신하지 못했습니다.");
    }

    private void PreferencesUpdated(object? sender, EventArgs e) => ApplyPreferences();

    private void WidgetAppearanceUpdated(object? sender, EventArgs e) => ApplyTransparency();

    private void ApplyTransparency()
        => Opacity = (100 - Math.Clamp(_controller.Settings.WidgetTransparencyPercent, 0, 100)) / 100d;

    private void PinnedAccountsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueLayoutUpdate();

    private void UsageUpdated(object? sender, EventArgs e) => QueueLayoutUpdate();

    private string LayoutKey() => string.Join("|", _controller.ViewModel.PinnedAccounts.Select(account =>
        account.Id + ":" + account.HasWindows + ":" + account.CanCancelConnection + ":" +
        string.Join(",", account.CompactWindows.Select(window => window.Id))));

    private void QueueLayoutUpdate()
    {
        if (!_ready || _layoutPending) return;
        _layoutPending = true;
        // Account settings rebuild the pinned collection. Wait for that whole update
        // before measuring, so intermediate empty collections cannot move the window.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _layoutPending = false;
            var key = LayoutKey();
            if (key == _layoutKey) return;
            _layoutKey = key;
            UpdateMinimumHeight();
            FitHeight();
        }));
    }

    private void UpdateMinimumHeight()
    {
        // The fitted height follows the selected rows. Keep the resize minimum small
        // so long lists can still scroll on a short display.
        var baseHeight = 44 + (_controller.IsDemo ? 13 : 0);
        MinHeight = baseHeight + (_controller.Settings.ShowLastChecked && _controller.ViewModel.PinnedAccounts.Count > 0 ? 13 : 0);
    }

    private void ApplyPreferences()
    {
        ApplyTransparency();
        Topmost = _controller.Settings.AlwaysOnTop;
        ResizeMode = _controller.Settings.PositionLocked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        var changed = _showLastChecked != _controller.Settings.ShowLastChecked;
        _showLastChecked = _controller.Settings.ShowLastChecked;
        UpdateMinimumHeight();
        if (changed && _ready) FitHeight();
    }

    private void WidgetKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
        else if (e.Key == Key.F5)
        {
            var command = _controller.ViewModel.RefreshAllCommand;
            if (command.CanExecute(null)) command.Execute(null);
            e.Handled = true;
        }
    }

    private void DragWidget(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.Thumb) return;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        if (e.ClickCount == 2)
        {
            _controller.ShowSettings();
            e.Handled = true;
            return;
        }
        if (_controller.Settings.PositionLocked) return;
        try { DragMove(); CapturePlacement(); _ = SavePositionAsync(); }
        catch (InvalidOperationException) { }
    }

    // Kept as the controller entry point; the widget now has a single compact layout.
    public void ApplyMode(bool fitHeight = false)
    {
        if (!_ready) return;
        var hasCompactPlacement = _controller.Settings.Placements.TryGetValue($"{PlacementKey}:last", out var placement);
        var hasPosition = hasCompactPlacement;
        if (!hasCompactPlacement)
        {
            var previousMode = _controller.Settings.MiniMode ? "mini" : "widget";
            hasPosition = _controller.Settings.Placements.TryGetValue($"{previousMode}:last", out var previous)
                || _controller.Settings.Placements.TryGetValue("widget:last", out previous)
                || _controller.Settings.Placements.TryGetValue("mini:last", out previous);
            placement = new WindowPlacement(previous?.Left ?? 0, previous?.Top ?? 0, DefaultWidth, ContentHeight(DefaultWidth));
        }
        var selected = placement!;
        var screen = hasPosition
            ? Forms.Screen.AllScreens.FirstOrDefault(s => s.WorkingArea.Contains((int)selected.Left + 10, (int)selected.Top + 10)) ?? Forms.Screen.PrimaryScreen!
            : Forms.Screen.PrimaryScreen!;
        if (hasCompactPlacement && _controller.Settings.Placements.TryGetValue($"{PlacementKey}:{screen.DeviceName}", out var onMonitor)) selected = onMonitor;
        if (fitHeight) selected = selected with { Height = ContentHeight(Math.Max(MinWidth, selected.Width)) };
        PlaceWindow(selected, screen, hasPosition);
        if (fitHeight)
            Dispatcher.BeginInvoke(new Action(() => { CapturePlacement(); _ = SavePositionAsync(); }));
    }

    public void FitContent()
    {
        if (!_ready) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(handle, out var bounds)) return;
        var placement = new WindowPlacement(bounds.Left, bounds.Top, DefaultWidth, ContentHeight(DefaultWidth));
        PlaceWindow(placement, Forms.Screen.FromHandle(handle), true);
        Dispatcher.BeginInvoke(new Action(() => { CapturePlacement(); _ = SavePositionAsync(); }));
    }

    private void FitContentClicked(object sender, RoutedEventArgs e) => FitContent();

    private void FitHeight()
    {
        // Selection and timestamp changes keep the user's width and screen position.
        var handle = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(handle, out var bounds)) return;
        var width = Math.Max(MinWidth, ActualWidth);
        var placement = new WindowPlacement(bounds.Left, bounds.Top, width, ContentHeight(width));
        PlaceWindow(placement, Forms.Screen.FromHandle(handle), true);
        Dispatcher.BeginInvoke(new Action(() => { CapturePlacement(); _ = SavePositionAsync(); }));
    }

    private double ContentHeight(double width)
    {
        // Collection changes can leave the ItemsPresenter dirty while its parent still
        // has a valid measure at the previous size. Flush that layout before measuring.
        WidgetContent.UpdateLayout();
        WidgetContent.InvalidateMeasure();
        // Outer margin (8) and border (2); rows are measured at their actual available width.
        WidgetContent.Measure(new Size(Math.Max(1, width - 10), double.PositiveInfinity));
        return Math.Max(MinHeight, Math.Ceiling(WidgetContent.DesiredSize.Height) + 10);
    }

    private void PlaceWindow(WindowPlacement placement, Forms.Screen screen, bool preservePosition)
    {
        var area = screen.WorkingArea;
        var scale = MonitorScale(area.Left + 10, area.Top + 10);
        var pixelWidth = (int)Math.Clamp(placement.Width * scale, Math.Min(MinWidth * scale, area.Width), area.Width);
        var pixelHeight = (int)Math.Clamp(placement.Height * scale, Math.Min(MinHeight * scale, area.Height), area.Height);
        var left = preservePosition ? (int)placement.Left : area.Right - pixelWidth - 10;
        var top = preservePosition ? (int)placement.Top : area.Bottom - pixelHeight - 10;
        left = Math.Clamp(left, area.Left, area.Right - pixelWidth);
        top = Math.Clamp(top, area.Top, area.Bottom - pixelHeight);
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, left, top, pixelWidth, pixelHeight, 0x0014);
    }

    public void CapturePlacement()
    {
        if (!_ready || WindowState != WindowState.Normal) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(handle, out var rect)) return;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var placement = new WindowPlacement(rect.Left, rect.Top, ActualWidth, ActualHeight, scale);
        var screen = Forms.Screen.FromHandle(handle);
        _controller.Settings.Placements[$"{PlacementKey}:{screen.DeviceName}"] = placement;
        _controller.Settings.Placements[$"{PlacementKey}:last"] = placement;
    }

    private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0232) { CapturePlacement(); _ = SavePositionAsync(); } // WM_EXITSIZEMOVE
        if (msg == 0x007E) Dispatcher.BeginInvoke(new Action(() => ApplyMode())); // display configuration changed
        return IntPtr.Zero;
    }

    private async Task SavePositionAsync()
    {
        try { await _controller.SaveSettingsAsync(); }
        catch (Exception ex) { _controller.ShowError(ex); }
    }

    private void ClosingWindow(object? sender, CancelEventArgs e)
    {
        CapturePlacement();
        if (AllowClose) return;
        e.Cancel = true;
        Hide();
        _ = SavePositionAsync();
    }

    private static double MonitorScale(int x, int y)
    {
        try
        {
            var monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, 2);
            return GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96d : 1;
        }
        catch (DllNotFoundException) { return 1; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong32(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
}
