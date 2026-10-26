using AIUsage.Core;
using AIUsage.Providers;
using Xunit;

namespace AIUsage.Tests;

public sealed class AntigravityCreditTests
{
    private const string QuotaResponse = """{"status":"SUCCESS","num_turns":0,"command":{"name":"usage","data":{"groups":[{"name":"Models","buckets":[{"id":"shared","name":"Weekly","remaining_fraction":0.75,"reset_time":"2026-11-01T00:00:00Z"}]}]}}}""";
    private const string CreditsResponse = """{"status":"SUCCESS","num_turns":0,"command":{"name":"credits","data":{"remaining_credits":1250}}}""";

    [Theory]
    [InlineData("exit-error")]
    [InlineData("timeout")]
    [InlineData("malformed-json")]
    [InlineData("missing-balance")]
    [InlineData("invalid-balance")]
    [InlineData("root-error")]
    public async Task OptionalCreditsFailureKeepsSuccessfulQuotasAndRechecksAccount(string failure)
    {
        using var fixture = new Fixture();
        fixture.Runner.CreditsBehavior = _ => failure switch
        {
            "exit-error" => Task.FromResult(new ProcessResult(1, "", "Connection unavailable")),
            "timeout" => Task.FromException<ProcessResult>(new TimeoutException()),
            "malformed-json" => Task.FromResult(new ProcessResult(0, "not json", "")),
            "missing-balance" => Task.FromResult(new ProcessResult(0,
                """{"status":"SUCCESS","num_turns":0,"command":{"name":"credits","data":{}}}""", "")),
            "invalid-balance" => Task.FromResult(new ProcessResult(0, CreditsResponse.Replace("1250", "\"1250\""), "")),
            "root-error" => Task.FromResult(new ProcessResult(0,
                """{"error":"Connection unavailable","status":"SUCCESS","num_turns":0,"command":{"name":"credits","data":{"remaining_credits":1250}}}""", "")),
            _ => throw new InvalidOperationException()
        };

        var snapshot = await fixture.Provider.FetchAsync(fixture.Account, null, default);

        AssertQuota(snapshot, fixture.Account);
        Assert.Null(snapshot.Credits);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.CreditsMessage));
        Assert.Equal(2, fixture.Runner.IdentityChecks);
        Assert.Equal(1, fixture.Runner.CreditCalls);
        Assert.Equal(5, fixture.Runner.Requests.Count);
        AssertReadOnlyRequests(fixture.Runner.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreditsRateLimitPausesCreditsWhileRefreshingQuotas(bool jsonError)
    {
        using var fixture = new Fixture();
        fixture.Runner.CreditsBehavior = _ => Task.FromResult(jsonError
            ? new ProcessResult(0, """{"error":{"code":429,"message":"Too many requests","retry_after":3600}}""", "")
            : new ProcessResult(1, "", "HTTP 429 Too many requests\nRetry-After: 3600"));

        var first = await fixture.Provider.FetchAsync(fixture.Account, null, default);
        var second = await fixture.Provider.FetchAsync(fixture.Account, null, default);

        AssertQuota(first, fixture.Account);
        AssertQuota(second, fixture.Account);
        Assert.Null(first.Credits);
        Assert.Null(second.Credits);
        Assert.Contains("재시도 대기", first.CreditsMessage);
        Assert.Contains("재시도 대기", second.CreditsMessage);
        Assert.Equal(1, fixture.Runner.CreditCalls);
        Assert.Equal(2, fixture.Runner.UsageCalls);
        Assert.Equal(4, fixture.Runner.IdentityChecks);
        AssertReadOnlyRequests(fixture.Runner.Requests);
    }

    [Fact]
    public async Task CreditRetryPauseDoesNotCarryOverToAReconnectedAccount()
    {
        using var fixture = new Fixture();
        fixture.Runner.CreditsBehavior = _ => Task.FromResult(new ProcessResult(1, "", "HTTP 429\nRetry-After: 3600"));
        await fixture.Provider.FetchAsync(fixture.Account, null, default);
        fixture.Account.IdentityKey = "antigravity:other@example.invalid";
        fixture.Runner.AuthEmail = _ => "other@example.invalid";
        fixture.Runner.CreditsBehavior = _ => Task.FromResult(new ProcessResult(0, CreditsResponse, ""));

        var snapshot = await fixture.Provider.FetchAsync(fixture.Account, null, default);

        AssertQuota(snapshot, fixture.Account);
        Assert.Equal(1250m, Assert.Single(snapshot.Credits!).Balance);
        Assert.Null(snapshot.CreditsMessage);
        Assert.Equal(2, fixture.Runner.CreditCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedAccountSwitchAfterCreditFailureStillInvalidatesTheWholeSnapshot(bool rateLimited)
    {
        using var fixture = new Fixture();
        fixture.Runner.AuthEmail = check => check == 1 ? "test@example.invalid" : "other@example.invalid";
        fixture.Runner.CreditsBehavior = _ => rateLimited
            ? Task.FromResult(new ProcessResult(1, "", "HTTP 429\nRetry-After: 3600"))
            : Task.FromException<ProcessResult>(new TimeoutException());

        var error = await Assert.ThrowsAsync<ProviderException>(() => fixture.Provider.FetchAsync(fixture.Account, null, default));

        Assert.Equal(UsageState.SignInRequired, error.State);
        Assert.True(error.IdentityChanged);
        Assert.Equal(2, fixture.Runner.IdentityChecks);
        Assert.Equal(1, fixture.Runner.CreditCalls);
        AssertReadOnlyRequests(fixture.Runner.Requests);
    }

    [Fact]
    public async Task CancellationDuringCreditsPropagatesAndReleasesSharedAccountGate()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Runner.CreditsBehavior = token =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<ProcessResult>(token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Provider.FetchAsync(fixture.Account, null, cancellation.Token));

        fixture.Runner.CreditsBehavior = _ => Task.FromResult(new ProcessResult(0, CreditsResponse, ""));
        var recovered = await fixture.Provider.FetchAsync(fixture.Account, null, default).WaitAsync(TimeSpan.FromSeconds(5));
        AssertQuota(recovered, fixture.Account);
        Assert.Equal(1250m, Assert.Single(recovered.Credits!).Balance);
        Assert.Equal(2, fixture.Runner.CreditCalls);
        AssertReadOnlyRequests(fixture.Runner.Requests);
    }

    private static void AssertQuota(UsageSnapshot snapshot, AccountSettings account)
    {
        Assert.Equal(UsageState.Ready, snapshot.State);
        Assert.Equal(account.IdentityKey, snapshot.IdentityKey);
        Assert.Null(snapshot.Message);
        var quota = Assert.Single(snapshot.Windows);
        Assert.Equal(25, quota.UsedPercent);
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T00:00:00Z"), quota.ResetsAt);
    }

    private static void AssertReadOnlyRequests(IEnumerable<ProcessRequest> requests)
    {
        foreach (var request in requests)
        {
            Assert.Null(request.StandardInput);
            Assert.DoesNotContain("login", request.Arguments);
            var authAction = request.Environment["AGY_CLI_CDE_AUTH_ACTION"];
            Assert.True(authAction is null or "check");
            if (request.Arguments.Contains("-p"))
            {
                Assert.True(request.Arguments.SequenceEqual(new[] { "-p", "/usage", "--output-format", "json" }) ||
                    request.Arguments.SequenceEqual(new[] { "-p", "/credits", "--output-format", "json" }));
                Assert.Null(authAction);
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "aiusage-antigravity-credit-test-" + Guid.NewGuid().ToString("N"));
        public AccountSettings Account { get; } = new()
        {
            Provider = ProviderKind.Antigravity,
            Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            IdentityKey = "antigravity:test@example.invalid"
        };
        public FakeRunner Runner { get; } = new();
        public AntigravityUsageProvider Provider { get; }

        public Fixture() => Provider = new(new AppPaths(_root), new FakeRuntime(), Runner, new NoLogin());
        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Func<CancellationToken, Task<ProcessResult>> CreditsBehavior { get; set; } =
            _ => Task.FromResult(new ProcessResult(0, CreditsResponse, ""));
        public Func<int, string> AuthEmail { get; set; } = _ => "test@example.invalid";
        public int IdentityChecks { get; private set; }
        public int CreditCalls { get; private set; }
        public int UsageCalls { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (request.Arguments.SequenceEqual(new[] { "--version" }))
                return Task.FromResult(new ProcessResult(0, "agy version " + AntigravityUsageProvider.SupportedRuntimeVersion, ""));
            if (request.Environment["AGY_CLI_CDE_AUTH_ACTION"] == "check")
            {
                Assert.Empty(request.Arguments);
                return Task.FromResult(new ProcessResult(0, "Already authenticated as " + AuthEmail(++IdentityChecks) + ".\n", ""));
            }
            if (request.Arguments.SequenceEqual(new[] { "-p", "/usage", "--output-format", "json" }))
            {
                UsageCalls++;
                return Task.FromResult(new ProcessResult(0, QuotaResponse, ""));
            }
            if (request.Arguments.SequenceEqual(new[] { "-p", "/credits", "--output-format", "json" }))
            {
                CreditCalls++;
                return CreditsBehavior(cancellationToken);
            }
            throw new InvalidOperationException("Unexpected CLI request in a read-only usage query.");
        }
    }

    private sealed class FakeRuntime : IRuntimeManager
    {
        public Task<string> EnsureAsync(ProviderKind provider, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult("unused-fake-executable");
    }

    private sealed class NoLogin : ILoginInteraction
    {
        public Task OpenBrowserAsync(Uri uri, CancellationToken cancellationToken) => throw new InvalidOperationException("Polling must not trigger login.");
        public Task<string?> RequestInputAsync(string title, string message, CancellationToken cancellationToken) => throw new InvalidOperationException("Polling must not trigger login.");
    }
}
