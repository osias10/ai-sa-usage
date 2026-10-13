namespace AIUsage.Core;

public sealed record UsageAlertNotification(string Title, string Message, bool IsWarning);

/// <summary>Revalidates queued alerts against current settings and the latest successful reading.</summary>
public static class UsageAlertFormatting
{
    public static UsageAlertNotification? Format(AccountSettings account, AccountUsageStatus? status,
        IReadOnlyList<UsageAlert> pending, DateTimeOffset now)
    {
        if (pending.Count == 0 || pending[0] is not { } first || !Enum.IsDefined(first.Kind) ||
            !Enum.IsDefined(account.Provider) || !Guid.TryParseExact(account.Id, "N", out _) ||
            string.IsNullOrWhiteSpace(account.IdentityKey) || !MatchesAccount(first, account)) return null;

        var snapshot = status?.Snapshot;
        if (status is null || status.State != UsageState.Ready || !StringComparer.Ordinal.Equals(status.AccountId, account.Id) ||
            snapshot is null || snapshot.State != UsageState.Ready || snapshot.Provider != account.Provider ||
            !StringComparer.Ordinal.Equals(snapshot.AccountId, account.Id) ||
            !StringComparer.Ordinal.Equals(snapshot.IdentityKey, account.IdentityKey) || snapshot.Windows is null ||
            snapshot.FetchedAt > now || now - snapshot.FetchedAt > UsageAlertEngine.MaximumSnapshotAge) return null;

        var resetNotification = first.Kind == UsageAlertKind.Reset;
        var lines = new List<string>();
        var includedWindows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alert in pending)
        {
            if (alert is null || alert.Kind != first.Kind || !MatchesAccount(alert, account) ||
                string.IsNullOrWhiteSpace(alert.WindowId) || includedWindows.Contains(alert.WindowId)) continue;
            var window = snapshot.Windows.FirstOrDefault(window => window is not null &&
                StringComparer.Ordinal.Equals(window.Id, alert.WindowId) && window.ResetsAt == alert.ResetsAt);
            if (window is null || string.IsNullOrWhiteSpace(window.Name) || window.UsedPercent is not { } used ||
                !double.IsFinite(used) || used is < 0 or > 100 || window.ResetsAt is { } reset && reset <= now) continue;

            var rules = account.UsageAlerts ?? [];
            if (resetNotification)
            {
                // A reset is a known new period, independent of the threshold alert option/value.
                if (window.ResetsAt is null || !rules.Any(rule => rule is not null && rule.ResetEnabled &&
                        StringComparer.Ordinal.Equals(rule.WindowId, window.Id))) continue;
                lines.Add($"{window.Name} · 초기화됨\n사용 {used:0.#}%");
            }
            else
            {
                if (!rules.Any(rule => rule is not null && rule.Enabled && rule.ThresholdPercent is >= 1 and <= 100 &&
                        StringComparer.Ordinal.Equals(rule.WindowId, window.Id) && used >= rule.ThresholdPercent)) continue;
                lines.Add($"{window.Name} · 사용 {used:0.#}%\n{UsageFormatting.Remaining(window.ResetsAt, now)}");
            }
            includedWindows.Add(window.Id);
        }

        if (lines.Count == 0) return null;
        return new(resetNotification ? "AI SA · 초기화 알림" : "AI SA · 사용량 알림",
            $"{account.Provider} · {account.Alias}\n" + string.Join("\n", lines), !resetNotification);
    }

    private static bool MatchesAccount(UsageAlert alert, AccountSettings account)
        => alert.Provider == account.Provider && StringComparer.Ordinal.Equals(alert.AccountId, account.Id) &&
            StringComparer.Ordinal.Equals(alert.IdentityKey, account.IdentityKey);
}
