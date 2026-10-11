using System.Text.Json;
using AIUsage.Core;
using AIUsage.Providers;
using Xunit;

namespace AIUsage.Tests;

public sealed class ProviderTests
{
    private static AccountSettings Account(ProviderKind provider) => new() { Provider = provider, Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Plan = "test-plan" };

    [Fact]
    public void ClaudeParsesOnlyStructuredSubscriberReportAndPreservesScopedWindows()
    {
        const string json = """
            {"type":"system","subtype":"init"}
            {"type":"assistant","message":{"content":[{"type":"text","text":"old cached text: 99%"}]},"usage_report":{"session":{"total_cost_usd":100},"rate_limits":{"limits":[{"kind":"session","group":"session","percent":12.5,"resets_at":"2026-10-08T13:00:00Z","severity":"normal","is_active":true},{"kind":"weekly_scoped","group":"weekly","percent":50,"resets_at":null,"scope":{"model":{"display_name":"Model A"}},"severity":"warning","is_active":false}]}}}
            {"type":"result","is_error":false,"total_cost_usd":100}
            """;
        var snapshot = ClaudeUsageParser.Parse(json, Account(ProviderKind.Claude));
        Assert.Equal(UsageState.Ready, snapshot.State);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal(12.5, snapshot.Windows[0].UsedPercent);
        Assert.True(snapshot.Windows[0].IsPrimary);
        Assert.Equal(DateTimeOffset.Parse("2026-10-08T13:00:00Z"), snapshot.Windows[0].ResetsAt);
        Assert.Contains("Model A", snapshot.Windows[1].Name);
        Assert.Null(snapshot.Windows[1].ResetsAt);
    }

    [Theory]
    [InlineData("{\"type\":\"result\",\"result\":\"Usage: 25%\",\"total_cost_usd\":25}")]
    [InlineData("{\"type\":\"assistant\",\"message\":{\"usage_report\":{}}}")]
    [InlineData("garbled json")]
    public void ClaudeNeverUsesTextOrTokenBillingAsSubscriptionQuota(string json)
    {
        Assert.Equal(UsageState.UpdateRequired, Assert.Throws<ProviderException>(() => ClaudeUsageParser.Parse(json, Account(ProviderKind.Claude))).State);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"limits\":null}")]
    [InlineData("{\"limits\":[]}")]
    public void ClaudeDoesNotTranslateUnavailableOrEmptyMetersToZero(string limits)
    {
        var json = "{\"type\":\"assistant\",\"usage_report\":{\"rate_limits\":" + limits + "}}";
        Assert.Equal(UsageState.Unavailable, Assert.Throws<ProviderException>(() => ClaudeUsageParser.Parse(json, Account(ProviderKind.Claude))).State);
    }

    [Fact]
    public async Task ClaudeVerifiedMaxAccountWithUnavailableQuotaDoesNotReauthenticateOrGenerate()
    {
        var root = Path.Combine(Path.GetTempPath(), "aiusage-provider-test-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var account = Account(ProviderKind.Claude);
        account.Plan = "max";
        account.IdentityKey = "claude:fixture-account:fixture-organization";
        var authStatus = JsonSerializer.Serialize(new
        {
            loggedIn = true, authMethod = "claude.ai", subscriptionType = "max",
            accountId = "fixture-account", orgId = "fixture-organization",
            email = "test@example.invalid", configDirectory = paths.ProfileDirectory(account)
        });
        const string usage = """
            {"type":"assistant","usage_report":{"session":{"total_cost_usd":0},"rate_limits":null}}
            {"type":"result","is_error":false,"num_turns":0,"total_cost_usd":0}
            """;
        var runner = new FakeRunner(new(0, "2.1.288 (Claude Code)", ""), new(0, authStatus, ""), new(0, usage, ""));
        try
        {
            var provider = new ClaudeUsageProvider(paths, new FakeRuntime(), runner, new NoLogin());
            var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.FetchAsync(account, null, default));
            Assert.Equal(UsageState.Unavailable, exception.State);
            Assert.False(exception.IdentityChanged);
            Assert.Equal("max", account.Plan);
            Assert.Equal(3, runner.Requests.Count);
            Assert.Equal(new[] { "auth", "status" }, runner.Requests[1].Arguments);
            var request = runner.Requests[2];
            Assert.Equal(new[] { "--safe-mode", "-p", "/usage", "--output-format", "stream-json", "--verbose", "--no-session-persistence" }, request.Arguments);
            Assert.Null(request.StandardInput);
            Assert.DoesNotContain(runner.Requests, x => x.Arguments.Contains("login") || x.Arguments.Contains("logout"));

            // Null child overrides remove inherited credentials/switches; all requests keep the same isolated profile.
            foreach (var child in runner.Requests)
            {
                Assert.Equal(paths.ProfileDirectory(account), child.Environment["CLAUDE_CONFIG_DIR"]);
                Assert.Null(child.Environment["ANTHROPIC_API_KEY"]);
                Assert.Null(child.Environment["ANTHROPIC_AUTH_TOKEN"]);
                Assert.Null(child.Environment["CLAUDE_CODE_OAUTH_TOKEN"]);
                Assert.Null(child.Environment["DISABLE_NONESSENTIAL_TRAFFIC"]);
                Assert.Null(child.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);
                Assert.Null(child.Environment["CLAUDE_CODE_ENABLE_TELEMETRY"]);
                Assert.Equal("1", child.Environment["DISABLE_TELEMETRY"]);
                Assert.Equal("1", child.Environment["DISABLE_ERROR_REPORTING"]);
                Assert.Equal("1", child.Environment["DISABLE_AUTOUPDATER"]);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void CodexUsesMultipleBucketsWithoutDuplicatingLegacyView()
    {
        const string json = """
            {"id":3,"result":{"rateLimits":{"primary":{"usedPercent":99}},"rateLimitsByLimitId":{"codex":{"limitId":"codex","planType":"plus","primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":1791464400},"secondary":{"usedPercent":70,"windowDurationMins":10080,"resetsAt":null}},"codex_other":{"limitName":"Other","primary":{"usedPercent":42,"windowDurationMins":60,"resetsAt":1791464400}}}}}
            """;
        var snapshot = CodexUsageParser.Parse(json, Account(ProviderKind.Codex));
        Assert.Equal(3, snapshot.Windows.Count);
        Assert.Equal(25, snapshot.Windows[0].UsedPercent);
        Assert.Equal("5시간", snapshot.Windows[0].Name);
        Assert.Equal("주간", snapshot.Windows[1].Name);
        Assert.Equal("Other · 1시간", snapshot.Windows[2].Name);
        Assert.DoesNotContain(snapshot.Windows, x => x.UsedPercent == 99);
    }

    [Fact]
    public void CodexRetainsMissingMetricAsUnknown()
    {
        var snapshot = CodexUsageParser.Parse("""{"rateLimits":{"primary":{"usedPercent":null,"resetsAt":null}}}""", Account(ProviderKind.Codex));
        Assert.Null(snapshot.Windows[0].UsedPercent);
        Assert.Null(snapshot.Windows[0].ResetsAt);
        Assert.Equal(UsageState.Unavailable, snapshot.State);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("\"25\"")]
    public void CodexRejectsMalformedPercent(string value)
    {
        var json = "{\"rateLimits\":{\"primary\":{\"usedPercent\":" + value + "}}}";
        Assert.Equal(UsageState.UpdateRequired, Assert.Throws<ProviderException>(() => CodexUsageParser.Parse(json, Account(ProviderKind.Codex))).State);
    }

    [Fact]
    public void CodexRejectsApiKeyIdentity()
    {
        using var json = JsonDocument.Parse("""{"account":{"type":"apiKey"}}""");
        Assert.Equal(UsageState.SignInRequired, Assert.Throws<ProviderException>(() => CodexUsageProvider.ParseAccount(json.RootElement)).State);
    }

    [Fact]
    public void CodexDoesNotTreatTokenActivityAsQuota()
    {
        Assert.Throws<ProviderException>(() => CodexUsageParser.Parse("""{"summary":{"lifetimeTokens":99999},"dailyUsageBuckets":[]}""", Account(ProviderKind.Codex)));
    }

    [Theory]
    [InlineData("{\"retry_after\":900}", 900)]
    [InlineData("{\"data\":{\"retryAfter\":60}}", 60)]
    [InlineData("{\"headers\":{\"retry-after\":\"120\"}}", 120)]
    [InlineData("{\"retry_after_ms\":2500}", 2.5)]
    public void RateLimitErrorsHonorExplicitProviderRetryDelay(string details, double expectedSeconds)
    {
        var error = "{\"error\":{\"code\":429,\"message\":\"Too many requests\",\"details\":" + details + "}}";
        var exception = Assert.Throws<ProviderException>(() => CodexUsageParser.Parse(error, Account(ProviderKind.Codex)));
        Assert.Equal(UsageState.RateLimited, exception.State);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), exception.RetryAfter);
    }

    [Fact]
    public void RateLimitDatePreservesServerWait()
    {
        var until = DateTimeOffset.UtcNow.AddMinutes(20);
        var json = JsonSerializer.Serialize(new { error = new { code = 429, data = new Dictionary<string, string> { ["retry-after"] = until.ToString("R") } } });
        var exception = Assert.Throws<ProviderException>(() => CodexUsageParser.Parse(json, Account(ProviderKind.Codex)));
        Assert.InRange(exception.RetryAfter!.Value.TotalSeconds, 1195, 1200);
    }

    [Fact]
    public void RateLimitWithoutHintUsesFallback()
    {
        var exception = Assert.Throws<ProviderException>(() => CodexUsageParser.Parse("""{"error":{"code":429}}""", Account(ProviderKind.Codex)));
        Assert.Equal(TimeSpan.FromMinutes(5), exception.RetryAfter);
    }

    // Reconstructed from the pinned vendor binary's JSON struct tags and native command path.
    // No real account quota or credentials are contained in this fixture.
    private const string AgFixture = """
        {"status":"SUCCESS","response":"ignored text","num_turns":0,"usage":{"total_tokens":999},"command":{"name":"usage","data":{"groups":[{"name":"Gemini","buckets":[{"id":"weekly","name":"Weekly","remaining_fraction":0.75,"reset_time":"2026-10-09T00:00:00Z"},{"name":"Disabled","disabled":true,"remaining_fraction":0.5},{"name":"Amount only","remaining_amount":23}]}]}}}
        """;

    [Fact]
    public void AntigravityConvertsQuotaFractionAndRetainsUnknownDisabledAndAmountOnlyRows()
    {
        var account = Account(ProviderKind.Antigravity);
        account.IdentityKey = "antigravity:test@example.invalid";
        var result = AntigravityUsageParser.Parse(AgFixture, account);
        Assert.Equal(25, result.Windows[0].UsedPercent);
        Assert.Null(result.Windows[1].UsedPercent);
        Assert.Null(result.Windows[2].UsedPercent);
        Assert.Equal(DateTimeOffset.Parse("2026-10-09T00:00:00Z"), result.Windows[0].ResetsAt);
        Assert.Equal("antigravity:test@example.invalid", result.IdentityKey);
        Assert.Equal("test-plan", result.Plan);
    }

    [Fact]
    public void AntigravityAccountSwitchExplicitlyInvalidatesPreviousValues()
    {
        var account = Account(ProviderKind.Antigravity);
        account.IdentityKey = "antigravity:another@example.invalid";
        var exception = Assert.Throws<ProviderException>(() => AntigravityUsageParser.ParseIdentity("Already authenticated as test@example.invalid.\r\n", account));
        Assert.True(exception.IdentityChanged);
        Assert.Equal(UsageState.SignInRequired, exception.State);
    }

    [Theory]
    [InlineData("{\"usage\":1234,\"cost\":1.2}")]
    [InlineData("{\"quota\":null}")]
    [InlineData("{\"status\":\"SUCCESS\",\"num_turns\":1,\"command\":{\"name\":\"usage\",\"data\":{\"groups\":[]}}}")]
    [InlineData("{\"status\":\"SUCCESS\",\"num_turns\":0,\"command\":{\"name\":\"usage\",\"data\":{\"groups\":[]}}}")]
    [InlineData("{\"status\":\"SUCCESS\",\"num_turns\":0,\"command\":{\"name\":\"credits\",\"data\":{\"groups\":[]}}}")]
    public void AntigravityUnknownSchemaOrModelResultCannotMasqueradeAsUsage(string json)
    {
        Assert.Throws<ProviderException>(() => AntigravityUsageParser.Parse(json, Account(ProviderKind.Antigravity)));
    }

    [Theory]
    [InlineData("Already authenticated as test@example.invalid.\r\n")]
    [InlineData("\u001b]0;Vendor CLI\u0007\u001b[32mSuccessfully authenticated as test@example.invalid.\u001b[0m\r\n")]
    public void AntigravityNativeIdentityAcceptsOnlyExactAuthSuccessLine(string output)
    {
        Assert.Equal("antigravity:test@example.invalid", AntigravityUsageParser.ParseIdentity(output).IdentityKey);
    }

    [Theory]
    [InlineData("email: test@example.invalid")]
    [InlineData("Already authenticated as not-an-email.")]
    [InlineData("Already authenticated as a@example.invalid.\nAlready authenticated as b@example.invalid.")]
    [InlineData("{\"email\":\"test@example.invalid\"}")]
    public void AntigravityCannotInferAccountFromUnrecognizedOrAmbiguousOutput(string output)
    {
        Assert.Throws<ProviderException>(() => AntigravityUsageParser.ParseIdentity(output));
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("1.1")]
    [InlineData("\"0.75\"")]
    public void AntigravityRejectsMalformedQuotaFraction(string fraction)
    {
        Assert.Throws<ProviderException>(() => AntigravityUsageParser.Parse(AgFixture.Replace("0.75", fraction), Account(ProviderKind.Antigravity)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AntigravityChecksSharedAccountBeforeAndAfterNativeQuotaQuery(bool switchAccount)
    {
        var root = Path.Combine(Path.GetTempPath(), "aiusage-provider-test-" + Guid.NewGuid().ToString("N"));
        var account = Account(ProviderKind.Antigravity);
        account.IdentityKey = "antigravity:test@example.invalid";
        var runner = new FakeRunner(new(0, "agy version 1.3.1", ""),
            new(0, "Already authenticated as test@example.invalid.\n", ""), new(0, AgFixture, ""),
            new(0, "Already authenticated as " + (switchAccount ? "other" : "test") + "@example.invalid.\n", ""));
        try
        {
            var provider = new AntigravityUsageProvider(new AppPaths(root), new FakeRuntime(), runner, new NoLogin());
            if (switchAccount)
            {
                var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.FetchAsync(account, null, default));
                Assert.True(exception.IdentityChanged);
            }
            else Assert.Equal(25, (await provider.FetchAsync(account, null, default)).Windows[0].UsedPercent);
            Assert.Equal(4, runner.Requests.Count);
            Assert.Equal("check", runner.Requests[1].Environment["AGY_CLI_CDE_AUTH_ACTION"]);
            Assert.Equal(new[] { "-p", "/usage", "--output-format", "json" }, runner.Requests[2].Arguments);
            Assert.Null(runner.Requests[2].Environment["AGY_CLI_CDE_AUTH_ACTION"]);
            Assert.Equal("check", runner.Requests[3].Environment["AGY_CLI_CDE_AUTH_ACTION"]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class FakeRunner(params ProcessResult[] results) : IProcessRunner
    {
        private readonly Queue<ProcessResult> remaining = new(results);
        public List<ProcessRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken token)
        { Requests.Add(request); return Task.FromResult(remaining.Dequeue()); }
    }
    private sealed class FakeRuntime : IRuntimeManager
    {
        public Task<string> EnsureAsync(ProviderKind kind, IProgress<ProviderProgress>? progress, CancellationToken token) => Task.FromResult("unused-fake-executable");
    }
    private sealed class NoLogin : ILoginInteraction
    {
        public Task OpenBrowserAsync(Uri uri, CancellationToken token) => throw new InvalidOperationException("Polling must not trigger login.");
        public Task<string?> RequestInputAsync(string title, string message, CancellationToken token) => throw new InvalidOperationException("Polling must not trigger login.");
    }
}
