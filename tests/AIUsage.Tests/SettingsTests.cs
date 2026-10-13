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
        Assert.True(loaded.AutoCheckUpdates);
        Assert.Null(loaded.TrayAccountId);
        Assert.Null(loaded.Accounts[0].TrayWindow);
        Assert.Null(loaded.Accounts[0].WidgetWindows);
        Assert.Empty(Assert.Single(loaded.Accounts).UsageAlerts);
    }
    [Fact]
    public async Task AutomaticUpdatePreferenceSurvivesRestart()
    {
        var paths = new AppPaths(_directory);
        var store = new JsonSettingsStore(paths);
        await store.SaveSettingsAsync(new() { AutoCheckUpdates = false });
        Assert.False((await new JsonSettingsStore(paths).LoadSettingsAsync()).AutoCheckUpdates);
    }
    [Fact]
    public async Task AlertRulesAndDisplayPreferencesRoundTrip()
    {
        var store = new JsonSettingsStore(new(_directory));
        var account = new AccountSettings
        {
            UsageAlerts = [new() { WindowId = "session", Enabled = true, ThresholdPercent = 85 },
                new() { WindowId = "weekly", ResetEnabled = true }]
        };
        await store.SaveSettingsAsync(new() { Accounts = [account], ShowRemaining = true, TrayAccountId = account.Id });
        var loaded = await store.LoadSettingsAsync();
        Assert.True(loaded.ShowRemaining);
        Assert.Equal(account.Id, loaded.TrayAccountId);
        Assert.Collection(loaded.Accounts[0].UsageAlerts,
            rule => { Assert.Equal("session", rule.WindowId); Assert.True(rule.Enabled); Assert.Equal(85, rule.ThresholdPercent); Assert.False(rule.ResetEnabled); },
            rule => { Assert.Equal("weekly", rule.WindowId); Assert.False(rule.Enabled); Assert.Equal(80, rule.ThresholdPercent); Assert.True(rule.ResetEnabled); });
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
    [Fact]
    public async Task OlderAlertRulesAndHistoryDefaultToNoResetNotifications()
    {
        var paths = new AppPaths(_directory);
        Directory.CreateDirectory(_directory);
        var id = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(paths.SettingsFile,
            "{\"Accounts\":[{\"Id\":\"" + id + "\",\"UsageAlerts\":[{\"WindowId\":\"session\",\"Enabled\":true,\"ThresholdPercent\":85}]}]}");
        await File.WriteAllTextAsync(paths.AlertStateFile, "{\"SchemaVersion\":1,\"Deliveries\":[]}");
        var store = new JsonSettingsStore(paths);
        var rule = Assert.Single(Assert.Single((await store.LoadSettingsAsync()).Accounts).UsageAlerts);
        Assert.True(rule.Enabled);
        Assert.False(rule.ResetEnabled);
        Assert.Equal(85, rule.ThresholdPercent);
        Assert.Empty((await store.LoadAlertStateAsync()).ResetObservations);
    }

    [Fact]
    public async Task ResetBaselinePersistsBeforeAnyNoticeAndAConfirmedResetCannotNotifyAgainAfterRestart()
    {
        var paths = new AppPaths(_directory);
        var store = new JsonSettingsStore(paths);
        var account = new AccountSettings
        {
            IdentityKey = "synthetic-reset-identity", UsageAlerts = [new() { WindowId = "session", ResetEnabled = true }]
        };
        var now = DateTimeOffset.UtcNow;
        var first = new UsageSnapshot(account.Provider, account.Id, account.IdentityKey, null, now,
            [new("session", "세션", 90, now.AddMinutes(1))]);
        var engine = new UsageAlertEngine();
        Assert.Empty(engine.Evaluate(account, first, now, true));
        await store.SaveAlertStateAsync(engine.GetState());
        Assert.Empty(engine.GetState().Deliveries);
        var restored = new UsageAlertEngine(await new JsonSettingsStore(paths).LoadAlertStateAsync());
        restored.SynchronizeSettings(account);
        var later = now.AddMinutes(2);
        var next = first with { FetchedAt = later, Windows = [new("session", "세션", 2, later.AddHours(5))] };
        Assert.Equal(UsageAlertKind.Reset, Assert.Single(restored.Evaluate(account, next, later, true)).Kind);
        await store.SaveAlertStateAsync(restored.GetState());
        var again = new UsageAlertEngine(await store.LoadAlertStateAsync());
        Assert.Empty(again.Evaluate(account, next with { FetchedAt = later.AddMinutes(1) }, later.AddMinutes(1), true));
        Assert.Single(again.GetState().ResetObservations);
        again.RemoveAccount(account.Id);
        await store.SaveAlertStateAsync(again.GetState());
        Assert.Empty((await store.LoadAlertStateAsync()).ResetObservations);
    }

    [Fact]
    public async Task InvalidAndDuplicateResetObservationsAreNormalizedOnLoad()
    {
        var store = new JsonSettingsStore(new(_directory));
        var now = DateTimeOffset.UtcNow;
        var valid = new UsageResetObservation(ProviderKind.Claude, Guid.NewGuid().ToString("N"),
            "synthetic-identity", "session", now.AddHours(5), now, now);
        var newer = valid with { FetchedAt = now.AddMinutes(1), ObservedAt = now.AddMinutes(1) };
        await store.SaveAlertStateAsync(new()
        {
            ResetObservations = [null!, valid, newer, valid with { AccountId = "invalid" },
                valid with { WindowId = "" }, valid with { IdentityKey = "" }, valid with { Provider = (ProviderKind)999 },
                valid with { ResetsAt = now }, valid with { FetchedAt = now.AddSeconds(1) },
                valid with { FetchedAt = now.AddMinutes(-6) }, valid with { ObservedAt = default }]
        });
        var loaded = await store.LoadAlertStateAsync();
        Assert.Equal(newer, Assert.Single(loaded.ResetObservations));
        Assert.Equal(newer, Assert.Single(new UsageAlertEngine(loaded).GetState().ResetObservations));
    }

    [Fact]
    public async Task ForecastActivationAndWidgetVisibilityAreIndependentAndLegacySettingsAreOptIn()
    {
        var paths = new AppPaths(_directory);
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(paths.SettingsFile, "{\"ShowLastChecked\":true}");
        var store = new JsonSettingsStore(paths);
        var legacy = await store.LoadSettingsAsync();
        Assert.False(legacy.UsageForecastEnabled);
        Assert.True(legacy.ShowUsageForecast);
        legacy.UsageForecastEnabled = true;
        legacy.ShowUsageForecast = false;
        await store.SaveSettingsAsync(legacy);
        var restored = await new JsonSettingsStore(paths).LoadSettingsAsync();
        Assert.True(restored.UsageForecastEnabled);
        Assert.False(restored.ShowUsageForecast);
        restored.UsageForecastEnabled = false;
        await store.SaveSettingsAsync(restored);
        restored = await store.LoadSettingsAsync();
        Assert.False(restored.UsageForecastEnabled);
        Assert.False(restored.ShowUsageForecast);
    }

    [Fact]
    public async Task ForecastHistoryRoundTripPreservesTheAbsoluteEstimateAndCanBeCleared()
    {
        var store = new JsonSettingsStore(new(_directory));
        var account = new AccountSettings { IdentityKey = "synthetic-forecast-account" };
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        var reset = now.AddHours(5);
        var engine = new UsageForecastEngine();
        UsageSnapshot Snapshot(int minutes, double used) => new(account.Provider, account.Id, account.IdentityKey,
            null, now.AddMinutes(minutes), [new("session", "세션", used, reset)]);
        engine.Observe(account, Snapshot(-20, 20), now.AddMinutes(-20));
        engine.Observe(account, Snapshot(-10, 30), now.AddMinutes(-10));
        var latest = Snapshot(0, 40);
        engine.Observe(account, latest, now);
        var status = new AccountUsageStatus(account.Id, latest, UsageState.Ready, null, false, null);
        var expected = engine.GetForecast(account, status, "session", now);
        Assert.Equal(UsageForecastState.Estimated, expected.State);
        await store.SaveForecastHistoryAsync(engine.GetHistory(now));
        var restored = new UsageForecastEngine(await new JsonSettingsStore(new(_directory)).LoadForecastHistoryAsync());
        Assert.Equal(expected, restored.GetForecast(account, status, "session", now));
        restored.Clear();
        await store.SaveForecastHistoryAsync(restored.GetHistory(now));
        Assert.Empty((await store.LoadForecastHistoryAsync()).Samples);
    }

    [Fact]
    public async Task CorruptForecastHistoryRecoversButFutureSchemaIsNotSilentlyOverwritten()
    {
        var paths = new AppPaths(_directory);
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(paths.ForecastHistoryFile, "{incomplete");
        var store = new JsonSettingsStore(paths);
        Assert.Empty((await store.LoadForecastHistoryAsync()).Samples);
        Assert.Single(Directory.GetFiles(_directory, "usage-forecast.json.corrupt-*"));
        await File.WriteAllTextAsync(paths.ForecastHistoryFile, "{\"SchemaVersion\":2}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadForecastHistoryAsync());
        Assert.Contains("2", await File.ReadAllTextAsync(paths.ForecastHistoryFile));
    }

    [Fact]
    public async Task StatisticsSavingIsIndependentAndMigratesPriorEnabledForecasting()
    {
        var paths = new AppPaths(_directory);
        Directory.CreateDirectory(_directory);
        var store = new JsonSettingsStore(paths);
        Assert.False(new AppSettings().SaveUsageStatistics);
        await File.WriteAllTextAsync(paths.SettingsFile, "{\"UsageForecastEnabled\":true,\"ShowUsageForecast\":false}");
        var migrated = await store.LoadSettingsAsync();
        Assert.True(migrated.SaveUsageStatistics);
        Assert.True(migrated.UsageForecastEnabled);
        Assert.False(migrated.ShowUsageForecast);
        migrated.SaveUsageStatistics = false;
        await store.SaveSettingsAsync(migrated);
        var restored = await new JsonSettingsStore(paths).LoadSettingsAsync();
        Assert.False(restored.SaveUsageStatistics);
        Assert.True(restored.UsageForecastEnabled);
        restored.SaveUsageStatistics = true;
        restored.UsageForecastEnabled = false;
        await store.SaveSettingsAsync(restored);
        restored = await store.LoadSettingsAsync();
        Assert.True(restored.SaveUsageStatistics);
        Assert.False(restored.UsageForecastEnabled);
        Assert.DoesNotContain("HasStatisticsPreference", await File.ReadAllTextAsync(paths.SettingsFile));
    }

    [Fact]
    public async Task StatisticsImportPriorHistoryButExplicitEmptyFileNeverReimportsDeletedRecords()
    {
        var paths = new AppPaths(_directory);
        var store = new JsonSettingsStore(paths);
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        var sample = new UsageForecastObservation(ProviderKind.Claude, Guid.NewGuid().ToString("N"),
            "synthetic-statistics", "session", now.AddHours(5), now, 42);
        await store.SaveForecastHistoryAsync(new() { Samples = [sample] });
        var imported = Assert.Single((await store.LoadUsageStatisticsAsync()).Samples);
        Assert.Equal(sample.UsedPercent, imported.UsedPercent);
        Assert.Equal(sample.FetchedAt, imported.FetchedAt);
        await store.SaveUsageStatisticsAsync(new() { Samples = [imported] });
        Assert.Equal(imported, Assert.Single((await new JsonSettingsStore(paths).LoadUsageStatisticsAsync()).Samples));
        await store.SaveUsageStatisticsAsync(new());
        Assert.Empty((await new JsonSettingsStore(paths).LoadUsageStatisticsAsync()).Samples);
        // The prior cache still exists, but cannot undo a deliberate statistics deletion.
        Assert.Single((await store.LoadForecastHistoryAsync()).Samples);
    }

    [Fact]
    public async Task CorruptStatisticsRecoverWithoutReimportingOldForecastData()
    {
        var paths = new AppPaths(_directory);
        Directory.CreateDirectory(_directory);
        var store = new JsonSettingsStore(paths);
        await File.WriteAllTextAsync(paths.UsageStatisticsFile, "{broken");
        Assert.Empty((await store.LoadUsageStatisticsAsync()).Samples);
        Assert.Single(Directory.GetFiles(_directory, "usage-statistics.json.corrupt-*"));
        await File.WriteAllTextAsync(paths.UsageStatisticsFile, "{\"SchemaVersion\":99}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadUsageStatisticsAsync());
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
