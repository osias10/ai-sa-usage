using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using AIUsage.Core;
using AIUsage.Infrastructure;

namespace AIUsage.Providers;

internal static class ProviderProcess
{
    public static Dictionary<string, string?> EnvironmentFor(ProviderKind kind, string profile)
    {
        // A widget launched by an existing coding session must not inherit that session's credentials.
        var environment = new Dictionary<string, string?>
        {
            ["ANTHROPIC_API_KEY"] = null, ["ANTHROPIC_AUTH_TOKEN"] = null,
            ["ANTHROPIC_BASE_URL"] = null, ["ANTHROPIC_PROFILE"] = null,
            ["CLAUDE_CODE_OAUTH_TOKEN"] = null, ["CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR"] = null,
            ["CLAUDE_CODE_API_KEY_FILE_DESCRIPTOR"] = null, ["CLAUDECODE"] = null,
            ["CLAUDE_CODE_USE_BEDROCK"] = null, ["CLAUDE_CODE_USE_VERTEX"] = null,
            ["CLAUDE_CODE_USE_FOUNDRY"] = null, ["CLAUDE_CODE_USE_GATEWAY"] = null,
            ["OPENAI_API_KEY"] = null, ["CODEX_API_KEY"] = null, ["OPENAI_BASE_URL"] = null,
            ["GEMINI_API_KEY"] = null, ["GOOGLE_API_KEY"] = null,
            ["AGY_CLI_CDE_AUTH_ACTION"] = null, ["AGY_ADC_AUTH"] = null,
            ["GOOGLE_APPLICATION_CREDENTIALS"] = null,
            ["NO_COLOR"] = "1", ["TERM"] = "dumb"
        };
        if (kind == ProviderKind.Claude)
        {
            environment["CLAUDE_CONFIG_DIR"] = profile;
            environment["DISABLE_AUTOUPDATER"] = "1";
            // Keep the native quota request available while opting out of telemetry separately.
            // Clear inherited umbrella switches as well: a parent coding session may set them.
            environment["DISABLE_NONESSENTIAL_TRAFFIC"] = null;
            environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = null;
            environment["DISABLE_TELEMETRY"] = "1";
            environment["DISABLE_ERROR_REPORTING"] = "1";
            environment["CLAUDE_CODE_ENABLE_TELEMETRY"] = null;
        }
        if (kind == ProviderKind.Codex) environment["CODEX_HOME"] = profile;
        if (kind == ProviderKind.Antigravity) environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "1";
        return environment;
    }

    public static ProcessStartInfo StartInfo(ProcessRequest request)
    {
        var info = new ProcessStartInfo(request.FileName)
        {
            WorkingDirectory = request.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var arg in request.Arguments) info.ArgumentList.Add(arg);
        foreach (var pair in request.Environment)
            if (pair.Value == null) info.Environment.Remove(pair.Key); else info.Environment[pair.Key] = pair.Value;
        return info;
    }

    public static async Task<ProcessResult> RunAsync(IProcessRunner runner, ProcessRequest request, CancellationToken token)
    {
        try { return await runner.RunAsync(request, token).ConfigureAwait(false); }
        catch (TimeoutException) { throw new ProviderException(UsageState.Offline, "서비스 응답 시간이 초과되었습니다. 잠시 후 다시 시도해 주세요."); }
        catch (InvalidDataException) { throw ProviderJson.Unsupported(); }
        catch (System.ComponentModel.Win32Exception) { throw new ProviderException(UsageState.UpdateRequired, "연결 구성요소를 실행할 수 없습니다. 업데이트해 주세요."); }
        catch (IOException) { throw new ProviderException(UsageState.Offline, "연결 구성요소와 통신하지 못했습니다."); }
    }
}

/// <summary>Private, short-lived stdio transport. It never opens a TCP listener or starts an inference turn.</summary>
internal sealed class CodexRpcSession : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task stderr;
    private readonly List<JsonElement> notifications = [];
    private int sequence;

    public CodexRpcSession(ProcessRequest request)
    {
        process = new Process { StartInfo = ProviderProcess.StartInfo(request) };
        try
        {
            if (!process.Start()) throw new IOException("Process not started.");
            stderr = DrainAsync(process.StandardError);
        }
        catch { process.Dispose(); throw; }
    }

    public async Task InitializeAsync(CancellationToken token)
    {
        await RequestAsync("initialize", new { clientInfo = new { name = "ai_usage_widget", title = "AI Usage Widget", version = "0.1.0" }, capabilities = new { experimentalApi = false } }, token).ConfigureAwait(false);
        await WriteAsync(new { method = "initialized", @params = new { } }, token).ConfigureAwait(false);
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken token)
    {
        var id = ++sequence;
        await WriteAsync(new { id, method, @params = parameters }, token).ConfigureAwait(false);
        while (true)
        {
            var message = await ReadAsync(token).ConfigureAwait(false);
            if (message.TryGetProperty("id", out var responseId) && responseId.ValueKind == JsonValueKind.Number && responseId.GetInt32() == id && !message.TryGetProperty("method", out _))
            {
                if (message.TryGetProperty("error", out var error)) throw ProviderErrors.FromText(error.ToString());
                return message.TryGetProperty("result", out var result) ? result.Clone() : throw ProviderJson.Unsupported();
            }
            await HandleOtherAsync(message, token).ConfigureAwait(false);
        }
    }

    public async Task<JsonElement> WaitForNotificationAsync(string method, string? loginId, CancellationToken token)
    {
        while (true)
        {
            var index = notifications.FindIndex(Matches);
            if (index >= 0)
            {
                var result = notifications[index].GetProperty("params").Clone();
                notifications.RemoveAt(index);
                return result;
            }
            var message = await ReadAsync(token).ConfigureAwait(false);
            if (Matches(message)) return message.GetProperty("params").Clone();
            await HandleOtherAsync(message, token).ConfigureAwait(false);
        }
        bool Matches(JsonElement message) => ProviderJson.String(message, "method") == method &&
            message.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object &&
            (loginId == null || ProviderJson.String(parameters, "loginId") == loginId);
    }

    private async Task HandleOtherAsync(JsonElement message, CancellationToken token)
    {
        if (message.TryGetProperty("method", out _) && message.TryGetProperty("id", out var id))
        {
            // No tools, token handoff, or approval requests are authorized by a usage query.
            await WriteAsync(new { id = id.Clone(), error = new { code = -32601, message = "Usage-only client does not handle this request." } }, token).ConfigureAwait(false);
            return;
        }
        if (notifications.Count >= 256) notifications.RemoveAt(0);
        notifications.Add(message.Clone());
    }
    private async Task WriteAsync(object data, CancellationToken token)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(data).AsMemory(), token).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
    }
    private async Task<JsonElement> ReadAsync(CancellationToken token)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
            if (line == null) throw ProviderJson.Unavailable("Codex 연결 구성요소가 응답을 마치기 전에 종료되었습니다.");
            if (line.Length > 4 * 1024 * 1024) throw ProviderJson.Unsupported();
            if (string.IsNullOrWhiteSpace(line)) continue;
            return ProviderJson.ReadObject(line);
        }
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer).ConfigureAwait(false) != 0) { } }
        catch (IOException) { } catch (ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
        process.Dispose();
        try { await stderr.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
    }
}

internal static partial class BrowserCliLogin
{
    public static string StripTerminalEscapes(string text) => Osc().Replace(Ansi().Replace(text, ""), "");

    public static async Task<ProcessResult> RunTerminalAsync(ProcessRequest request, ProviderKind kind, ILoginInteraction interaction,
        IProgress<ProviderProgress>? progress, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(request.Timeout);
        try
        {
            await using var terminal = new ConPtySession(request);
            var output = new StringBuilder();
            var recent = new StringBuilder();
            var opened = new HashSet<string>(StringComparer.Ordinal);
            var requestedCode = false;
            while (await terminal.ReadAsync(deadline.Token).ConfigureAwait(false) is { } chunk)
            {
                output.Append(chunk);
                if (output.Length > 4 * 1024 * 1024) throw ProviderJson.Unsupported();
                recent.Append(chunk);
                if (recent.Length > 32768) recent.Remove(0, recent.Length - 32768);
                var text = StripTerminalEscapes(recent.ToString());
                foreach (Match match in Url().Matches(text))
                {
                    if (match.Index + match.Length == text.Length) continue;
                    var candidate = match.Value.TrimEnd('.', ',', ')', ']', '\'', '"');
                    if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || !IsTrustedLoginUri(uri, kind) || !IsAuthorizationUri(uri)) continue;
                    if (opened.Add(uri.AbsoluteUri))
                    {
                        progress?.Report(new("브라우저에서 로그인을 완료해 주세요."));
                        await interaction.OpenBrowserAsync(uri, deadline.Token).ConfigureAwait(false);
                    }
                }
                if (!requestedCode && (text.Contains("Paste code", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("authorization code:", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Enter the code", StringComparison.OrdinalIgnoreCase)))
                {
                    requestedCode = true;
                    var code = await interaction.RequestInputAsync("로그인 완료", "브라우저에 인증 코드가 표시되면 붙여 넣어 주세요. 브라우저에서 완료되었다면 이 창을 닫아 주세요.", deadline.Token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        if (code.Length > 4096 || code.Any(char.IsControl)) throw new ProviderException(UsageState.SignInRequired, "인증 코드 형식을 확인해 주세요.");
                        await terminal.WriteAsync(code + "\r", deadline.Token).ConfigureAwait(false);
                    }
                }
            }
            await terminal.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return new(terminal.ExitCode, StripTerminalEscapes(output.ToString()), "");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new ProviderException(UsageState.SignInRequired, "로그인 대기 시간이 초과되었습니다. 다시 연결해 주세요."); }
        catch (TimeoutException)
        { throw new ProviderException(UsageState.SignInRequired, "로그인 대기 시간이 초과되었습니다. 다시 연결해 주세요."); }
        catch (System.ComponentModel.Win32Exception)
        { throw new ProviderException(UsageState.UpdateRequired, "로그인 구성요소를 실행할 수 없습니다."); }
        catch (InvalidDataException) { throw ProviderJson.Unsupported(); }
        catch (IOException) { throw new ProviderException(UsageState.Unavailable, "로그인 구성요소와 통신하지 못했습니다. 다시 연결해 주세요."); }
    }

    // Only official sign-in hosts are allowed to open a browser. The URL is never logged or persisted.
    public static bool IsTrustedLoginUri(Uri uri, ProviderKind provider)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        var hosts = provider switch
        {
            ProviderKind.Claude => new[] { "claude.ai", "claude.com", "console.anthropic.com", "platform.claude.com" },
            ProviderKind.Codex => new[] { "auth.openai.com", "chatgpt.com", "auth.chatgpt.com" },
            _ => new[] { "accounts.google.com", "antigravity.google", "antigravity.google.com" }
        };
        return hosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<ProcessResult> RunAsync(ProcessRequest request, ProviderKind kind, ILoginInteraction interaction,
        IProgress<ProviderProgress>? progress, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        using var process = new Process { StartInfo = ProviderProcess.StartInfo(request) };
        var channel = Channel.CreateBounded<(bool Error, string Text)>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        try
        {
            process.Start();
            var stdoutTask = PumpAsync(process.StandardOutput, false, channel.Writer, deadline.Token);
            var stderrTask = PumpAsync(process.StandardError, true, channel.Writer, deadline.Token);
            var complete = CompleteAsync();
            StringBuilder stdout = new(), stderr = new(), recent = new();
            var opened = new HashSet<string>(StringComparer.Ordinal);
            var requestedCode = false;
            await foreach (var piece in channel.Reader.ReadAllAsync(deadline.Token).ConfigureAwait(false))
            {
                (piece.Error ? stderr : stdout).Append(piece.Text);
                if (stdout.Length + stderr.Length > 4 * 1024 * 1024) throw ProviderJson.Unsupported();
                recent.Append(piece.Text);
                if (recent.Length > 16384) recent.Remove(0, recent.Length - 16384);
                var text = StripTerminalEscapes(recent.ToString());
                foreach (Match match in Url().Matches(text))
                {
                    // Wait for a delimiter: stdout chunks may split a long OAuth URL in the middle.
                    if (match.Index + match.Length == text.Length) continue;
                    var candidate = match.Value.TrimEnd('.', ',', ')', ']', '\'', '"');
                    if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || !IsTrustedLoginUri(uri, kind) || !IsAuthorizationUri(uri)) continue;
                    if (opened.Add(uri.AbsoluteUri))
                    {
                        progress?.Report(new("브라우저에서 로그인을 완료해 주세요."));
                        await interaction.OpenBrowserAsync(uri, deadline.Token).ConfigureAwait(false);
                    }
                }
                if (!requestedCode && (text.Contains("Paste code", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("authorization code:", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Enter the code", StringComparison.OrdinalIgnoreCase)))
                {
                    requestedCode = true;
                    var code = await interaction.RequestInputAsync("로그인 완료", "브라우저에 인증 코드가 표시되면 여기에 붙여 넣어 주세요. 브라우저에서 로그인이 완료되었다면 이 창을 닫아 주세요.", deadline.Token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        if (code.Length > 4096 || code.Contains('\n') || code.Contains('\r')) throw new ProviderException(UsageState.SignInRequired, "인증 코드 형식을 확인해 주세요.");
                        await process.StandardInput.WriteLineAsync(code.AsMemory(), deadline.Token).ConfigureAwait(false);
                        await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
                    }
                }
            }
            await complete.ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return new(process.ExitCode, stdout.ToString(), stderr.ToString());

            async Task CompleteAsync()
            {
                try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); channel.Writer.TryComplete(); }
                catch (Exception ex) { channel.Writer.TryComplete(ex); }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new ProviderException(UsageState.SignInRequired, "로그인 대기 시간이 초과되었습니다. 다시 연결해 주세요."); }
        catch (System.ComponentModel.Win32Exception)
        { throw new ProviderException(UsageState.UpdateRequired, "로그인 구성요소를 실행할 수 없습니다."); }
        finally
        {
            deadline.Cancel();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        }
    }
    private static bool IsAuthorizationUri(Uri uri) => uri.AbsolutePath.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
        uri.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase) || uri.Query.Contains("client_id", StringComparison.OrdinalIgnoreCase);
    private static async Task PumpAsync(StreamReader reader, bool error, ChannelWriter<(bool, string)> writer, CancellationToken token)
    {
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            await writer.WriteAsync((error, new string(buffer, 0, count)), token).ConfigureAwait(false);
    }
    [GeneratedRegex(@"https://[^\s<>\x1b]+", RegexOptions.CultureInvariant)] private static partial Regex Url();
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)] private static partial Regex Ansi();
    [GeneratedRegex(@"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)", RegexOptions.CultureInvariant)] private static partial Regex Osc();
}
