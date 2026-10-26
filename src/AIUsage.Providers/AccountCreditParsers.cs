using System.Globalization;
using System.Text.Json;
using AIUsage.Core;

namespace AIUsage.Providers;

/// <summary>Reads only optional, explicit account credit fields. Missing amounts remain unknown.</summary>
public static class AccountCreditParsers
{
    public static AccountCredit? Claude(JsonElement rateLimits)
    {
        if (!ObjectProperty(rateLimits, "extra_usage", out var extra) ||
            Boolean(extra, "is_enabled") is not { } enabled)
            return null;

        var currency = Currency(extra);
        var unit = currency == "USD" ? "USD" : currency == null
            ? "최소 단위 (통화 미제공)" : currency + " 최소 단위";
        var divisor = currency == "USD" ? 100m : 1m;
        return new("claude:extra_usage", "추가 사용량", unit,
            Used: Scale(Number(extra, "used_credits"), divisor),
            Limit: Scale(Number(extra, "monthly_limit"), divisor), IsEnabled: enabled);
    }

    public static AccountCredit? CodexBucket(JsonElement bucket, string id)
    {
        if (!ObjectProperty(bucket, "credits", out var credits) ||
            Boolean(credits, "hasCredits") is not { } hasCredits ||
            Boolean(credits, "unlimited") is not { } unlimited)
            return null;

        decimal? balance = null;
        if (credits.TryGetProperty("balance", out var value) && value.ValueKind == JsonValueKind.String)
            balance = NonnegativeDecimal(value.GetString());
        var name = id == "codex" ? "크레딧" : (String(bucket, "limitName") ?? id) + " 크레딧";
        return new(id + ":credits", name, "크레딧", Balance: balance,
            HasCredits: hasCredits, Unlimited: unlimited);
    }

    public static AccountCredit? CodexResetCredits(JsonElement root)
    {
        if (!ObjectProperty(root, "rateLimitResetCredits", out var reset) ||
            !reset.TryGetProperty("availableCount", out var count) ||
            count.ValueKind != JsonValueKind.Number || !count.TryGetInt64(out var available) || available < 0)
            return null;
        // Details may be capped or absent; only availableCount describes the account's balance.
        return new("codex:reset-credits", "초기화 크레딧", "회", Balance: available);
    }

    public static AccountCredit? Antigravity(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null && error.ToString().Length > 0 ||
            !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() != "SUCCESS" ||
            !root.TryGetProperty("num_turns", out var turns) || turns.ValueKind != JsonValueKind.Number ||
            !turns.TryGetInt64(out var turnCount) || turnCount != 0 ||
            !ObjectProperty(root, "command", out var command) ||
            !command.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != "credits" ||
            !ObjectProperty(command, "data", out var data) ||
            !data.TryGetProperty("remaining_credits", out var remaining) || remaining.ValueKind != JsonValueKind.Number ||
            !remaining.TryGetInt64(out var balance) || balance < 0)
            return null;

        return new("antigravity:credits", "크레딧", "크레딧", Balance: balance);
    }

    private static bool ObjectProperty(JsonElement element, string name, out JsonElement property)
    {
        property = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out property) &&
            property.ValueKind == JsonValueKind.Object;
    }

    private static bool? Boolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        } : null;

    private static decimal? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? NonnegativeDecimal(value.GetRawText()) : null;

    private static decimal? NonnegativeDecimal(string? raw)
    {
        if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) || amount < 0)
            return null;
        // Decimal parsing can round a tiny nonzero mantissa to zero, including a negative one.
        // Preserve genuine zeros (such as 0e-100), but never present underflow as a zero balance.
        if (amount == 0 && raw!.TakeWhile(c => c is not ('e' or 'E')).Any(c => c is >= '1' and <= '9'))
            return null;
        return amount;
    }

    private static decimal? Scale(decimal? amount, decimal divisor)
    {
        var scaled = amount / divisor;
        return amount is > 0 && scaled == 0 ? null : scaled;
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;

    private static string? Currency(JsonElement extra)
    {
        var currency = String(extra, "currency")?.ToUpperInvariant();
        return currency is { Length: 3 } && currency.All(c => c is >= 'A' and <= 'Z') ? currency : null;
    }
}
