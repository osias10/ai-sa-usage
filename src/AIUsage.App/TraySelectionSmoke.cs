using System.Windows.Controls;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public partial class App
{
    private async Task VerifyTrayWindowSelectionAsync(SettingsWindow settings, AccountViewModel claude, AccountViewModel codex)
    {
        ((TabControl)settings.FindName("SettingsTabs")).SelectedIndex = 1;
        var accountChoice = (ComboBox)settings.FindName("TrayAccountChoice");
        var quotaChoice = (ComboBox)settings.FindName("TrayWindowChoice");
        accountChoice.SelectedValue = claude.Id;
        await FlushAsync();
        if (claude.Settings.TrayWindow is not null || quotaChoice.SelectedIndex != 0 || _tray!.CurrentReading.IconText != "37")
            throw new InvalidOperationException("The default tray quota must follow the first widget item.");

        const string fableId = "weekly_scoped:Fable:0";
        quotaChoice.SelectedValue = fableId;
        await FlushAsync();
        if (claude.Settings.TrayWindow?.Id != fableId || _tray!.CurrentReading.IconText != "21" ||
            claude.CompactWindows.Count != 2 || claude.CompactWindows.Any(window => window.Id == fableId))
            throw new InvalidOperationException("The tray must display Fable independently of widget selection.");

        accountChoice.SelectedValue = codex.Id;
        await FlushAsync();
        if (codex.Settings.TrayWindow is not null || quotaChoice.SelectedIndex != 0 || _tray!.CurrentReading.IconText != "24")
            throw new InvalidOperationException("Changing tray accounts must not copy another account's quota selection.");
        quotaChoice.SelectedValue = "weekly";
        await FlushAsync();
        if (codex.Settings.TrayWindow?.Id != "weekly" || _tray!.CurrentReading.IconText != "43")
            throw new InvalidOperationException("Codex must support an explicit weekly tray quota.");

        accountChoice.SelectedValue = claude.Id;
        await FlushAsync();
        if ((string?)quotaChoice.SelectedValue != fableId || _tray!.CurrentReading.IconText != "21")
            throw new InvalidOperationException("Returning to an account must restore its chosen tray quota.");
        ((ComboBox)settings.FindName("DisplayModeChoice")).SelectedIndex = 1;
        await FlushAsync();
        if (_tray!.CurrentReading.IconText != "79")
            throw new InvalidOperationException("Remaining mode must also apply to the explicit tray quota.");
        ((ComboBox)settings.FindName("DisplayModeChoice")).SelectedIndex = 0;
        quotaChoice.SelectedIndex = 0;
        await FlushAsync();
        if (claude.Settings.TrayWindow is not null || _tray!.CurrentReading.IconText != "37")
            throw new InvalidOperationException("Choosing the default must resume following the widget's first quota.");

        quotaChoice.SelectedValue = fableId;
        await FlushAsync();
        var original = _controller!.GetStatuses().Single(status => status.AccountId == claude.Id);
        try
        {
            // A transient missing meter must remain selectable by its saved name.
            claude.Apply(original with
            {
                Snapshot = original.Snapshot! with { Windows = original.Snapshot.Windows.Where(window => window.Id != fableId).ToArray() }
            });
            accountChoice.SelectedValue = codex.Id;
            await FlushAsync();
            accountChoice.SelectedValue = claude.Id;
            await FlushAsync();
            if ((string?)quotaChoice.SelectedValue != fableId || claude.Settings.TrayWindow?.Name != "주간 · Fable")
                throw new InvalidOperationException("A missing tray quota must retain its selection and saved name.");
        }
        finally { claude.Apply(original); }
        await FlushAsync();

        async Task FlushAsync() => await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
    }
}
