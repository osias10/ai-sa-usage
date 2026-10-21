using System.Text.Json;
using AIUsage.Core;
using AIUsage.Providers;
using Xunit;

namespace AIUsage.Tests;

public sealed class ClaudeResetMessageTests
{
    private const string Completed = """{"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"OK","total_cost_usd":0.001}""";

    [Fact]
    public async Task SendsOneBoundedMessageUsingTheVerifiedSubscriptionProfileWithoutToolsOrPersistence()
    {
        using var fixture = new Fixture();
        var runner = fixture.Runner(Completed);
        await fixture.Provider(runner).SendResetMessageAsync(fixture.Account, default);

        Assert.Equal(3, runner.Requests.Count);
        Assert.Equal(new[] { "--version" }, runner.Requests[0].Arguments);
        Assert.Equal(new[] { "auth", "status" }, runner.Requests[1].Arguments);
        var request = runner.Requests[2];
        Assert.Equal(fixture.Paths.WorkDirectory, request.WorkingDirectory);
        Assert.Equal("Reply with exactly OK.", Argument(request, "-p"));
        Assert.Equal("haiku", Argument(request, "--model"));
        Assert.Equal("low", Argument(request, "--effort"));
        Assert.Equal("", Argument(request, "--tools"));
        Assert.Equal("*", Argument(request, "--disallowedTools"));
        Assert.Equal("none", Argument(request, "--permission-prompts"));
        Assert.Equal("dontAsk", Argument(request, "--permission-mode"));
        Assert.Equal("1", Argument(request, "--max-turns"));
        Assert.Equal("json", Argument(request, "--output-format"));
        foreach (var flag in new[] { "--safe-mode", "--restricted", "--strict-mcp-config", "--no-session-persistence", "--no-chrome", "--disable-slash-commands" })
            Assert.Contains(flag, request.Arguments);
        foreach (var flag in new[] { "--bare", "--continue", "--resume", "--dangerously-skip-permissions", "--add-dir", "--mcp-config" })
            Assert.DoesNotContain(flag, request.Arguments);
        using var settings = JsonDocument.Parse(Argument(request, "--settings"));
        Assert.True(settings.RootElement.GetProperty("disableAllHooks").GetBoolean());
        Assert.False(settings.RootElement.GetProperty("fastMode").GetBoolean());
        Assert.Equal("claudeai", settings.RootElement.GetProperty("forceLoginMethod").GetString());
        Assert.Null(request.StandardInput);
        Assert.Null(request.OnOutput);
        Assert.Null(request.OnError);
        Assert.Equal(TimeSpan.FromSeconds(60), request.Timeout);
        Assert.Equal("128", request.Environment["CLAUDE_CODE_MAX_OUTPUT_TOKENS"]);
        Assert.Equal("0", request.Environment["CLAUDE_CODE_MAX_RETRIES"]);
        Assert.Equal("0", request.Environment["MAX_THINKING_TOKENS"]);
        Assert.Equal("false", request.Environment["CLAUDE_CODE_AUTO_CONNECT_IDE"]);
        Assert.Equal("1", request.Environment["CLAUDE_CODE_SKIP_PROMPT_HISTORY"]);
        foreach (var name in new[]
        {
            "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_BASE_URL",
            "ANTHROPIC_PROFILE", "ANTHROPIC_CUSTOM_HEADERS", "ANTHROPIC_DEFAULT_HAIKU_MODEL",
            "ANTHROPIC_FEDERATION_RULE_ID", "ANTHROPIC_ORGANIZATION_ID", "ANTHROPIC_WORKSPACE_ID",
            "CLAUDE_CODE_USE_ANTHROPIC_AWS", "CLAUDE_CODE_USE_MANTLE", "CLAUDE_CODE_SIMPLE",
            "CLAUDE_CODE_EXTRA_BODY", "CLAUDE_CODE_EFFORT_LEVEL", "CLAUDE_CODE_RETRY_WATCHDOG"
        }) Assert.Null(request.Environment[name]);
        Assert.All(runner.Requests, child => Assert.Equal(fixture.Paths.ProfileDirectory(fixture.Account), child.Environment["CLAUDE_CONFIG_DIR"]));
        Assert.Empty(Directory.GetFiles(fixture.Paths.WorkDirectory));
    }

    [Fact]
    public async Task MissingRegisteredIdentityCannotSendOrStartAnyProviderProcess()
    {
        using var fixture = new Fixture();
        fixture.Account.IdentityKey = null;
        var runner = fixture.Runner(Completed);
        var error = await Assert.ThrowsAsync<ProviderException>(() => fixture.Provider(runner).SendResetMessageAsync(fixture.Account, default));
        Assert.Equal(UsageState.SignInRequired, error.State);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task DifferentAccountCannotReceiveTheMessage()
    {
        using var fixture = new Fixture();
        fixture.Account.IdentityKey = "claude:another-account";
        var runner = fixture.Runner(Completed);
        var error = await Assert.ThrowsAsync<ProviderException>(() => fixture.Provider(runner).SendResetMessageAsync(fixture.Account, default));
        Assert.True(error.IdentityChanged);
        Assert.Equal(2, runner.Requests.Count);
    }

    [Theory]
    [InlineData("api_key")]
    [InlineData("api_key_helper")]
    [InlineData("oauth_token")]
    [InlineData("third_party")]
    [InlineData("none")]
    public async Task NonSubscriptionAuthenticationCannotSend(string method)
    {
        using var fixture = new Fixture();
        var runner = fixture.Runner(Completed, authMethod: method);
        var error = await Assert.ThrowsAsync<ProviderException>(() => fixture.Provider(runner).SendResetMessageAsync(fixture.Account, default));
        Assert.Equal(UsageState.SignInRequired, error.State);
        Assert.Equal(2, runner.Requests.Count);
    }

    [Fact]
    public async Task GlobalProfileFallbackCannotSend()
    {
        using var fixture = new Fixture();
        var runner = fixture.Runner(Completed, directory: Path.Combine(fixture.Paths.Root, "different-profile"));
        await Assert.ThrowsAsync<ProviderException>(() => fixture.Provider(runner).SendResetMessageAsync(fixture.Account, default));
        Assert.Equal(2, runner.Requests.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("free")]
    public async Task UnverifiedPaidPlanCannotUseTheSavedPlanAsAFallback(string? plan)
    {
        using var fixture = new Fixture();
        var runner = fixture.Runner(Completed, plan: plan);
        await Assert.ThrowsAsync<ProviderException>(() => fixture.Provider(runner).SendResetMessageAsync(fixture.Account, default));
        Assert.Equal(2, runner.Requests.Count);
        Assert.Equal("max", fixture.Account.Plan);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"type\":\"result\",\"subtype\":\"success\",\"num_turns\":1}")]
    [InlineData("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"num_turns\":0}")]
    [InlineData("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"num_turns\":2}")]
    [InlineData("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"num_turns\":1.5}")]
    [InlineData("{\"type\":\"result\",\"subtype\":\"error_max_turns\",\"is_error\":false,\"num_turns\":1}")]
    [InlineData("{\"type\":\"assistant\",\"subtype\":\"success\",\"is_error\":false,\"num_turns\":1}")]
    public async Task InvalidCompletionDoesNotCountAsSuccessOrTriggerARetry(string output)
    {
        using var fixture = new Fixture();
        var runner = fixture.Runner(output);
        var error = await Assert.ThrowsAsync<ProviderException>(() => fixture.Provider(runner).SendResetMessageAsync(fixture.Account, default));
        Assert.Equal(UsageState.UpdateRequired, error.State);
        Assert.Equal(3, runner.Requests.Count);
    }

    [Fact]
    public async Task ResultErrorIsNotSuccessEvenWhenProcessExitsZero()
    {
        using var fixture = new Fixture();
        var runner = fixture.Runner("""{"type":"result","subtype":"success","is_error":true,"num_turns":1,"result":"429 rate limit retry-after: 120"}""");
        var error = await Assert.ThrowsAsync<ProviderException>(() => fixture.Provider(runner).SendResetMessageAsync(fixture.Account, default));
        Assert.Equal(UsageState.RateLimited, error.State);
        Assert.Equal(3, runner.Requests.Count);
    }

    [Fact]
    public async Task NonZeroExitCannotCountAsSuccessfulGeneration()
    {
        using var fixture = new Fixture();
        var runner = fixture.Runner(Completed, exitCode: 1);
        await Assert.ThrowsAsync<ProviderException>(() => fixture.Provider(runner).SendResetMessageAsync(fixture.Account, default));
        Assert.Equal(3, runner.Requests.Count);
    }

    [Fact]
    public async Task AlreadyCancelledCannotStartAProcess()
    {
        using var fixture = new Fixture();
        var runner = fixture.Runner(Completed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Provider(runner).SendResetMessageAsync(fixture.Account, new CancellationToken(true)));
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task CancellationAfterIdentityCheckPreventsGeneration()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var runner = fixture.Runner(Completed);
        runner.AfterRequest = request => { if (request.Arguments.Contains("status")) cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Provider(runner).SendResetMessageAsync(fixture.Account, cancellation.Token));
        Assert.Equal(2, runner.Requests.Count);
    }

    [Fact]
    public async Task WrongProviderCannotUseClaudeSubscription()
    {
        using var fixture = new Fixture();
        fixture.Account.Provider = ProviderKind.Codex;
        var runner = fixture.Runner(Completed);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Provider(runner).SendResetMessageAsync(fixture.Account, default));
        Assert.Empty(runner.Requests);
    }

    private static string Argument(ProcessRequest request, string flag)
    {
        var args = request.Arguments.ToList();
        var index = args.IndexOf(flag);
        Assert.InRange(index, 0, args.Count - 2);
        return args[index + 1];
    }

    private sealed class Fixture : IDisposable
    {
        public AppPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "aiusage-reset-message-test-" + Guid.NewGuid().ToString("N")));
        public AccountSettings Account { get; } = new()
        {
            Provider = ProviderKind.Claude, IdentityKey = "claude:test-account:test-org", Plan = "max"
        };
        public RecordingRunner Runner(string output, string authMethod = "claude.ai", string? plan = "max", string? directory = null, int exitCode = 0) => new(
            new(0, "2.1.288 (Claude Code)", ""),
            new(0, JsonSerializer.Serialize(new
            {
                loggedIn = true, authMethod, subscriptionType = plan, accountId = "test-account", orgId = "test-org",
                configDirectory = directory ?? Paths.ProfileDirectory(Account)
            }), ""),
            new(exitCode, output, ""));
        public IResetMessageProvider Provider(IProcessRunner runner) => new ClaudeUsageProvider(Paths, new FakeRuntime(), runner, new NoLogin());
        public void Dispose() { if (Directory.Exists(Paths.Root)) Directory.Delete(Paths.Root, true); }
    }

    private sealed class RecordingRunner(params ProcessResult[] results) : IProcessRunner
    {
        private readonly Queue<ProcessResult> remaining = new(results);
        public List<ProcessRequest> Requests { get; } = [];
        public Action<ProcessRequest>? AfterRequest { get; set; }
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            var result = remaining.Dequeue();
            AfterRequest?.Invoke(request);
            return Task.FromResult(result);
        }
    }
    private sealed class FakeRuntime : IRuntimeManager
    {
        public Task<string> EnsureAsync(ProviderKind provider, IProgress<ProviderProgress>? progress, CancellationToken token) => Task.FromResult("fake-executable-never-run");
    }
    private sealed class NoLogin : ILoginInteraction
    {
        public Task OpenBrowserAsync(Uri uri, CancellationToken token) => throw new InvalidOperationException("An automatic message must never launch a login flow.");
        public Task<string?> RequestInputAsync(string title, string message, CancellationToken token) => throw new InvalidOperationException("An automatic message must never prompt for login.");
    }
}
