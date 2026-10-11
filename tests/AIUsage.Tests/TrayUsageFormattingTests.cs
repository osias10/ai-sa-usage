using System.Globalization;
using System.Text;
using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class TrayUsageFormattingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-09T03:00:00Z");

    [Fact]
    public void AccountSelectionUsesExplicitChoiceThenFirstPinnedThenFirstInOrder()
    {
        var first = Account("first", 0, false);
        var pinned = Account("pinned", 1, true);
        var last = Account("last", 2, true);
        var settings = new AppSettings { Accounts = [last, pinned, first], TrayAccountId = last.Id };
        AccountUsageStatus[] statuses = [Status(first), Status(pinned), Status(last)];

        Assert.Equal(last.Id, TrayUsageFormatting.GetReading(settings, statuses, Now).AccountId);
        settings.TrayAccountId = "deleted-account";
        Assert.Equal(pinned.Id, TrayUsageFormatting.GetReading(settings, statuses, Now).AccountId);
        pinned.Pinned = last.Pinned = false;
        Assert.Equal(first.Id, TrayUsageFormatting.GetReading(settings, statuses, Now).AccountId);
    }

    [Fact]
    public void LegacyClaudeTrayMatchesFirstWidgetQuotaInsteadOfLegacySelectionOrMaximum()
    {
        var account = Account();
        account.SelectedWindowId = "weekly_all::0";
        UsageWindow session = new("session::0", "현재 세션", 25, Now.AddHours(1), true);
        UsageWindow weekly = new("weekly_all::0", "주간 전체", 80, Now.AddDays(1));
        UsageWindow scoped = new("weekly_scoped:Model:0", "주간 · Model", 99, Now.AddDays(1));
        var status = Status(account) with { Snapshot = Snapshot(account, [weekly, session, scoped]) };
        var settings = new AppSettings { Accounts = [account] };

        var reading = TrayUsageFormatting.GetReading(settings, [status], Now);
        Assert.Equal(session.Id, reading.WindowId);
        Assert.Equal("25", reading.IconText);
        Assert.Equal(UsageFormatting.SelectCompactWindows(account, status.Snapshot).First().Id, reading.WindowId);

        status = status with { Snapshot = Snapshot(account, [weekly, scoped]) };
        var missingSession = TrayUsageFormatting.GetReading(settings, [status], Now);
        Assert.Equal(session.Id, missingSession.WindowId);
        Assert.Equal("—", missingSession.IconText);
    }

    [Fact]
    public void FableOnlyWidgetAlsoShowsFableInTray()
    {
        var account = Account();
        account.WidgetWindows = [new("weekly_scoped:Fable:0", "주간 · Fable")];
        UsageWindow session = new("session::0", "현재 세션", 25, Now.AddHours(1), true);
        UsageWindow weekly = new("weekly_all::0", "주간 전체", 80, Now.AddDays(1));
        UsageWindow fable = new("weekly_scoped:Fable:0", "주간 · Fable", 46, Now.AddDays(1));
        var status = Status(account) with { Snapshot = Snapshot(account, [session, weekly, fable]) };

        var reading = TrayUsageFormatting.GetReading(new() { Accounts = [account] }, [status], Now);

        Assert.Equal(fable.Id, reading.WindowId);
        Assert.Equal("46", reading.IconText);
        Assert.Contains("주간 · Fable", reading.Tooltip);
    }

    [Theory]
    [InlineData(false, "46")]
    [InlineData(true, "54")]
    public void ExplicitTrayFableCanDifferFromWidgetSessionWithoutChangingWidgetSelection(bool remaining, string icon)
    {
        var account = Account();
        account.WidgetWindows = [new("session::0", "현재 세션")];
        var widgetSelections = account.WidgetWindows;
        account.TrayWindow = new("weekly_scoped:Fable:0", "이전 Fable 이름");
        var status = Status(account) with { Snapshot = Snapshot(account,
            [new("session::0", "현재 세션", 25, Now.AddHours(1), true), new("weekly_scoped:Fable:0", "주간 · Fable", 46, Now.AddDays(1))]) };

        var reading = TrayUsageFormatting.GetReading(new() { Accounts = [account], ShowRemaining = remaining }, [status], Now);

        Assert.Equal("weekly_scoped:Fable:0", reading.WindowId);
        Assert.Equal(icon, reading.IconText);
        Assert.Contains("주간 · Fable", reading.Tooltip);
        Assert.Same(widgetSelections, account.WidgetWindows);
        Assert.Equal(new WidgetWindowSelection("session::0", "현재 세션"), Assert.Single(account.WidgetWindows));
    }

    [Fact]
    public void EachTrayAccountKeepsItsOwnQuotaChoiceAndClearingItRestoresWidgetDefault()
    {
        var first = Account("first");
        var second = Account("second", 1);
        first.WidgetWindows = [new("session::0", "현재 세션")];
        second.WidgetWindows = [new("session::0", "현재 세션")];
        first.TrayWindow = new("weekly_all::0", "주간 전체");
        second.TrayWindow = new("weekly_scoped:Fable:0", "주간 · Fable");
        var firstStatus = Status(first) with { Snapshot = Snapshot(first,
            [new("session::0", "현재 세션", 11, Now.AddHours(1), true), new("weekly_all::0", "주간 전체", 67, Now.AddDays(1))]) };
        var secondStatus = Status(second) with { Snapshot = Snapshot(second,
            [new("session::0", "현재 세션", 42, Now.AddHours(1), true), new("weekly_scoped:Fable:0", "주간 · Fable", 21, Now.AddDays(1))]) };
        var settings = new AppSettings { Accounts = [first, second], TrayAccountId = first.Id };

        var firstReading = TrayUsageFormatting.GetReading(settings, [firstStatus, secondStatus], Now);
        Assert.Equal("weekly_all::0", firstReading.WindowId);
        Assert.Equal("67", firstReading.IconText);
        settings.TrayAccountId = second.Id;
        var secondReading = TrayUsageFormatting.GetReading(settings, [firstStatus, secondStatus], Now);
        Assert.Equal("weekly_scoped:Fable:0", secondReading.WindowId);
        Assert.Equal("21", secondReading.IconText);
        Assert.Equal("weekly_all::0", first.TrayWindow.Id);

        second.TrayWindow = null;
        var restoredDefault = TrayUsageFormatting.GetReading(settings, [firstStatus, secondStatus], Now);
        Assert.Equal("session::0", restoredDefault.WindowId);
        Assert.Equal("42", restoredDefault.IconText);
    }

    [Fact]
    public void MissingExplicitTrayQuotaKeepsSavedNameAndNeverFallsBackToAvailableWidgetMeter()
    {
        var account = Account();
        account.WidgetWindows = [new("session::0", "현재 세션")];
        account.TrayWindow = new("weekly_scoped:Fable:0", "저장된 Fable 한도");
        var settings = new AppSettings { Accounts = [account] };

        foreach (var statuses in new AccountUsageStatus[][] { [Status(account, 90)], [] })
        {
            var reading = TrayUsageFormatting.GetReading(settings, statuses, Now);
            Assert.Equal("weekly_scoped:Fable:0", reading.WindowId);
            Assert.Equal("—", reading.IconText);
            Assert.False(reading.IsWarning);
            Assert.Contains("저장된 Fable 한도", reading.Tooltip);
            Assert.DoesNotContain("90%", reading.Tooltip);
        }
        Assert.Equal("session::0", Assert.Single(account.WidgetWindows).Id);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("offline")]
    [InlineData("identity")]
    public void ExplicitTraySelectionStillRequiresFreshSuccessfulMatchingAccountData(string failure)
    {
        var account = Account();
        account.TrayWindow = new("weekly_scoped:Fable:0", "저장 Fable");
        var snapshot = Snapshot(account,
            [new("weekly_scoped:Fable:0", "주간 · Fable", 98, failure == "expired" ? Now : Now.AddDays(1))]);
        snapshot = failure switch
        {
            "stale" => snapshot with { FetchedAt = Now.AddMinutes(-11) },
            "future" => snapshot with { FetchedAt = Now.AddSeconds(1) },
            "identity" => snapshot with { IdentityKey = "another-login" },
            _ => snapshot
        };
        var status = Status(account) with { Snapshot = snapshot, State = failure == "offline" ? UsageState.Offline : UsageState.Ready };

        var reading = TrayUsageFormatting.GetReading(new() { Accounts = [account] }, [status], Now);

        Assert.Equal("—", reading.IconText);
        Assert.False(reading.IsWarning);
        Assert.DoesNotContain("98%", reading.Tooltip);
        Assert.Null(account.WidgetWindows); // An explicit tray choice must not initialize or rewrite widget choices.
    }

    [Fact]
    public void MultipleClaudeSelectionsUseTheSameFirstQuotaAsWidget()
    {
        var account = Account();
        account.WidgetWindows = [new("weekly_scoped:Fable:0", "주간 · Fable"), new("weekly_all::0", "주간 전체"), new("session::0", "현재 세션")];
        var status = Status(account) with { Snapshot = Snapshot(account,
            [new("weekly_scoped:Fable:0", "주간 · Fable", 99, Now.AddDays(1)),
             new("weekly_all::0", "주간 전체", 80, Now.AddDays(1)),
             new("session::0", "현재 세션", 25, Now.AddHours(1), true)]) };

        var reading = TrayUsageFormatting.GetReading(new() { Accounts = [account] }, [status], Now);

        Assert.Equal("session::0", reading.WindowId);
        Assert.Equal(UsageFormatting.SelectCompactWindows(account, status.Snapshot).First().Id, reading.WindowId);
        Assert.Equal("25", reading.IconText);
    }

    [Theory]
    [InlineData(ProviderKind.Codex)]
    [InlineData(ProviderKind.Antigravity)]
    public void OtherProvidersTrayKeepsTheWidgetsSavedFirstSelection(ProviderKind provider)
    {
        var account = Account();
        account.Provider = provider;
        account.WidgetWindows = [new("secondary", "주간"), new("primary", "기본 한도")];
        var status = Status(account) with { Snapshot = Snapshot(account,
            [new("primary", "기본 한도", 90, Now.AddHours(1), true), new("secondary", "주간", 41, Now.AddDays(1))]) };

        var reading = TrayUsageFormatting.GetReading(new() { Accounts = [account] }, [status], Now);

        Assert.Equal("secondary", reading.WindowId);
        Assert.Equal("41", reading.IconText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingFirstWidgetQuotaRemainsUnknownEvenWhenOtherQuotasAreAvailable(bool includeAnotherSelection)
    {
        var account = Account();
        account.WidgetWindows = [new("weekly_scoped:Fable:0", "주간 · Fable")];
        if (includeAnotherSelection) account.WidgetWindows.Add(new("weekly_scoped:Other:0", "주간 · Other"));
        var status = Status(account) with { Snapshot = Snapshot(account,
            [new("session::0", "현재 세션", 25, Now.AddHours(1), true),
             new("weekly_all::0", "주간 전체", 80, Now.AddDays(1)),
             new("weekly_scoped:Other:0", "주간 · Other", 60, Now.AddDays(1))]) };

        var reading = TrayUsageFormatting.GetReading(new() { Accounts = [account] }, [status], Now);

        Assert.Equal("weekly_scoped:Fable:0", reading.WindowId);
        Assert.Equal("—", reading.IconText);
        Assert.False(reading.IsWarning);
        Assert.Contains("주간 · Fable", reading.Tooltip);
        Assert.Contains("사용량 미제공", reading.Tooltip);
    }

    [Fact]
    public void HiddenQuotaAlertRuleIsPreservedWithoutChoosingItsQuotaForTray()
    {
        var account = Account();
        account.WidgetWindows = [new("weekly_scoped:Fable:0", "주간 · Fable")];
        UsageAlertRule sessionAlert = new() { WindowId = "session::0", Enabled = true, ThresholdPercent = 10 };
        account.UsageAlerts = [sessionAlert];
        var status = Status(account) with { Snapshot = Snapshot(account,
            [new("session::0", "현재 세션", 95, Now.AddHours(1), true), new("weekly_scoped:Fable:0", "주간 · Fable", 21, Now.AddDays(1))]) };

        var reading = TrayUsageFormatting.GetReading(new() { Accounts = [account] }, [status], Now);

        Assert.Equal("weekly_scoped:Fable:0", reading.WindowId);
        Assert.Equal("21", reading.IconText);
        Assert.False(reading.IsWarning);
        Assert.Same(sessionAlert, Assert.Single(account.UsageAlerts));
        Assert.True(sessionAlert.Enabled);
        Assert.Equal(10, sessionAlert.ThresholdPercent);
    }

    [Theory]
    [InlineData(false, 12.5, "13", "사용 12.5%")]
    [InlineData(true, 12.5, "88", "잔여 87.5%")]
    [InlineData(true, 68.1, "32", "잔여 31.9%")]
    [InlineData(false, 0, "0", "사용 0%")]
    [InlineData(true, 100, "0", "잔여 0%")]
    public void IconAndTooltipUseTheirOwnPrecisionAndKeepDisplayMode(bool remaining, double used, string icon, string text)
    {
        var account = Account();
        var reading = TrayUsageFormatting.GetReading(new() { Accounts = [account], ShowRemaining = remaining }, [Status(account, used)], Now);

        Assert.Equal(icon, reading.IconText);
        Assert.Contains(text, reading.Tooltip);
        Assert.Contains("Claude · 개인", reading.Tooltip);
        Assert.Contains("현재 세션", reading.Tooltip);
        Assert.Contains("초기화 " + Now.AddHours(1).ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture), reading.Tooltip);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1d)]
    [InlineData(101d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void MissingOrInvalidMetricNeverBecomesZeroOrFullRemaining(double? used)
    {
        var account = Account();
        var reading = TrayUsageFormatting.GetReading(new() { Accounts = [account], ShowRemaining = true }, [Status(account, used)], Now);

        Assert.Equal("—", reading.IconText);
        Assert.False(reading.IsWarning);
        Assert.Contains("잔여 —", reading.Tooltip);
        Assert.Contains("사용량 미제공", reading.Tooltip);
    }

    [Theory]
    [InlineData(UsageState.Offline, "오프라인")]
    [InlineData(UsageState.SignInRequired, "로그인 필요")]
    [InlineData(UsageState.RateLimited, "조회 제한")]
    [InlineData(UsageState.UpdateRequired, "업데이트 필요")]
    [InlineData(UsageState.Unavailable, "사용량 미제공")]
    public void FailedStatusDoesNotExposeLastGoodSnapshotAsCurrentValue(UsageState state, string label)
    {
        var account = Account();
        var status = Status(account, 95) with { State = state };
        var reading = TrayUsageFormatting.GetReading(new() { Accounts = [account] }, [status], Now);

        Assert.Equal("—", reading.IconText);
        Assert.False(reading.IsWarning);
        Assert.Contains(label, reading.Tooltip);
        Assert.Contains("마지막 갱신", reading.Tooltip);
        Assert.DoesNotContain("95%", reading.Tooltip);
    }

    [Theory]
    [InlineData(1, 600, "42")]
    [InlineData(1, 601, "—")]
    [InlineData(15, 1800, "42")]
    [InlineData(15, 1801, "—")]
    public void StalenessUsesStrictBoundaryOfTwiceIntervalWithTenMinuteMinimum(int interval, int ageSeconds, string icon)
    {
        var account = Account();
        account.RefreshMinutes = interval;
        var status = Status(account) with { Snapshot = Snapshot(account) with { FetchedAt = Now.AddSeconds(-ageSeconds) } };
        var reading = TrayUsageFormatting.GetReading(new() { Accounts = [account] }, [status], Now);

        Assert.Equal(icon, reading.IconText);
        if (icon == "—") Assert.Contains("오래된 조회 결과", reading.Tooltip);
    }

    [Theory]
    [InlineData(-1, "42")]
    [InlineData(0, "42")]
    [InlineData(1, "—")]
    public void FutureDatedSnapshotRequiresTimeConfirmation(long offsetTicks, string icon)
    {
        var account = Account();
        var status = Status(account) with { Snapshot = Snapshot(account) with { FetchedAt = Now.AddTicks(offsetTicks) } };
        var reading = TrayUsageFormatting.GetReading(new() { Accounts = [account] }, [status], Now);

        Assert.Equal(icon, reading.IconText);
        if (icon == "—") Assert.Contains("조회 시간 확인 필요", reading.Tooltip);
    }

    [Theory]
    [InlineData(-1, "—")]
    [InlineData(0, "—")]
    [InlineData(1, "42")]
    public void ExpiredResetRequiresARefreshInsteadOfInventingAResetValue(int resetOffset, string icon)
    {
        var account = Account();
        var status = Status(account) with { Snapshot = Snapshot(account, [new("session::0", "현재 세션", 42, Now.AddSeconds(resetOffset), true)]) };
        var reading = TrayUsageFormatting.GetReading(new() { Accounts = [account] }, [status], Now);

        Assert.Equal(icon, reading.IconText);
        if (icon == "—") Assert.Contains("초기화 확인 중", reading.Tooltip);
    }

    [Fact]
    public void MissingResetTimeDoesNotInvalidateAKnownMetric()
    {
        var account = Account();
        var status = Status(account) with { Snapshot = Snapshot(account, [new("session::0", "현재 세션", 42, null, true)]) };
        var reading = TrayUsageFormatting.GetReading(new() { Accounts = [account] }, [status], Now);

        Assert.Equal("42", reading.IconText);
        Assert.Contains("초기화 시간 미제공", reading.Tooltip);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("provider")]
    [InlineData("account")]
    [InlineData("missing-identity")]
    public void MismatchedSnapshotCannotLeakThePreviousAccountsReading(string mismatch)
    {
        var account = Account();
        var snapshot = Snapshot(account) with { Windows = [new("secret-window", "Previous account window", 98, Now.AddHours(1), true)] };
        snapshot = mismatch switch
        {
            "identity" => snapshot with { IdentityKey = "other-login" },
            "provider" => snapshot with { Provider = ProviderKind.Codex },
            "account" => snapshot with { AccountId = "other-account" },
            _ => snapshot with { IdentityKey = null }
        };
        var reading = TrayUsageFormatting.GetReading(new() { Accounts = [account] }, [Status(account) with { Snapshot = snapshot }], Now);

        Assert.Equal("—", reading.IconText);
        Assert.Null(reading.WindowId);
        Assert.False(reading.IsWarning);
        Assert.Contains("계정 정보 확인 필요", reading.Tooltip);
        Assert.DoesNotContain("Previous account", reading.Tooltip);
        Assert.DoesNotContain("98", reading.Tooltip);
    }

    [Fact]
    public void WarningUsesActualUsedPercentAndSelectedWindowsEnabledThresholdRegardlessOfDisplayMode()
    {
        var account = Account();
        account.UsageAlerts = [new() { WindowId = "session::0", Enabled = true, ThresholdPercent = 75 },
            new() { WindowId = "weekly_all::0", Enabled = true, ThresholdPercent = 10 }];
        var settings = new AppSettings { Accounts = [account], ShowRemaining = true };

        var below = TrayUsageFormatting.GetReading(settings, [Status(account, 74.9)], Now);
        Assert.False(below.IsWarning); // The rounded icon is 25, while the actual used quota is below the threshold.
        Assert.True(TrayUsageFormatting.GetReading(settings, [Status(account, 75)], Now).IsWarning);
        account.UsageAlerts[0].Enabled = false;
        Assert.False(TrayUsageFormatting.GetReading(settings, [Status(account, 79.9)], Now).IsWarning);
        Assert.True(TrayUsageFormatting.GetReading(settings, [Status(account, 80)], Now).IsWarning);
    }

    [Fact]
    public void TooltipFitsNativeLimitWithoutSplittingSurrogatePairsOrAddingLinesFromNames()
    {
        var account = Account();
        account.Alias = string.Concat(Enumerable.Repeat("😀", 90)) + "\r\nalias";
        var status = Status(account) with { Snapshot = Snapshot(account,
            [new("session::0", string.Concat(Enumerable.Repeat("🚀", 90)) + "\nwindow", 12.3456789, Now.AddHours(1), true)]) };
        var reading = TrayUsageFormatting.GetReading(new() { Accounts = [account] }, [status], Now);

        Assert.InRange(reading.Tooltip.Length, 1, 127);
        Assert.Equal(5, reading.Tooltip.Split('\n').Length);
        Assert.DoesNotContain('\r', reading.Tooltip);
        Assert.Equal(reading.Tooltip, string.Concat(reading.Tooltip.EnumerateRunes().Select(rune => rune.ToString())));
        Assert.Contains("사용 12.3%", reading.Tooltip);
        Assert.Contains("마지막 갱신", reading.Tooltip);
    }

    [Fact]
    public void EmptyAccountsAndUnfetchedSelectedAccountStayUnknown()
    {
        var empty = TrayUsageFormatting.GetReading(new(), [], Now);
        Assert.Null(empty.AccountId);
        Assert.Equal("—", empty.IconText);
        var account = Account();
        var missing = TrayUsageFormatting.GetReading(new() { Accounts = [account] }, [], Now);
        Assert.Equal(account.Id, missing.AccountId);
        Assert.Equal("—", missing.IconText);
        Assert.Contains("조회 기록 없음", missing.Tooltip);
    }

    private static AccountSettings Account(string id = "account", int order = 0, bool pinned = true) => new()
    {
        Id = id, Provider = ProviderKind.Claude, IdentityKey = id + ":login", Alias = "개인", Order = order, Pinned = pinned
    };

    private static UsageSnapshot Snapshot(AccountSettings account, IReadOnlyList<UsageWindow>? windows = null)
        => new(account.Provider, account.Id, account.IdentityKey, "Max", Now,
            windows ?? [new("session::0", "현재 세션", 42, Now.AddHours(1), true)]);

    private static AccountUsageStatus Status(AccountSettings account, double? used = 42)
        => new(account.Id, Snapshot(account, [new("session::0", "현재 세션", used, Now.AddHours(1), true)]), UsageState.Ready, null, false, null);
}
