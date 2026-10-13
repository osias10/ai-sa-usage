namespace AIUsage.Core;

/// <summary>Evaluates new successful reads without performing UI or storage operations.</summary>
public sealed class UsageAlertEngine
{
    public static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromMinutes(5);
    private readonly object _sync = new();
    private readonly List<UsageAlertDelivery> _deliveries;
    private readonly List<UsageResetObservation> _resetObservations;

    public UsageAlertEngine(UsageAlertState? state = null)
    {
        if (state?.SchemaVersion > 1) throw new InvalidOperationException("새 버전에서 저장한 알림 상태입니다. 앱을 업데이트해 주세요.");
        _deliveries = (state?.Deliveries ?? []).Where(IsValidDelivery).Distinct().ToList();
        _resetObservations = NormalizeResetObservations(state?.ResetObservations);
    }

    /// <summary>
    /// Call only for a newly completed read, with isFresh false for cache replays or status-only changes.
    /// The returned alerts are marked delivered immediately. Persist GetState() before presenting them.
    /// Persist after every fresh evaluation, including empty results, to retain reset observations.
    /// Unknown reset times receive at most one alert per account, identity and window until account removal.
    /// </summary>
    public IReadOnlyList<UsageAlert> Evaluate(AccountSettings account, UsageSnapshot snapshot, DateTimeOffset now, bool isFresh)
    {
        SynchronizeSettings(account);
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
            var resetRules = (account.UsageAlerts ?? []).Where(rule => rule is not null && rule.ResetEnabled &&
                !string.IsNullOrWhiteSpace(rule.WindowId))
                .DistinctBy(rule => rule.WindowId, StringComparer.Ordinal).ToDictionary(rule => rule.WindowId, StringComparer.Ordinal);

            foreach (var window in snapshot.Windows)
            {
                if (window is null || string.IsNullOrWhiteSpace(window.Id) || string.IsNullOrWhiteSpace(window.Name) ||
                    window.UsedPercent is not { } percent ||
                    !double.IsFinite(percent) || percent is < 0 or > 100 ||
                    window.ResetsAt is { } reset && reset <= now)
                    continue;

                if (resetRules.TryGetValue(window.Id, out var resetRule) && window.ResetsAt is not null)
                    ObserveReset(account, snapshot, window, resetRule, percent, now, alerts);

                if (!rules.TryGetValue(window.Id, out var rule) || percent < rule.ThresholdPercent) continue;

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
        lock (_sync) return new() { Deliveries = [.. _deliveries], ResetObservations = [.. _resetObservations] };
    }

    /// <summary>
    /// Call when settings change and during startup, then persist GetState(). Disabling a reset rule
    /// discards its baseline, so enabling it again starts with the next successful read.
    /// Threshold receipts are intentionally retained when their options are changed.
    /// </summary>
    public void SynchronizeSettings(AccountSettings account)
    {
        var enabled = (account.UsageAlerts ?? []).Where(rule => rule is not null && rule.ResetEnabled &&
            !string.IsNullOrWhiteSpace(rule.WindowId)).Select(rule => rule.WindowId).ToHashSet(StringComparer.Ordinal);
        lock (_sync) _resetObservations.RemoveAll(observation =>
            StringComparer.Ordinal.Equals(observation.AccountId, account.Id) &&
            (observation.Provider != account.Provider ||
             !StringComparer.Ordinal.Equals(observation.IdentityKey, account.IdentityKey) || !enabled.Contains(observation.WindowId)));
    }

    public void RemoveAccount(string accountId)
    {
        lock (_sync)
        {
            _deliveries.RemoveAll(delivery => StringComparer.Ordinal.Equals(delivery.AccountId, accountId));
            _resetObservations.RemoveAll(observation => StringComparer.Ordinal.Equals(observation.AccountId, accountId));
        }
    }

    private void ObserveReset(AccountSettings account, UsageSnapshot snapshot, UsageWindow window,
        UsageAlertRule rule, double percent, DateTimeOffset now, List<UsageAlert> alerts)
    {
        var reset = window.ResetsAt!.Value;
        var index = _resetObservations.FindIndex(observation => observation.Provider == snapshot.Provider &&
            StringComparer.Ordinal.Equals(observation.AccountId, account.Id) &&
            StringComparer.Ordinal.Equals(observation.IdentityKey, account.IdentityKey) &&
            StringComparer.Ordinal.Equals(observation.WindowId, window.Id));
        var observation = new UsageResetObservation(snapshot.Provider, account.Id, account.IdentityKey!,
            window.Id, reset, snapshot.FetchedAt, now);
        if (index < 0)
        {
            _resetObservations.Add(observation);
            return;
        }

        var previous = _resetObservations[index];
        // Replayed/out-of-order reads and a local clock rollback cannot advance the baseline.
        if (snapshot.FetchedAt <= previous.FetchedAt || now < previous.ObservedAt) return;
        // Never move a reset baseline backwards. A corrected timestamp within an active period
        // must not manufacture a later transition back to the previously observed timestamp.
        if (reset < previous.ResetsAt) observation = observation with { ResetsAt = previous.ResetsAt };
        _resetObservations[index] = observation;
        // A delayed read fetched before expiry is still evidence from the old period, even when
        // it is processed after the boundary. Only a newer, post-boundary read confirms a reset.
        if (snapshot.FetchedAt < previous.ResetsAt || reset <= previous.ResetsAt) return;
        alerts.Add(new(snapshot.Provider, account.Id, account.IdentityKey!, window.Id, window.Name,
            percent, rule.ThresholdPercent, reset, UsageAlertKind.Reset));
    }

    internal static bool IsValidDelivery(UsageAlertDelivery? delivery) => delivery is not null &&
        Enum.IsDefined(delivery.Provider) && Guid.TryParseExact(delivery.AccountId, "N", out _) &&
        !string.IsNullOrWhiteSpace(delivery.IdentityKey) && !string.IsNullOrWhiteSpace(delivery.WindowId) &&
        delivery.NotifiedAt != default && (delivery.ResetsAt is null || delivery.ResetsAt > delivery.NotifiedAt);

    internal static List<UsageResetObservation> NormalizeResetObservations(IEnumerable<UsageResetObservation>? observations)
        => (observations ?? []).Where(observation => observation is not null && Enum.IsDefined(observation.Provider) &&
            Guid.TryParseExact(observation.AccountId, "N", out _) && !string.IsNullOrWhiteSpace(observation.IdentityKey) &&
            !string.IsNullOrWhiteSpace(observation.WindowId) && observation.FetchedAt != default &&
            observation.ObservedAt != default && observation.FetchedAt <= observation.ObservedAt &&
            observation.ObservedAt - observation.FetchedAt <= MaximumSnapshotAge && observation.ResetsAt > observation.ObservedAt)
            .OrderByDescending(observation => observation.ObservedAt)
            .ThenByDescending(observation => observation.FetchedAt)
            .ThenByDescending(observation => observation.ResetsAt)
            .DistinctBy(observation => (observation.Provider, observation.AccountId, observation.IdentityKey, observation.WindowId))
            .ToList();
}
