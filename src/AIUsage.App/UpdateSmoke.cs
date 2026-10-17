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
        var prereleaseChoice = (CheckBox)settings.FindName("IncludePrereleaseUpdatesChoice");
        var current = (TextBlock)settings.FindName("VersionLabel");
        var status = (TextBlock)settings.FindName("UpdateStatus");
        var latest = (TextBlock)settings.FindName("LatestVersionLabel");
        var checkedAt = (TextBlock)settings.FindName("LastUpdateCheckLabel");
        var progress = (ProgressBar)settings.FindName("UpdateProgress");
        var progressPanel = (StackPanel)settings.FindName("UpdateProgressPanel");
        var notes = (Button)settings.FindName("ReleaseNotesButton");
        var release = ReleaseFixture("9999.0.0");
        var time = DateTimeOffset.UtcNow;
        try
        {
            settings.ShowUpdateInfo();
            await LayoutAsync();
            if (tabs.SelectedIndex != 2 || autoCheck.IsChecked != true || autoCheck.IsEnabled || check.IsEnabled ||
                prereleaseChoice.IsChecked != false || prereleaseChoice.IsEnabled || _controller.Settings.IncludePrereleaseUpdates ||
                !(current.Text.EndsWith(" · 정식", StringComparison.Ordinal) || current.Text.EndsWith(" · 프리릴리스", StringComparison.Ordinal)) ||
                !status.Text.Contains("데모", StringComparison.Ordinal) || install.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Demo updates must show the current release type, automatic checks on and prereleases off, while keeping network and installation unavailable.");

            // Even a programmatic toggle in a demo must not persist settings or start network work.
            prereleaseChoice.IsChecked = true;
            if (_controller.Settings.IncludePrereleaseUpdates || !_controller.Settings.AutoCheckUpdates || autoCheck.IsChecked != true)
                throw new InvalidOperationException("The prerelease preference must remain independent of automatic checks and inert in demos.");

            settings.RenderUpdateState(new(AppUpdatePhase.Available, release, time, Action: UpdateAction.Upgrade, Channel: UpdateChannel.Stable));
            await LayoutAsync();
            if (latest.Text != release.Version + " · 정식" || checkedAt.Text == "아직 확인하지 않음" || prereleaseChoice.IsChecked != false ||
                download.Visibility != Visibility.Visible || install.Visibility != Visibility.Collapsed || notes.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Available updates must show their version and release notes, with download separate from installation.");

            var preview = ReleaseFixture("9999.0.0-beta.2", prerelease: true);
            settings.RenderUpdateState(new(AppUpdatePhase.Available, preview, time, Action: UpdateAction.Upgrade, Channel: UpdateChannel.Preview));
            await LayoutAsync();
            if (latest.Text != "9999.0.0-beta.2 · 프리릴리스" || prereleaseChoice.IsChecked != true || autoCheck.IsChecked != true ||
                download.Visibility != Visibility.Visible || !Equals(download.Content, "다운로드") || install.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Preview releases must retain their full version and type, without changing the automatic-check preference.");

            settings.RenderUpdateState(new(AppUpdatePhase.Downloading, preview, time, DownloadPercent: 42,
                Action: UpdateAction.Upgrade, Channel: UpdateChannel.Preview));
            await LayoutAsync();
            if (progressPanel.Visibility != Visibility.Visible || progress.IsIndeterminate || progress.Value != 42 ||
                download.Visibility != Visibility.Collapsed || install.Visibility != Visibility.Collapsed || prereleaseChoice.IsChecked != true)
                throw new InvalidOperationException("Downloading must show progress without offering an unverified installer.");

            settings.RenderUpdateState(new(AppUpdatePhase.Downloading, release, time, Action: UpdateAction.Upgrade, Channel: UpdateChannel.Stable));
            if (!progress.IsIndeterminate)
                throw new InvalidOperationException("Unknown download progress must remain indeterminate.");
            settings.RenderUpdateState(new(AppUpdatePhase.ReadyToInstall, release, time, DownloadPercent: 100,
                InstallerPath: Path.Combine(Path.GetTempPath(), "aisa-smoke-never-executed.exe"), Action: UpdateAction.Upgrade, Channel: UpdateChannel.Stable));
            await LayoutAsync();
            if (install.Visibility != Visibility.Visible || install.IsEnabled || download.Visibility != Visibility.Collapsed ||
                progressPanel.Visibility != Visibility.Collapsed || !window.IsVisible || !settings.IsVisible)
                throw new InvalidOperationException("A verified download must offer a separate install action and leave the application open; demos cannot execute it.");

            const string failure = "업데이트 검사 오류: 잠시 후 다시 확인하세요.";
            settings.RenderUpdateState(new(AppUpdatePhase.Error, release, time, time.AddMinutes(1), Message: failure,
                Action: UpdateAction.Upgrade, Channel: UpdateChannel.Stable));
            await LayoutAsync();
            if (status.Text != failure || check.IsEnabled ||
                ((TextBlock)settings.FindName("UpdateRetryLabel")).Visibility != Visibility.Visible || _controller.ViewModel.Error != usageError)
                throw new InvalidOperationException("Update failures must remain in the information tab and respect retry timing.");

            settings.RenderUpdateState(new(AppUpdatePhase.Available, release with { Asset = null }, time,
                Action: UpdateAction.Upgrade, Channel: UpdateChannel.Stable));
            if (download.Visibility != Visibility.Collapsed || notes.Visibility != Visibility.Visible || install.Visibility != Visibility.Collapsed || status.Text != "설치 파일 준비 중")
                throw new InvalidOperationException("A release without a verifiable installer must offer only its release page.");

            settings.RenderUpdateState(new(AppUpdatePhase.Available, release with { Asset = release.Asset! with { Sha256 = null } }, time,
                Action: UpdateAction.Upgrade, Channel: UpdateChannel.Stable));
            if (download.Visibility != Visibility.Collapsed || status.Text != "설치 파일 검증 정보가 준비되지 않았습니다.")
                throw new InvalidOperationException("Missing integrity metadata must remain distinct from a missing installer.");

            // A stable return can target a lower numeric version. The service's decision controls the UI.
            var stableReturn = ReleaseFixture("0.0.1");
            settings.RenderUpdateState(new(AppUpdatePhase.Available, stableReturn, time,
                Action: UpdateAction.ReturnToStable, Channel: UpdateChannel.Stable));
            await LayoutAsync();
            if (latest.Text != "0.0.1 · 정식" || prereleaseChoice.IsChecked != false || autoCheck.IsChecked != true ||
                download.Visibility != Visibility.Visible || !Equals(download.Content, "정식 버전 다운로드") ||
                install.Visibility != Visibility.Collapsed || !status.Text.Contains("정식 버전으로 돌아", StringComparison.Ordinal) ||
                !window.IsVisible || !settings.IsVisible)
                throw new InvalidOperationException("Returning to stable must offer the lower-version download and leave the application running.");

            settings.RenderUpdateState(new(AppUpdatePhase.ReadyToInstall, stableReturn, time, DownloadPercent: 100,
                InstallerPath: Path.Combine(Path.GetTempPath(), "aisa-stable-smoke-never-executed.exe"),
                Action: UpdateAction.ReturnToStable, Channel: UpdateChannel.Stable));
            await LayoutAsync();
            if (install.Visibility != Visibility.Visible || !Equals(install.Content, "정식 버전 설치") || install.IsEnabled ||
                download.Visibility != Visibility.Collapsed || !window.IsVisible || !settings.IsVisible)
                throw new InvalidOperationException("A verified stable return must keep installation separate and never execute from demo verification.");

            settings.RenderUpdateState(new(AppUpdatePhase.ReadyToInstall, release, time,
                InstallerPath: Path.Combine(Path.GetTempPath(), "aisa-obsolete-smoke-never-executed.exe"),
                Action: UpdateAction.None, Channel: UpdateChannel.Stable));
            if (install.Visibility != Visibility.Collapsed || download.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("A cancelled channel action must not expose an old installer even when its file path remains in a supplied snapshot.");

            settings.RenderUpdateState(new(AppUpdatePhase.UpToDate, ReleaseFixture("0.0.0"), time,
                Action: UpdateAction.None, Channel: UpdateChannel.Stable));
            if (install.Visibility != Visibility.Collapsed || download.Visibility != Visibility.Collapsed ||
                latest.Text != "0.0.0 · 정식" || !status.Text.Contains("최신", StringComparison.Ordinal))
                throw new InvalidOperationException("An up-to-date app must not expose download or install actions.");
            settings.RenderUpdateState(new(AppUpdatePhase.UpToDate, LastCheckedAt: time, Action: UpdateAction.None, Channel: UpdateChannel.Stable));
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
        static ReleaseInfo ReleaseFixture(string version, bool prerelease = false) => new(version,
            new Uri($"https://github.com/aisa-test/update-fixture/releases/tag/v{version}"), "검사용 변경 사항",
            new ReleaseAsset($"AISA-{version}-win-x64-setup.exe",
                new Uri($"https://github.com/aisa-test/update-fixture/releases/download/v{version}/AISA-{version}-win-x64-setup.exe"),
                100, new string('a', 64)), IsPrerelease: prerelease);
    }
}
