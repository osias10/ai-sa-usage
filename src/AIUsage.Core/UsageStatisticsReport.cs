namespace AIUsage.Core;

/// <summary>
/// A read-only view of observed quota percentages, not token counts or billable usage.
/// An increase is expressed in percentage points and includes only comparable readings.
/// </summary>
public sealed record UsageStatisticsReport(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<UsageStatisticSample> Samples,
    IReadOnlyList<IReadOnlyList<UsageStatisticSample>> Segments,
    double? LatestPercent,
    double? PeakPercent,
    double? ObservedIncreasePercent,
    DateTimeOffset? LastObservedAt,
    int SampleCount,
    int ComparablePairCount);

public static class UsageStatisticsReportBuilder
{
    public static UsageStatisticsReport Build(IEnumerable<UsageStatisticSample> samples,
        AccountSettings account, string windowId, DateTimeOffset now, TimeSpan range)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(account);
        if (range <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(range));

        var ticks = Math.Min(range.Ticks, UsageStatistics.HistoryRetention.Ticks);
        var from = new DateTimeOffset(Math.Max(0, now.UtcTicks - ticks), TimeSpan.Zero);
        var validAccount = Enum.IsDefined(account.Provider) && Guid.TryParseExact(account.Id, "N", out _) &&
            !string.IsNullOrWhiteSpace(account.IdentityKey) && !string.IsNullOrWhiteSpace(windowId);
        var readings = validAccount
            ? samples.Where(sample => IsValid(sample) && sample.Provider == account.Provider &&
                StringComparer.Ordinal.Equals(sample.AccountId, account.Id) &&
                StringComparer.Ordinal.Equals(sample.IdentityKey, account.IdentityKey) &&
                StringComparer.Ordinal.Equals(sample.WindowId, windowId) &&
                sample.FetchedAt >= from && sample.FetchedAt <= now)
                // Retention deduplicates with the same first-reading-wins rule. Do not turn
                // duplicate responses or a changed timezone offset into an extra observation.
                .DistinctBy(sample => sample.FetchedAt)
                .OrderBy(sample => sample.FetchedAt).ToArray()
            : [];

        var segments = new List<IReadOnlyList<UsageStatisticSample>>();
        var current = new List<UsageStatisticSample>();
        var maximumGap = TimeSpan.FromMinutes(Math.Max(15, Math.Clamp(account.RefreshMinutes, 1, 1440) * 2));
        var comparablePairs = 0;
        var observedIncrease = 0d;
        UsageStatisticSample? previous = null;
        foreach (var reading in readings)
        {
            var continuous = previous is not null &&
                reading.FetchedAt - previous.FetchedAt <= maximumGap &&
                reading.ResetsAt == previous.ResetsAt && reading.UsedPercent >= previous.UsedPercent &&
                (reading.ResetsAt is null || reading.ResetsAt > reading.FetchedAt);
            if (!continuous && current.Count > 0)
            {
                segments.Add(current.AsReadOnly());
                current = [];
            }
            current.Add(reading);
            // Unknown resets can be drawn, but they cannot establish a comparable period.
            // A period may have expired by now: what matters is its validity when fetched.
            if (continuous && reading.ResetsAt is { } reset && reset > previous!.FetchedAt)
            {
                comparablePairs++;
                observedIncrease += reading.UsedPercent - previous.UsedPercent;
            }
            previous = reading;
        }
        if (current.Count > 0) segments.Add(current.AsReadOnly());

        return new(from, now, Array.AsReadOnly(readings), segments.AsReadOnly(),
            readings.Length > 0 ? readings[^1].UsedPercent : null,
            readings.Length > 0 ? readings.Max(sample => sample.UsedPercent) : null,
            comparablePairs > 0 ? observedIncrease : null,
            readings.Length > 0 ? readings[^1].FetchedAt : null,
            readings.Length, comparablePairs);
    }

    private static bool IsValid(UsageStatisticSample? sample) => sample is not null &&
        Enum.IsDefined(sample.Provider) && Guid.TryParseExact(sample.AccountId, "N", out _) &&
        !string.IsNullOrWhiteSpace(sample.IdentityKey) && !string.IsNullOrWhiteSpace(sample.WindowId) &&
        !string.IsNullOrWhiteSpace(sample.WindowName) && sample.FetchedAt != default &&
        double.IsFinite(sample.UsedPercent) && sample.UsedPercent is >= 0 and <= 100;
}
