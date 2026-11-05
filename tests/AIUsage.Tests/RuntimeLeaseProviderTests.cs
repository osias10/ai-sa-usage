using System.Text.Json;
using AIUsage.Core;
using AIUsage.Providers;
using Xunit;

namespace AIUsage.Tests;

public sealed class RuntimeLeaseProviderTests
{
    [Theory]
    [InlineData(ProviderKind.Claude)]
    [InlineData(ProviderKind.Codex)]
    [InlineData(ProviderKind.Antigravity)]
    public async Task UsagePreparationAcquiresOnlyRuntimeAndExecutesInsideFetch(ProviderKind kind)
    {
        using var fixture = new Fixture(kind);
        var provider = Assert.IsAssignableFrom<IPreparedUsageProvider>(fixture.Provider());
        await using (var prepared = await provider.PrepareUsageReadAsync(fixture.Account, null, default))
        {
            Assert.Empty(fixture.Requests);
            Assert.Empty(fixture.RpcRequests);
            Assert.Equal(1, fixture.Runtime.Active);
            var snapshot = await prepared.FetchAsync(default);
            Assert.Equal(UsageState.Ready, snapshot.State);
            Assert.NotEmpty(fixture.Requests.Concat(fixture.RpcRequests));
            Assert.Equal(1, fixture.Runtime.Acquisitions);
            Assert.Equal(1, fixture.Runtime.Active);
        }
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData(ProviderKind.Claude)]
    [InlineData(ProviderKind.Codex)]
    [InlineData(ProviderKind.Antigravity)]
    public async Task AbandonedPreparedUsageReleasesRuntimeWithoutStartingProcess(ProviderKind kind)
    {
        using var fixture = new Fixture(kind);
        var provider = Assert.IsAssignableFrom<IPreparedUsageProvider>(fixture.Provider());
        var prepared = await provider.PrepareUsageReadAsync(fixture.Account, null, default);
        await prepared.DisposeAsync();
        Assert.Empty(fixture.Requests);
        Assert.Empty(fixture.RpcRequests);
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData(ProviderKind.Claude, 3)]
    [InlineData(ProviderKind.Antigravity, 5)]
    public async Task FetchPinsOneExecutableUntilAllQueriesFinish(ProviderKind kind, int expectedProcesses)
    {
        using var fixture = new Fixture(kind);
        await fixture.Provider().FetchAsync(fixture.Account, null, default);
        Assert.Equal(expectedProcesses, fixture.Requests.Count);
        Assert.Single(fixture.Requests.Select(request => request.FileName).Distinct());
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData(ProviderKind.Claude, 0)]
    [InlineData(ProviderKind.Claude, 1)]
    [InlineData(ProviderKind.Claude, 2)]
    [InlineData(ProviderKind.Antigravity, 0)]
    [InlineData(ProviderKind.Antigravity, 1)]
    [InlineData(ProviderKind.Antigravity, 2)]
    [InlineData(ProviderKind.Antigravity, 4)]
    public async Task FetchFailureAlwaysReleasesLease(ProviderKind kind, int failingIndex)
    {
        using var fixture = new Fixture(kind) { FailingProcess = failingIndex };
        await Assert.ThrowsAsync<ProviderException>(() => fixture.Provider().FetchAsync(fixture.Account, null, default));
        fixture.AssertReleased();
    }

    [Fact]
    public async Task OptionalAntigravityCreditFailureKeepsLeaseUntilFinalIdentityCheck()
    {
        using var fixture = new Fixture(ProviderKind.Antigravity) { FailingProcess = 3 };
        var snapshot = await fixture.Provider().FetchAsync(fixture.Account, null, default);
        Assert.Equal(UsageState.Ready, snapshot.State);
        Assert.Null(snapshot.Credits);
        Assert.Equal(5, fixture.Requests.Count);
        Assert.Equal("check", fixture.Requests[^1].Environment["AGY_CLI_CDE_AUTH_ACTION"]);
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaudeLogoutHoldsLeaseThroughProcessCompletion(bool fail)
    {
        using var fixture = new Fixture(ProviderKind.Claude) { FailingProcess = fail ? 0 : null };
        var operation = fixture.Provider().DisconnectAsync(fixture.Account, default);
        if (fail) await Assert.ThrowsAsync<ProviderException>(() => operation);
        else await operation;
        Assert.Equal(new[] { "auth", "logout" }, Assert.Single(fixture.Requests).Arguments);
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData(ProviderKind.Claude)]
    [InlineData(ProviderKind.Codex)]
    public async Task ResetPreparationSendsNothingAndRetainsSameRuntimeForExactlyOneSend(ProviderKind kind)
    {
        using var fixture = new Fixture(kind);
        var provider = Assert.IsAssignableFrom<IPreparedResetMessageProvider>(fixture.Provider());
        var prepared = await provider.PrepareResetMessageAsync(fixture.Account, default);
        Assert.Equal(1, fixture.Runtime.Active);
        Assert.Empty(fixture.Requests);
        Assert.Empty(fixture.RpcRequests);
        await Assert.ThrowsAsync<InvalidOperationException>(() => prepared.SendAsync(default));
        await prepared.ValidateAsync(default);
        Assert.DoesNotContain(fixture.Requests, IsGeneration);
        var preparationExecutable = kind == ProviderKind.Codex ? fixture.RpcRequests.Single().FileName : fixture.Requests[0].FileName;
        if (kind == ProviderKind.Codex) Assert.Equal("rpc-dispose", fixture.Events[^1]);

        await prepared.SendAsync(default);

        Assert.Equal(preparationExecutable, fixture.Requests.Single(IsGeneration).FileName);
        Assert.Equal(1, fixture.Runtime.Acquisitions);
        Assert.Equal(1, fixture.Runtime.Active);
        await Assert.ThrowsAsync<InvalidOperationException>(() => prepared.SendAsync(default));
        Assert.Single(fixture.Requests, IsGeneration);
        await prepared.DisposeAsync();
        fixture.AssertReleased();
        await prepared.DisposeAsync();
        Assert.Equal(1, fixture.Runtime.Releases);
    }

    [Theory]
    [InlineData(ProviderKind.Claude)]
    [InlineData(ProviderKind.Codex)]
    public async Task AbandonedPreparedResetReleasesWithoutSending(ProviderKind kind)
    {
        using var fixture = new Fixture(kind);
        var provider = Assert.IsAssignableFrom<IPreparedResetMessageProvider>(fixture.Provider());
        var prepared = await provider.PrepareResetMessageAsync(fixture.Account, default);
        await prepared.DisposeAsync();
        Assert.DoesNotContain(fixture.Requests, IsGeneration);
        fixture.AssertReleased();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => prepared.SendAsync(default));
    }

    [Theory]
    [InlineData(ProviderKind.Claude)]
    [InlineData(ProviderKind.Codex)]
    public async Task IdentityChangedAfterPreparationCannotSend(ProviderKind kind)
    {
        using var fixture = new Fixture(kind);
        var provider = Assert.IsAssignableFrom<IPreparedResetMessageProvider>(fixture.Provider());
        await using (var prepared = await provider.PrepareResetMessageAsync(fixture.Account, default))
        {
            await prepared.ValidateAsync(default);
            fixture.Account.IdentityKey = "different-account";
            var error = await Assert.ThrowsAsync<ProviderException>(() => prepared.SendAsync(default));
            Assert.True(error.IdentityChanged);
            Assert.DoesNotContain(fixture.Requests, IsGeneration);
        }
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData(ProviderKind.Claude)]
    [InlineData(ProviderKind.Codex)]
    public async Task CancelledPreparedSendDoesNotStartGenerationAndCanRelease(ProviderKind kind)
    {
        using var fixture = new Fixture(kind);
        var provider = Assert.IsAssignableFrom<IPreparedResetMessageProvider>(fixture.Provider());
        await using (var prepared = await provider.PrepareResetMessageAsync(fixture.Account, default))
        {
            await prepared.ValidateAsync(default);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepared.SendAsync(new CancellationToken(true)));
        }
        Assert.DoesNotContain(fixture.Requests, IsGeneration);
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData(ProviderKind.Claude)]
    [InlineData(ProviderKind.Codex)]
    public async Task ResetWrapperDisposesLeaseAfterGenerationFailure(ProviderKind kind)
    {
        using var fixture = new Fixture(kind) { FailGeneration = true };
        var provider = Assert.IsAssignableFrom<IResetMessageProvider>(fixture.Provider());
        await Assert.ThrowsAsync<ProviderException>(() => provider.SendResetMessageAsync(fixture.Account, default));
        Assert.Single(fixture.Requests, IsGeneration);
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData(ProviderKind.Claude)]
    [InlineData(ProviderKind.Codex)]
    public async Task ValidationFailureReleasesBeforeReservationCouldHappen(ProviderKind kind)
    {
        using var fixture = new Fixture(kind) { FailIdentity = true };
        var provider = Assert.IsAssignableFrom<IPreparedResetMessageProvider>(fixture.Provider());
        await using (var prepared = await provider.PrepareResetMessageAsync(fixture.Account, default))
            await Assert.ThrowsAsync<ProviderException>(() => prepared.ValidateAsync(default));
        Assert.DoesNotContain(fixture.Requests, IsGeneration);
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData(ProviderKind.Claude)]
    [InlineData(ProviderKind.Codex)]
    public async Task ValidationCancellationReleasesBeforeSend(ProviderKind kind)
    {
        using var fixture = new Fixture(kind);
        using var cancellation = new CancellationTokenSource();
        fixture.AfterIdentity = cancellation.Cancel;
        var provider = Assert.IsAssignableFrom<IPreparedResetMessageProvider>(fixture.Provider());
        await using (var prepared = await provider.PrepareResetMessageAsync(fixture.Account, cancellation.Token))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepared.ValidateAsync(cancellation.Token));
        Assert.DoesNotContain(fixture.Requests, IsGeneration);
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData("fetch")]
    [InlineData("connect")]
    [InlineData("disconnect")]
    public async Task CodexRpcClosesBeforeRuntimeLeaseIsReleased(string operation)
    {
        using var fixture = new Fixture(ProviderKind.Codex);
        var provider = fixture.Provider();
        switch (operation)
        {
            case "fetch": await provider.FetchAsync(fixture.Account, null, default); break;
            case "connect": await provider.ConnectAsync(fixture.Account, null, default); break;
            case "disconnect": await provider.DisconnectAsync(fixture.Account, default); break;
        }
        Assert.Single(fixture.RpcRequests);
        Assert.Equal(new[] { "rpc-dispose", "release" }, fixture.Events.TakeLast(2));
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData("open")]
    [InlineData("initialize")]
    [InlineData("account/read")]
    [InlineData("account/rateLimits/read")]
    public async Task CodexRpcFailureDisposesSessionAndLease(string failingStep)
    {
        using var fixture = new Fixture(ProviderKind.Codex) { FailingRpc = failingStep };
        await Assert.ThrowsAsync<ProviderException>(() => fixture.Provider().FetchAsync(fixture.Account, null, default));
        if (failingStep != "open") Assert.Equal(new[] { "rpc-dispose", "release" }, fixture.Events.TakeLast(2));
        fixture.AssertReleased();
    }

    [Fact]
    public async Task CodexConfigFailureDoesNotLeakLeaseOrGenerate()
    {
        using var fixture = new Fixture(ProviderKind.Codex) { FailingRpc = "config/read" };
        var provider = Assert.IsAssignableFrom<IPreparedResetMessageProvider>(fixture.Provider());
        await using (var prepared = await provider.PrepareResetMessageAsync(fixture.Account, default))
            await Assert.ThrowsAsync<ProviderException>(() => prepared.ValidateAsync(default));
        Assert.Empty(fixture.Requests);
        Assert.Equal(new[] { "rpc-dispose", "release" }, fixture.Events.TakeLast(2));
        fixture.AssertReleased();
    }

    [Fact]
    public async Task ConcurrentDisposalWaitsForInFlightPreparedSendBeforeReleasingRuntime()
    {
        var events = new List<string>();
        var runtime = new LeaseRuntime(events);
        var lease = await runtime.AcquireAsync(ProviderKind.Claude, null, default);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepared = new PreparedResetMessage(lease, _ => completed.Task);
        var sending = prepared.SendAsync(default);
        var disposing = prepared.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        Assert.Equal(1, runtime.Active);
        completed.SetResult();
        await Task.WhenAll(sending, disposing);
        Assert.Equal(0, runtime.Active);
        Assert.Equal(1, runtime.Releases);
    }

    [Fact]
    public async Task ConcurrentDisposalWaitsForValidationBeforeReleasingRuntime()
    {
        var runtime = new LeaseRuntime([]);
        var lease = await runtime.AcquireAsync(ProviderKind.Codex, null, default);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = false;
        var prepared = new PreparedResetMessage(lease, _ => { sent = true; return Task.CompletedTask; }, _ => completed.Task);
        var validating = prepared.ValidateAsync(default);
        var disposing = prepared.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        Assert.Equal(1, runtime.Active);
        completed.SetResult();
        await Task.WhenAll(validating, disposing);
        Assert.False(sent);
        Assert.Equal(0, runtime.Active);
        Assert.Equal(1, runtime.Releases);
    }

    private static bool IsGeneration(ProcessRequest request) => request.Arguments.Contains(ClaudeUsageProvider.ResetMessagePrompt) ||
        request.Arguments.Contains(CodexUsageProvider.ResetMessagePrompt);

    private sealed class Fixture : IDisposable, IProcessRunner, ILoginInteraction
    {
        public Fixture(ProviderKind kind)
        {
            Account = new() { Provider = kind, IdentityKey = kind switch
            {
                ProviderKind.Claude => "claude:fixture", ProviderKind.Codex => "codex:fixture", _ => "antigravity:fixture@example.invalid"
            }, Plan = "pro" };
            Runtime = new(Events);
        }
        public AppPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "aisa-lease-provider-" + Guid.NewGuid().ToString("N")));
        public AccountSettings Account { get; }
        public LeaseRuntime Runtime { get; }
        public List<string> Events { get; } = [];
        public List<ProcessRequest> Requests { get; } = [];
        public List<ProcessRequest> RpcRequests { get; } = [];
        public int? FailingProcess { get; init; }
        public string? FailingRpc { get; init; }
        public bool FailGeneration { get; init; }
        public bool FailIdentity { get; init; }
        public Action? AfterIdentity { get; set; }

        public IUsageProvider Provider() => Account.Provider switch
        {
            ProviderKind.Claude => new ClaudeUsageProvider(Paths, Runtime, this, this),
            ProviderKind.Antigravity => new AntigravityUsageProvider(Paths, Runtime, this, this),
            _ => new CodexUsageProvider(Paths, Runtime, this, this) { OpenRpcConnection = request =>
            {
                AssertActive(request);
                RpcRequests.Add(request);
                Events.Add("rpc-open");
                if (FailingRpc == "open") throw new IOException("fixture");
                return new FakeRpc(this);
            } }
        };

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            AssertActive(request);
            Requests.Add(request);
            Events.Add(IsGeneration(request) ? "generate" : "process");
            if (FailingProcess == Requests.Count - 1 || FailGeneration && IsGeneration(request))
                return Task.FromResult(new ProcessResult(1, "", "network failed"));
            string output;
            if (Account.Provider == ProviderKind.Claude)
            {
                if (request.Arguments.Contains("--version")) output = "2.1.288 (Claude Code)";
                else if (request.Arguments.Contains("status"))
                {
                    output = JsonSerializer.Serialize(new { loggedIn = !FailIdentity, authMethod = "claude.ai", accountId = "fixture", subscriptionType = "pro", configDirectory = Paths.ProfileDirectory(Account) });
                    AfterIdentity?.Invoke();
                }
                else if (request.Arguments.Contains("/usage")) output = """{"type":"assistant","usage_report":{"rate_limits":{"limits":[{"kind":"session","percent":20}]}}}""";
                else output = """{"type":"result","subtype":"success","is_error":false,"num_turns":1}""";
            }
            else if (Account.Provider == ProviderKind.Antigravity)
            {
                if (request.Arguments.Contains("--version")) output = "agy version 1.3.1";
                else if (request.Arguments.Contains("/usage")) output = """{"status":"SUCCESS","num_turns":0,"command":{"name":"usage","data":{"groups":[{"name":"Gemini","buckets":[{"id":"weekly","name":"Weekly","remaining_fraction":0.75}]}]}}}""";
                else if (request.Arguments.Contains("/credits")) output = """{"status":"SUCCESS","num_turns":0,"command":{"name":"credits","data":{"remaining_credits":100}}}""";
                else output = "Already authenticated as fixture@example.invalid.";
            }
            else output = """{"type":"turn.completed","usage":{"input_tokens":10,"output_tokens":1}}""";
            return Task.FromResult(new ProcessResult(0, output, ""));
        }

        public Task OpenBrowserAsync(Uri uri, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Assert.Equal(1, Runtime.Active); Events.Add("browser"); return Task.CompletedTask; }
        public Task<string?> RequestInputAsync(string title, string message, CancellationToken token) => throw new InvalidOperationException("Fixture must not prompt.");
        public void AssertReleased()
        { Assert.Equal(1, Runtime.Acquisitions); Assert.Equal(1, Runtime.Releases); Assert.Equal(0, Runtime.Active); Assert.Equal("release", Events[^1]); }
        private void AssertActive(ProcessRequest request)
        { Assert.Equal(1, Runtime.Active); Assert.Equal(Runtime.CurrentPath, request.FileName); }
        public void Dispose() { if (Directory.Exists(Paths.Root)) Directory.Delete(Paths.Root, true); }
    }

    private sealed class FakeRpc(Fixture fixture) : ICodexRpcConnection
    {
        public Task InitializeAsync(CancellationToken token) { Check("initialize", token); return Task.CompletedTask; }
        public Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken token)
        {
            Check(method, token);
            var json = method switch
            {
                "account/read" => fixture.FailIdentity ? "{}" : """{"account":{"type":"chatgpt","accountId":"fixture","planType":"pro"}}""",
                "account/rateLimits/read" => """{"rateLimits":{"primary":{"usedPercent":20}}}""",
                "config/read" => """{"config":{"mcp_servers":{"fixture-server":{}}}}""",
                "account/login/start" => """{"loginId":"fixture","authUrl":"https://auth.openai.com/authorize?client_id=fixture"}""",
                _ => "{}"
            };
            if (method == "account/read") fixture.AfterIdentity?.Invoke();
            return Task.FromResult(Parse(json));
        }
        public Task<JsonElement> WaitForNotificationAsync(string method, string? loginId, CancellationToken token)
        { Check(method, token); return Task.FromResult(Parse("""{"success":true}""")); }
        public ValueTask DisposeAsync()
        { Assert.Equal(1, fixture.Runtime.Active); fixture.Events.Add("rpc-dispose"); return ValueTask.CompletedTask; }
        private void Check(string method, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(1, fixture.Runtime.Active);
            fixture.Events.Add(method);
            if (fixture.FailingRpc == method) throw new IOException("fixture");
        }
        private static JsonElement Parse(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    }

    private sealed class LeaseRuntime(List<string> events) : IRuntimeManager
    {
        public int Acquisitions { get; private set; }
        public int Releases { get; private set; }
        public int Active { get; private set; }
        public string CurrentPath { get; private set; } = "";
        public Task<string> EnsureAsync(ProviderKind provider, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Provider operations must acquire a lease, not resolve another executable path.");
        public Task<RuntimeLease> AcquireAsync(ProviderKind provider, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Acquisitions++;
            Active++;
            CurrentPath = Path.Combine(Path.GetTempPath(), "fixture-runtime-" + Acquisitions + ".exe");
            events.Add("acquire");
            return Task.FromResult(new RuntimeLease(CurrentPath, "fixture", () => { Active--; Releases++; events.Add("release"); }));
        }
    }
}
