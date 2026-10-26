using System.Text.Json;
using AIUsage.Core;
using AIUsage.Providers;
using Xunit;

namespace AIUsage.Tests;

public sealed class CreditParserTests
{
    [Fact]
    public void ClaudeExtraUsageConvertsExplicitUsdMinorUnitsWithoutInventingBalance()
    {
        var credit = Claude("""{"is_enabled":true,"monthly_limit":10000,"used_credits":1234,"utilization":12.34,"currency":"USD"}""");
        Assert.NotNull(credit);
        Assert.Equal("claude:extra_usage", credit.Id);
        Assert.Equal("추가 사용량", credit.Name);
        Assert.Equal("USD", credit.Unit);
        Assert.Equal(12.34m, credit.Used);
        Assert.Equal(100m, credit.Limit);
        Assert.Null(credit.Balance);
        Assert.True(credit.IsEnabled);
    }

    [Theory]
    [InlineData("null", "최소 단위 (통화 미제공)")]
    [InlineData("\"\"", "최소 단위 (통화 미제공)")]
    [InlineData("\"dollars\"", "최소 단위 (통화 미제공)")]
    [InlineData("123", "최소 단위 (통화 미제공)")]
    [InlineData("\"EUR\"", "EUR 최소 단위")]
    [InlineData("\"JPY\"", "JPY 최소 단위")]
    public void ClaudeUnknownCurrencyKeepsMinorAmountsWithoutAssumingDollars(string currency, string unit)
    {
        var credit = Claude($$"""{"is_enabled":true,"monthly_limit":4000,"used_credits":500,"currency":{{currency}}}""");
        Assert.Equal(unit, credit!.Unit);
        Assert.Equal(500m, credit.Used);
        Assert.Equal(4000m, credit.Limit);
    }

    [Fact]
    public void ClaudeMissingCurrencyIsNotAssumedFromSessionCost()
    {
        using var document = JsonDocument.Parse("""{"extra_usage":{"is_enabled":true,"monthly_limit":10000,"used_credits":50},"total_cost_usd":13.20}""");
        var credit = AccountCreditParsers.Claude(document.RootElement);
        Assert.Equal("최소 단위 (통화 미제공)", credit!.Unit);
        Assert.Equal(50m, credit.Used);
        Assert.Equal(10000m, credit.Limit);
        Assert.Null(credit.Balance);
    }

    [Fact]
    public void ClaudeDisabledIsPreservedAndNullAmountsStayUnknown()
    {
        var credit = Claude("""{"is_enabled":false,"monthly_limit":null,"used_credits":null,"currency":"USD"}""");
        Assert.False(credit!.IsEnabled);
        Assert.Null(credit.Used);
        Assert.Null(credit.Limit);
        Assert.Null(credit.Balance);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("\"0\"")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("1e1000")]
    public void ClaudeMalformedAmountsDoNotHideValidFlagsOrInventZero(string amount)
    {
        var credit = Claude($$"""{"is_enabled":true,"monthly_limit":{{amount}},"used_credits":{{amount}},"currency":"USD"}""");
        Assert.True(credit!.IsEnabled);
        Assert.Null(credit.Used);
        Assert.Null(credit.Limit);
    }

    [Fact]
    public void ClaudeActualZeroAndMissingAmountsRemainDistinct()
    {
        var zero = Claude("""{"is_enabled":true,"monthly_limit":0,"used_credits":0,"currency":"USD"}""");
        var unknown = Claude("""{"is_enabled":true,"currency":"USD"}""");
        Assert.Equal(0m, zero!.Used);
        Assert.Equal(0m, zero.Limit);
        Assert.Null(unknown!.Used);
        Assert.Null(unknown.Limit);
    }

    [Theory]
    [InlineData("1e-100")]
    [InlineData("-1e-100")]
    [InlineData("0.000000000000000000000000000001")]
    public void ClaudeDecimalUnderflowIsUnknownInsteadOfZero(string amount)
    {
        var credit = Claude($$"""{"is_enabled":true,"monthly_limit":{{amount}},"used_credits":{{amount}}}""");
        Assert.Null(credit!.Used);
        Assert.Null(credit.Limit);
    }

    [Fact]
    public void ClaudeCurrencyScalingUnderflowIsUnknownInsteadOfZero()
    {
        var credit = Claude("""{"is_enabled":true,"monthly_limit":1e-28,"used_credits":1e-28,"currency":"USD"}""");
        Assert.Null(credit!.Used);
        Assert.Null(credit.Limit);
    }

    [Theory]
    [InlineData("0e-100")]
    [InlineData("-0.00e-100")]
    public void ClaudeExponentOfActualZeroRemainsZero(string amount)
    {
        var credit = Claude($$"""{"is_enabled":true,"monthly_limit":{{amount}},"used_credits":{{amount}},"currency":"USD"}""");
        Assert.Equal(0m, credit!.Used);
        Assert.Equal(0m, credit.Limit);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"is_enabled\":\"true\"}")]
    [InlineData("{\"is_enabled\":null}")]
    public void ClaudeInvalidOptionalCreditStructureIsIgnored(string extra) => Assert.Null(Claude(extra));

    [Fact]
    public void CodexBalanceIsCreditsAndBucketNamesArePreserved()
    {
        var primary = Codex("""{"balance":"125.75","hasCredits":true,"unlimited":false}""");
        var named = Codex("""{"balance":"500","hasCredits":true,"unlimited":false}""", "review", "Code review");
        Assert.Equal("codex:credits", primary!.Id);
        Assert.Equal("크레딧", primary.Name);
        Assert.Equal("크레딧", primary.Unit);
        Assert.Equal(125.75m, primary.Balance);
        Assert.Null(primary.Used);
        Assert.Null(primary.Limit);
        Assert.True(primary.HasCredits);
        Assert.Equal("review:credits", named!.Id);
        Assert.Equal("Code review 크레딧", named.Name);
        Assert.Equal(500m, named.Balance);
    }

    [Fact]
    public void CodexUnlimitedDoesNotBecomeZeroOrAnInventedNumericLimit()
    {
        var credit = Codex("""{"balance":null,"hasCredits":true,"unlimited":true}""");
        Assert.True(credit!.Unlimited);
        Assert.True(credit.HasCredits);
        Assert.Null(credit.Balance);
        Assert.Null(credit.Limit);
    }

    [Fact]
    public void CodexExplicitNoCreditsAndNumericZeroAreRetained()
    {
        var credit = Codex("""{"balance":"0","hasCredits":false,"unlimited":false}""");
        Assert.False(credit!.HasCredits);
        Assert.False(credit.Unlimited);
        Assert.Equal(0m, credit.Balance);
    }

    [Theory]
    [InlineData("1e-100")]
    [InlineData("-1e-100")]
    [InlineData("0.000000000000000000000000000001")]
    public void CodexDecimalUnderflowIsUnknownInsteadOfZero(string balance)
    {
        var credit = Codex($$"""{"balance":"{{balance}}","hasCredits":true,"unlimited":false}""");
        Assert.Null(credit!.Balance);
        Assert.True(credit.HasCredits);
    }

    [Theory]
    [InlineData("0e-100")]
    [InlineData("-0.00e-100")]
    public void CodexExponentOfActualZeroRemainsZero(string balance)
    {
        var credit = Codex($$"""{"balance":"{{balance}}","hasCredits":false,"unlimited":false}""");
        Assert.Equal(0m, credit!.Balance);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"1e1000\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"1,234\"")]
    [InlineData("\"$2.50\"")]
    [InlineData("\"\"")]
    public void CodexMalformedOptionalBalanceRemainsUnknownWithoutLosingFlags(string balance)
    {
        var credit = Codex($$"""{"balance":{{balance}},"hasCredits":true,"unlimited":false}""");
        Assert.NotNull(credit);
        Assert.True(credit.HasCredits);
        Assert.Null(credit.Balance);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"balance\":\"5\"}")]
    [InlineData("{\"hasCredits\":true}")]
    [InlineData("{\"unlimited\":false}")]
    [InlineData("{\"hasCredits\":\"true\",\"unlimited\":false}")]
    [InlineData("{\"hasCredits\":true,\"unlimited\":0}")]
    public void CodexInvalidRequiredFlagsOrAbsentCreditsAreIgnored(string credits) => Assert.Null(Codex(credits));

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[{\"id\":\"one\"}]")]
    public void ResetCreditCountUsesAuthoritativeTotalInsteadOfDetailCount(string details)
    {
        using var document = JsonDocument.Parse("{\"rateLimitResetCredits\":{\"availableCount\":7,\"credits\":" + details + "}}");
        var credit = AccountCreditParsers.CodexResetCredits(document.RootElement);
        Assert.Equal("codex:reset-credits", credit!.Id);
        Assert.Equal("초기화 크레딧", credit.Name);
        Assert.Equal("회", credit.Unit);
        Assert.Equal(7m, credit.Balance);
    }

    [Fact]
    public void ResetCreditCountPreservesExplicitZero()
    {
        using var document = JsonDocument.Parse("""{"rateLimitResetCredits":{"availableCount":0}}""");
        Assert.Equal(0m, AccountCreditParsers.CodexResetCredits(document.RootElement)!.Balance);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"2\"")]
    [InlineData("true")]
    [InlineData("1e1000")]
    public void ResetCreditMalformedCountIsUnknownEvenWhenDetailsExist(string count)
    {
        using var document = JsonDocument.Parse("{\"rateLimitResetCredits\":{\"availableCount\":" + count + ",\"credits\":[{\"id\":\"one\"}]}}");
        Assert.Null(AccountCreditParsers.CodexResetCredits(document.RootElement));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1250L)]
    [InlineData(long.MaxValue)]
    public void AntigravityNativeCreditsReturnsExactNonnegativeIntegerBalance(long remaining)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            status = "SUCCESS", num_turns = 0,
            command = new
            {
                name = "credits",
                data = new { remaining_credits = remaining, plan_name = "Example plan", upgrade_uri = "https://example.invalid/" }
            }
        }));
        var credit = AccountCreditParsers.Antigravity(document.RootElement);
        Assert.NotNull(credit);
        Assert.Equal("antigravity:credits", credit.Id);
        Assert.Equal("크레딧", credit.Name);
        Assert.Equal("크레딧", credit.Unit);
        Assert.Equal((decimal)remaining, credit.Balance);
        Assert.Null(credit.Used);
        Assert.Null(credit.Limit);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"remaining_credits\":null}")]
    [InlineData("{\"remaining_credits\":-1}")]
    [InlineData("{\"remaining_credits\":\"1250\"}")]
    [InlineData("{\"remaining_credits\":1.5}")]
    [InlineData("{\"remaining_credits\":true}")]
    [InlineData("{\"remaining_credits\":1e-100}")]
    [InlineData("{\"remaining_credits\":9223372036854775808}")]
    [InlineData("{\"balance\":1250,\"text\":\"Remaining credits: 1250\"}")]
    public void AntigravityMissingOrMalformedBalanceIsNeverInvented(string data)
    {
        using var document = JsonDocument.Parse("{\"status\":\"SUCCESS\",\"num_turns\":0,\"command\":{\"name\":\"credits\",\"data\":" + data + "}}");
        Assert.Null(AccountCreditParsers.Antigravity(document.RootElement));
    }

    [Theory]
    [InlineData("{\"status\":\"SUCCESS\",\"command\":{\"name\":\"credits\",\"data\":{\"remaining_credits\":1}}}")]
    [InlineData("{\"status\":\"SUCCESS\",\"num_turns\":1,\"command\":{\"name\":\"credits\",\"data\":{\"remaining_credits\":1}}}")]
    [InlineData("{\"status\":\"SUCCESS\",\"num_turns\":\"0\",\"command\":{\"name\":\"credits\",\"data\":{\"remaining_credits\":1}}}")]
    [InlineData("{\"status\":\"SUCCESS\",\"num_turns\":0.0,\"command\":{\"name\":\"credits\",\"data\":{\"remaining_credits\":1}}}")]
    [InlineData("{\"status\":\"SUCCESS\",\"num_turns\":0,\"command\":{\"name\":\"usage\",\"data\":{\"remaining_credits\":1}}}")]
    [InlineData("{\"status\":\"FAILED\",\"num_turns\":0,\"command\":{\"name\":\"credits\",\"data\":{\"remaining_credits\":1}}}")]
    [InlineData("{\"status\":\"SUCCESS\",\"num_turns\":0,\"result\":\"Remaining credits: 1\"}")]
    public void AntigravityRequiresSuccessfulNativeCreditsCommandWithoutAnAgentTurn(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Null(AccountCreditParsers.Antigravity(document.RootElement));
    }

    [Theory]
    [InlineData("\"connection failed\"")]
    [InlineData("{\"code\":429}")]
    [InlineData("true")]
    [InlineData("[]")]
    public void AntigravityRootErrorPreventsDisplayingBalance(string error)
    {
        using var document = JsonDocument.Parse("{\"status\":\"SUCCESS\",\"num_turns\":0,\"command\":{\"name\":\"credits\",\"data\":{\"remaining_credits\":1}},\"error\":" + error + "}");
        Assert.Null(AccountCreditParsers.Antigravity(document.RootElement));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    public void AntigravityEmptyErrorDoesNotHideValidNativeBalance(string error)
    {
        using var document = JsonDocument.Parse("{\"status\":\"SUCCESS\",\"num_turns\":0,\"command\":{\"name\":\"credits\",\"data\":{\"remaining_credits\":1}},\"error\":" + error + "}");
        Assert.Equal(1m, AccountCreditParsers.Antigravity(document.RootElement)!.Balance);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("{}")]
    [InlineData("{\"total_cost_usd\":4.25}")]
    public void OptionalCreditParsersIgnoreMissingOrUnrelatedData(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Null(AccountCreditParsers.Claude(document.RootElement));
        Assert.Null(AccountCreditParsers.CodexBucket(document.RootElement, "codex"));
        Assert.Null(AccountCreditParsers.CodexResetCredits(document.RootElement));
        Assert.Null(AccountCreditParsers.Antigravity(document.RootElement));
    }

    private static AccountCredit? Claude(string extra)
    {
        using var document = JsonDocument.Parse("{\"extra_usage\":" + extra + "}");
        return AccountCreditParsers.Claude(document.RootElement);
    }

    private static AccountCredit? Codex(string credits, string id = "codex", string? name = null)
    {
        using var document = JsonDocument.Parse("{\"credits\":" + credits + ",\"limitName\":" + JsonSerializer.Serialize(name) + "}");
        return AccountCreditParsers.CodexBucket(document.RootElement, id);
    }
}
