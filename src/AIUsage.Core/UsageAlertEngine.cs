namespace AIUsage.Core;

/// <summary>Evaluates new successful reads without performing UI or storage operations.</summary>
public sealed class UsageAlertEngine
{
    public static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromMinutes(5);
    private readonly object _sync = new();
    private readonly List<UsageAlertDelivery> _deliveries;

    public UsageAlertEngine(UsageAlertState? state = null)
    {
        if (state?.SchemaVersion > 1) throw new InvalidOperationException("새 버전에서 저장한 알림 상태입니다. 앱을 업데이트해 주세요.");
        _deliveries = (state?.Deliveries ?? []).Where(IsValidDelivery).Distinct().ToList();
    }

    /// <summary>
    /// Call only for a newly completed read, with isFresh false for cache replays or status-only changes.
    /// The returned alerts are marked delivered immediately. Persist GetState() before presenting them.
    /// Unknown reset times receive at most one alert per account, identity and window until account removal.
    /// </summary>
    public IReadOnlyList<UsageAlert> Evaluate(AccountSettings account, UsageSnapshot snapshot, DateTimeOffset now, bool isFresh)
    {
        if (!isFresh || snapshot.State != UsageState.Ready || snapshot.Provider != account.Provider ||
            !Enum.IsDefined(snapshot.Provider) || !StringComparer.Ordinal.Equals(snapshot.AccountId, account.Id) ||
            !Guid.TryParseExact(account.Id, "N", out _) || string.IsNullOrWhiteSpace(account.IdentityKey) ||
            !StringComparer.Ordinal.Equals(snapshot.IdentityKey, account.IdentityKey) ||
            snapshot.FetchedAt > now || now - snapshot.FetchedAt > MaximumSnapshotAge || snapshot.Windows is null)
            return [];

        lock (_sync)
        {
            var alerts = new List<UsageAlert>();
            var rules = (account.UsageAlerts ?? []).Where(rule => rule is not null && rule.Enabled &&
                !string.IsNullOrWhiteSpace(rule.WindowId) && rule.ThresholdPercent is >= 1 and <= 100)
                .DistinctBy(rule => rule.WindowId, StringComparer.Ordinal).ToDictionary(rule => rule.WindowId, StringComparer.Ordinal);

            foreach (var window in snapshot.Windows)
            {
                if (window is null || string.IsNullOrWhiteSpace(window.Id) || string.IsNullOrWhiteSpace(window.Name) ||
                    !rules.TryGetValue(window.Id, out var rule) || window.UsedPercent is not { } percent ||
                    !double.IsFinite(percent) || percent is < 0 or > 100 || percent < rule.ThresholdPercent ||
                    window.ResetsAt is { } reset && reset <= now)
                    continue;

                var matching = _deliveries.Where(delivery => delivery.Provider == snapshot.Provider &&
                    StringComparer.Ordinal.Equals(delivery.AccountId, account.Id) &&
                    StringComparer.Ordinal.Equals(delivery.IdentityKey, account.IdentityKey) &&
                    StringComparer.Ordinal.Equals(delivery.WindowId, window.Id));

                // A missing reset is not evidence of a new period. An unknown-period receipt
                // suppresses this window indefinitely; a previously known period also suppresses
                // an unknown reset so intermittent missing data cannot produce a second alert.
                if (matching.Any(delivery => delivery.ResetsAt is null || window.ResetsAt is null ||
                    delivery.ResetsAt == window.ResetsAt || delivery.ResetsAt > now))
                    continue;

                _deliveries.Add(new(snapshot.Provider, account.Id, account.IdentityKey, window.Id, window.ResetsAt, now));
                alerts.Add(new(snapshot.Provider, account.Id, account.IdentityKey, window.Id, window.Name,
                    percent, rule.ThresholdPercent, window.ResetsAt));
            }
            return alerts;
        }
    }

    public UsageAlertState GetState()
    {
        lock (_sync) return new() { Deliveries = [.. _deliveries] };
    }

    public void RemoveAccount(string accountId)
    {
        lock (_sync) _deliveries.RemoveAll(delivery => StringComparer.Ordinal.Equals(delivery.AccountId, accountId));
    }

    internal static bool IsValidDelivery(UsageAlertDelivery? delivery) => delivery is not null &&
        Enum.IsDefined(delivery.Provider) && Guid.TryParseExact(delivery.AccountId, "N", out _) &&
        !string.IsNullOrWhiteSpace(delivery.IdentityKey) && !string.IsNullOrWhiteSpace(delivery.WindowId) &&
        delivery.NotifiedAt != default && (delivery.ResetsAt is null || delivery.ResetsAt > delivery.NotifiedAt);
}
