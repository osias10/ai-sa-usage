namespace AIUsage.Core;

public static class UsageFormatting
{
    public static string Remaining(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is null) return "초기화 시간 미제공";
        var remaining = resetsAt.Value - now;
        if (remaining <= TimeSpan.Zero) return "초기화 확인 중";
        if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays}일 {remaining.Hours}시간";
        if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours}시간 {remaining.Minutes}분";
        if (remaining.TotalMinutes >= 1) return $"{(int)remaining.TotalMinutes}분 {remaining.Seconds}초";
        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))}초";
    }
    public static UsageWindow? SelectWindow(AccountSettings account, UsageSnapshot? snapshot)
        => snapshot?.Windows.FirstOrDefault(w => w.Id == account.SelectedWindowId)
            ?? snapshot?.Windows.FirstOrDefault(w => w.IsPrimary)
            ?? snapshot?.Windows.OrderByDescending(w => w.UsedPercent).FirstOrDefault();

    /// <summary>
    /// Migrates an account's legacy widget defaults once their IDs are known, and retains the
    /// last known names of selected limits. Returns true when settings should be saved.
    /// </summary>
    public static bool EnsureWidgetWindows(AccountSettings account, UsageSnapshot? snapshot)
    {
        var changed = NormalizeWidgetWindows(account, snapshot);
        if (account.WidgetWindows is not null) return changed;

        var windows = AvailableWindows(account, snapshot);
        if (account.Provider == ProviderKind.Claude)
        {
            var session = windows.FirstOrDefault(window => IsClaudeAggregate(window.Id, "session"));
            var weekly = windows.FirstOrDefault(window => IsClaudeAggregate(window.Id, "weekly_all"));
            account.WidgetWindows =
            [
                new(session?.Id ?? "session::0", session?.Name ?? "현재 세션"),
                new(weekly?.Id ?? "weekly_all::0", weekly?.Name ?? "주간 전체")
            ];
            return true;
        }

        // Never persist the display-only unavailable placeholder as a user's selection.
        var selected = windows.FirstOrDefault(window => window.Id == account.SelectedWindowId)
            ?? windows.FirstOrDefault(window => window.IsPrimary)
            ?? windows.FirstOrDefault();
        if (selected is null) return changed;
        account.WidgetWindows = [new(selected.Id, selected.Name)];
        return true;
    }

    /// <summary>Returns exactly the chosen limits; a missing selection keeps its name and unknown values.</summary>
    public static IReadOnlyList<UsageWindow> SelectCompactWindows(AccountSettings account, UsageSnapshot? snapshot)
    {
        EnsureWidgetWindows(account, snapshot);
        if (account.WidgetWindows is null) return [new("compact:unavailable", "사용량", null, null)];
        var windows = AvailableWindows(account, snapshot).ToDictionary(window => window.Id, StringComparer.Ordinal);
        return account.WidgetWindows.Select(selection => windows.TryGetValue(selection.Id, out var window)
            ? window : new UsageWindow(selection.Id, selection.Name, null, null)).ToArray();
    }

    // Loading settings must leave null selections unmigrated until cached/live windows are available.
    internal static bool NormalizeWidgetWindows(AccountSettings account, UsageSnapshot? snapshot = null)
    {
        if (account.WidgetWindows is null) return false;
        var windows = AvailableWindows(account, snapshot);
        var byId = windows.ToDictionary(window => window.Id, StringComparer.Ordinal);
        var normalized = account.WidgetWindows
            .Where(selection => selection is not null && !string.IsNullOrWhiteSpace(selection.Id) && selection.Id != "compact:unavailable")
            .DistinctBy(selection => selection.Id, StringComparer.Ordinal)
            .Select(selection => new WidgetWindowSelection(selection.Id,
                byId.TryGetValue(selection.Id, out var window) ? window.Name :
                    string.IsNullOrWhiteSpace(selection.Name) ? "선택한 한도" : selection.Name))
            .ToList();
        if (normalized.Count == 0)
        {
            account.WidgetWindows = null;
            return true;
        }
        if (account.Provider == ProviderKind.Claude) normalized = OrderClaudeSelections(normalized, windows).ToList();
        if (account.WidgetWindows.SequenceEqual(normalized)) return false;
        account.WidgetWindows = normalized;
        return true;
    }

    private static IReadOnlyList<UsageWindow> AvailableWindows(AccountSettings account, UsageSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.Provider != account.Provider || snapshot.AccountId != account.Id ||
            account.IdentityKey is not null && account.IdentityKey != snapshot.IdentityKey) return [];
        return (snapshot.Windows ?? []).Where(window => window is not null && !string.IsNullOrWhiteSpace(window.Id) &&
                !string.IsNullOrWhiteSpace(window.Name) && window.Id != "compact:unavailable")
            .DistinctBy(window => window.Id, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<WidgetWindowSelection> OrderClaudeSelections(
        IReadOnlyList<WidgetWindowSelection> selections, IReadOnlyList<UsageWindow> windows)
    {
        var positions = windows.Select((window, index) => (window.Id, index))
            .ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        foreach (var group in selections.GroupBy(selection => ClaudeOrder(selection.Id)).OrderBy(group => group.Key))
        {
            var known = new Queue<WidgetWindowSelection>(group.Where(selection => positions.ContainsKey(selection.Id))
                .OrderBy(selection => positions[selection.Id]));
            // Missing selections retain their slots instead of silently changing the first tray meter.
            foreach (var selection in group)
                yield return positions.ContainsKey(selection.Id) ? known.Dequeue() : selection;
        }
    }

    private static int ClaudeOrder(string id) => IsClaudeAggregate(id, "session") ? 0 :
        IsClaudeAggregate(id, "weekly_all") ? 1 : 2;

    public static string CompactName(UsageWindow window)
    {
        if (IsClaudeAggregate(window.Id, "session")) return "세션";
        if (IsClaudeAggregate(window.Id, "weekly_all")) return "주간";
        var name = window.Name.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "사용량";
        return name switch
        {
            "현재 세션" => "세션",
            "주간 전체" or "주간 한도" => "주간",
            _ when name.EndsWith(" 한도", StringComparison.Ordinal) => name[..^3],
            _ => name
        };
    }

    public static string CompactRemaining(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is null) return "—";
        var remaining = resetsAt.Value - now;
        if (remaining <= TimeSpan.Zero) return "확인 중";
        if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays}일{remaining.Hours}시간";
        if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours}시간{remaining.Minutes}분";
        if (remaining.TotalMinutes >= 1) return $"{(int)remaining.TotalMinutes}분{remaining.Seconds:00}초";
        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))}초";
    }

    private static bool IsClaudeAggregate(string id, string kind)
    {
        // ClaudeUsageParser emits kind + ':' + scope labels + ':' + ordinal.
        // The empty scope is intentional: a model/surface row must never replace the aggregate.
        var prefix = kind + "::";
        return id.StartsWith(prefix, StringComparison.Ordinal) && id.Length > prefix.Length &&
            id.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
    }
}
