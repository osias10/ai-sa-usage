using System.Text.Json;
using AIUsage.Core;

namespace AIUsage.Providers;

public sealed partial class ClaudeUsageProvider : IResetMessageProvider
{
    public const string ResetMessagePrompt = "Reply with exactly OK.";

    public async Task SendResetMessageAsync(AccountSettings account, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateAccount(account);
        if (string.IsNullOrWhiteSpace(account.IdentityKey))
            throw new ProviderException(UsageState.SignInRequired, "자동 메시지를 사용하려면 계정을 다시 연결해 주세요.");

        var executable = await PrepareAsync(account, null, cancellationToken).ConfigureAwait(false);
        var identity = await ReadIdentityAsync(executable, account, cancellationToken).ConfigureAwait(false);
        ProviderIdentity.EnsureMatches(account, identity.IdentityKey);
        if (string.IsNullOrWhiteSpace(identity.Plan) || string.Equals(identity.Plan, "free", StringComparison.OrdinalIgnoreCase))
            throw new ProviderException(UsageState.Unavailable, "Claude 구독 요금제를 확인하지 못해 메시지를 보내지 않았습니다.");

        // Use the existing isolated subscription profile, but never its customizations or project files.
        // Bare mode cannot be used here: it deliberately ignores subscription OAuth credentials.
        var request = Request(executable, account,
            ["--safe-mode", "--restricted", "-p", ResetMessagePrompt,
                "--output-format", "json", "--model", "haiku", "--effort", "low",
                "--tools", "", "--disallowedTools", "*", "--strict-mcp-config",
                "--no-session-persistence", "--permission-prompts", "none", "--permission-mode", "dontAsk",
                "--no-chrome", "--disable-slash-commands", "--max-turns", "1",
                "--system-prompt", "Respond briefly without using tools.",
                "--settings", "{\"disableAllHooks\":true,\"fastMode\":false,\"forceLoginMethod\":\"claudeai\"}"],
            TimeSpan.FromSeconds(60));
        var environment = new Dictionary<string, string?>(request.Environment);
        foreach (var name in new[]
        {
            "ANTHROPIC_CUSTOM_HEADERS", "ANTHROPIC_BETAS", "ANTHROPIC_MODEL", "ANTHROPIC_DEFAULT_MODEL",
            "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_SMALL_FAST_MODEL", "ANTHROPIC_FEDERATION_RULE_ID",
            "ANTHROPIC_ORGANIZATION_ID", "ANTHROPIC_WORKSPACE_ID", "ANTHROPIC_AWS_API_KEY",
            "ANTHROPIC_AWS_BASE_URL", "ANTHROPIC_AWS_WORKSPACE_ID", "CLAUDE_CODE_USE_ANTHROPIC_AWS",
            "CLAUDE_CODE_USE_MANTLE", "CLAUDE_CODE_SIMPLE", "CLAUDE_CODE_EXTRA_BODY",
            "CLAUDE_CODE_EFFORT_LEVEL", "CLAUDE_CODE_RETRY_WATCHDOG", "CLAUDE_CODE_FORCE_SESSION_PERSISTENCE"
        }) environment[name] = null;
        environment["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = "128";
        environment["CLAUDE_CODE_MAX_RETRIES"] = "0";
        environment["MAX_THINKING_TOKENS"] = "0";
        environment["CLAUDE_CODE_AUTO_CONNECT_IDE"] = "false";
        environment["CLAUDE_CODE_SKIP_PROMPT_HISTORY"] = "1";
        cancellationToken.ThrowIfCancellationRequested();
        var result = await ProviderProcess.RunAsync(runner, new ProcessRequest
        {
            FileName = request.FileName, Arguments = request.Arguments,
            WorkingDirectory = request.WorkingDirectory, Environment = environment, Timeout = request.Timeout
        }, cancellationToken).ConfigureAwait(false);
        ProviderErrors.EnsureSuccess(result);
        ValidateResetMessageResult(result.StandardOutput);
    }

    private static void ValidateResetMessageResult(string output)
    {
        var result = ProviderJson.ReadObject(output);
        if (ProviderJson.Boolean(result, "is_error") == true)
            throw ProviderErrors.FromText(ProviderJson.String(result, "result") ??
                (result.TryGetProperty("errors", out var errors) ? errors.ToString() : "reset message failed"));
        if (ProviderJson.String(result, "type") != "result" || ProviderJson.String(result, "subtype") != "success" ||
            ProviderJson.Boolean(result, "is_error") != false ||
            !result.TryGetProperty("num_turns", out var turns) || turns.ValueKind != JsonValueKind.Number ||
            !turns.TryGetInt32(out var count) || count != 1)
            throw ProviderJson.Unsupported("자동 메시지의 완료 여부를 확인하지 못했습니다. 같은 초기화에는 다시 보내지 않습니다.");
        // Only completion metadata is used. Never persist or display generated text or billing estimates.
    }
}
