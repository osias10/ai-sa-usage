namespace AIUsage.Core;

public enum UsageForecastState { Collecting, NoGrowth, Estimated, ResetFirst, Exhausted, Stale, Unavailable }

public sealed record UsageForecast(UsageForecastState State, DateTimeOffset? ExhaustsAt = null,
    double? PercentPerHour = null, DateTimeOffset? ObservedAt = null, int SampleCount = 0, TimeSpan? SampleSpan = null);

public sealed class UsageForecastHistory
{
    public int SchemaVersion { get; set; } = 1;
    public List<UsageForecastObservation> Samples { get; set; } = [];
}

public sealed record UsageForecastObservation(ProviderKind Provider, string AccountId, string IdentityKey,
    string WindowId, DateTimeOffset ResetsAt, DateTimeOffset FetchedAt, double UsedPercent);

/// <summary>
/// A local linear estimate from observed usage increases, not a provider guarantee.
/// The caller must invoke Observe only after a genuinely completed successful read, never to replay
/// startup caches or on UI ticks. GetForecast only reads history and never synthesizes observations.
/// </summary>
public sealed class UsageForecastEngine
{
    public static readonly TimeSpan HistoryRetention = TimeSpan.FromHours(24);
    public static readonly TimeSpan Lookback = TimeSpan.FromHours(1);
    public static readonly TimeSpan MinimumSampleSpan = TimeSpan.FromMinutes(10);
    public const int MinimumSampleCount = 3;
    public const int MaximumSamplesPerWindow = 1500;
    private readonly object _sync = new();
    private readonly List<UsageForecastObservation> _samples = [];

    public UsageForecastEngine(UsageForecastHistory? history = null)
    {
        if (history?.SchemaVersion > 1)
            throw new InvalidOperationException("새 버전에서 저장한 예측 기록입니다. 앱을 업데이트해 주세요.");
        // Apply the same monotonic/coalescing/period rules to restored data as to fresh readings.
        // Bounds and validity are checked before the history can influence any forecast.
        // Coalesce raw readings before imposing the forecast-sample cap. Otherwise frequent
        // refreshes could evict a whole hour of useful data before minute normalization.
        foreach (var sample in (history?.Samples ?? []).Where(IsValidSample).OrderBy(sample => sample.FetchedAt))
            AddObservation(sample);
        BoundHistory();
    }

    /// <returns>Whether persisted history changed, including cleanup.</returns>
    public bool Observe(AccountSettings account, UsageSnapshot snapshot, DateTimeOffset now)
    {
        lock (_sync)
        {
            var changed = SynchronizeAccountCore(account);
            changed |= Prune(now);
            if (!MatchesAccount(account, snapshot) || snapshot.State != UsageState.Ready || snapshot.FetchedAt == default ||
                snapshot.FetchedAt > now || now - snapshot.FetchedAt > AllowedAge(account) || snapshot.Windows is null)
                return changed;

            foreach (var window in snapshot.Windows.Where(window => window is not null && !string.IsNullOrWhiteSpace(window.Id))
                .DistinctBy(window => window.Id, StringComparer.Ordinal))
            {
                if (window.ResetsAt is not { } reset || reset <= now || window.UsedPercent is not { } used ||
                    !double.IsFinite(used) || used is < 0 or > 100)
                    continue;
                changed |= AddObservation(new(snapshot.Provider, account.Id, account.IdentityKey!, window.Id,
                    reset, snapshot.FetchedAt, used));
            }
            changed |= BoundHistory();
            return changed;
        }
    }

    public UsageForecast GetForecast(AccountSettings account, AccountUsageStatus status, string windowId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(windowId) || !StringComparer.Ordinal.Equals(status.AccountId, account.Id) ||
            status.State != UsageState.Ready || status.Snapshot is not { } snapshot ||
            snapshot.State != UsageState.Ready || snapshot.FetchedAt == default ||
            !MatchesAccount(account, snapshot) || snapshot.Windows is null)
            return new(UsageForecastState.Unavailable);
        var window = snapshot.Windows.FirstOrDefault(window => window is not null && window.Id == windowId);
        if (window?.ResetsAt is not { } reset || reset <= now || window.UsedPercent is not { } used ||
            !double.IsFinite(used) || used is < 0 or > 100)
            return new(UsageForecastState.Unavailable, ObservedAt: snapshot.FetchedAt);
        if (snapshot.FetchedAt > now || now - snapshot.FetchedAt > AllowedAge(account))
            return new(UsageForecastState.Stale, ObservedAt: snapshot.FetchedAt);
        if (used == 100)
            return new(UsageForecastState.Exhausted, ObservedAt: snapshot.FetchedAt);

        lock (_sync)
        {
            var key = new UsageForecastObservation(snapshot.Provider, account.Id, account.IdentityKey!, windowId,
                reset, snapshot.FetchedAt, used);
            var latest = _samples.LastOrDefault(sample => SameWindow(sample, key));
            if (latest is not null && latest.FetchedAt > snapshot.FetchedAt)
                return new(UsageForecastState.Stale, ObservedAt: snapshot.FetchedAt);
            // A newer UI/cache snapshot must be observed as a real completed read before its values
            // are combined with history. An absent/changed period starts from Collecting instead.
            if (latest is null || latest.FetchedAt != snapshot.FetchedAt || latest.ResetsAt != reset || latest.UsedPercent != used)
                return new(UsageForecastState.Collecting, ObservedAt: snapshot.FetchedAt);

            // Anchor the fitting interval and predicted exhaustion time to the last reading.
            // Advancing the UI clock alone must not move the estimate or change its sample set.
            var samples = _samples.Where(sample => SameWindow(sample, key) && sample.ResetsAt == reset &&
                sample.FetchedAt >= snapshot.FetchedAt - Lookback && sample.FetchedAt <= snapshot.FetchedAt)
                .OrderBy(sample => sample.FetchedAt).ToArray();
            var count = samples.Length;
            var span = count > 0 ? samples[^1].FetchedAt - samples[0].FetchedAt : TimeSpan.Zero;
            if (count < MinimumSampleCount || span < MinimumSampleSpan)
                return new(UsageForecastState.Collecting, ObservedAt: snapshot.FetchedAt, SampleCount: count, SampleSpan: span);
            var increase = samples[^1].UsedPercent - samples[0].UsedPercent;
            if (increase == 0)
                return new(UsageForecastState.NoGrowth, ObservedAt: snapshot.FetchedAt, SampleCount: count, SampleSpan: span);
            if (increase < 1)
                return new(UsageForecastState.Collecting, ObservedAt: snapshot.FetchedAt, SampleCount: count, SampleSpan: span);
            var rate = increase / span.TotalHours;
            if (!double.IsFinite(rate) || rate <= 0) return new(UsageForecastState.Unavailable, ObservedAt: snapshot.FetchedAt);
            DateTimeOffset exhaustion;
            try { exhaustion = snapshot.FetchedAt.AddHours((100 - used) / rate); }
            catch (ArgumentOutOfRangeException) { return new(UsageForecastState.Unavailable, ObservedAt: snapshot.FetchedAt); }
            var state = exhaustion <= now ? UsageForecastState.Stale : exhaustion >= reset
                ? UsageForecastState.ResetFirst : UsageForecastState.Estimated;
            return new(state, exhaustion, rate, snapshot.FetchedAt, count, span);
        }
    }

    public void SynchronizeAccount(AccountSettings account)
    {
        lock (_sync) SynchronizeAccountCore(account);
    }

    public void RemoveAccount(string accountId)
    {
        lock (_sync) _samples.RemoveAll(sample => StringComparer.Ordinal.Equals(sample.AccountId, accountId));
    }

    public void Clear() { lock (_sync) _samples.Clear(); }

    public UsageForecastHistory GetHistory(DateTimeOffset now)
    {
        lock (_sync)
        {
            Prune(now);
            BoundHistory();
            return new() { Samples = [.. _samples] };
        }
    }

    private bool SynchronizeAccountCore(AccountSettings account)
        => _samples.RemoveAll(sample => StringComparer.Ordinal.Equals(sample.AccountId, account.Id) &&
            (sample.Provider != account.Provider || !StringComparer.Ordinal.Equals(sample.IdentityKey, account.IdentityKey))) > 0;

    private bool AddObservation(UsageForecastObservation sample)
    {
        var index = _samples.FindLastIndex(existing => SameWindow(existing, sample));
        if (index >= 0)
        {
            var previous = _samples[index];
            // Duplicate cache replays, delayed responses and backwards clocks add no information.
            if (sample.FetchedAt <= previous.FetchedAt) return false;
            if (sample.ResetsAt != previous.ResetsAt || sample.UsedPercent < previous.UsedPercent)
            {
                // Both a new reset timestamp and a usage decrease invalidate the old fitting period.
                // Removing it also prevents a later clock correction from rejoining an older period.
                _samples.RemoveAll(existing => SameWindow(existing, sample));
            }
            else if (MinuteBucket(sample.FetchedAt) == MinuteBucket(previous.FetchedAt))
            {
                _samples[index] = sample;
                return true;
            }
        }
        _samples.Add(sample);
        return true;
    }

    private bool Prune(DateTimeOffset now) => _samples.RemoveAll(sample => now - sample.FetchedAt > HistoryRetention) > 0;

    private bool BoundHistory()
    {
        if (_samples.Count <= MaximumSamplesPerWindow) return false;
        var retained = _samples.GroupBy(sample => (sample.AccountId, sample.WindowId))
            .SelectMany(group => group.OrderByDescending(sample => sample.FetchedAt).Take(MaximumSamplesPerWindow))
            .OrderBy(sample => sample.FetchedAt).ToList();
        if (retained.Count == _samples.Count) return false;
        _samples.Clear();
        _samples.AddRange(retained);
        return true;
    }

    private static long MinuteBucket(DateTimeOffset time) => time.UtcTicks / TimeSpan.TicksPerMinute;
    private static TimeSpan AllowedAge(AccountSettings account) => TimeSpan.FromMinutes(Math.Clamp(account.RefreshMinutes * 2d, 15, 60));
    private static bool MatchesAccount(AccountSettings account, UsageSnapshot snapshot) =>
        Guid.TryParseExact(account.Id, "N", out _) && Enum.IsDefined(account.Provider) &&
        snapshot.Provider == account.Provider && StringComparer.Ordinal.Equals(snapshot.AccountId, account.Id) &&
        !string.IsNullOrWhiteSpace(account.IdentityKey) && StringComparer.Ordinal.Equals(snapshot.IdentityKey, account.IdentityKey);
    private static bool SameWindow(UsageForecastObservation one, UsageForecastObservation two) =>
        one.Provider == two.Provider && StringComparer.Ordinal.Equals(one.AccountId, two.AccountId) &&
        StringComparer.Ordinal.Equals(one.IdentityKey, two.IdentityKey) && StringComparer.Ordinal.Equals(one.WindowId, two.WindowId);
    private static bool IsValidSample(UsageForecastObservation? sample) => sample is not null &&
        Enum.IsDefined(sample.Provider) && Guid.TryParseExact(sample.AccountId, "N", out _) &&
        !string.IsNullOrWhiteSpace(sample.IdentityKey) && !string.IsNullOrWhiteSpace(sample.WindowId) &&
        sample.FetchedAt != default && sample.ResetsAt > sample.FetchedAt &&
        double.IsFinite(sample.UsedPercent) && sample.UsedPercent is >= 0 and <= 100;
}
