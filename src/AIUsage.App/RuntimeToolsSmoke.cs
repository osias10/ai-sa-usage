using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public partial class App
{
    private async Task VerifyRuntimeToolsAsync(SettingsWindow settings)
    {
        if (_controller is not { IsDemo: true } controller)
            throw new InvalidOperationException("Runtime tool UI verification requires the isolated demo.");
        var tabs = (TabControl)settings.FindName("SettingsTabs");
        var page = (RuntimeToolsView)settings.FindName("RuntimeToolsPage");
        var originalTab = tabs.SelectedIndex;
        var originalWidth = settings.Width;
        var usage = controller.GetStatuses().ToArray();
        var usageError = controller.ViewModel.Error;
        var service = new RuntimeToolsSmokeService();
        SettingsWindow? reopened = null;
        try
        {
            page.InitializeService(service);
            settings.Width = settings.MinWidth;
            tabs.SelectedIndex = 2;
            await FlushAsync();
            var rows = VisualChildren<Border>(page).Where(border => border.Name == "RuntimeToolRow").ToArray();
            if (!page.IsVisible || rows.Length != 3 || page.ActualWidth < 400 ||
                !Text(ProviderKind.Claude, "RuntimeToolCurrentVersion").Text.Equals("2.1.288", StringComparison.Ordinal) ||
                !Text(ProviderKind.Claude, "RuntimeToolSupportedVersion").Text.Equals("2.1.289", StringComparison.Ordinal) ||
                !Equals(FindButton(ProviderKind.Codex, "UpdateRuntimeToolButton").Content, "설치") ||
                FindButton(ProviderKind.Antigravity, "UpdateRuntimeToolButton").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Runtime tools must show installed and compatible versions with the correct actions at the minimum settings width.");
            foreach (var button in VisualChildren<Button>(page).Where(button => button.Visibility == Visibility.Visible))
            {
                var left = button.TransformToAncestor(page).Transform(new Point()).X;
                if (button.ActualHeight < 34 || button.ActualWidth < 65 || left < -1 || left + button.ActualWidth > page.ActualWidth + 1)
                    throw new InvalidOperationException("Runtime tool buttons must remain readable inside the minimum-width settings page.");
            }

            var check = (Button)page.FindName("CheckRuntimeToolsButton");
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await FlushAsync();
            if (service.CheckCount != 1 || Text(ProviderKind.Claude, "RuntimeToolLastChecked").Text == "아직 확인하지 않음")
                throw new InvalidOperationException("Checking runtime tools must update their last-check labels.");

            service.FailCheck = true;
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await FlushAsync();
            if (((TextBlock)page.FindName("RuntimeToolsError")).Visibility != Visibility.Visible || controller.ViewModel.Error != usageError)
                throw new InvalidOperationException("Runtime checks must show errors only in their own section.");
            service.FailCheck = false;
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await FlushAsync();
            if (((TextBlock)page.FindName("RuntimeToolsError")).Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("A new runtime check must clear its previous local error.");

            FindButton(ProviderKind.Claude, "UpdateRuntimeToolButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await FlushAsync();
            var progress = VisualChildren<ProgressBar>(page).Single(bar => bar.DataContext is RuntimeToolRowModel { Provider: ProviderKind.Claude });
            if (!service.IsPending(ProviderKind.Claude) || progress.Value != 42 || progress.IsIndeterminate || progress.ActualWidth < 200 ||
                FindButton(ProviderKind.Claude, "CancelRuntimeToolButton").Visibility != Visibility.Visible ||
                FindButton(ProviderKind.Claude, "UpdateRuntimeToolButton").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("A tool download must show progress and cancellation instead of competing install actions.");

            service.Report(ProviderKind.Claude, RuntimeToolPhase.WaitingForIdle, "진행 중인 조회가 끝나면 적용합니다.");
            await FlushAsync();
            if (!progress.IsIndeterminate || Text(ProviderKind.Claude, "RuntimeToolState").Text != "사용 종료 대기" ||
                Text(ProviderKind.Claude, "RuntimeToolCurrentVersion").Text != "2.1.288")
                throw new InvalidOperationException("A pending switch must retain the active version and explain its wait.");

            // Reopening shares the operation; closing only removes this window's subscription.
            reopened = new SettingsWindow(controller) { Width = settings.MinWidth };
            var reopenedPage = (RuntimeToolsView)reopened.FindName("RuntimeToolsPage");
            reopenedPage.InitializeService(service);
            reopened.Show();
            ((TabControl)reopened.FindName("SettingsTabs")).SelectedIndex = 2;
            await Dispatcher.InvokeAsync(reopened.UpdateLayout, DispatcherPriority.ApplicationIdle);
            if (service.SubscriberCount != 2 ||
                !VisualChildren<Button>(reopenedPage).Any(button => button.Name == "CancelRuntimeToolButton" &&
                    button.DataContext is RuntimeToolRowModel { Provider: ProviderKind.Claude } && button.Visibility == Visibility.Visible))
                throw new InvalidOperationException("A reopened settings window must display the existing runtime operation.");
            reopened.Close();
            reopened = null;
            if (service.SubscriberCount != 1 || service.CancelCount != 0 || !service.IsPending(ProviderKind.Claude))
                throw new InvalidOperationException("Closing Settings must unsubscribe without cancelling an app-owned tool operation.");

            FindButton(ProviderKind.Claude, "CancelRuntimeToolButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await FlushAsync();
            if (service.CancelCount != 1 || service.IsPending(ProviderKind.Claude) ||
                Text(ProviderKind.Claude, "RuntimeToolState").Text != "취소됨" ||
                Text(ProviderKind.Claude, "RuntimeToolCurrentVersion").Text != "2.1.288" ||
                FindButton(ProviderKind.Claude, "UpdateRuntimeToolButton").Visibility != Visibility.Visible)
                throw new InvalidOperationException("Cancelling must retain the installed version and allow a later update.");

            FindButton(ProviderKind.Claude, "UpdateRuntimeToolButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            service.Complete(ProviderKind.Claude);
            await FlushAsync();
            if (Text(ProviderKind.Claude, "RuntimeToolCurrentVersion").Text != "2.1.289" ||
                FindButton(ProviderKind.Claude, "UpdateRuntimeToolButton").Visibility != Visibility.Collapsed ||
                FindButton(ProviderKind.Claude, "ReinstallRuntimeToolButton").Visibility != Visibility.Visible)
                throw new InvalidOperationException("A completed tool update must show the new version and a repair action.");

            FindButton(ProviderKind.Claude, "ReinstallRuntimeToolButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!service.LastReinstall) throw new InvalidOperationException("Reinstall must request verified replacement even for the current version.");
            service.Complete(ProviderKind.Claude);
            FindButton(ProviderKind.Codex, "UpdateRuntimeToolButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (service.LastReinstall) throw new InvalidOperationException("Installing a missing tool must not be treated as a repair.");
            service.Complete(ProviderKind.Codex);
            await FlushAsync();
            if (Text(ProviderKind.Codex, "RuntimeToolCurrentVersion").Text != "0.157.0" ||
                FindButton(ProviderKind.Codex, "ReinstallRuntimeToolButton").Visibility != Visibility.Visible)
                throw new InvalidOperationException("A newly installed tool must become available without restarting Settings.");

            service.FailUpdate = true;
            FindButton(ProviderKind.Antigravity, "ReinstallRuntimeToolButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await FlushAsync();
            if (Text(ProviderKind.Antigravity, "RuntimeToolMessage").Visibility != Visibility.Visible ||
                !Text(ProviderKind.Antigravity, "RuntimeToolMessage").Text.Contains("준비하지 못했습니다.", StringComparison.Ordinal) ||
                controller.ViewModel.Error != usageError || !controller.GetStatuses().SequenceEqual(usage))
                throw new InvalidOperationException("Tool action failures must stay separate from account usage state.");
            service.FailUpdate = false;
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await FlushAsync();
            if (Text(ProviderKind.Antigravity, "RuntimeToolState").Text != "준비됨" ||
                Text(ProviderKind.Antigravity, "RuntimeToolMessage").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("A later successful status check must clear stale local tool errors.");
        }
        finally
        {
            reopened?.Close();
            service.CancelAll();
            await FlushAsync();
            page.InitializeService(controller.RuntimeTools);
            settings.Width = originalWidth;
            tabs.SelectedIndex = originalTab;
            await FlushAsync();
            if (service.SubscriberCount != 0)
                throw new InvalidOperationException("The runtime view must release its previous service subscription.");
        }

        async Task FlushAsync()
        {
            // Async click continuations complete before the idle-priority layout pass.
            await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
        }
        Button FindButton(ProviderKind provider, string name) => VisualChildren<Button>(page).Single(button =>
            button.Name == name && button.DataContext is RuntimeToolRowModel row && row.Provider == provider);
        TextBlock Text(ProviderKind provider, string name) => VisualChildren<TextBlock>(page).Single(text =>
            text.Name == name && text.DataContext is RuntimeToolRowModel row && row.Provider == provider);
    }

    /// <summary>Only in-memory state transitions: never invokes a tool, downloads a file, or reads account data.</summary>
    private sealed class RuntimeToolsSmokeService : IRuntimeToolService
    {
        private readonly Dictionary<ProviderKind, RuntimeToolStatus> _states = new()
        {
            [ProviderKind.Claude] = new(ProviderKind.Claude, "2.1.289", "2.1.288", RuntimeToolPhase.UpdateAvailable, IsInstalled: true),
            [ProviderKind.Codex] = new(ProviderKind.Codex, "0.157.0"),
            [ProviderKind.Antigravity] = new(ProviderKind.Antigravity, "1.3.1", "1.3.1", RuntimeToolPhase.Ready, IsInstalled: true)
        };
        private readonly Dictionary<ProviderKind, TaskCompletionSource> _pending = [];
        private EventHandler? _changed;
        public int SubscriberCount { get; private set; }
        public int CheckCount { get; private set; }
        public int CancelCount { get; private set; }
        public bool LastReinstall { get; private set; }
        public bool FailCheck { get; set; }
        public bool FailUpdate { get; set; }
        public event EventHandler? Changed
        {
            add { _changed += value; SubscriberCount++; }
            remove { _changed -= value; SubscriberCount--; }
        }
        public IReadOnlyList<RuntimeToolStatus> GetStatuses() => _states.Values.OrderBy(status => status.Provider).ToArray();
        public Task CheckAsync(CancellationToken cancellationToken)
        {
            if (FailCheck) throw new IOException("테스트용 확인 실패");
            CheckCount++;
            foreach (var provider in _states.Keys.ToArray())
                _states[provider] = _states[provider] with { LastCheckedAt = DateTimeOffset.UtcNow };
            _changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        public Task UpdateAsync(ProviderKind provider, bool reinstall, CancellationToken cancellationToken)
        {
            if (FailUpdate) throw new IOException("테스트용 준비 실패");
            LastReinstall = reinstall;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(provider, completion);
            Report(provider, RuntimeToolPhase.Downloading, "다운로드 중", 42);
            return completion.Task;
        }
        public void Report(ProviderKind provider, RuntimeToolPhase phase, string? message, double? percent = null)
        {
            _states[provider] = _states[provider] with { Phase = phase, Message = message, Percent = percent };
            _changed?.Invoke(this, EventArgs.Empty);
        }
        public bool IsPending(ProviderKind provider) => _pending.ContainsKey(provider);
        public void Complete(ProviderKind provider)
        {
            var status = _states[provider];
            _states[provider] = status with { Phase = RuntimeToolPhase.Ready, InstalledVersion = status.TargetVersion, IsInstalled = true, Message = null, Percent = null };
            _changed?.Invoke(this, EventArgs.Empty);
            if (_pending.Remove(provider, out var completion)) completion.TrySetResult();
        }
        public void Cancel(ProviderKind provider)
        {
            CancelCount++;
            Report(provider, RuntimeToolPhase.Cancelled, "준비를 취소했습니다.");
            if (_pending.Remove(provider, out var completion)) completion.TrySetCanceled();
        }
        public void CancelAll()
        {
            foreach (var provider in _pending.Keys.ToArray()) Cancel(provider);
        }
    }
}
