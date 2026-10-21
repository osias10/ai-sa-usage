namespace AIUsage.Core;

public sealed class ResetMessageState
{
    public int SchemaVersion { get; set; } = 1;
    public List<ResetMessageObservation> Observations { get; set; } = [];
    public List<ResetMessageAttempt> Attempts { get; set; } = [];
}

public sealed record ResetMessageTicket(
    ProviderKind Provider, string AccountId, string IdentityKey, string WindowId, DateTimeOffset ResetsAt);

public sealed record ResetMessageObservation(
    ProviderKind Provider, string AccountId, string IdentityKey, string WindowId,
    DateTimeOffset ResetsAt, DateTimeOffset FetchedAt, DateTimeOffset ObservedAt);

public enum ResetMessageOutcome { Reserved, Succeeded, Failed, Unknown }
public enum ResetMessageDecision { Reserved, Waiting, AlreadyActive, Expired, Invalid }

/// <summary>A reserved attempt is durable even if the process ends before its result is known.</summary>
public sealed record ResetMessageAttempt(
    ProviderKind Provider, string AccountId, string IdentityKey, string WindowId, DateTimeOffset ResetsAt,
    DateTimeOffset ReservedAt, ResetMessageOutcome Outcome = ResetMessageOutcome.Reserved,
    DateTimeOffset? CompletedAt = null, string? Message = null);

/// <summary>
/// Arms a future reset from a successful read, then requires a new post-reset read before reserving
/// one message. This policy does no I/O. Persist GetState() after changes, especially before sending.
/// </summary>
public sealed class ResetMessageEngine
{
    public static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaximumOverdue = TimeSpan.FromHours(1);
    private readonly object _sync = new();
    private readonly List<ResetMessageObservation> _observations;
    private readonly List<ResetMessageAttempt> _attempts;

    public ResetMessageEngine(ResetMessageState? state = null)
    {
        if (state?.SchemaVersion > 1) throw new InvalidOperationException("Unsupported reset message state version.");
        _observations = (state?.Observations ?? []).Where(IsValidObservation)
            .OrderByDescending(item => item.ObservedAt).ThenByDescending(item => item.FetchedAt)
            .DistinctBy(item => (item.Provider, item.AccountId, item.IdentityKey, item.WindowId)).ToList();
        _attempts = (state?.Attempts ?? []).Where(IsValidAttempt)
            .DistinctBy(item => (item.Provider, item.IdentityKey, item.WindowId, item.ResetsAt)).ToList();
    }

    /// <summary>Disabling or changing the target cancels arming, but never erases send receipts.</summary>
    public void SynchronizeSettings(AccountSettings account, bool enabled, string? targetWindowId)
    {
        lock (_sync) Synchronize(account, enabled, targetWindowId);
    }

    /// <summary>
    /// Only a newly completed successful read may establish a baseline. Cache replays and status-only
    /// updates must pass isFresh=false. A due baseline is retained for preflight PrepareAttempt.
    /// </summary>
    public void Observe(AccountSettings account, bool enabled, string? targetWindowId,
        UsageSnapshot snapshot, DateTimeOffset now, bool isFresh)
    {
        lock (_sync)
        {
            Synchronize(account, enabled, targetWindowId);
            if (!enabled || !ValidAccount(account, targetWindowId) ||
                !ValidSnapshot(snapshot, account.Provider, account.Id, account.IdentityKey!, now, isFresh)) return;
            var index = FindObservation(account.Provider, account.Id, account.IdentityKey!, targetWindowId!);
            if (index >= 0)
            {
                var prior = _observations[index];
                if (snapshot.FetchedAt <= prior.FetchedAt || now < prior.ObservedAt) return;
                if (prior.ResetsAt <= now && now - prior.ResetsAt <= MaximumOverdue) return;
                // Expired observations must not turn into a catch-up message after a long sleep.
                if (prior.ResetsAt <= now) _observations.RemoveAt(index);
            }
            ArmFuture(snapshot, targetWindowId!, now);
        }
    }

    /// <summary>Returns the reset that needs a fresh provider read; never authorizes a message itself.</summary>
    public ResetMessageTicket? GetDue(AccountSettings account, bool enabled, string? targetWindowId, DateTimeOffset now)
    {
        lock (_sync)
        {
            Synchronize(account, enabled, targetWindowId);
            if (!enabled || !ValidAccount(account, targetWindowId)) return null;
            var index = FindObservation(account.Provider, account.Id, account.IdentityKey!, targetWindowId!);
            if (index < 0) return null;
            var observation = _observations[index];
            if (now < observation.ObservedAt || observation.ResetsAt > now) return null;
            var ticket = Ticket(observation);
            if (HasAttempt(ticket) || now - observation.ResetsAt > MaximumOverdue)
            {
                _observations.RemoveAt(index);
                return null;
            }
            return ticket;
        }
    }

    /// <summary>
    /// Recheck current settings with SynchronizeSettings first. Reserved means a receipt has already
    /// been inserted: persist it before sending. A persistence failure must prevent the send.
    /// All outcomes, including crashes, timeout and cancellation after reservation, suppress retries.
    /// </summary>
    public ResetMessageDecision PrepareAttempt(ResetMessageTicket ticket, UsageSnapshot snapshot,
        DateTimeOffset now, bool isFresh)
    {
        lock (_sync)
        {
            if (!ValidKey(ticket.Provider, ticket.AccountId, ticket.IdentityKey, ticket.WindowId) || HasAttempt(ticket))
                return ResetMessageDecision.Invalid;
            var index = FindObservation(ticket.Provider, ticket.AccountId, ticket.IdentityKey, ticket.WindowId);
            if (index < 0 || _observations[index].ResetsAt != ticket.ResetsAt) return ResetMessageDecision.Invalid;
            var observation = _observations[index];
            if (now < observation.ObservedAt || now < ticket.ResetsAt) return ResetMessageDecision.Waiting;
            if (now - ticket.ResetsAt > MaximumOverdue)
            {
                _observations.RemoveAt(index);
                return ResetMessageDecision.Expired;
            }
            if (!ValidSnapshot(snapshot, ticket.Provider, ticket.AccountId, ticket.IdentityKey, now, isFresh) ||
                snapshot.FetchedAt < ticket.ResetsAt || snapshot.FetchedAt <= observation.FetchedAt)
                return ResetMessageDecision.Waiting;
            var window = FindWindow(snapshot, ticket.WindowId);
            if (window?.UsedPercent is not { } used || !ValidPercent(used)) return ResetMessageDecision.Waiting;

            // A fresh non-empty future period is already running. Do not spend usage starting it again.
            if (used > 0 && window.ResetsAt > now)
            {
                _observations.RemoveAt(index);
                ArmFuture(snapshot, ticket.WindowId, now);
                return ResetMessageDecision.AlreadyActive;
            }
            // An expired/missing reset with non-zero usage is ambiguous, so wait for clearer evidence.
            if (used != 0 || snapshot.Windows.Any(item => item is not null &&
                item.UsedPercent is { } value && ValidPercent(value) && value >= 100 &&
                (item.ResetsAt is null || item.ResetsAt > now))) return ResetMessageDecision.Waiting;

            _attempts.Add(new(ticket.Provider, ticket.AccountId, ticket.IdentityKey, ticket.WindowId,
                ticket.ResetsAt, now));
            _observations.RemoveAt(index);
            ArmFuture(snapshot, ticket.WindowId, now);
            return ResetMessageDecision.Reserved;
        }
    }

    public void CompleteAttempt(ResetMessageTicket ticket, ResetMessageOutcome outcome, DateTimeOffset now, string? message = null)
    {
        if (!Enum.IsDefined(outcome) || outcome == ResetMessageOutcome.Reserved) return;
        lock (_sync)
        {
            var index = _attempts.FindIndex(item => MatchesPeriod(item, ticket) &&
                StringComparer.Ordinal.Equals(item.AccountId, ticket.AccountId));
            if (index < 0 || _attempts[index].Outcome != ResetMessageOutcome.Reserved || now < _attempts[index].ReservedAt) return;
            _attempts[index] = _attempts[index] with { Outcome = outcome, CompletedAt = now, Message = message };
        }
    }

    public ResetMessageState GetState()
    {
        lock (_sync) return new() { Observations = [.. _observations], Attempts = [.. _attempts] };
    }

    /// <summary>
    /// Removes this local registration's arming. Keep opaque identity receipts so another registration
    /// or later re-add of the same provider account cannot resend the same period, even after clock rollback.
    /// </summary>
    public void RemoveAccount(string accountId)
    {
        lock (_sync)
        {
            _observations.RemoveAll(item => StringComparer.Ordinal.Equals(item.AccountId, accountId));
        }
    }

    private void Synchronize(AccountSettings account, bool enabled, string? targetWindowId)
        => _observations.RemoveAll(item => StringComparer.Ordinal.Equals(item.AccountId, account.Id) &&
            (!enabled || !ValidAccount(account, targetWindowId) || item.Provider != account.Provider ||
             !StringComparer.Ordinal.Equals(item.IdentityKey, account.IdentityKey) ||
             !StringComparer.Ordinal.Equals(item.WindowId, targetWindowId)));

    private void ArmFuture(UsageSnapshot snapshot, string targetWindowId, DateTimeOffset now)
    {
        var window = FindWindow(snapshot, targetWindowId);
        if (window?.ResetsAt is not { } reset || reset <= now ||
            window.UsedPercent is not { } used || !ValidPercent(used)) return;
        var observation = new ResetMessageObservation(snapshot.Provider, snapshot.AccountId, snapshot.IdentityKey!,
            targetWindowId, reset, snapshot.FetchedAt, now);
        if (HasAttempt(Ticket(observation))) return;
        var index = FindObservation(snapshot.Provider, snapshot.AccountId, snapshot.IdentityKey!, targetWindowId);
        if (index < 0) _observations.Add(observation);
        else if (snapshot.FetchedAt > _observations[index].FetchedAt && now >= _observations[index].ObservedAt)
            _observations[index] = observation with { ResetsAt = reset > _observations[index].ResetsAt ? reset : _observations[index].ResetsAt };
    }

    private static UsageWindow? FindWindow(UsageSnapshot snapshot, string windowId)
    {
        var matches = snapshot.Windows.Where(item => item is not null && StringComparer.Ordinal.Equals(item.Id, windowId)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool ValidSnapshot(UsageSnapshot snapshot, ProviderKind provider, string accountId,
        string identity, DateTimeOffset now, bool isFresh) => isFresh && snapshot.State == UsageState.Ready &&
        snapshot.Provider == provider && StringComparer.Ordinal.Equals(snapshot.AccountId, accountId) &&
        StringComparer.Ordinal.Equals(snapshot.IdentityKey, identity) && snapshot.Windows is not null &&
        snapshot.FetchedAt != default && snapshot.FetchedAt <= now && now - snapshot.FetchedAt <= MaximumSnapshotAge;

    private static bool ValidAccount(AccountSettings account, string? targetWindowId)
        => ValidKey(account.Provider, account.Id, account.IdentityKey, targetWindowId);
    private static bool ValidKey(ProviderKind provider, string? accountId, string? identity, string? windowId)
        => Enum.IsDefined(provider) && Guid.TryParseExact(accountId, "N", out _) &&
           !string.IsNullOrWhiteSpace(identity) && !string.IsNullOrWhiteSpace(windowId);
    private static bool ValidPercent(double value) => double.IsFinite(value) && value is >= 0 and <= 100;
    private int FindObservation(ProviderKind provider, string accountId, string identity, string windowId)
        => _observations.FindIndex(item => item.Provider == provider &&
            StringComparer.Ordinal.Equals(item.AccountId, accountId) && StringComparer.Ordinal.Equals(item.IdentityKey, identity) &&
            StringComparer.Ordinal.Equals(item.WindowId, windowId));
    private static ResetMessageTicket Ticket(ResetMessageObservation observation) => new(observation.Provider,
        observation.AccountId, observation.IdentityKey, observation.WindowId, observation.ResetsAt);
    private bool HasAttempt(ResetMessageTicket ticket) => _attempts.Any(item => MatchesPeriod(item, ticket));
    private static bool MatchesPeriod(ResetMessageAttempt attempt, ResetMessageTicket ticket) => attempt.Provider == ticket.Provider &&
        StringComparer.Ordinal.Equals(attempt.IdentityKey, ticket.IdentityKey) &&
        StringComparer.Ordinal.Equals(attempt.WindowId, ticket.WindowId) && attempt.ResetsAt == ticket.ResetsAt;
    private static bool IsValidObservation(ResetMessageObservation? item) => item is not null &&
        ValidKey(item.Provider, item.AccountId, item.IdentityKey, item.WindowId) &&
        item.FetchedAt != default && item.FetchedAt <= item.ObservedAt && item.ResetsAt > item.ObservedAt &&
        item.ObservedAt - item.FetchedAt <= MaximumSnapshotAge;
    private static bool IsValidAttempt(ResetMessageAttempt? item) => item is not null &&
        ValidKey(item.Provider, item.AccountId, item.IdentityKey, item.WindowId) && Enum.IsDefined(item.Outcome) &&
        item.ResetsAt != default && item.ReservedAt >= item.ResetsAt && item.ReservedAt - item.ResetsAt <= MaximumOverdue &&
        (item.CompletedAt is null || item.CompletedAt >= item.ReservedAt);
}
