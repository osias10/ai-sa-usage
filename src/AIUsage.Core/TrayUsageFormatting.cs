using System.Globalization;
using System.Text;

namespace AIUsage.Core;

public sealed record TrayUsageReading(string? AccountId, string? WindowId, string IconText, string Tooltip, bool IsWarning);

/// <summary>Builds one tray reading without treating missing or stale quota data as zero usage.</summary>
public static class TrayUsageFormatting
{
    public static TrayUsageReading GetReading(AppSettings settings, IEnumerable<AccountUsageStatus> statuses, DateTimeOffset now)
    {
        var ordered = settings.Accounts.OrderBy(account => account.Order).ToArray();
        var account = ordered.FirstOrDefault(account => account.Id == settings.TrayAccountId)
            ?? ordered.FirstOrDefault(account => account.Pinned)
            ?? ordered.FirstOrDefault();
        if (account is null) return new(null, null, "—", "AI SA · 연결된 계정 없음", false);

        var status = statuses.FirstOrDefault(value => value.AccountId == account.Id);
        var candidate = status?.Snapshot;
        var matchesAccount = candidate is not null && candidate.AccountId == account.Id &&
            candidate.Provider == account.Provider && !string.IsNullOrWhiteSpace(candidate.IdentityKey) &&
            string.Equals(candidate.IdentityKey, account.IdentityKey, StringComparison.Ordinal);
        var snapshot = matchesAccount ? candidate : null;
        var window = account.TrayWindow is { } selected
            ? snapshot?.Windows.FirstOrDefault(value => value.Id == selected.Id)
                ?? new UsageWindow(selected.Id, string.IsNullOrWhiteSpace(selected.Name) ? "선택한 한도" : selected.Name, null, null)
            : snapshot is null ? null : UsageFormatting.SelectCompactWindows(account, snapshot).FirstOrDefault();
        var stale = snapshot is not null && now - snapshot.FetchedAt >
            TimeSpan.FromMinutes(Math.Max(account.RefreshMinutes * 2d, 10));
        var future = snapshot is not null && snapshot.FetchedAt > now;
        var expired = window?.ResetsAt is { } reset && reset <= now;
        var used = window?.UsedPercent;
        var hasMetric = used is { } percent && double.IsFinite(percent) && percent is >= 0 and <= 100;
        var canShow = status?.State == UsageState.Ready && snapshot?.State == UsageState.Ready &&
            !stale && !future && !expired && hasMetric;
        var mode = settings.ShowRemaining ? "잔여" : "사용";
        var displayed = canShow ? settings.ShowRemaining ? 100 - used!.Value : used!.Value : (double?)null;
        var icon = displayed is { } amount
            ? Math.Round(amount, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)
            : "—";
        var threshold = account.UsageAlerts.FirstOrDefault(rule => rule.Enabled && rule.WindowId == window?.Id)?.ThresholdPercent ?? 80;
        var warning = canShow && used!.Value >= Math.Clamp(threshold, 1, 100);

        string state;
        if (candidate is not null && !matchesAccount) state = "계정 정보 확인 필요";
        else if (status?.State is { } currentState && currentState != UsageState.Ready) state = StateText(currentState);
        else if (snapshot is null) state = "사용량 미조회";
        else if (future) state = "조회 시간 확인 필요";
        else if (stale) state = "오래된 조회 결과";
        else if (expired) state = "초기화 확인 중";
        else if (!hasMetric || snapshot.State != UsageState.Ready) state = "사용량 미제공";
        else state = status?.IsRefreshing == true ? "조회 중" : "정상";

        var provider = account.Provider switch { ProviderKind.Claude => "Claude", ProviderKind.Codex => "Codex", _ => "Antigravity" };
        var alias = string.IsNullOrWhiteSpace(account.Alias) ? "계정" : SingleLine(account.Alias);
        var label = window is null ? "사용량" : SingleLine(window.Name);
        var exactPercent = displayed is { } value ? value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—";
        var updated = snapshot is null ? "조회 기록 없음" : "마지막 갱신 " + LocalTime(snapshot.FetchedAt);
        var resetText = window?.ResetsAt is { } resetsAt ? "초기화 " + LocalTime(resetsAt) : "초기화 시간 미제공";
        var measurement = mode + " " + exactPercent;
        // Keep all numeric/status/time information, shrinking only long account and meter names when necessary.
        var nameBudget = 127 - (provider.Length + measurement.Length + state.Length + resetText.Length + updated.Length + 10);
        if (alias.Length + label.Length > nameBudget)
        {
            var aliasBudget = Math.Min(alias.Length, Math.Max(nameBudget - label.Length, nameBudget / 3));
            alias = LimitText(alias, aliasBudget);
            label = LimitText(label, nameBudget - alias.Length);
        }
        var tooltip = string.Join('\n',
            provider + " · " + alias,
            label + " · " + measurement,
            state, resetText, updated);
        return new(account.Id, window?.Id, icon, LimitText(tooltip, 127), warning);
    }

    private static string StateText(UsageState state) => state switch
    {
        UsageState.SignInRequired => "로그인 필요",
        UsageState.Offline => "오프라인 · 이전 값",
        UsageState.RateLimited => "조회 제한 · 이전 값",
        UsageState.UpdateRequired => "연결 도구 업데이트 필요",
        _ => "사용량 미제공"
    };

    private static string LocalTime(DateTimeOffset value) => value.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);
    private static string SingleLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

    // NotifyIcon.Text limits UTF-16 length; iterating runes also avoids splitting surrogate pairs.
    private static string LimitText(string value, int maximumLength)
    {
        if (value.Length <= maximumLength) return value;
        var result = new StringBuilder(maximumLength);
        foreach (var rune in value.EnumerateRunes())
        {
            if (result.Length + rune.Utf16SequenceLength > maximumLength - 1) break;
            result.Append(rune.ToString());
        }
        return result.Append('…').ToString();
    }
}
