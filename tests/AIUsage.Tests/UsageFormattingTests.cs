using AIUsage.Core;
using AIUsage.Providers;
using Xunit;

namespace AIUsage.Tests;

public sealed class UsageFormattingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");

    [Fact]
    public void ClaudeCompactViewUsesActualParserIdsAndIgnoresSelectedModelQuota()
    {
        var account = new AccountSettings { Provider = ProviderKind.Claude, SelectedWindowId = "weekly_scoped:Model A:0" };
        var snapshot = ClaudeUsageParser.Parse("""
            {"type":"assistant","usage_report":{"rate_limits":{"limits":[
              {"kind":"weekly_scoped","percent":91,"resets_at":"2026-10-09T12:00:00Z","scope":{"model":{"display_name":"Model A"}},"is_active":true},
              {"kind":"weekly_all","percent":42,"resets_at":"2026-10-12T12:00:00Z"},
              {"kind":"session","percent":75,"resets_at":null,"scope":{"surface":{"display_name":"Code"}}},
              {"kind":"session","percent":12,"resets_at":"2026-10-08T14:17:00Z"}
            ]}}}
            """, account, Now);

        var windows = UsageFormatting.SelectCompactWindows(account, snapshot);

        Assert.Collection(windows,
            session =>
            {
                Assert.Equal("session::0", session.Id);
                Assert.Equal("세션", UsageFormatting.CompactName(session));
                Assert.Equal(12d, session.UsedPercent);
                Assert.Equal(Now.AddHours(2).AddMinutes(17), session.ResetsAt);
                Assert.Same(snapshot.Windows[3], session);
            },
            weekly =>
            {
                Assert.Equal("weekly_all::0", weekly.Id);
                Assert.Equal("주간", UsageFormatting.CompactName(weekly));
                Assert.Equal(42d, weekly.UsedPercent);
                Assert.Equal(Now.AddDays(4), weekly.ResetsAt);
                Assert.Same(snapshot.Windows[1], weekly);
            });
    }

    [Theory]
    [InlineData("weekly_scoped")]
    [InlineData("weekly_all")]
    public void ClaudeMissingAggregateWeeklyStaysUnknownEvenWithScopedWeeklyQuota(string scopedKind)
    {
        var account = new AccountSettings { Provider = ProviderKind.Claude };
        var json = """
            {"type":"assistant","usage_report":{"rate_limits":{"limits":[
              {"kind":"session","percent":0,"resets_at":null},
              {"kind":"SCOPED_KIND","percent":90,"resets_at":"2026-10-09T12:00:00Z","scope":{"model":{"display_name":"Model A"}},"is_active":true}
            ]}}}
            """.Replace("SCOPED_KIND", scopedKind, StringComparison.Ordinal);
        var snapshot = ClaudeUsageParser.Parse(json, account, Now);

        var windows = UsageFormatting.SelectCompactWindows(account, snapshot);

        Assert.Equal(2, windows.Count);
        Assert.Equal(0d, windows[0].UsedPercent); // A reported zero remains different from a missing meter.
        Assert.Equal("weekly_all::0", windows[1].Id);
        Assert.Null(windows[1].UsedPercent);
        Assert.Null(windows[1].ResetsAt);
    }

    [Fact]
    public void ClaudeMissingSessionKeepsItsSlotAheadOfAvailableAggregateWeekly()
    {
        var account = new AccountSettings { Provider = ProviderKind.Claude };
        var snapshot = ClaudeUsageParser.Parse("""
            {"type":"assistant","usage_report":{"rate_limits":{"limits":[
              {"kind":"weekly_all","percent":42,"resets_at":"2026-10-12T12:00:00Z"},
              {"kind":"session","percent":85,"resets_at":"2026-10-08T15:00:00Z","scope":{"surface":{"display_name":"Code"}}}
            ]}}}
            """, account, Now);

        var windows = UsageFormatting.SelectCompactWindows(account, snapshot);

        Assert.Equal(2, windows.Count);
        Assert.Equal("session::0", windows[0].Id);
        Assert.Null(windows[0].UsedPercent);
        Assert.Null(windows[0].ResetsAt);
        Assert.Same(snapshot.Windows[0], windows[1]);
    }

    [Theory]
    [InlineData(ProviderKind.Claude, 2)]
    [InlineData(ProviderKind.Codex, 1)]
    [InlineData(ProviderKind.Antigravity, 1)]
    public void UnfetchedAccountsHaveStableUnknownSlots(ProviderKind provider, int count)
    {
        var windows = UsageFormatting.SelectCompactWindows(new() { Provider = provider }, null);

        Assert.Equal(count, windows.Count);
        Assert.All(windows, window =>
        {
            Assert.Null(window.UsedPercent);
            Assert.Null(window.ResetsAt);
            Assert.Equal("—", UsageFormatting.CompactRemaining(window.ResetsAt, Now));
        });
    }

    [Theory]
    [InlineData(ProviderKind.Codex)]
    [InlineData(ProviderKind.Antigravity)]
    public void OtherProvidersMigrateSelectedMeterThenPrimaryThenFirstOnlyOnce(ProviderKind provider)
    {
        var account = new AccountSettings { Provider = provider, SelectedWindowId = "secondary" };
        UsageWindow first = new("first", "첫 한도", 95, Now.AddHours(1));
        UsageWindow primary = new("primary", "기본 한도", 25, Now.AddHours(2), true);
        UsageWindow secondary = new("secondary", "주간", 60, Now.AddDays(1));
        UsageSnapshot snapshot = new(provider, account.Id, null, null, Now, [first, primary, secondary]);

        Assert.Same(secondary, Assert.Single(UsageFormatting.SelectCompactWindows(account, snapshot)));
        Assert.Equal(new WidgetWindowSelection("secondary", "주간"), Assert.Single(account.WidgetWindows!));

        account.SelectedWindowId = "removed-meter";
        Assert.Same(secondary, Assert.Single(UsageFormatting.SelectCompactWindows(account, snapshot)));

        account.WidgetWindows = null;
        Assert.Same(primary, Assert.Single(UsageFormatting.SelectCompactWindows(account, snapshot)));

        snapshot = snapshot with { Windows = [first, secondary] };
        var missing = Assert.Single(UsageFormatting.SelectCompactWindows(account, snapshot));
        Assert.Equal("primary", missing.Id);
        Assert.Equal("기본 한도", missing.Name);
        Assert.Null(missing.UsedPercent);

        account.WidgetWindows = null;
        Assert.Same(first, Assert.Single(UsageFormatting.SelectCompactWindows(account, snapshot)));
    }

    [Theory]
    [InlineData(ProviderKind.Codex)]
    [InlineData(ProviderKind.Antigravity)]
    public void MissingDataDoesNotPersistAnUnavailablePlaceholder(ProviderKind provider)
    {
        var account = new AccountSettings { Provider = provider, SelectedWindowId = "weekly" };
        Assert.False(UsageFormatting.EnsureWidgetWindows(account, null));
        Assert.Null(account.WidgetWindows);
        Assert.Equal("compact:unavailable", Assert.Single(UsageFormatting.SelectCompactWindows(account, null)).Id);
        Assert.Null(account.WidgetWindows);

        var empty = new UsageSnapshot(provider, account.Id, null, null, Now, []);
        Assert.False(UsageFormatting.EnsureWidgetWindows(account, empty));
        Assert.Null(account.WidgetWindows);

        var available = empty with { Windows = [new("primary", "기본", 0, null, true), new("weekly", "주간", 25, Now.AddDays(1))] };
        Assert.True(UsageFormatting.EnsureWidgetWindows(account, available));
        Assert.Equal("weekly", Assert.Single(account.WidgetWindows!).Id);
        Assert.False(UsageFormatting.EnsureWidgetWindows(account, available));
    }

    [Fact]
    public void ClaudeMigrationUsesActualAggregateIdsAndRetainsBothDefaultSlots()
    {
        var account = new AccountSettings { Provider = ProviderKind.Claude, SelectedWindowId = "weekly_scoped:Fable:0" };
        UsageSnapshot snapshot = new(account.Provider, account.Id, null, null, Now,
            [new("weekly_scoped:Fable:0", "Fable", 50, null, true), new("weekly_all::2", "전체 주간", 10, null), new("session::1", "현재 세션", 20, null)]);
        Assert.True(UsageFormatting.EnsureWidgetWindows(account, snapshot));
        Assert.Equal(new[] { "session::1", "weekly_all::2" }, account.WidgetWindows!.Select(selection => selection.Id));
        Assert.Equal("weekly_scoped:Fable:0", account.SelectedWindowId);
        Assert.False(UsageFormatting.EnsureWidgetWindows(account, snapshot));

        var unconnected = new AccountSettings { Provider = ProviderKind.Claude };
        Assert.True(UsageFormatting.EnsureWidgetWindows(unconnected, null));
        Assert.Equal(new[] { "session::0", "weekly_all::0" }, unconnected.WidgetWindows!.Select(selection => selection.Id));
    }

    [Fact]
    public void ExplicitClaudeFableSelectionDoesNotAddDefaultMeters()
    {
        var account = new AccountSettings
        {
            Provider = ProviderKind.Claude,
            WidgetWindows = [new("weekly_scoped:Fable:0", "Fable")]
        };
        UsageSnapshot snapshot = new(account.Provider, account.Id, null, null, Now,
            [new("session::0", "현재 세션", 25, null, true), new("weekly_all::0", "주간 전체", 35, null), new("weekly_scoped:Fable:0", "주간 · Fable", 75, Now.AddDays(1))]);
        Assert.Same(snapshot.Windows[2], Assert.Single(UsageFormatting.SelectCompactWindows(account, snapshot)));
        Assert.Equal("주간 · Fable", Assert.Single(account.WidgetWindows!).Name);

        var missing = Assert.Single(UsageFormatting.SelectCompactWindows(account, snapshot with { Windows = [snapshot.Windows[0], snapshot.Windows[1]] }));
        Assert.Equal("weekly_scoped:Fable:0", missing.Id);
        Assert.Equal("주간 · Fable", missing.Name);
        Assert.Null(missing.UsedPercent);
        Assert.Null(missing.ResetsAt);
    }

    [Fact]
    public void ClaudeSelectionsUseAggregateOrderThenTheProvidersModelOrder()
    {
        var account = new AccountSettings
        {
            Provider = ProviderKind.Claude,
            WidgetWindows = [new("weekly_scoped:Sonnet:0", "Sonnet"), new("weekly_all::0", "주간"), new("weekly_scoped:Fable:0", "Fable"), new("session::0", "세션")]
        };
        UsageSnapshot snapshot = new(account.Provider, account.Id, null, null, Now,
            [new("weekly_scoped:Fable:0", "Fable", 70, null), new("weekly_scoped:Sonnet:0", "Sonnet", 10, null), new("weekly_all::0", "주간", 35, null), new("session::0", "세션", 25, null)]);
        var windows = UsageFormatting.SelectCompactWindows(account, snapshot);
        Assert.Equal(new[] { "session::0", "weekly_all::0", "weekly_scoped:Fable:0", "weekly_scoped:Sonnet:0" }, windows.Select(window => window.Id));
        Assert.Equal(windows.Select(window => window.Id), account.WidgetWindows!.Select(selection => selection.Id));
    }

    [Fact]
    public void MissingFirstClaudeSelectionKeepsItsSlotAheadOfAnAvailableModel()
    {
        var account = new AccountSettings
        {
            Provider = ProviderKind.Claude,
            WidgetWindows = [new("weekly_scoped:Fable:0", "주간 · Fable"), new("weekly_scoped:Other:0", "주간 · Other")]
        };
        UsageSnapshot snapshot = new(account.Provider, account.Id, null, null, Now,
            [new("weekly_scoped:Other:0", "주간 · Other", 30, Now.AddDays(1))]);
        var windows = UsageFormatting.SelectCompactWindows(account, snapshot);
        Assert.Equal("weekly_scoped:Fable:0", windows[0].Id);
        Assert.Null(windows[0].UsedPercent);
        Assert.Same(snapshot.Windows[0], windows[1]);
    }

    [Theory]
    [InlineData(ProviderKind.Codex)]
    [InlineData(ProviderKind.Antigravity)]
    public void OtherProvidersRespectMultipleSelectionsAndSavedOrder(ProviderKind provider)
    {
        var account = new AccountSettings
        {
            Provider = provider, SelectedWindowId = "ignored",
            WidgetWindows = [new("weekly", "저장된 주간"), new("short", "저장된 단기")]
        };
        UsageSnapshot snapshot = new(provider, account.Id, null, null, Now,
            [new("short", "짧은 한도", 20, null, true), new("ignored", "최고 사용률", 99, null), new("weekly", "주간", 80, Now.AddDays(1))]);
        var windows = UsageFormatting.SelectCompactWindows(account, snapshot);
        Assert.Equal(new[] { "weekly", "short" }, windows.Select(window => window.Id));
        Assert.Same(snapshot.Windows[2], windows[0]);
        Assert.Same(snapshot.Windows[0], windows[1]);
    }

    [Fact]
    public void ASelectedNameIsUpdatedWhenKnownAndRetainedWhileMissing()
    {
        var account = new AccountSettings { Provider = ProviderKind.Codex, WidgetWindows = [new("weekly", "기존 이름")] };
        UsageSnapshot snapshot = new(account.Provider, account.Id, null, null, Now, [new("weekly", "새 주간 이름", 50, null)]);
        Assert.True(UsageFormatting.EnsureWidgetWindows(account, snapshot));
        Assert.False(UsageFormatting.EnsureWidgetWindows(account, snapshot));
        var missing = Assert.Single(UsageFormatting.SelectCompactWindows(account, null));
        Assert.Equal("weekly", missing.Id);
        Assert.Equal("새 주간 이름", missing.Name);
        Assert.Null(missing.UsedPercent);
    }

    [Fact]
    public void MalformedSelectionsAreNormalizedWithoutChangingAValidMissingId()
    {
        var account = new AccountSettings
        {
            Provider = ProviderKind.Codex,
            WidgetWindows = [null!, new(" ", "무효"), new("missing", "선택한 이름"), new("missing", "중복"), new("unnamed", "")]
        };
        Assert.True(UsageFormatting.EnsureWidgetWindows(account, null));
        Assert.Equal(new[] { "missing", "unnamed" }, account.WidgetWindows!.Select(selection => selection.Id));
        Assert.Equal("선택한 이름", account.WidgetWindows[0].Name);
        Assert.Equal("선택한 한도", account.WidgetWindows[1].Name);
        Assert.False(UsageFormatting.EnsureWidgetWindows(account, null));
        Assert.All(UsageFormatting.SelectCompactWindows(account, null), window => Assert.Null(window.UsedPercent));
    }

    [Fact]
    public void EmptySelectionsRecoverDefaultsButDoNotMigrateAnUnknownProviderMeter()
    {
        var claude = new AccountSettings { Provider = ProviderKind.Claude, WidgetWindows = [] };
        Assert.True(UsageFormatting.EnsureWidgetWindows(claude, null));
        Assert.Equal(2, claude.WidgetWindows!.Count);
        var codex = new AccountSettings { Provider = ProviderKind.Codex, WidgetWindows = [new("compact:unavailable", "사용량")] };
        Assert.True(UsageFormatting.EnsureWidgetWindows(codex, null));
        Assert.Null(codex.WidgetWindows);
    }

    [Fact]
    public void AnotherAccountsSnapshotCannotMigrateOrFillSelections()
    {
        var account = new AccountSettings { Provider = ProviderKind.Codex };
        UsageSnapshot unrelated = new(account.Provider, Guid.NewGuid().ToString("N"), null, null, Now, [new("weekly", "다른 계정", 90, null)]);
        Assert.False(UsageFormatting.EnsureWidgetWindows(account, unrelated));
        Assert.Null(account.WidgetWindows);
        account.WidgetWindows = [new("weekly", "내 주간")];
        var window = Assert.Single(UsageFormatting.SelectCompactWindows(account, unrelated));
        Assert.Equal("내 주간", window.Name);
        Assert.Null(window.UsedPercent);
    }

    [Theory]
    [InlineData(0, "확인 중")]
    [InlineData(-1, "확인 중")]
    [InlineData(0.1, "1초")]
    [InlineData(60, "1분00초")]
    [InlineData(137, "2분17초")]
    [InlineData(8220, "2시간17분")]
    [InlineData(183600, "2일3시간")]
    public void CompactCountdownKeepsUnknownExpiredAndFutureStatesDistinct(double seconds, string expected)
    {
        Assert.Equal(expected, UsageFormatting.CompactRemaining(Now.AddSeconds(seconds), Now));
        Assert.Equal("—", UsageFormatting.CompactRemaining(null, Now));
    }
}
