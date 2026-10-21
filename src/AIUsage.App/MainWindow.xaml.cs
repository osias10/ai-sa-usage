using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public partial class MainWindow : Window
{
    private const string PlacementKey = "compact-v2";
    private const double DefaultWidth = 240;
    internal const int ClickThroughHotkeyId = 0x4153;
    private readonly AppController _controller;
    private HwndSource? _source;
    private bool _ready;
    private bool _showLastChecked;
    private bool _showForecast;
    private bool _layoutPending;
    private string _layoutKey = "";
    internal bool AllowClose { get; set; }
    internal bool ClickThroughHotkeyAvailable { get; private set; }

    public MainWindow(AppController controller)
    {
        _controller = controller;
        _showLastChecked = controller.Settings.ShowLastChecked;
        _showForecast = controller.Settings.UsageForecastEnabled && controller.Settings.ShowUsageForecast;
        InitializeComponent();
        InitializePlacementBehavior();
        DataContext = controller.ViewModel;
        ShowActivated = !controller.Settings.ClickThrough;
        ApplyTransparency();
        controller.ViewModel.PinnedAccounts.CollectionChanged += PinnedAccountsChanged;
        controller.UsageUpdated += UsageUpdated;
        controller.WidgetAppearanceUpdated += WidgetAppearanceUpdated;
        UpdateMinimumHeight();
        controller.PreferencesUpdated += PreferencesUpdated;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(WindowMessage);
            ApplyInputPreferences();
            // MOD_NOREPEAT prevents a held shortcut from repeatedly changing the setting.
            // A shortcut owned by another app is left alone; the tray remains available.
            ClickThroughHotkeyAvailable = RegisterHotKey(handle, ClickThroughHotkeyId, 0x0001 | 0x0002 | 0x4000, 0x57);
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
            if (ClickThroughHotkeyAvailable && _source is not null)
                UnregisterHotKey(_source.Handle, ClickThroughHotkeyId);
            ClickThroughHotkeyAvailable = false;
            _source?.RemoveHook(WindowMessage);
        };
    }

    private static void ApplyWindowStyles(IntPtr handle, bool clickThrough)
    {
        const int extendedStyleIndex = -20;
        const long toolWindow = 0x00000080;
        const long appWindow = 0x00040000;
        const long transparent = 0x00000020;
        const long noActivate = 0x08000000;
        var current = IntPtr.Size == 8 ? GetWindowLongPtr64(handle, extendedStyleIndex) : new IntPtr(GetWindowLong32(handle, extendedStyleIndex));
        var error = Marshal.GetLastPInvokeError();
        if (current == IntPtr.Zero && error != 0) throw new Win32Exception(error, "위젯 창 스타일을 확인하지 못했습니다.");
        var style = (current.ToInt64() | toolWindow) & ~appWindow;
        // AllowsTransparency creates a layered WPF window. WS_EX_TRANSPARENT on that
        // window passes mouse input to applications underneath, including other threads.
        // HTTRANSPARENT alone would only continue hit testing within our own UI thread.
        style = clickThrough ? style | transparent | noActivate : style & ~(transparent | noActivate);
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

    private string LayoutKey() => (_controller.Settings.UsageForecastEnabled && _controller.Settings.ShowUsageForecast) + "|" + string.Join("|", _controller.ViewModel.PinnedAccounts.Select(account =>
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
        var hasAccounts = _controller.ViewModel.PinnedAccounts.Count > 0;
        MinHeight = baseHeight + (_controller.Settings.ShowLastChecked && hasAccounts ? 13 : 0)
            + (_controller.Settings.UsageForecastEnabled && _controller.Settings.ShowUsageForecast && hasAccounts ? 12 : 0);
    }

    private void ApplyPreferences()
    {
        ApplyTransparency();
        Topmost = _controller.Settings.AlwaysOnTop;
        ApplyInputPreferences();
        var showForecast = _controller.Settings.UsageForecastEnabled && _controller.Settings.ShowUsageForecast;
        var changed = _showLastChecked != _controller.Settings.ShowLastChecked || _showForecast != showForecast;
        _showLastChecked = _controller.Settings.ShowLastChecked;
        _showForecast = showForecast;
        UpdateMinimumHeight();
        if (changed && _ready) { _layoutKey = LayoutKey(); FitHeight(); }
    }

    private void ApplyInputPreferences()
    {
        var clickThrough = _controller.Settings.ClickThrough;
        ShowActivated = !clickThrough;
        Focusable = !clickThrough;
        IsHitTestVisible = !clickThrough;
        ResizeMode = clickThrough || _controller.Settings.PositionLocked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        if (clickThrough)
        {
            if (ContextMenu is { IsOpen: true }) ContextMenu.IsOpen = false;
            if (IsKeyboardFocusWithin) Keyboard.ClearFocus();
        }
        if (_source is not null) ApplyWindowStyles(_source.Handle, clickThrough);
    }

    private async Task ToggleClickThroughAsync()
    {
        try { await _controller.SetClickThroughAsync(!_controller.Settings.ClickThrough); }
        catch (Exception ex) { _controller.ShowError(ex); }
    }

    private void WidgetKeyDown(object sender, KeyEventArgs e)
    {
        if (_controller.Settings.ClickThrough) return;
        if (e.Key == Key.Escape) { _controller.HideWidget(); e.Handled = true; }
        else if (e.Key == Key.F5)
        {
            var command = _controller.ViewModel.RefreshAllCommand;
            if (command.CanExecute(null)) command.Execute(null);
            e.Handled = true;
        }
    }

    private void DragWidget(object sender, MouseButtonEventArgs e)
    {
        if (_controller.Settings.ClickThrough) return;
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
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void FitContentClicked(object sender, RoutedEventArgs e) => FitContent();

    private double ContentHeight(double width) => MeasureWidgetHeight(WidgetFrame, width, MinHeight);

    internal static double MeasureWidgetHeight(FrameworkElement frame, double width, double minimumHeight)
    {
        // Measure the complete viewport and chrome, not only the inner StackPanel.
        // Their template borders/margins and per-row DPI rounding can otherwise leave
        // a fractional overflow, which is sufficient to expose an Auto scrollbar.
        // Flush item generation first so a freshly selected third quota is included.
        frame.UpdateLayout();
        frame.InvalidateMeasure();
        frame.Measure(new Size(Math.Max(1, width), double.PositiveInfinity));
        var scale = VisualTreeHelper.GetDpi(frame).DpiScaleY;
        return Math.Ceiling(Math.Max(minimumHeight, frame.DesiredSize.Height) * scale) / scale;
    }

    private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == ClickThroughHotkeyId && ClickThroughHotkeyAvailable) // WM_HOTKEY
        {
            handled = true;
            // Keep asynchronous settings/native style changes outside the window procedure.
            Dispatcher.BeginInvoke(new Action(() => _ = ToggleClickThroughAsync()));
        }
        HandlePlacementMessage(msg, wParam);
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
        _controller.HideWidget();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong32(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);
}
