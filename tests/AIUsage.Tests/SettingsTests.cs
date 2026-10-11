using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AIUsage-tests-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task SettingsAndSnapshotsRoundTripWithoutCredentials()
    {
        var paths = new AppPaths(_directory); var store = new JsonSettingsStore(paths);
        var account = new AccountSettings { Alias = "업무", IdentityKey = "opaque-account", RefreshMinutes = 17 };
        await store.SaveSettingsAsync(new() { Accounts = [account], MiniMode = true });
        await store.SaveSnapshotsAsync([new(account.Provider, account.Id, account.IdentityKey, "Pro", DateTimeOffset.UtcNow, [new("weekly", "주간", null, null)])]);
        var loaded = await store.LoadSettingsAsync();
        Assert.True(loaded.MiniMode); Assert.Equal(17, loaded.Accounts[0].RefreshMinutes);
        Assert.Null((await store.LoadSnapshotsAsync())[0].Windows[0].UsedPercent);
        Assert.DoesNotContain("token", await File.ReadAllTextAsync(paths.SettingsFile), StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task CorruptJsonIsPreservedAndDefaultsRecovered()
    {
        var paths = new AppPaths(_directory); Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(paths.SettingsFile, "{truncated");
        var store = new JsonSettingsStore(paths);
        Assert.Empty((await store.LoadSettingsAsync()).Accounts);
        Assert.NotNull(store.LastWarning);
        Assert.Single(Directory.GetFiles(_directory, "*.corrupt-*"));
    }
    [Fact]
    public async Task ConcurrentSavesAlwaysLeaveCompleteJson()
    {
        var store = new JsonSettingsStore(new(_directory));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.SaveSettingsAsync(new() { Accounts = [new() { Alias = i.ToString() }] })));
        Assert.Single((await store.LoadSettingsAsync()).Accounts);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
    }
    [Fact]
    public async Task NewerSchemaCannotBeSilentlyOverwritten()
    {
        var paths = new AppPaths(_directory); Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(paths.SettingsFile, "{\"SchemaVersion\":99}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new JsonSettingsStore(paths).LoadSettingsAsync());
    }
    [Fact]
    public void ProfileIdCannotEscapeAppDirectory()
        => Assert.Throws<ArgumentException>(() => new AppPaths(_directory).ProfileDirectory(new() { Id = "../../elsewhere" }));
    [Fact]
    public async Task StructurallyIncompleteCacheDoesNotCrashWidget()
    {
        var paths = new AppPaths(_directory); Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(paths.CacheFile, "[{\"Provider\":\"Claude\",\"AccountId\":\"" + Guid.NewGuid().ToString("N") + "\",\"Windows\":null}]");
        Assert.Empty(await new JsonSettingsStore(paths).LoadSnapshotsAsync());
    }
    [Fact]
    public async Task OlderSettingsUseDisabledAlertsAndDefaultDisplayOptions()
    {
        var paths = new AppPaths(_directory); Directory.CreateDirectory(_directory);
        var id = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(paths.SettingsFile, "{\"Accounts\":[{\"Id\":\"" + id + "\",\"Provider\":\"Claude\"}]}");
        var loaded = await new JsonSettingsStore(paths).LoadSettingsAsync();
        Assert.False(loaded.ShowRemaining);
        Assert.Null(loaded.TrayAccountId);
        Assert.Null(loaded.Accounts[0].TrayWindow);
        Assert.Null(loaded.Accounts[0].WidgetWindows);
        Assert.Empty(Assert.Single(loaded.Accounts).UsageAlerts);
    }
    [Fact]
    public async Task AlertRulesAndDisplayPreferencesRoundTrip()
    {
        var store = new JsonSettingsStore(new(_directory));
        var account = new AccountSettings
        {
            UsageAlerts = [new() { WindowId = "session", Enabled = true, ThresholdPercent = 85 }, new() { WindowId = "weekly" }]
        };
        await store.SaveSettingsAsync(new() { Accounts = [account], ShowRemaining = true, TrayAccountId = account.Id });
        var loaded = await store.LoadSettingsAsync();
        Assert.True(loaded.ShowRemaining);
        Assert.Equal(account.Id, loaded.TrayAccountId);
        Assert.Collection(loaded.Accounts[0].UsageAlerts,
            rule => { Assert.Equal("session", rule.WindowId); Assert.True(rule.Enabled); Assert.Equal(85, rule.ThresholdPercent); },
            rule => { Assert.Equal("weekly", rule.WindowId); Assert.False(rule.Enabled); Assert.Equal(80, rule.ThresholdPercent); });
    }
    [Fact]
    public async Task MalformedRulesAndRemovedTrayAccountAreNormalized()
    {
        var paths = new AppPaths(_directory); Directory.CreateDirectory(_directory);
        var id = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(paths.SettingsFile,
            "{\"TrayAccountId\":\"removed-account\",\"Accounts\":[{\"Id\":\"" + id +
            "\",\"UsageAlerts\":[null,{\"WindowId\":\"\"},{\"WindowId\":\"session\",\"ThresholdPercent\":-5}," +
            "{\"WindowId\":\"weekly\",\"ThresholdPercent\":120},{\"WindowId\":\"session\",\"Enabled\":true}]}]}");
        var loaded = await new JsonSettingsStore(paths).LoadSettingsAsync();
        Assert.Null(loaded.TrayAccountId);
        Assert.Equal(new[] { 1, 100 }, loaded.Accounts[0].UsageAlerts.Select(rule => rule.ThresholdPercent));
    }
    [Fact]
    public async Task AlertHistoryPersistsSeparatelyAndPreventsRestartDuplicates()
    {
        var paths = new AppPaths(_directory); var store = new JsonSettingsStore(paths);
        var account = new AccountSettings
        {
            IdentityKey = "opaque-account", UsageAlerts = [new() { WindowId = "weekly", Enabled = true }]
        };
        var now = DateTimeOffset.UtcNow;
        var snapshot = new UsageSnapshot(account.Provider, account.Id, account.IdentityKey, "Max", now,
            [new("weekly", "주간", 90, now.AddDays(3))]);
        var engine = new UsageAlertEngine();
        Assert.Single(engine.Evaluate(account, snapshot, now, true));
        await store.SaveAlertStateAsync(engine.GetState());
        Assert.True(File.Exists(paths.AlertStateFile));
        Assert.False(File.Exists(paths.SettingsFile));
        Assert.False(File.Exists(paths.CacheFile));
        var restored = new UsageAlertEngine(await new JsonSettingsStore(paths).LoadAlertStateAsync());
        Assert.Empty(restored.Evaluate(account, snapshot, now, true));
        restored.RemoveAccount(account.Id);
        await store.SaveAlertStateAsync(restored.GetState());
        Assert.Empty((await store.LoadAlertStateAsync()).Deliveries);
    }
    [Fact]
    public async Task FutureAlertStateSchemaCannotBeOverwrittenOnLoad()
    {
        var paths = new AppPaths(_directory); Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(paths.AlertStateFile, "{\"SchemaVersion\":99}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new JsonSettingsStore(paths).LoadAlertStateAsync());
    }
    [Fact]
    public async Task WidgetSelectionsPersistIdsNamesAndNonClaudeOrder()
    {
        var store = new JsonSettingsStore(new(_directory));
        var account = new AccountSettings
        {
            Provider = ProviderKind.Codex, SelectedWindowId = "legacy",
            WidgetWindows = [new("weekly", "주간 전체"), new("primary", "5시간 한도")]
        };
        await store.SaveSettingsAsync(new() { Accounts = [account] });
        var restored = Assert.Single((await store.LoadSettingsAsync()).Accounts);
        Assert.Equal(account.WidgetWindows, restored.WidgetWindows);
        Assert.Equal("legacy", restored.SelectedWindowId);
        var missing = UsageFormatting.SelectCompactWindows(restored, null);
        Assert.Equal(new[] { "주간 전체", "5시간 한도" }, missing.Select(window => window.Name));
        Assert.All(missing, window => Assert.Null(window.UsedPercent));
    }
    [Fact]
    public async Task LoadingLegacySelectionsDefersMigrationUntilCachedWindowsAreKnown()
    {
        var store = new JsonSettingsStore(new(_directory));
        var account = new AccountSettings { Provider = ProviderKind.Claude, SelectedWindowId = "legacy" };
        await store.SaveSettingsAsync(new() { Accounts = [account] });
        var restored = Assert.Single((await store.LoadSettingsAsync()).Accounts);
        Assert.Null(restored.WidgetWindows);
        var snapshot = new UsageSnapshot(restored.Provider, restored.Id, null, null, DateTimeOffset.UtcNow,
            [new("session::1", "세션", 12, null), new("weekly_all::2", "주간", 34, null)]);
        UsageFormatting.EnsureWidgetWindows(restored, snapshot);
        await store.SaveSettingsAsync(new() { Accounts = [restored] });
        Assert.Equal(new[] { "session::1", "weekly_all::2" },
            (await store.LoadSettingsAsync()).Accounts[0].WidgetWindows!.Select(selection => selection.Id));
    }
    [Fact]
    public async Task MalformedWidgetSelectionsKeepValidMissingIdsAndDeferEmptyOnes()
    {
        var paths = new AppPaths(_directory); Directory.CreateDirectory(_directory);
        var one = Guid.NewGuid().ToString("N"); var two = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(paths.SettingsFile,
            "{\"Accounts\":[{\"Id\":\"" + one + "\",\"Provider\":\"Codex\",\"WidgetWindows\":[null,{\"Id\":\"\",\"Name\":\"무효\"}," +
            "{\"Id\":\"missing\",\"Name\":\"저장 이름\"},{\"Id\":\"missing\",\"Name\":\"중복\"},{\"Id\":\"unnamed\",\"Name\":null}]}" +
            ",{\"Id\":\"" + two + "\",\"Provider\":\"Codex\",\"WidgetWindows\":[]}]}");
        var loaded = await new JsonSettingsStore(paths).LoadSettingsAsync();
        Assert.Equal(new[] { new WidgetWindowSelection("missing", "저장 이름"), new WidgetWindowSelection("unnamed", "선택한 한도") }, loaded.Accounts[0].WidgetWindows);
        Assert.Null(loaded.Accounts[1].WidgetWindows);
    }

    [Fact]
    public async Task AccountTrayQuotaIdsAndSavedNamesSurviveRestartIndependentlyOfWidgetSelections()
    {
        var store = new JsonSettingsStore(new(_directory));
        var first = new AccountSettings
        {
            Provider = ProviderKind.Claude,
            WidgetWindows = [new("session::0", "현재 세션")],
            TrayWindow = new("weekly_scoped:Fable:0", "주간 · Fable")
        };
        var second = new AccountSettings
        {
            Provider = ProviderKind.Codex, Order = 1,
            WidgetWindows = [new("codex:primary", "5시간 한도")],
            TrayWindow = new("unavailable-old-limit", "조회되지 않는 이전 한도")
        };
        await store.SaveSettingsAsync(new() { Accounts = [first, second], TrayAccountId = second.Id });

        var restored = await new JsonSettingsStore(new(_directory)).LoadSettingsAsync();

        Assert.Equal(second.Id, restored.TrayAccountId);
        Assert.Equal(first.TrayWindow, restored.Accounts[0].TrayWindow);
        Assert.Equal(second.TrayWindow, restored.Accounts[1].TrayWindow);
        Assert.Equal(first.WidgetWindows, restored.Accounts[0].WidgetWindows);
        Assert.Equal(second.WidgetWindows, restored.Accounts[1].WidgetWindows);
        var reading = TrayUsageFormatting.GetReading(restored, [], DateTimeOffset.UtcNow);
        Assert.Equal(second.TrayWindow.Id, reading.WindowId);
        Assert.Equal("—", reading.IconText);
        Assert.Contains(second.TrayWindow.Name, reading.Tooltip);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("compact:unavailable")]
    public async Task InvalidTrayQuotaIdentifiersRevertToAutomaticWithoutChangingWidgetSelection(string? id)
    {
        var store = new JsonSettingsStore(new(_directory));
        var account = new AccountSettings
        {
            WidgetWindows = [new("session::0", "현재 세션")],
            TrayWindow = new(id!, "잘못된 선택")
        };
        await store.SaveSettingsAsync(new() { Accounts = [account] });

        var restored = Assert.Single((await store.LoadSettingsAsync()).Accounts);

        Assert.Null(restored.TrayWindow);
        Assert.Equal(account.WidgetWindows, restored.WidgetWindows);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UnnamedTrayQuotaRetainsUnknownIdentifierWithReadableFallbackName(string? name)
    {
        var store = new JsonSettingsStore(new(_directory));
        var account = new AccountSettings { TrayWindow = new("unavailable-limit", name!) };
        await store.SaveSettingsAsync(new() { Accounts = [account] });

        var restored = Assert.Single((await store.LoadSettingsAsync()).Accounts);

        Assert.Equal(new WidgetWindowSelection("unavailable-limit", "선택한 한도"), restored.TrayWindow);
        Assert.Null(restored.WidgetWindows);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
