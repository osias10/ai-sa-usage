using System.Runtime.CompilerServices;
using System.Text.Json;
using AIUsage.Core;

[assembly: InternalsVisibleTo("AIUsage.Tests")]

namespace AIUsage.Providers;

public sealed partial class CodexUsageProvider : IResetMessageProvider
{
    public const string ResetMessagePrompt = "Reply with exactly OK. Do not use tools or read any files.";

    public async Task SendResetMessageAsync(AccountSettings account, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (account.Provider != Kind) throw new ArgumentException("Provider mismatch.", nameof(account));
        if (string.IsNullOrWhiteSpace(account.IdentityKey))
            throw new ProviderException(UsageState.SignInRequired, "자동 메시지를 사용하려면 계정을 다시 연결해 주세요.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            List<string> mcpServers;
            await using (var session = await OpenAsync(account, null, deadline.Token).ConfigureAwait(false))
            {
                var identity = ParseAccount(await session.RequestAsync("account/read", new { refreshToken = false }, deadline.Token).ConfigureAwait(false));
                ProviderIdentity.EnsureMatches(account, identity.IdentityKey);
                if (string.IsNullOrWhiteSpace(identity.Plan))
                    throw new ProviderException(UsageState.Unavailable, "ChatGPT 요금제를 확인하지 못해 메시지를 보내지 않았습니다.");
                var config = await session.RequestAsync("config/read", new { includeLayers = false, cwd = paths.WorkDirectory }, deadline.Token).ConfigureAwait(false);
                mcpServers = ReadResetMcpServers(config);
            }
            var executable = await runtimes.EnsureAsync(Kind, null, deadline.Token).ConfigureAwait(false);
            await ExecuteResetMessageAsync(runner, CreateResetMessageRequest(executable, paths, account, mcpServers), deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ProviderException(UsageState.Offline, "자동 메시지 응답 시간이 초과되었습니다. 같은 초기화에는 다시 보내지 않습니다."); }
        catch (IOException)
        { throw new ProviderException(UsageState.Offline, "Codex 자동 메시지 연결을 확인하지 못했습니다."); }
    }

    internal static List<string> ReadResetMcpServers(JsonElement result)
    {
        if (!result.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
            throw ProviderJson.Unsupported();
        if (!config.TryGetProperty("mcp_servers", out var servers) || servers.ValueKind == JsonValueKind.Null) return [];
        if (servers.ValueKind != JsonValueKind.Object) throw ProviderJson.Unsupported();
        return servers.EnumerateObject().Select(x => x.Name).ToList();
    }

    internal static ProcessRequest CreateResetMessageRequest(string executable, AppPaths appPaths, AccountSettings account, IReadOnlyList<string> mcpServers)
    {
        if (account.Provider != ProviderKind.Codex) throw new ArgumentException("Provider mismatch.", nameof(account));
        List<string> args = ["exec", "--ignore-user-config", "--ignore-rules", "--ephemeral", "--skip-git-repo-check", "--sandbox", "read-only", "--json", "--color", "never"];
        foreach (var value in new[]
        {
            "cli_auth_credentials_store=\"keyring\"", "forced_login_method=\"chatgpt\"", "model_provider=\"openai\"",
            "approval_policy=\"never\"", "web_search=\"disabled\"", "project_doc_max_bytes=0",
            "model_reasoning_effort=\"low\"", "model_verbosity=\"low\"", "history.persistence=\"none\"",
            "skills.include_instructions=false", "tools.update_plan.enabled=false",
            "developer_instructions=\"Respond with exactly OK. Do not use tools or read files.\""
        }) { args.Add("-c"); args.Add(value); }
        foreach (var feature in new[]
        {
            "shell_tool", "shell_snapshot", "apps", "plugins", "hooks", "multi_agent", "multi_agent_v2",
            "browser_use", "browser_use_external", "computer_use", "image_generation", "view_image",
            "memories", "skill_search", "skill_mcp_dependency_install", "tool_suggest", "remote_plugin",
            "unbounded_connection_retries", "sleep_tool", "goals", "code_mode", "code_mode_host"
        }) { args.Add("--disable"); args.Add(feature); }
        // Empty maps merge with lower config layers; disable each resolved server explicitly instead.
        foreach (var name in mcpServers)
        {
            args.Add("-c");
            args.Add("mcp_servers." + JsonSerializer.Serialize(name) + ".enabled=false");
        }
        args.Add(ResetMessagePrompt);
        var environment = ProviderProcess.EnvironmentFor(ProviderKind.Codex, appPaths.ProfileDirectory(account));
        foreach (var name in new[] { "OPENAI_ORG_ID", "OPENAI_ORGANIZATION", "OPENAI_PROJECT", "CODEX_CONFIG_OVERRIDES", "CODEX_MODEL_PROVIDER" })
            environment[name] = null;
        return new()
        {
            FileName = executable, Arguments = args, Environment = environment,
            WorkingDirectory = appPaths.WorkDirectory, Timeout = TimeSpan.FromSeconds(60)
        };
    }

    internal static async Task ExecuteResetMessageAsync(IProcessRunner processRunner, ProcessRequest request, CancellationToken token)
    {
        var result = await ProviderProcess.RunAsync(processRunner, request, token).ConfigureAwait(false);
        ProviderErrors.EnsureSuccess(result);
        var completed = 0;
        foreach (var item in ProviderJson.ReadDocuments(result.StandardOutput))
        {
            switch (ProviderJson.String(item, "type"))
            {
                case "error":
                case "turn.failed":
                    throw ProviderErrors.FromText(item.ToString());
                case "turn.completed":
                    completed++;
                    if (!item.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object ||
                        ProviderJson.Number(usage, "input_tokens") is not > 0 || ProviderJson.Number(usage, "output_tokens") is not > 0)
                        throw ProviderJson.Unsupported("자동 메시지의 완료 여부를 확인하지 못했습니다.");
                    break;
                case "item.started":
                case "item.updated":
                case "item.completed":
                    if (item.TryGetProperty("item", out var details) && ProviderJson.String(details, "type") is { } kind &&
                        kind is not ("agent_message" or "reasoning"))
                        throw ProviderJson.Unsupported("자동 메시지에서 예상하지 않은 동작이 감지되었습니다.");
                    break;
            }
        }
        if (completed != 1) throw ProviderJson.Unsupported("자동 메시지의 완료 여부를 확인하지 못했습니다. 같은 초기화에는 다시 보내지 않습니다.");
    }
}
