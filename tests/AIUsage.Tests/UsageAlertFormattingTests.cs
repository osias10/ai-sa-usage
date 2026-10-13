using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class UsageAlertFormattingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ResetAt = Now.AddHours(2);
    private const string Session = "session::0";
    private const string Weekly = "weekly_all::0";

    [Fact]
    public void ThresholdUsesTheLatestPercentAndRetainsWarningAndCountdown()
    {
        var account = Account();
        var message = UsageAlertFormatting.Format(account, Status(account, 95.4), [Alert(account, used: 80)], Now);
        Assert.NotNull(message);
        Assert.True(message.IsWarning);
        Assert.Equal("AI SA · 사용량 알림", message.Title);
        Assert.Contains("Claude · 개인", message.Message);
        Assert.Contains($"현재 세션 · 사용 {95.4:0.#}%", message.Message);
        Assert.Contains("2시간 0분", message.Message);
    }

    [Fact]
    public void ResetDoesNotRequireThresholdEnabledOrThresholdReached()
    {
        var account = Account();
        account.UsageAlerts[0].Enabled = false;
        account.UsageAlerts[0].ThresholdPercent = 95;
        var message = UsageAlertFormatting.Format(account, Status(account, 0), [Alert(account, UsageAlertKind.Reset, 0)], Now);
        Assert.NotNull(message);
        Assert.False(message.IsWarning);
        Assert.Equal("AI SA · 초기화 알림", message.Title);
        Assert.Equal("Claude · 개인\n현재 세션 · 초기화됨\n사용 0%", message.Message);
    }

    [Fact]
    public void ThresholdIsIndependentOfTheResetOption()
    {
        var account = Account();
        account.UsageAlerts[0].ResetEnabled = false;
        var status = Status(account);
        Assert.NotNull(UsageAlertFormatting.Format(account, status, [Alert(account)], Now));
        Assert.Null(UsageAlertFormatting.Format(account, status, [Alert(account, UsageAlertKind.Reset)], Now));
    }

    [Theory]
    [InlineData(UsageAlertKind.Threshold)]
    [InlineData(UsageAlertKind.Reset)]
    public void TurningTheOptionOffWhileQueuedSuppressesTheNotification(UsageAlertKind kind)
    {
        var account = Account();
        var pending = Alert(account, kind);
        if (kind == UsageAlertKind.Reset) account.UsageAlerts[0].ResetEnabled = false;
        else account.UsageAlerts[0].Enabled = false;
        Assert.Null(UsageAlertFormatting.Format(account, Status(account), [pending], Now));
    }

    [Fact]
    public void ChangedThresholdIsAppliedToTheLatestReading()
    {
        var account = Account();
        var pending = Alert(account, used: 85);
        account.UsageAlerts[0].ThresholdPercent = 90;
        Assert.Null(UsageAlertFormatting.Format(account, Status(account, 89), [pending], Now));
        Assert.NotNull(UsageAlertFormatting.Format(account, Status(account, 90), [pending], Now));
        account.UsageAlerts[0].ThresholdPercent = 0;
        Assert.Null(UsageAlertFormatting.Format(account, Status(account, 90), [pending], Now));
    }

    [Theory]
    [InlineData(UsageAlertKind.Threshold)]
    [InlineData(UsageAlertKind.Reset)]
    public void ReconnectedIdentityCannotDisplayThePreviousAccountsAlert(UsageAlertKind kind)
    {
        var account = Account();
        var pending = Alert(account, kind);
        account.IdentityKey = "new-identity";
        Assert.Null(UsageAlertFormatting.Format(account, Status(account), [pending], Now));
    }

    [Theory]
    [InlineData("status-account")]
    [InlineData("snapshot-account")]
    [InlineData("snapshot-provider")]
    [InlineData("snapshot-identity")]
    public void SnapshotMustBelongToTheSelectedAccount(string mismatch)
    {
        var account = Account();
        var status = Status(account);
        status = mismatch switch
        {
            "status-account" => status with { AccountId = Guid.NewGuid().ToString("N") },
            "snapshot-account" => status with { Snapshot = status.Snapshot! with { AccountId = Guid.NewGuid().ToString("N") } },
            "snapshot-provider" => status with { Snapshot = status.Snapshot! with { Provider = ProviderKind.Codex } },
            _ => status with { Snapshot = status.Snapshot! with { IdentityKey = "different-identity" } }
        };
        Assert.Null(UsageAlertFormatting.Format(account, status, [Alert(account)], Now));
        Assert.Null(UsageAlertFormatting.Format(account, status, [Alert(account, UsageAlertKind.Reset)], Now));
    }

    [Theory]
    [InlineData(UsageState.Offline)]
    [InlineData(UsageState.SignInRequired)]
    [InlineData(UsageState.RateLimited)]
    [InlineData(UsageState.Unavailable)]
    public void AnErrorStatusCannotPresentCachedValuesAsANewNotification(UsageState state)
    {
        var account = Account();
        var status = Status(account);
        foreach (var kind in new[] { UsageAlertKind.Threshold, UsageAlertKind.Reset })
        {
            var pending = new[] { Alert(account, kind) };
            Assert.Null(UsageAlertFormatting.Format(account, status with { State = state }, pending, Now));
            Assert.Null(UsageAlertFormatting.Format(account, status with { Snapshot = status.Snapshot! with { State = state } }, pending, Now));
        }
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(1)]
    public void StaleAndFutureReadingsAreSuppressed(int offsetSeconds)
    {
        var account = Account();
        var status = Status(account);
        status = status with { Snapshot = status.Snapshot! with { FetchedAt = Now.AddSeconds(offsetSeconds) } };
        Assert.Null(UsageAlertFormatting.Format(account, status, [Alert(account)], Now));
        Assert.Null(UsageAlertFormatting.Format(account, status, [Alert(account, UsageAlertKind.Reset)], Now));
    }

    [Fact]
    public void FreshnessBoundaryMatchesTheAlertEngine()
    {
        var account = Account();
        var status = Status(account);
        status = status with { Snapshot = status.Snapshot! with { FetchedAt = Now - UsageAlertEngine.MaximumSnapshotAge } };
        Assert.NotNull(UsageAlertFormatting.Format(account, status, [Alert(account)], Now));
        Assert.NotNull(UsageAlertFormatting.Format(account, status, [Alert(account, UsageAlertKind.Reset)], Now));
    }

    [Theory]
    [InlineData(UsageAlertKind.Threshold)]
    [InlineData(UsageAlertKind.Reset)]
    public void AChangedPeriodMakesTheQueuedNotificationObsolete(UsageAlertKind kind)
    {
        var account = Account();
        var status = Status(account);
        status = WithWindow(status, status.Snapshot!.Windows[0] with { ResetsAt = ResetAt.AddSeconds(1) });
        Assert.Null(UsageAlertFormatting.Format(account, status, [Alert(account, kind)], Now));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void ExpiredPeriodsAreSuppressedEvenWhenTheQueuedPeriodMatches(int seconds)
    {
        var account = Account();
        var reset = Now.AddSeconds(seconds);
        var status = Status(account);
        status = WithWindow(status, status.Snapshot!.Windows[0] with { ResetsAt = reset });
        foreach (var kind in new[] { UsageAlertKind.Threshold, UsageAlertKind.Reset })
            Assert.Null(UsageAlertFormatting.Format(account, status, [Alert(account, kind) with { ResetsAt = reset }], Now));
    }

    [Fact]
    public void MissingResetTimeCanWarnAboutUsageButCannotClaimAReset()
    {
        var account = Account();
        var status = Status(account);
        status = WithWindow(status, status.Snapshot!.Windows[0] with { ResetsAt = null });
        var threshold = Alert(account) with { ResetsAt = null };
        var message = UsageAlertFormatting.Format(account, status, [threshold], Now);
        Assert.NotNull(message);
        Assert.Contains("초기화 시간 미제공", message.Message);
        Assert.Null(UsageAlertFormatting.Format(account, status, [threshold with { Kind = UsageAlertKind.Reset }], Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1d)]
    [InlineData(101d)]
    public void UnknownAndInvalidUsageCannotBeDisplayedAsResetOrThreshold(double? used)
    {
        var account = Account();
        Assert.Null(UsageAlertFormatting.Format(account, Status(account, used), [Alert(account)], Now));
        Assert.Null(UsageAlertFormatting.Format(account, Status(account, used), [Alert(account, UsageAlertKind.Reset)], Now));
    }

    [Theory]
    [InlineData(UsageAlertKind.Threshold)]
    [InlineData(UsageAlertKind.Reset)]
    public void MixedKindsUseOnlyTheFirstKindAndDoNotDuplicateWindows(UsageAlertKind kind)
    {
        var account = Account();
        var first = Alert(account, kind);
        var otherKind = kind == UsageAlertKind.Threshold ? UsageAlertKind.Reset : UsageAlertKind.Threshold;
        var message = UsageAlertFormatting.Format(account, Status(account), [first, Alert(account, otherKind), first], Now);
        Assert.NotNull(message);
        Assert.Equal(kind == UsageAlertKind.Threshold, message.IsWarning);
        Assert.Equal(3, message.Message.Split('\n').Length);
        Assert.Equal(kind == UsageAlertKind.Reset, message.Message.Contains("초기화됨", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("provider")]
    [InlineData("identity")]
    public void OtherAccountsInTheQueueCannotBorrowAnExistingWindow(string mismatch)
    {
        var account = Account();
        account.UsageAlerts.Add(new() { WindowId = Weekly, Enabled = true, ResetEnabled = true });
        var status = Status(account);
        status = status with { Snapshot = status.Snapshot! with { Windows = [status.Snapshot!.Windows[0], new(Weekly, "주간 전체", 95, ResetAt)] } };
        var foreign = Alert(account) with { WindowId = Weekly };
        foreign = mismatch switch
        {
            "account" => foreign with { AccountId = Guid.NewGuid().ToString("N") },
            "provider" => foreign with { Provider = ProviderKind.Codex },
            _ => foreign with { IdentityKey = "another-identity" }
        };
        foreach (var kind in new[] { UsageAlertKind.Threshold, UsageAlertKind.Reset })
        {
            var message = UsageAlertFormatting.Format(account, status, [Alert(account, kind), foreign with { Kind = kind }], Now);
            Assert.NotNull(message);
            Assert.DoesNotContain("주간 전체", message.Message);
            Assert.Null(UsageAlertFormatting.Format(account, status, [foreign with { Kind = kind }, Alert(account, kind)], Now));
        }
    }

    [Fact]
    public void MissingSelectedWindowDoesNotFallBackToAnotherQuota()
    {
        var account = Account();
        account.UsageAlerts.Add(new() { WindowId = Weekly, Enabled = true, ResetEnabled = true });
        var status = WithWindow(Status(account), new(Weekly, "주간 전체", 95, ResetAt));
        Assert.Null(UsageAlertFormatting.Format(account, status, [Alert(account)], Now));
        Assert.Null(UsageAlertFormatting.Format(account, status, [Alert(account, UsageAlertKind.Reset)], Now));
    }

    [Fact]
    public void MissingStatusAndEmptyOrUnknownKindQueuesHaveNoNotification()
    {
        var account = Account();
        Assert.Null(UsageAlertFormatting.Format(account, null, [Alert(account)], Now));
        Assert.Null(UsageAlertFormatting.Format(account, Status(account), [], Now));
        Assert.Null(UsageAlertFormatting.Format(account, Status(account), [Alert(account) with { Kind = (UsageAlertKind)123 }], Now));
    }

    private static AccountSettings Account() => new()
    {
        Provider = ProviderKind.Claude, Alias = "개인", IdentityKey = "account-identity",
        UsageAlerts = [new() { WindowId = Session, Enabled = true, ResetEnabled = true, ThresholdPercent = 80 }]
    };
    private static AccountUsageStatus Status(AccountSettings account, double? used = 85) => new(account.Id,
        new(account.Provider, account.Id, account.IdentityKey, "Max", Now, [new(Session, "현재 세션", used, ResetAt)]),
        UsageState.Ready, null, false, null);
    private static UsageAlert Alert(AccountSettings account, UsageAlertKind kind = UsageAlertKind.Threshold, double used = 85)
        => new(account.Provider, account.Id, account.IdentityKey!, Session, "현재 세션", used, 80, ResetAt, kind);
    private static AccountUsageStatus WithWindow(AccountUsageStatus status, UsageWindow window)
        => status with { Snapshot = status.Snapshot! with { Windows = [window] } };
}
