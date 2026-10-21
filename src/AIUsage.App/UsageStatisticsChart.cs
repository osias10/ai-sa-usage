using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using AIUsage.Core;

namespace AIUsage.App;

/// <summary>Displays saved observations. Separate periods and gaps are never joined.</summary>
public sealed class UsageStatisticsChart : FrameworkElement
{
    public static readonly DependencyProperty ReportProperty = DependencyProperty.Register(
        nameof(Report), typeof(UsageStatisticsReport), typeof(UsageStatisticsChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, ReportChanged));

    private static readonly DependencyProperty AccentProperty = ThemeProperty("Accent");
    private static readonly DependencyProperty GridBrushProperty = ThemeProperty("GridBrush");
    private static readonly DependencyProperty LabelBrushProperty = ThemeProperty("LabelBrush");
    private static readonly DependencyProperty TextBrushProperty = ThemeProperty("TextBrush");
    private static readonly DependencyProperty SurfaceBrushProperty = ThemeProperty("SurfaceBrush");
    private static readonly Typeface LabelTypeface = new("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");
    private int _selectedIndex = -1;
    private UsageStatisticsReport? _geometryReport;
    private Rect _geometryPlot;
    private StreamGeometry? _lineGeometry;
    private IReadOnlyList<Point> _markers = [];

    public UsageStatisticsChart()
    {
        Focusable = true;
        MinHeight = 220;
        SnapsToDevicePixels = true;
        SetValue(KeyboardNavigation.IsTabStopProperty, true);
        SetResourceReference(AccentProperty, "AccentBrush");
        SetResourceReference(GridBrushProperty, "BorderBrush");
        SetResourceReference(LabelBrushProperty, "MutedBrush");
        SetResourceReference(TextBrushProperty, "TextBrush");
        SetResourceReference(SurfaceBrushProperty, "SurfaceBrush");
        AutomationProperties.SetName(this, "사용량 추이");
        UpdateAccessibleSummary();
    }

    public UsageStatisticsReport? Report
    {
        get => (UsageStatisticsReport?)GetValue(ReportProperty);
        set => SetValue(ReportProperty, value);
    }

    internal int SelectedSampleIndex => _selectedIndex;
    internal UsageStatisticSample? SelectedSample => Report is { } report && _selectedIndex >= 0 &&
        _selectedIndex < report.Samples.Count ? report.Samples[_selectedIndex] : null;
    internal string SelectedDescription => SelectedSample is { } sample ? Describe(sample) : "";

    private static DependencyProperty ThemeProperty(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(UsageStatisticsChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private Brush Brush(DependencyProperty property) => (Brush)GetValue(property);

    private static void ReportChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var chart = (UsageStatisticsChart)owner;
        chart._geometryReport = null;
        chart._lineGeometry = null;
        chart._markers = [];
        chart._selectedIndex = chart.IsKeyboardFocused && chart.Report?.Samples.Count > 0
            ? chart.Report.Samples.Count - 1 : -1;
        chart.UpdateAccessibleSummary();
    }

    private void UpdateAccessibleSummary()
    {
        var summary = Report is { SampleCount: > 0 } report
            ? $"저장된 기록 {report.SampleCount:N0}개. 마지막 {Percent(report.LatestPercent)}. 최고 {Percent(report.PeakPercent)}."
            : "이 기간에 저장된 기록이 없습니다.";
        AutomationProperties.SetHelpText(this, summary + " 좌우 화살표로 기록을 확인하고 Home·End로 처음·마지막 기록을 선택합니다.");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this);

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 440 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 230 : Math.Min(230, availableSize.Height));

    private Rect Plot => new(42, 16, Math.Max(1, ActualWidth - 56), Math.Max(1, ActualHeight - 52));

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth < 100 || ActualHeight < 70) return;
        // A transparent hit area lets pointer and keyboard selection work between observations.
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var plot = Plot;
        var grid = new Pen(Brush(GridBrushProperty), 1);
        var muted = Brush(LabelBrushProperty);
        for (var percent = 0; percent <= 100; percent += 25)
        {
            var y = plot.Bottom - plot.Height * percent / 100;
            drawing.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Label($"{percent}%", muted, 10);
            drawing.DrawText(label, new Point(plot.Left - label.Width - 9, y - label.Height / 2));
        }
        if (Report is not { } report || report.To <= report.From)
        {
            DrawEmpty(drawing, plot, muted);
            DrawFocus(drawing);
            return;
        }

        var duration = report.To - report.From;
        for (var tick = 0; tick <= 2; tick++)
        {
            var time = report.From + TimeSpan.FromTicks(duration.Ticks * tick / 2);
            var text = Label(time.ToLocalTime().ToString(duration.TotalHours >= 24 ? "M/d HH:mm" : "HH:mm", Korean), muted, 10);
            var x = plot.Left + plot.Width * tick / 2 - text.Width / 2;
            drawing.DrawText(text, new Point(Math.Clamp(x, plot.Left, Math.Max(plot.Left, plot.Right - text.Width)), plot.Bottom + 12));
        }

        if (report.Samples.Count == 0) DrawEmpty(drawing, plot, muted);
        else
        {
            EnsureGeometry(report, plot);
            var accent = Brush(AccentProperty);
            // One cached geometry holds the disconnected paths, keeping 10,000-point histories
            // inexpensive to redraw when only the selected observation changes.
            var clip = plot;
            clip.Inflate(4, 4);
            drawing.PushClip(new RectangleGeometry(clip));
            drawing.DrawGeometry(null, new Pen(accent, 1.8) { LineJoin = PenLineJoin.Round }, _lineGeometry);
            foreach (var marker in _markers) drawing.DrawEllipse(accent, null, marker, 2.2, 2.2);
            drawing.Pop();
            if (SelectedSample is { } sample) DrawSelection(drawing, plot, report, sample);
        }
        DrawFocus(drawing);
    }

    private void DrawEmpty(DrawingContext drawing, Rect plot, Brush muted)
    {
        var text = Label("저장된 기록이 없습니다", muted, 12);
        drawing.DrawText(text, new Point(plot.Left + (plot.Width - text.Width) / 2,
            plot.Top + (plot.Height - text.Height) / 2));
    }

    private void DrawFocus(DrawingContext drawing)
    {
        if (IsKeyboardFocused)
            drawing.DrawRoundedRectangle(null, new Pen(Brush(AccentProperty), 1),
                new Rect(1, 1, ActualWidth - 2, ActualHeight - 2), 5, 5);
    }

    private void EnsureGeometry(UsageStatisticsReport report, Rect plot)
    {
        if (ReferenceEquals(_geometryReport, report) && _geometryPlot == plot && _lineGeometry is not null) return;
        var geometry = new StreamGeometry();
        var markers = new List<Point>();
        var showEveryMarker = report.Samples.Count <= 120;
        using (var context = geometry.Open())
        {
            foreach (var segment in report.Segments)
            {
                if (segment.Count == 0) continue;
                var first = PointFor(segment[0], report, plot);
                if (segment.Count == 1 || showEveryMarker) markers.Add(first);
                if (segment.Count == 1) continue;
                context.BeginFigure(first, isFilled: false, isClosed: false);
                for (var index = 1; index < segment.Count; index++)
                {
                    var point = PointFor(segment[index], report, plot);
                    context.LineTo(point, isStroked: true, isSmoothJoin: false);
                    if (showEveryMarker) markers.Add(point);
                }
            }
        }
        geometry.Freeze();
        _lineGeometry = geometry;
        _markers = markers;
        _geometryReport = report;
        _geometryPlot = plot;
    }

    private static Point PointFor(UsageStatisticSample sample, UsageStatisticsReport report, Rect plot) => new(
        plot.Left + Math.Clamp((sample.FetchedAt - report.From).TotalSeconds / (report.To - report.From).TotalSeconds, 0, 1) * plot.Width,
        plot.Bottom - Math.Clamp(sample.UsedPercent, 0, 100) / 100 * plot.Height);

    private void DrawSelection(DrawingContext drawing, Rect plot, UsageStatisticsReport report, UsageStatisticSample sample)
    {
        var position = PointFor(sample, report, plot);
        var accent = Brush(AccentProperty);
        drawing.DrawLine(new Pen(accent, 1) { DashStyle = DashStyles.Dot },
            new Point(position.X, plot.Top), new Point(position.X, plot.Bottom));
        drawing.DrawEllipse(Brush(SurfaceBrushProperty), new Pen(accent, 2), position, 4, 4);

        var text = Label(Describe(sample), Brush(TextBrushProperty), 11);
        text.MaxTextWidth = Math.Max(60, Math.Min(230, plot.Width - 20));
        text.Trimming = TextTrimming.CharacterEllipsis;
        var width = Math.Min(plot.Width, text.Width + 20);
        var height = text.Height + 16;
        var x = position.X + 14;
        if (x + width > plot.Right) x = position.X - width - 14;
        x = Math.Clamp(x, plot.Left, Math.Max(plot.Left, plot.Right - width));
        var y = Math.Clamp(position.Y - height / 2, plot.Top, Math.Max(plot.Top, plot.Bottom - height));
        drawing.DrawRoundedRectangle(Brush(SurfaceBrushProperty), new Pen(Brush(GridBrushProperty), 1),
            new Rect(x, y, width, height), 6, 6);
        drawing.DrawText(text, new Point(x + 10, y + 8));
    }

    private FormattedText Label(string text, Brush foreground, double size) => new(text, Korean,
        FlowDirection.LeftToRight, LabelTypeface, size, foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static string Percent(double? value) => value is { } percent ? $"{percent:0.#}%" : "—";

    private static string Describe(UsageStatisticSample sample)
    {
        var reset = sample.ResetsAt is { } resetsAt ? resetsAt.ToLocalTime().ToString("M/d HH:mm", Korean) : "미제공";
        return $"{sample.FetchedAt.ToLocalTime().ToString("M/d HH:mm:ss", Korean)}\n사용 {Percent(sample.UsedPercent)}\n초기화 {reset}";
    }

    internal bool SelectSample(int index)
    {
        var next = Report is { Samples.Count: > 0 } report ? Math.Clamp(index, -1, report.Samples.Count - 1) : -1;
        if (_selectedIndex == next) return false;
        var previousDescription = SelectedDescription;
        _selectedIndex = next;
        InvalidateVisual();
        if (UIElementAutomationPeer.FromElement(this) is { } peer)
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.ItemStatusProperty, previousDescription, SelectedDescription);
        return true;
    }

    internal void SelectNearestSampleAt(double horizontalPosition)
    {
        if (Report is not { Samples.Count: > 0 } report || report.To <= report.From) return;
        var fraction = Math.Clamp((horizontalPosition - Plot.Left) / Plot.Width, 0, 1);
        var target = report.From + TimeSpan.FromTicks((long)((report.To - report.From).Ticks * fraction));
        var samples = report.Samples;
        var low = 0;
        var high = samples.Count - 1;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (samples[middle].FetchedAt < target) low = middle + 1;
            else high = middle;
        }
        if (low > 0 && target - samples[low - 1].FetchedAt <= samples[low].FetchedAt - target) low--;
        SelectSample(low);
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        var position = args.GetPosition(this);
        if (Plot.Contains(position)) SelectNearestSampleAt(position.X);
        else if (!IsKeyboardFocused) SelectSample(-1);
    }

    protected override void OnMouseLeave(MouseEventArgs args)
    {
        base.OnMouseLeave(args);
        if (!IsKeyboardFocused) SelectSample(-1);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        base.OnMouseLeftButtonDown(args);
        Focus();
        SelectNearestSampleAt(args.GetPosition(this).X);
        args.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs args)
    {
        base.OnGotKeyboardFocus(args);
        if (_selectedIndex < 0 && Report is { Samples.Count: > 0 } report) SelectSample(report.Samples.Count - 1);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs args)
    {
        base.OnLostKeyboardFocus(args);
        if (!IsMouseOver) SelectSample(-1);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (Report is not { Samples.Count: > 0 } report) return;
        switch (args.Key)
        {
            case Key.Left: SelectSample(Math.Max(0, _selectedIndex - 1)); break;
            case Key.Right: SelectSample(_selectedIndex + 1); break;
            case Key.Home: SelectSample(0); break;
            case Key.End: SelectSample(report.Samples.Count - 1); break;
            default: return;
        }
        args.Handled = true;
    }

    private sealed class ChartAutomationPeer(UsageStatisticsChart chart) : FrameworkElementAutomationPeer(chart)
    {
        protected override string GetClassNameCore() => nameof(UsageStatisticsChart);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override string GetItemStatusCore() => chart.SelectedDescription;
    }
}
