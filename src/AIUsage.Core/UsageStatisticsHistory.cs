namespace AIUsage.Core;

public sealed record UsageStatisticSample(ProviderKind Provider, string AccountId, string IdentityKey,
    string WindowId, string WindowName, double UsedPercent, DateTimeOffset? ResetsAt, DateTimeOffset FetchedAt);

public sealed class UsageStatisticsHistory
{
    public int SchemaVersion { get; set; } = 1;
    public List<UsageStatisticSample> Samples { get; set; } = [];
}

/// <summary>
/// Keeps successful source readings independently of forecasting preferences. Call Observe only
/// for freshly completed reads; cache replay, settings changes and UI ticks are not observations.
/// Disabling collection means not calling Observe; it does not clear retained history.
/// Clear and account removal are explicit deletion operations; retention still applies.
/// </summary>
public sealed class UsageStatistics
{
    public static readonly TimeSpan HistoryRetention = TimeSpan.FromHours(24);
    public const int MaximumSamplesPerWindow = 10000;
    private readonly object _sync = new();
    private readonly List<UsageStatisticSample> _samples;
    private readonly HashSet<SampleKey> _keys;

    public UsageStatistics(UsageStatisticsHistory? history = null)
    {
        if (history?.SchemaVersion > 1)
            throw new InvalidOperationException("새 버전에서 저장한 통계 기록입니다. 앱을 업데이트해 주세요.");
        _samples = (history?.Samples ?? []).Where(IsValidSample)
            .DistinctBy(Key)
            .GroupBy(sample => (sample.AccountId, sample.WindowId))
            .SelectMany(group => group.OrderByDescending(sample => sample.FetchedAt).Take(MaximumSamplesPerWindow))
            .OrderBy(sample => sample.FetchedAt).ToList();
        _keys = _samples.Select(Key).ToHashSet();
    }

    /// <returns>Whether history changed, including account synchronization or retention cleanup.</returns>
    public bool Observe(AccountSettings account, UsageSnapshot snapshot, DateTimeOffset now)
    {
        lock (_sync)
        {
            var changed = SynchronizeAccountCore(account);
            changed |= Prune(now);
            if (!MatchesAccount(account, snapshot) || snapshot.State != UsageState.Ready || snapshot.FetchedAt == default ||
                snapshot.FetchedAt > now || now - snapshot.FetchedAt > AllowedAge(account) || snapshot.Windows is null)
                return changed;

            foreach (var window in snapshot.Windows)
            {
                if (window is null || string.IsNullOrWhiteSpace(window.Id) || string.IsNullOrWhiteSpace(window.Name) ||
                    window.UsedPercent is not { } used || !double.IsFinite(used) || used is < 0 or > 100)
                    continue;
                var sample = new UsageStatisticSample(snapshot.Provider, account.Id, account.IdentityKey!, window.Id,
                    window.Name, used, window.ResetsAt, snapshot.FetchedAt);
                if (!_keys.Add(Key(sample))) continue;
                // Every distinct source timestamp is retained. Reset transitions, usage decreases,
                // and missing reset metadata are meaningful raw readings, not reasons to erase data.
                _samples.Add(sample);
                changed = true;
            }
            changed |= BoundHistory();
            return changed;
        }
    }

    public void SynchronizeAccount(AccountSettings account)
    {
        lock (_sync) SynchronizeAccountCore(account);
    }

    public void RemoveAccount(string accountId)
    {
        lock (_sync) RemoveWhere(sample => StringComparer.Ordinal.Equals(sample.AccountId, accountId));
    }

    public void Clear()
    {
        lock (_sync) { _samples.Clear(); _keys.Clear(); }
    }

    public UsageStatisticsHistory GetHistory(DateTimeOffset now)
    {
        lock (_sync)
        {
            Prune(now);
            return new() { Samples = _samples.OrderBy(sample => sample.FetchedAt).ToList() };
        }
    }

    public UsageForecastHistory GetForecastHistory(DateTimeOffset now)
    {
        // Project only real source readings whose reset was still in the future when fetched.
        // The forecast engine handles minute coalescing, period changes, decreases and fitting.
        // It receives the original identity, reset and fetched timestamp without fabricated data.
        var projection = GetHistory(now).Samples
            .Where(sample => sample.ResetsAt is { } reset && reset > sample.FetchedAt)
            .Select(sample => new UsageForecastObservation(sample.Provider, sample.AccountId, sample.IdentityKey,
                sample.WindowId, sample.ResetsAt!.Value, sample.FetchedAt, sample.UsedPercent)).ToList();
        return new UsageForecastEngine(new() { Samples = projection }).GetHistory(now);
    }

    public int Count(DateTimeOffset now)
    {
        lock (_sync) { Prune(now); return _samples.Count; }
    }

    private bool SynchronizeAccountCore(AccountSettings account) => RemoveWhere(sample =>
        StringComparer.Ordinal.Equals(sample.AccountId, account.Id) &&
        (sample.Provider != account.Provider || !StringComparer.Ordinal.Equals(sample.IdentityKey, account.IdentityKey)));

    private bool Prune(DateTimeOffset now) => RemoveWhere(sample => now - sample.FetchedAt > HistoryRetention);

    private bool RemoveWhere(Predicate<UsageStatisticSample> predicate)
    {
        var removed = _samples.RemoveAll(sample =>
        {
            if (!predicate(sample)) return false;
            _keys.Remove(Key(sample));
            return true;
        });
        return removed > 0;
    }

    private bool BoundHistory()
    {
        if (_samples.Count <= MaximumSamplesPerWindow) return false;
        var excess = _samples.GroupBy(sample => (sample.AccountId, sample.WindowId))
            .SelectMany(group => group.OrderByDescending(sample => sample.FetchedAt).Skip(MaximumSamplesPerWindow))
            .Select(Key).ToHashSet();
        return excess.Count > 0 && RemoveWhere(sample => excess.Contains(Key(sample)));
    }

    private static TimeSpan AllowedAge(AccountSettings account) => TimeSpan.FromMinutes(Math.Clamp(account.RefreshMinutes * 2d, 15, 60));
    private static bool MatchesAccount(AccountSettings account, UsageSnapshot snapshot) =>
        Guid.TryParseExact(account.Id, "N", out _) && Enum.IsDefined(account.Provider) &&
        snapshot.Provider == account.Provider && StringComparer.Ordinal.Equals(snapshot.AccountId, account.Id) &&
        !string.IsNullOrWhiteSpace(account.IdentityKey) && StringComparer.Ordinal.Equals(snapshot.IdentityKey, account.IdentityKey);
    private static bool IsValidSample(UsageStatisticSample? sample) => sample is not null &&
        Enum.IsDefined(sample.Provider) && Guid.TryParseExact(sample.AccountId, "N", out _) &&
        !string.IsNullOrWhiteSpace(sample.IdentityKey) && !string.IsNullOrWhiteSpace(sample.WindowId) &&
        !string.IsNullOrWhiteSpace(sample.WindowName) && sample.FetchedAt != default &&
        double.IsFinite(sample.UsedPercent) && sample.UsedPercent is >= 0 and <= 100;
    private static SampleKey Key(UsageStatisticSample sample)
        => new(sample.Provider, sample.AccountId, sample.IdentityKey, sample.WindowId, sample.FetchedAt);
    private sealed record SampleKey(ProviderKind Provider, string AccountId, string IdentityKey, string WindowId, DateTimeOffset FetchedAt);
}
