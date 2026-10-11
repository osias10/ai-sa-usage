using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIUsage.Core;

namespace AIUsage.App;

public partial class App
{
    private async Task VerifyUpdateInfoAsync(MainWindow window, SettingsWindow settings)
    {
        if (_controller is not { IsDemo: true }) throw new InvalidOperationException("Update UI verification requires the isolated demo.");
        var tabs = (TabControl)settings.FindName("SettingsTabs");
        var previousTab = tabs.SelectedIndex;
        var usageError = _controller.ViewModel.Error;
        var check = (Button)settings.FindName("UpdateButton");
        var download = (Button)settings.FindName("DownloadUpdateButton");
        var install = (Button)settings.FindName("InstallUpdateButton");
        var autoCheck = (CheckBox)settings.FindName("AutoCheckUpdatesChoice");
        var status = (TextBlock)settings.FindName("UpdateStatus");
        var latest = (TextBlock)settings.FindName("LatestVersionLabel");
        var checkedAt = (TextBlock)settings.FindName("LastUpdateCheckLabel");
        var progress = (ProgressBar)settings.FindName("UpdateProgress");
        var progressPanel = (StackPanel)settings.FindName("UpdateProgressPanel");
        var notes = (Button)settings.FindName("ReleaseNotesButton");
        var release = new ReleaseInfo("9999.0.0", new Uri("https://github.com/aisa-test/update-fixture/releases/tag/v9999.0.0"),
            "검사용 변경 사항", new ReleaseAsset("AISA-9999.0.0-win-x64-setup.exe",
                new Uri("https://github.com/aisa-test/update-fixture/releases/download/v9999.0.0/AISA-9999.0.0-win-x64-setup.exe"),
                100, new string('a', 64)));
        var time = DateTimeOffset.UtcNow;
        try
        {
            settings.ShowUpdateInfo();
            await LayoutAsync();
            if (tabs.SelectedIndex != 2 || autoCheck.IsChecked != true || autoCheck.IsEnabled || check.IsEnabled ||
                !status.Text.Contains("데모", StringComparison.Ordinal) || install.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Demo update settings must show the default automatic preference while keeping network and installation unavailable.");

            settings.RenderUpdateState(new(AppUpdatePhase.Available, release, time));
            await LayoutAsync();
            if (latest.Text != release.Version || checkedAt.Text == "아직 확인하지 않음" ||
                download.Visibility != Visibility.Visible || install.Visibility != Visibility.Collapsed || notes.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Available updates must show their version and release notes, with download separate from installation.");

            settings.RenderUpdateState(new(AppUpdatePhase.Downloading, release, time, DownloadPercent: 42));
            await LayoutAsync();
            if (progressPanel.Visibility != Visibility.Visible || progress.IsIndeterminate || progress.Value != 42 ||
                download.Visibility != Visibility.Collapsed || install.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Downloading must show progress without offering an unverified installer.");

            settings.RenderUpdateState(new(AppUpdatePhase.Downloading, release, time));
            if (!progress.IsIndeterminate)
                throw new InvalidOperationException("Unknown download progress must remain indeterminate.");
            settings.RenderUpdateState(new(AppUpdatePhase.ReadyToInstall, release, time, DownloadPercent: 100,
                InstallerPath: Path.Combine(Path.GetTempPath(), "aisa-smoke-never-executed.exe")));
            await LayoutAsync();
            if (install.Visibility != Visibility.Visible || install.IsEnabled || download.Visibility != Visibility.Collapsed ||
                progressPanel.Visibility != Visibility.Collapsed || !window.IsVisible || !settings.IsVisible)
                throw new InvalidOperationException("A verified download must offer a separate install action and leave the application open; demos cannot execute it.");

            const string failure = "업데이트 검사 오류: 잠시 후 다시 확인하세요.";
            settings.RenderUpdateState(new(AppUpdatePhase.Error, release, time, time.AddMinutes(1), Message: failure));
            await LayoutAsync();
            if (status.Text != failure || check.IsEnabled ||
                ((TextBlock)settings.FindName("UpdateRetryLabel")).Visibility != Visibility.Visible || _controller.ViewModel.Error != usageError)
                throw new InvalidOperationException("Update failures must remain in the information tab and respect retry timing.");

            settings.RenderUpdateState(new(AppUpdatePhase.Available, release with { Asset = null }, time));
            if (download.Visibility != Visibility.Collapsed || notes.Visibility != Visibility.Visible || install.Visibility != Visibility.Collapsed || status.Text != "설치 파일 준비 중")
                throw new InvalidOperationException("A release without a verifiable installer must offer only its release page.");

            settings.RenderUpdateState(new(AppUpdatePhase.Available, release with { Asset = release.Asset! with { Sha256 = null } }, time));
            if (download.Visibility != Visibility.Collapsed || status.Text != "설치 파일 검증 정보가 준비되지 않았습니다.")
                throw new InvalidOperationException("Missing integrity metadata must remain distinct from a missing installer.");

            settings.RenderUpdateState(new(AppUpdatePhase.UpToDate, release with { Version = "0.0.0" }, time));
            if (install.Visibility != Visibility.Collapsed || download.Visibility != Visibility.Collapsed ||
                latest.Text != "0.0.0" || !status.Text.Contains("최신", StringComparison.Ordinal))
                throw new InvalidOperationException("An up-to-date app must not expose download or install actions.");
            settings.RenderUpdateState(new(AppUpdatePhase.UpToDate, LastCheckedAt: time));
            if (latest.Text != "공개된 릴리스 없음")
                throw new InvalidOperationException("An unpublished release must not fabricate a latest version.");
        }
        finally
        {
            settings.ShowUpdateInfo();
            tabs.SelectedIndex = previousTab;
            await LayoutAsync();
        }

        async Task LayoutAsync() => await Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.ApplicationIdle);
    }
}
