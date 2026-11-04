namespace AIUsage.Core;

/// <summary>Checks the requested quota values independently of optional credit data.</summary>
internal static class UsageCompleteness
{
    public static string? FindIssue(AccountSettings account, UsageSnapshot snapshot, UsageSnapshot? lastComplete)
    {
        // Match the display's first-occurrence rule for older caches containing duplicate IDs.
        var windows = snapshot.Windows.DistinctBy(window => window.Id, StringComparer.Ordinal)
            .ToDictionary(window => window.Id, StringComparer.Ordinal);
        var missing = new Dictionary<string, string>(StringComparer.Ordinal);
        var selected = account.WidgetWindows?.ToList() ?? [];
        if (selected.Count == 0 && account.SelectedWindowId is { Length: > 0 } legacy)
            selected.Add(new(legacy, "선택한 한도"));
        if (account.TrayWindow is { } tray) selected.Add(tray);
        foreach (var selection in selected)
            if (!windows.TryGetValue(selection.Id, out var window) || !window.IsDisabled && !HasValue(window))
                missing.TryAdd(selection.Id, selection.Name);

        if (lastComplete is not null && lastComplete.Provider == snapshot.Provider &&
            lastComplete.AccountId == snapshot.AccountId && lastComplete.IdentityKey == snapshot.IdentityKey)
        {
            foreach (var old in lastComplete.Windows.Where(HasValue))
                // A removed, unselected model is allowed; a returned value becoming unknown is not.
                if (windows.TryGetValue(old.Id, out var current) && !current.IsDisabled && !HasValue(current))
                    missing.TryAdd(old.Id, current.Name);
            if (windows.Count == 0 && lastComplete.Windows.Any(HasValue))
                return "사용량 수치를 확인하지 못했습니다. 크레딧과 별도로 다시 조회합니다.";
        }
        if (missing.Count > 0)
            return "일부 사용량을 확인하지 못했습니다: " + string.Join(" · ", missing.Values.Take(3)) + (missing.Count > 3 ? " 외" : "") + ".";
        if (windows.Values.Any(window => !window.IsDisabled) && !windows.Values.Any(HasValue))
            return "사용량 수치를 확인하지 못했습니다.";
        if (windows.Count == 0 && !HasCreditInformation(snapshot.Credits))
            return "사용량 수치를 확인하지 못했습니다.";
        return null;
    }

    private static bool HasValue(UsageWindow window) => !window.IsDisabled && window.UsedPercent is { } value &&
        double.IsFinite(value) && value is >= 0 and <= 100;

    private static bool HasCreditInformation(IReadOnlyList<AccountCredit>? credits) => credits?.Any(credit =>
        CreditFormatting.IsValid(credit) && (credit.Balance.HasValue || credit.Used.HasValue || credit.Limit.HasValue ||
            credit.Unlimited || credit.IsEnabled == false || credit.HasCredits.HasValue)) == true;
}
