using System.Text.Json;
using AIUsage.Core;
using AIUsage.Providers;
using Xunit;

namespace AIUsage.Tests;

public sealed class CodexResetMessageTests
{
    private const string Completed = """
        {"type":"thread.started","thread_id":"fixture"}
        {"type":"turn.started"}
        {"type":"item.completed","item":{"id":"0","type":"agent_message","text":"OK"}}
        {"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":1}}
        """;

    [Fact]
    public async Task OneEphemeralRequestUsesSameAccountAndRestrictsExternalActions()
    {
        var account = new AccountSettings { Provider = ProviderKind.Codex, IdentityKey = "codex:fixture" };
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "aiusage-codex-request-test"));
        var request = CodexUsageProvider.CreateResetMessageRequest("unused-executable", paths, account, ["external-server", "name.with.dots"]);
        var runner = new FakeRunner(new(0, Completed, ""));
        await CodexUsageProvider.ExecuteResetMessageAsync(runner, request, default);
        Assert.Single(runner.Requests);
        Assert.Equal("exec", request.Arguments[0]);
        Assert.Equal(CodexUsageProvider.ResetMessagePrompt, request.Arguments[^1]);
        Assert.Equal(paths.WorkDirectory, request.WorkingDirectory);
        Assert.Equal(paths.ProfileDirectory(account), request.Environment["CODEX_HOME"]);
        Assert.Null(request.Environment["OPENAI_API_KEY"]);
        Assert.Null(request.Environment["CODEX_API_KEY"]);
        Assert.Null(request.Environment["OPENAI_BASE_URL"]);
        Assert.Null(request.StandardInput);
        Assert.Null(request.OnOutput);
        Assert.Null(request.OnError);
        Assert.Equal(TimeSpan.FromSeconds(60), request.Timeout);
        foreach (var flag in new[] { "--ignore-user-config", "--ignore-rules", "--ephemeral", "--skip-git-repo-check", "--json" })
            Assert.Contains(flag, request.Arguments);
        Assert.Equal("read-only", Values(request, "--sandbox").Single());
        var config = Values(request, "-c").ToArray();
        Assert.Contains("forced_login_method=\"chatgpt\"", config);
        Assert.Contains("cli_auth_credentials_store=\"keyring\"", config);
        Assert.Contains("model_provider=\"openai\"", config);
        Assert.Contains("approval_policy=\"never\"", config);
        Assert.Contains("web_search=\"disabled\"", config);
        Assert.Contains("project_doc_max_bytes=0", config);
        Assert.Contains("skills.include_instructions=false", config);
        Assert.Contains("mcp_servers.\"external-server\".enabled=false", config);
        Assert.Contains("mcp_servers.\"name.with.dots\".enabled=false", config);
        var disabled = Values(request, "--disable").ToArray();
        foreach (var feature in new[] { "shell_tool", "hooks", "apps", "plugins", "browser_use", "computer_use", "multi_agent", "image_generation", "memories", "unbounded_connection_retries" })
            Assert.Contains(feature, disabled);
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", request.Arguments);
    }

    [Fact]
    public void ResolvedMcpNamesAreRetainedForExplicitDisableOverrides()
    {
        using var document = JsonDocument.Parse("""{"config":{"mcp_servers":{"alpha":{},"name.with.dots":{"enabled":true}}}}""");
        Assert.Equal(new[] { "alpha", "name.with.dots" }, CodexUsageProvider.ReadResetMcpServers(document.RootElement));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"config\":null}")]
    [InlineData("{\"config\":{\"mcp_servers\":[]}}")]
    public void UnknownConfigCannotSilentlySkipToolIsolation(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<ProviderException>(() => CodexUsageProvider.ReadResetMcpServers(document.RootElement));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{\"type\":\"turn.started\"}")]
    [InlineData("{\"type\":\"turn.completed\"}")]
    [InlineData("{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":0,\"output_tokens\":0}}")]
    [InlineData("{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":\"1\"}}")]
    public async Task MissingOrInvalidCompletionFailsWithoutAnotherProcess(string output)
    {
        var runner = new FakeRunner(new(0, output, ""));
        await Assert.ThrowsAsync<ProviderException>(() => CodexUsageProvider.ExecuteResetMessageAsync(runner, Request(), default));
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task MultipleCompletedTurnsAreNotAcceptedAsASingleMessage()
    {
        var runner = new FakeRunner(new(0, Completed + "\n" + Completed, ""));
        await Assert.ThrowsAsync<ProviderException>(() => CodexUsageProvider.ExecuteResetMessageAsync(runner, Request(), default));
        Assert.Single(runner.Requests);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("turn.failed")]
    public async Task FailureEventIsNotSuccessDespiteAZeroExitCode(string type)
    {
        var runner = new FakeRunner(new(0, JsonSerializer.Serialize(new { type, message = "429 rate limit" }), ""));
        var error = await Assert.ThrowsAsync<ProviderException>(() => CodexUsageProvider.ExecuteResetMessageAsync(runner, Request(), default));
        Assert.Equal(UsageState.RateLimited, error.State);
        Assert.Single(runner.Requests);
    }

    [Theory]
    [InlineData("command_execution")]
    [InlineData("file_change")]
    [InlineData("mcp_tool_call")]
    [InlineData("web_search")]
    public async Task UnexpectedToolEventsCannotBeReportedAsOrdinaryMessageSuccess(string type)
    {
        var output = JsonSerializer.Serialize(new { type = "item.completed", item = new { type } }) + "\n" + Completed;
        var runner = new FakeRunner(new(0, output, ""));
        await Assert.ThrowsAsync<ProviderException>(() => CodexUsageProvider.ExecuteResetMessageAsync(runner, Request(), default));
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task ProcessFailureCannotBeHiddenByCompletionMetadata()
    {
        var runner = new FakeRunner(new(1, Completed, "network unavailable"));
        await Assert.ThrowsAsync<ProviderException>(() => CodexUsageProvider.ExecuteResetMessageAsync(runner, Request(), default));
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task CancellationPreventsGeneration()
    {
        var runner = new FakeRunner(new(0, Completed, ""));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CodexUsageProvider.ExecuteResetMessageAsync(runner, Request(), new CancellationToken(true)));
        Assert.Empty(runner.Requests);
    }

    private static ProcessRequest Request() => CodexUsageProvider.CreateResetMessageRequest("fake-never-run", new AppPaths(Path.GetTempPath()), new AccountSettings { Provider = ProviderKind.Codex }, []);
    private static IEnumerable<string> Values(ProcessRequest request, string name)
    {
        for (var i = 0; i < request.Arguments.Count - 1; i++)
            if (request.Arguments[i] == name) yield return request.Arguments[i + 1];
    }
    private sealed class FakeRunner(ProcessResult result) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }
}
