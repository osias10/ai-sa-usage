using System.Text.Json;
using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class UpdateDataBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AIUsage-backup-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BackupContainsLatestAppDataWithoutCopyingCredentialsOrMutatingLiveData()
    {
        var paths = new AppPaths(_root);
        var store = new JsonSettingsStore(paths);
        await store.SaveSettingsAsync(new() { IncludePrereleaseUpdates = false, StartWithWindows = true,
            Placements = new() { ["monitor"] = new(20, 30, 250, 100) } });
        const string statistics = "{\"SchemaVersion\":1,\"Samples\":[{\"UsedPercent\":42}]}";
        await File.WriteAllTextAsync(paths.UsageStatisticsFile, statistics);
        Directory.CreateDirectory(Path.Combine(_root, "profiles"));
        await File.WriteAllTextAsync(Path.Combine(_root, "profiles", "auth.json"), "private-test-value");
        var backup = await store.BackupForVersionChangeAsync("0.4.11-beta.2", "0.4.10");
        Assert.Equal(statistics, await File.ReadAllTextAsync(Path.Combine(backup, "usage-statistics.json")));
        Assert.Equal(statistics, await File.ReadAllTextAsync(paths.UsageStatisticsFile));
        Assert.Equal(await File.ReadAllTextAsync(paths.SettingsFile), await File.ReadAllTextAsync(Path.Combine(backup, "settings.json")));
        Assert.Equal(new[] { "manifest.json", "settings.json", "usage-statistics.json" }, Directory.GetFiles(backup).Select(Path.GetFileName).Order().ToArray());
        Assert.False(Directory.Exists(Path.Combine(backup, "profiles")));
        Assert.True((await store.LoadSettingsAsync()).StartWithWindows);
        Assert.False((await store.LoadSettingsAsync()).IncludePrereleaseUpdates);
    }

    [Theory]
    [InlineData("{\"SchemaVersion\":99}")]
    [InlineData("{\"SchemaVersion\":\"unknown\"}")]
    public async Task UnsupportedStatePreventsBackupAndLeavesLiveDataIntact(string json)
    {
        var paths = new AppPaths(_root);
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(paths.SettingsFile, json);
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateDataBackup(paths).CreateAsync("0.4.11-beta.1", "0.4.10"));
        Assert.Empty(Directory.GetDirectories(paths.UpdateBackupsDirectory));
        Assert.Equal(json, await File.ReadAllTextAsync(paths.SettingsFile));
    }

    [Fact]
    public async Task CorruptStateDoesNotProduceACompletedBackup()
    {
        var paths = new AppPaths(_root);
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(paths.SettingsFile, "{broken");
        await Assert.ThrowsAnyAsync<JsonException>(() => new UpdateDataBackup(paths).CreateAsync("0.4.11-beta.1", "0.4.10"));
        Assert.Empty(Directory.GetDirectories(paths.UpdateBackupsDirectory));
    }

    [Fact]
    public async Task ExpirationOnlyCleansOwnedBackupsAndStatisticsClearIncludesBackups()
    {
        var paths = new AppPaths(_root);
        var store = new JsonSettingsStore(paths);
        await store.SaveSettingsAsync(new());
        await store.SaveUsageStatisticsAsync(new());
        await store.SaveForecastHistoryAsync(new());
        var clock = new Clock();
        var backups = new UpdateDataBackup(paths, clock);
        var backup = await backups.CreateAsync("0.4.10", "0.4.11-beta.1");
        var userBackup = Path.Combine(paths.UpdateBackupsDirectory, "my-backup");
        Directory.CreateDirectory(userBackup);
        await File.WriteAllTextAsync(Path.Combine(userBackup, "keep.txt"), "untouched");
        await store.ClearStatisticsBackupsAsync();
        Assert.False(File.Exists(Path.Combine(backup, "usage-statistics.json")));
        Assert.False(File.Exists(Path.Combine(backup, "usage-forecast.json")));
        Assert.True(File.Exists(Path.Combine(backup, "settings.json")));
        Assert.True(File.Exists(paths.UsageStatisticsFile));
        clock.Now += TimeSpan.FromHours(24);
        backups.CleanupExpired();
        Assert.False(Directory.Exists(backup));
        Assert.True(File.Exists(Path.Combine(userBackup, "keep.txt")));
    }

    [Fact]
    public async Task CancellationDoesNotPublishIncompleteBackup()
    {
        var paths = new AppPaths(_root);
        await new JsonSettingsStore(paths).SaveSettingsAsync(new());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UpdateDataBackup(paths).CreateAsync("0.4.10", "0.4.11-beta.1", cancel.Token));
        Assert.Empty(Directory.GetDirectories(paths.UpdateBackupsDirectory));
        Assert.True(File.Exists(paths.SettingsFile));
    }

    [Fact]
    public async Task PreviewPreferenceIsIndependentAndAdditivePreviewSettingsSurviveStableSave()
    {
        var paths = new AppPaths(_root);
        Directory.CreateDirectory(_root);
        var id = Guid.NewGuid().ToString("N");
        var json = $$"""{"SchemaVersion":1,"AutoCheckUpdates":false,"IncludePrereleaseUpdates":true,"PreviewOption":{"value":7},"Accounts":[{"Id":"{{id}}","FutureAccountOption":true,"UsageAlerts":[{"WindowId":"session","FutureAlertOption":9}]}]}""";
        await File.WriteAllTextAsync(paths.SettingsFile, json);
        var store = new JsonSettingsStore(paths);
        var settings = await store.LoadSettingsAsync();
        Assert.False(settings.AutoCheckUpdates);
        Assert.True(settings.IncludePrereleaseUpdates);
        settings.IncludePrereleaseUpdates = false;
        await store.SaveSettingsAsync(settings);
        var reopened = await new JsonSettingsStore(paths).LoadSettingsAsync();
        Assert.False(reopened.AutoCheckUpdates);
        Assert.False(reopened.IncludePrereleaseUpdates);
        Assert.Equal(7, reopened.AdditionalSettings!["PreviewOption"].GetProperty("value").GetInt32());
        Assert.True(reopened.Accounts[0].AdditionalSettings!["FutureAccountOption"].GetBoolean());
        Assert.Equal(9, reopened.Accounts[0].UsageAlerts[0].AdditionalSettings!["FutureAlertOption"].GetInt32());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
