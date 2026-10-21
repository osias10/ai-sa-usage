using System.Windows;

namespace AIUsage.App;

public sealed partial class AppController
{
    private readonly SemaphoreSlim _widgetVisibilityGate = new(1, 1);

    // Used only by the isolated demo smoke test; real profiles always use the store.
    internal Func<Task>? SaveWidgetVisibilityForVerification { get; set; }

    public async Task SetWidgetVisibilityAsync(bool visible)
    {
        await _widgetVisibilityGate.WaitAsync();
        try
        {
            if (_disposed || _preparingUpdate) return;
            var previous = Settings.ShowWidget;
            if (previous != visible)
            {
                Settings.ShowWidget = visible;
                try
                {
                    if (IsDemo && SaveWidgetVisibilityForVerification is { } save) await save();
                    else await SaveSettingsAsync();
                }
                catch
                {
                    Settings.ShowWidget = previous;
                    PreferencesUpdated?.Invoke(this, EventArgs.Empty);
                    throw;
                }
            }
            // Persist first. A rejected save leaves both windows and the tray usable
            // in the previous state, rather than hiding the only available widget.
            if (!_disposed && Window is { } widget)
            {
                if (visible)
                {
                    widget.Show();
                    widget.WindowState = WindowState.Normal;
                    widget.EnsurePlacementVisible();
                    if (!Settings.ClickThrough) widget.Activate();
                }
                else widget.Hide();
            }
            PreferencesUpdated?.Invoke(this, EventArgs.Empty);
        }
        finally { _widgetVisibilityGate.Release(); }
    }

    public void ShowWidget() => _ = ChangeWidgetVisibilityFromActionAsync(true);
    public void HideWidget() => _ = ChangeWidgetVisibilityFromActionAsync(false);

    private async Task ChangeWidgetVisibilityFromActionAsync(bool visible)
    {
        try { await SetWidgetVisibilityAsync(visible); }
        catch (Exception error) { ShowError(error); }
    }
}
