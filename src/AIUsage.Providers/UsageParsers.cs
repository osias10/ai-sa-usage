using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIUsage.Core;

namespace AIUsage.Providers;

/// <summary>Parses the structured /usage assistant event, never the model's text response.</summary>
public static class ClaudeUsageParser
{
    public static UsageSnapshot Parse(string json, AccountSettings account, DateTimeOffset? fetchedAt = null)
    {
        JsonElement? report = null;
        foreach (var item in ProviderJson.ReadDocuments(json))
        {
            if (ProviderJson.String(item, "type") == "assistant" && item.TryGetProperty("usage_report", out var usage))
                report = usage;
            if (ProviderJson.String(item, "type") == "result" && ProviderJson.Boolean(item, "is_error") == true)
                throw ProviderErrors.FromText(ProviderJson.String(item, "result") ?? "usage failed");
        }
        if (report is not { ValueKind: JsonValueKind.Object } value)
            throw ProviderJson.Unsupported("Claude가 구조화된 사용량을 반환하지 않았습니다. 연결 구성요소를 업데이트해 주세요.");
        if (!value.TryGetProperty("rate_limits", out var rateLimits) || rateLimits.ValueKind == JsonValueKind.Null)
            throw ProviderJson.Unavailable("Claude 로그인은 확인됐지만 서버에서 사용량을 받지 못했습니다. 잠시 후 다시 새로고침해 주세요.");
        if (rateLimits.ValueKind != JsonValueKind.Object) throw ProviderJson.Unsupported();
        if (!rateLimits.TryGetProperty("limits", out var limits) || limits.ValueKind == JsonValueKind.Null)
            throw ProviderJson.Unavailable("Claude 서버에서 최신 사용량을 받지 못했습니다.");
        if (limits.ValueKind != JsonValueKind.Array) throw ProviderJson.Unsupported();
        List<UsageWindow> windows = [];
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in limits.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) throw ProviderJson.Unsupported();
            var kind = ProviderJson.String(row, "kind") ?? throw ProviderJson.Unsupported();
            var group = ProviderJson.String(row, "group") ?? kind;
            var scopeNames = new List<string>();
            if (row.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object)
                foreach (var key in new[] { "model", "surface" })
                    if (scope.TryGetProperty(key, out var scoped) && scoped.ValueKind == JsonValueKind.Object &&
                        ProviderJson.String(scoped, "display_name") is { Length: > 0 } name) scopeNames.Add(name);
            var title = kind switch { "session" => "현재 세션", "weekly_all" => "주간 전체", "weekly_scoped" => "주간", _ => group };
            if (scopeNames.Count > 0) title += " · " + string.Join(" · ", scopeNames);
            var idBase = kind + ":" + string.Join(":", scopeNames);
            occurrences.TryGetValue(idBase, out var ordinal);
            occurrences[idBase] = ordinal + 1;
            windows.Add(new UsageWindow(idBase + ":" + ordinal, title,
                ProviderJson.Percent(row, "percent"), ProviderJson.IsoTimestamp(row, "resets_at"),
                ProviderJson.Boolean(row, "is_active") == true));
        }
        return ProviderJson.Snapshot(ProviderKind.Claude, account, fetchedAt, windows);
    }
}

public static class CodexUsageParser
{
    /// <summary>Accepts the result object of account/rateLimits/read, or its JSON-RPC response.</summary>
    public static UsageSnapshot Parse(string json, AccountSettings account, DateTimeOffset? fetchedAt = null)
    {
        var root = ProviderJson.ReadObject(json);
        if (root.TryGetProperty("error", out var error)) throw ProviderErrors.FromText(error.ToString());
        if (root.TryGetProperty("result", out var result)) root = result;
        if (root.ValueKind != JsonValueKind.Object) throw ProviderJson.Unsupported();
        List<UsageWindow> windows = [];
        var plan = account.Plan;
        if (root.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object && byId.EnumerateObject().Any())
        {
            foreach (var bucket in byId.EnumerateObject()) AddBucket(bucket.Value, bucket.Name);
        }
        else if (root.TryGetProperty("rateLimits", out var single) && single.ValueKind == JsonValueKind.Object)
            AddBucket(single, ProviderJson.String(single, "limitId") ?? "codex");
        else throw ProviderJson.Unavailable("Codex 서버에서 구독 사용량을 받지 못했습니다.");
        return ProviderJson.Snapshot(ProviderKind.Codex, account, fetchedAt, windows) with { Plan = plan };

        void AddBucket(JsonElement bucket, string id)
        {
            if (bucket.ValueKind != JsonValueKind.Object) throw ProviderJson.Unsupported();
            plan ??= ProviderJson.String(bucket, "planType");
            var name = ProviderJson.String(bucket, "limitName") ?? id;
            foreach (var slot in new[] { "primary", "secondary" })
            {
                if (!bucket.TryGetProperty(slot, out var window) || window.ValueKind == JsonValueKind.Null) continue;
                if (window.ValueKind != JsonValueKind.Object) throw ProviderJson.Unsupported();
                var duration = ProviderJson.Number(window, "windowDurationMins");
                string label = duration switch
                {
                    300 => "5시간", 10080 => "주간", >= 1440 when duration % 1440 == 0 => $"{duration / 1440:0}일",
                    >= 60 when duration % 60 == 0 => $"{duration / 60:0}시간", > 0 => $"{duration:0}분",
                    _ => slot == "primary" ? "기본 한도" : "추가 한도"
                };
                if (id != "codex" || name != "codex") label = name + " · " + label;
                windows.Add(new UsageWindow(id + ":" + slot, label, ProviderJson.Percent(window, "usedPercent"),
                    ProviderJson.UnixTimestamp(window, "resetsAt"), slot == "primary" && id == "codex"));
            }
        }
    }
}

public static partial class AntigravityUsageParser
{
    /// <summary>Reads the pinned CLI's native /usage command result, never model tokens or response text.</summary>
    public static UsageSnapshot Parse(string json, AccountSettings account, DateTimeOffset? fetchedAt = null)
    {
        var root = ProviderJson.ReadObject(json);
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null && error.ToString().Length > 0)
            throw ProviderErrors.FromText(error.ToString());
        if (ProviderJson.String(root, "status") != "SUCCESS" || ProviderJson.Number(root, "num_turns") != 0 ||
            !root.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.Object ||
            ProviderJson.String(command, "name") != "usage" || !command.TryGetProperty("data", out var quotas) || quotas.ValueKind != JsonValueKind.Object)
            throw ProviderJson.Unsupported("Antigravity의 사용량 응답 형식이 예상과 다릅니다. 연결 구성요소를 업데이트해 주세요.");
        if (!quotas.TryGetProperty("groups", out var groups) || groups.ValueKind == JsonValueKind.Null)
            throw ProviderJson.Unavailable("Antigravity 서버에서 사용량을 받지 못했습니다.");
        if (groups.ValueKind != JsonValueKind.Array) throw ProviderJson.Unsupported();
        List<UsageWindow> windows = [];
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object) throw ProviderJson.Unsupported();
            var groupName = ProviderJson.String(group, "name") ?? throw ProviderJson.Unsupported();
            if (!group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array) throw ProviderJson.Unsupported();
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object) throw ProviderJson.Unsupported();
                var name = ProviderJson.String(bucket, "name") ?? throw ProviderJson.Unsupported();
                var remaining = ProviderJson.Number(bucket, "remaining_fraction");
                if (remaining is < 0 or > 1) throw ProviderJson.Unsupported();
                if (bucket.TryGetProperty("disabled", out var disabledValue) && disabledValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw ProviderJson.Unsupported();
                var disabled = ProviderJson.Boolean(bucket, "disabled") == true;
                var window = ProviderJson.String(bucket, "window");
                var label = string.Join(" · ", new[] { groupName, name, window }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
                if (disabled) label += " (사용 안 함)";
                var id = ProviderJson.String(bucket, "id") ?? groupName + ":" + name + ":" + window;
                occurrences.TryGetValue(id, out var ordinal);
                occurrences[id] = ordinal + 1;
                windows.Add(new UsageWindow(id + ":" + ordinal, label,
                    !disabled && remaining.HasValue ? Math.Round((1 - remaining.Value) * 100, 4) : null,
                    ProviderJson.IsoTimestamp(bucket, "reset_time"), windows.Count == 0));
            }
        }
        return ProviderJson.Snapshot(ProviderKind.Antigravity, account, fetchedAt, windows);
    }

    /// <summary>Parses only the native auth command's explicit successful account line; no credential files are read.</summary>
    public static ConnectionResult ParseIdentity(string output, AccountSettings? expectedAccount = null)
    {
        var matches = AuthenticatedLine().Matches(BrowserCliLogin.StripTerminalEscapes(output));
        var identities = matches.Select(x => x.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (identities.Length != 1 || !System.Net.Mail.MailAddress.TryCreate(identities[0], out var address) ||
            !string.Equals(address.Address, identities[0], StringComparison.OrdinalIgnoreCase))
            throw ProviderJson.Unsupported("Antigravity 로그인 계정을 확인하지 못했습니다. 연결 구성요소를 업데이트하거나 다시 연결해 주세요.");
        var identity = ProviderIdentity.Create(ProviderKind.Antigravity, address.Address);
        if (expectedAccount != null) ProviderIdentity.EnsureMatches(expectedAccount, identity);
        return new(identity, address.Address, expectedAccount?.Plan);
    }
    [GeneratedRegex(@"(?m)^(?:Already|Successfully) authenticated as ([^\s<>]+)\.[ \t]*\r?$", RegexOptions.CultureInvariant)]
    private static partial Regex AuthenticatedLine();
}

internal static class ProviderJson
{
    public static JsonElement ReadObject(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Unsupported();
            return document.RootElement.Clone();
        }
        catch (JsonException) { throw Unsupported(); }
    }

    public static IEnumerable<JsonElement> ReadDocuments(string text)
    {
        // CLI stream-json is NDJSON. Also accept a single JSON array for recorded fixtures.
        JsonDocument? whole = null;
        try { whole = JsonDocument.Parse(text); } catch (JsonException) { }
        if (whole != null)
        {
            using (whole)
            {
                if (whole.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var value in whole.RootElement.EnumerateArray()) yield return value.Clone();
                else if (whole.RootElement.ValueKind == JsonValueKind.Object) yield return whole.RootElement.Clone();
                else throw Unsupported();
            }
            yield break;
        }
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return ReadObject(line);
    }

    public static string? String(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    public static bool? Boolean(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.GetBoolean() : null;
    public static double? Number(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out var item) || item.ValueKind == JsonValueKind.Null) return null;
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var number) || !double.IsFinite(number)) throw Unsupported();
        return number;
    }
    public static double? Percent(JsonElement value, string key)
    {
        var number = Number(value, key);
        if (number is < 0 or > 100) throw Unsupported();
        return number;
    }
    public static DateTimeOffset? IsoTimestamp(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out var item) || item.ValueKind == JsonValueKind.Null) return null;
        if (item.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(item.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) throw Unsupported();
        return date;
    }
    public static DateTimeOffset? UnixTimestamp(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out var item) || item.ValueKind == JsonValueKind.Null) return null;
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var seconds)) throw Unsupported();
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { throw Unsupported(); }
    }
    public static UsageSnapshot Snapshot(ProviderKind provider, AccountSettings account, DateTimeOffset? at, List<UsageWindow> windows)
    {
        if (windows.Count == 0) throw Unavailable("표시할 구독 사용량 항목을 서버에서 받지 못했습니다.");
        return new(provider, account.Id, account.IdentityKey, account.Plan, at ?? DateTimeOffset.UtcNow, windows,
            windows.Any(x => x.UsedPercent.HasValue) ? UsageState.Ready : UsageState.Unavailable,
            windows.Any(x => x.UsedPercent.HasValue) ? null : "사용량 수치가 아직 제공되지 않았습니다.");
    }
    public static ProviderException Unsupported(string message = "사용량 응답 형식이 변경되었습니다. 연결 구성요소를 업데이트해 주세요.") => new(UsageState.UpdateRequired, message);
    public static ProviderException Unavailable(string message) => new(UsageState.Unavailable, message);
}

internal static class ProviderIdentity
{
    public static string Create(ProviderKind provider, string identity) => provider.ToString().ToLowerInvariant() + ":" + identity.Trim().ToLowerInvariant();
    public static void EnsureMatches(AccountSettings account, string identity)
    {
        if (account.IdentityKey != null && !string.Equals(account.IdentityKey, identity, StringComparison.Ordinal))
            throw new ProviderException(UsageState.SignInRequired, "로그인된 계정이 등록된 계정과 다릅니다. 계정을 다시 연결해 주세요.") { IdentityChanged = true };
    }
}

internal static partial class ProviderErrors
{
    public static void EnsureSuccess(ProcessResult result)
    {
        if (result.ExitCode != 0) throw FromText(result.StandardError + "\n" + result.StandardOutput);
    }
    public static ProviderException FromText(string text)
    {
        if (Contains("429", "rate limit", "rate_limit", "too many requests"))
            return new(UsageState.RateLimited, "조회 요청이 많아 잠시 기다려야 합니다.", RetryDelay(text) ?? TimeSpan.FromMinutes(5));
        if (Contains("401", "unauthorized", "authentication", "not logged", "not signed", "login required", "sign in", "oauth"))
            return new(UsageState.SignInRequired, "로그인이 필요합니다. 계정을 다시 연결해 주세요.");
        if (Contains("ENOTFOUND", "ECONN", "network", "timed out", "timeout", "connection", "DNS"))
            return new(UsageState.Offline, "서비스에 연결할 수 없습니다. 네트워크를 확인해 주세요.");
        if (Contains("unknown option", "unknown command", "unsupported", "method not found", "-32601"))
            return new(UsageState.UpdateRequired, "현재 연결 구성요소가 이 기능을 지원하지 않습니다. 업데이트해 주세요.");
        return new(UsageState.Unavailable, "서비스에서 사용량을 가져오지 못했습니다. 잠시 후 다시 시도해 주세요.");
        bool Contains(params string[] terms) => terms.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    private static TimeSpan? RetryDelay(string text)
    {
        TimeSpan? longest = null;
        var now = DateTimeOffset.UtcNow;
        TryDocument(text);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.TrimStart().StartsWith('{')) TryDocument(line);
            var match = RetryHeader().Match(line);
            if (match.Success) Add(Parse(match.Groups[1].Value.Trim().Trim('"'), false));
        }
        return longest;

        void TryDocument(string value)
        {
            try { using var document = JsonDocument.Parse(value); Visit(document.RootElement, 0); }
            catch (JsonException) { }
        }
        void Visit(JsonElement value, int depth)
        {
            if (depth > 6) return;
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject().Take(64))
                {
                    var key = property.Name.Replace("_", "").Replace("-", "").ToLowerInvariant();
                    if (key is "retryafter" or "retryafterseconds" or "retryafterms" or "retryaftermilliseconds")
                    {
                        if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                            Add(Parse(property.Value.ToString(), key is "retryafterms" or "retryaftermilliseconds"));
                    }
                    else Visit(property.Value, depth + 1);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var child in value.EnumerateArray().Take(64)) Visit(child, depth + 1);
        }
        TimeSpan? Parse(string value, bool milliseconds)
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number >= 0)
            {
                var seconds = milliseconds ? number / 1000 : number;
                if (seconds < TimeSpan.MaxValue.TotalSeconds) return TimeSpan.FromSeconds(seconds);
            }
            if (!milliseconds && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
                return when > now ? when - now : TimeSpan.Zero;
            return null;
        }
        void Add(TimeSpan? delay)
        { if (delay.HasValue && (!longest.HasValue || delay > longest)) longest = delay; }
    }
    [GeneratedRegex(@"(?i)\bretry[-_ ]?after(?:[-_ ]?seconds)?\s*[:=]\s*(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex RetryHeader();
}
