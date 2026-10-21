using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIUsage.Core;

public sealed class JsonSettingsStore(AppPaths paths)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public string? LastWarning { get; private set; }

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadAsync<AppSettings>(paths.SettingsFile, cancellationToken).ConfigureAwait(false) ?? new();
        if (settings.SchemaVersion > 1) throw new InvalidOperationException("새 버전에서 저장한 설정입니다. 앱을 업데이트해 주세요.");
        // Older forecasting settings also enabled collection. Preserve that explicit choice on upgrade.
        if (!settings.HasStatisticsPreference) settings.SaveUsageStatistics = settings.UsageForecastEnabled;
        settings.Accounts ??= [];
        settings.Placements ??= [];
        settings.WidgetTransparencyPercent = Math.Clamp(settings.WidgetTransparencyPercent, 0, 100);
        settings.Accounts = settings.Accounts.Where(a => a is not null && Guid.TryParseExact(a.Id, "N", out _) && Enum.IsDefined(a.Provider))
            .DistinctBy(a => a.Id).OrderBy(a => a.Order).ToList();
        var firstAntigravity = settings.Accounts.FirstOrDefault(a => a.Provider == ProviderKind.Antigravity);
        settings.Accounts.RemoveAll(a => a.Provider == ProviderKind.Antigravity && a != firstAntigravity);
        foreach (var account in settings.Accounts)
        {
            account.RefreshMinutes = Math.Clamp(account.RefreshMinutes, 1, 1440);
            account.UsageAlerts = (account.UsageAlerts ?? []).Where(rule => rule is not null && !string.IsNullOrWhiteSpace(rule.WindowId))
                .DistinctBy(rule => rule.WindowId, StringComparer.Ordinal).ToList();
            foreach (var rule in account.UsageAlerts) rule.ThresholdPercent = Math.Clamp(rule.ThresholdPercent, 1, 100);
            UsageFormatting.NormalizeWidgetWindows(account);
            if (account.TrayWindow is { } tray)
                account.TrayWindow = string.IsNullOrWhiteSpace(tray.Id) || tray.Id == "compact:unavailable"
                    ? null : tray with { Name = string.IsNullOrWhiteSpace(tray.Name) ? "선택한 한도" : tray.Name };
        }
        if (!settings.Accounts.Any(account => StringComparer.Ordinal.Equals(account.Id, settings.TrayAccountId)))
            settings.TrayAccountId = null;
        return settings;
    }
    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
        => SaveAsync(paths.SettingsFile, settings, cancellationToken);
    public async Task<IReadOnlyList<UsageSnapshot>> LoadSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        var snapshots = await LoadAsync<List<UsageSnapshot>>(paths.CacheFile, cancellationToken).ConfigureAwait(false) ?? [];
        return snapshots.Where(s => s is not null && Enum.IsDefined(s.Provider) && s.State == UsageState.Ready &&
            Guid.TryParseExact(s.AccountId, "N", out _) && s.Windows is not null &&
            s.Windows.All(w => w is not null && !string.IsNullOrWhiteSpace(w.Id) && !string.IsNullOrWhiteSpace(w.Name) &&
                (w.UsedPercent is null || (double.IsFinite(w.UsedPercent.Value) && w.UsedPercent is >= 0 and <= 100))))
            .ToArray();
    }
    public Task SaveSnapshotsAsync(IEnumerable<UsageSnapshot> snapshots, CancellationToken cancellationToken = default)
        => SaveAsync(paths.CacheFile, snapshots.Where(s => s.State == UsageState.Ready).ToArray(), cancellationToken);

    public async Task<UsageAlertState> LoadAlertStateAsync(CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync<UsageAlertState>(paths.AlertStateFile, cancellationToken).ConfigureAwait(false) ?? new();
        if (state.SchemaVersion > 1) throw new InvalidOperationException("새 버전에서 저장한 알림 상태입니다. 앱을 업데이트해 주세요.");
        state.Deliveries = (state.Deliveries ?? []).Where(UsageAlertEngine.IsValidDelivery).Distinct().ToList();
        state.ResetObservations = UsageAlertEngine.NormalizeResetObservations(state.ResetObservations);
        return state;
    }
    public Task SaveAlertStateAsync(UsageAlertState state, CancellationToken cancellationToken = default)
        => SaveAsync(paths.AlertStateFile, state, cancellationToken);

    public async Task<UsageForecastHistory> LoadForecastHistoryAsync(CancellationToken cancellationToken = default)
    {
        var history = await LoadAsync<UsageForecastHistory>(paths.ForecastHistoryFile, cancellationToken).ConfigureAwait(false);
        return new UsageForecastEngine(history).GetHistory(DateTimeOffset.UtcNow);
    }
    public Task SaveForecastHistoryAsync(UsageForecastHistory history, CancellationToken cancellationToken = default)
        => SaveAsync(paths.ForecastHistoryFile, history, cancellationToken);

    public async Task<UsageStatisticsHistory> LoadUsageStatisticsAsync(CancellationToken cancellationToken = default)
    {
        UsageStatisticsHistory? history;
        if (File.Exists(paths.UsageStatisticsFile))
            history = await LoadAsync<UsageStatisticsHistory>(paths.UsageStatisticsFile, cancellationToken).ConfigureAwait(false);
        else
        {
            // Import the prior forecast cache only until a statistics file (including an empty one) exists.
            var legacy = await LoadForecastHistoryAsync(cancellationToken).ConfigureAwait(false);
            history = new() { Samples = legacy.Samples.Select(sample => new UsageStatisticSample(sample.Provider,
                sample.AccountId, sample.IdentityKey, sample.WindowId, sample.WindowId, sample.UsedPercent,
                sample.ResetsAt, sample.FetchedAt)).ToList() };
        }
        return new UsageStatistics(history).GetHistory(DateTimeOffset.UtcNow);
    }
    public Task SaveUsageStatisticsAsync(UsageStatisticsHistory history, CancellationToken cancellationToken = default)
        => SaveAsync(paths.UsageStatisticsFile, history, cancellationToken);

    public async Task<ResetMessageState> LoadResetMessageStateAsync(CancellationToken cancellationToken = default)
        => new ResetMessageEngine(await LoadAsync<ResetMessageState>(paths.ResetMessageStateFile, cancellationToken).ConfigureAwait(false)).GetState();

    public Task SaveResetMessageStateAsync(ResetMessageState state, CancellationToken cancellationToken = default)
        => SaveAsync(paths.ResetMessageStateFile, state, cancellationToken, durable: true);

    public async Task<string> BackupForVersionChangeAsync(string fromVersion, string toVersion, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await new UpdateDataBackup(paths).CreateAsync(fromVersion, toVersion, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task ClearStatisticsBackupsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { new UpdateDataBackup(paths).ClearStatistics(); }
        finally { _gate.Release(); }
    }

    private async Task<T?> LoadAsync<T>(string file, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(file)) return default;
            try
            {
                using var stream = File.OpenRead(file);
                return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                var backup = file + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                File.Copy(file, backup, true);
                LastWarning = "손상된 설정 파일을 별도로 보관하고 기본 설정으로 시작했습니다.";
                return default;
            }
        }
        finally { _gate.Release(); }
    }
    private async Task SaveAsync<T>(string file, T value, CancellationToken cancellationToken, bool durable = false)
    {
        // Serialize immediately so later UI changes cannot mutate an in-flight write.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(paths.Root);
            if (durable)
            {
                await using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            else await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, file, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { _gate.Release(); }
        }
    }
}
