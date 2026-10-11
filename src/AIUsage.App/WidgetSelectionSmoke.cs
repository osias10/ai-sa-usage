using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public partial class App
{
    private async Task VerifyWidgetSelectionAsync(MainWindow window, SettingsWindow settings,
        AccountViewModel claude, AccountViewModel codex)
    {
        var tabs = (TabControl)settings.FindName("SettingsTabs");
        tabs.SelectedIndex = 0;
        settings.Width = settings.MinWidth;
        window.Width = window.MinWidth;
        window.Left = 100;
        window.Top = 100;
        await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
        var fable = claude.WidgetWindowOptions.Single(option => option.Id == "weekly_scoped:Fable:0");
        var fableChoice = VisualChildren<CheckBox>(settings).Single(choice => ReferenceEquals(choice.DataContext, fable));
        if (fableChoice.IsChecked == true) throw new InvalidOperationException("Fable must be opt-in for existing Claude accounts.");
        var originalHeight = window.ActualHeight;
        var originalWidth = window.ActualWidth;
        var originalLeft = window.Left;
        var originalTop = window.Top;
        fableChoice.IsChecked = true;
        await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        if (!claude.CompactWindows.Select(item => item.Id).SequenceEqual(new[] { "session::0", "weekly_all::0", fable.Id }) ||
            claude.CompactWindows[2].Percent != "21%" || window.ActualHeight <= originalHeight)
            throw new InvalidOperationException("Selecting Fable must append its actual usage and fit the widget height.");
        AssertWidgetPosition();
        var fableName = VisualChildren<TextBlock>(window).Single(text => text.Text == "Fable");
        if (fableName.ActualWidth < MeasureText(fableName, "Fable"))
            throw new InvalidOperationException("Fable must be identifiable at the minimum widget width.");

        claude.WidgetWindowOptions.Single(option => option.Id == "session::0").IsSelected = false;
        claude.WidgetWindowOptions.Single(option => option.Id == "weekly_all::0").IsSelected = false;
        tabs.SelectedIndex = 1;
        ((ComboBox)settings.FindName("TrayAccountChoice")).SelectedValue = claude.Id;
        await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        if (claude.CompactWindows.Count != 1 || claude.CompactWindows[0].CompactName != "Fable" || _tray!.CurrentReading.IconText != "21" ||
            window.ActualHeight >= originalHeight)
            throw new InvalidOperationException("Fable-only selection must shrink the widget and drive the tray number.");
        AssertWidgetPosition();
        fable.IsSelected = false;
        if (!fable.IsSelected || fable.CanToggle)
            throw new InvalidOperationException("The last displayed quota must remain selected.");
        if (!claude.Settings.UsageAlerts.Any(rule => rule.WindowId == "session::0" && rule.Enabled && rule.ThresholdPercent == 85))
            throw new InvalidOperationException("Changing widget visibility must leave alert rules intact.");

        var status = _controller!.GetStatuses().Single(item => item.AccountId == claude.Id);
        claude.Apply(status with { Snapshot = status.Snapshot! with { Windows = status.Snapshot.Windows.Where(item => item.Id != fable.Id).ToArray() } });
        if (claude.CompactWindows.Count != 1 || claude.CompactWindows[0].Name != "주간 · Fable" ||
            claude.CompactWindows[0].Percent != "—" || claude.CompactWindows[0].CompactReset != "—" ||
            !claude.WidgetWindowOptions.Single(option => option.Id == fable.Id).IsSelected)
            throw new InvalidOperationException("Missing selected usage must retain its name and selection with unknown values.");
        claude.Apply(status);
        claude.WidgetWindowOptions.Single(option => option.Id == "weekly_all::0").IsSelected = true;
        claude.WidgetWindowOptions.Single(option => option.Id == "session::0").IsSelected = true;
        claude.WidgetWindowOptions.Single(option => option.Id == fable.Id).IsSelected = false;
        codex.WidgetWindowOptions.Single(option => option.Id == "weekly").IsSelected = true;
        await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        if (!codex.CompactWindows.Select(item => item.Id).SequenceEqual(new[] { "primary", "weekly" }))
            throw new InvalidOperationException("Other providers must support multiple widget quotas too.");
        codex.WidgetWindowOptions.Single(option => option.Id == "weekly").IsSelected = false;
        var savedSelection = claude.Settings.WidgetWindows!.ToList();
        claude.Settings.WidgetWindows = savedSelection.Concat(Enumerable.Range(0, 100)
            .Select(index => new WidgetWindowSelection($"missing:{index}", $"한도 {index}"))).ToList();
        claude.RefreshMinutes = 6;
        await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        var scroll = VisualChildren<ScrollViewer>(window).First();
        var monitor = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(window).Handle);
        if (scroll.ScrollableHeight <= 0 || window.ActualHeight * VisualTreeHelper.GetDpi(window).DpiScaleY > monitor.WorkingArea.Height + 1 ||
            Math.Abs(window.ActualWidth - originalWidth) > 1)
            throw new InvalidOperationException("A long quota selection must scroll within the screen while preserving width.");
        claude.Settings.WidgetWindows = savedSelection;
        claude.RefreshMinutes = 5;
        ((ComboBox)settings.FindName("TrayAccountChoice")).SelectedValue = codex.Id;
        tabs.SelectedIndex = 0;
        await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
        await VerifyNumericInputsAsync(settings, claude);
        tabs.SelectedIndex = 1;

        void AssertWidgetPosition()
        {
            if (Math.Abs(window.ActualWidth - originalWidth) > 1 || Math.Abs(window.Left - originalLeft) > 1 || Math.Abs(window.Top - originalTop) > 1)
                throw new InvalidOperationException("Changing selected quotas must preserve widget width and position within the work area.");
        }
    }

    private async Task VerifyNumericInputsAsync(SettingsWindow settings, AccountViewModel claude)
    {
        var threshold = VisualChildren<TextBox>(settings).First(text => AutomationProperties.GetAutomationId(text) == "UsageAlertThreshold");
        var refresh = VisualChildren<TextBox>(settings).Single(text => AutomationProperties.GetAutomationId(text) == "RefreshMinutes" && ReferenceEquals(text.DataContext, claude));
        var alert = (UsageAlertViewModel)threshold.DataContext;
        alert.ThresholdPercent = 100;
        claude.RefreshMinutes = 1440;
        await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
        VerifyInput(threshold, "100", 64);
        VerifyInput(refresh, "1440", 72);

        // Measure the same templates under enlarged WPF layout transforms. This tests
        // clipping math; it does not substitute for physical Windows DPI visual review.
        foreach (var scale in new[] { 1.25, 1.5, 2.0 })
        {
            foreach (var source in new[] { threshold, refresh })
            {
                var copy = new TextBox
                {
                    Style = source.Style, Text = source.Text, Width = source.ActualWidth,
                    FontFamily = source.FontFamily, FontSize = source.FontSize,
                    LayoutTransform = new ScaleTransform(scale, scale)
                };
                copy.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                copy.Arrange(new Rect(copy.DesiredSize));
                copy.UpdateLayout();
                VerifyInput(copy, source.Text, source.ActualWidth);
            }
        }
        alert.ThresholdPercent = 85;
        claude.RefreshMinutes = 5;
    }

    private static void VerifyInput(TextBox input, string expected, double width)
    {
        input.ApplyTemplate();
        input.UpdateLayout();
        var host = (ScrollViewer)input.Template.FindName("PART_ContentHost", input);
        var available = host.ActualWidth - host.Padding.Left - host.Padding.Right;
        var text = new FormattedText(expected, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(input.FontFamily, input.FontStyle, input.FontWeight, input.FontStretch), input.FontSize,
            Brushes.Black, VisualTreeHelper.GetDpi(input).PixelsPerDip);
        if (input.Text != expected || Math.Abs(input.ActualWidth - width) > 1 || input.ActualHeight < 33 ||
            input.Padding != new Thickness(8, 4, 8, 4) || input.TextAlignment != TextAlignment.Right ||
            host.Margin != new Thickness(0) || available < text.WidthIncludingTrailingWhitespace + 2)
            throw new InvalidOperationException($"Numeric input clips {expected}: width={input.ActualWidth}, available={available}, glyphs={text.WidthIncludingTrailingWhitespace}.");
    }

    private static double MeasureText(TextBlock block, string value) => new FormattedText(value,
        CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
        block.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(block).PixelsPerDip).WidthIncludingTrailingWhitespace;
}
