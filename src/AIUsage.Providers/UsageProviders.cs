using System.Text.Json;
using System.Text.RegularExpressions;
using AIUsage.Core;

namespace AIUsage.Providers;

public sealed partial class ClaudeUsageProvider(AppPaths paths, IRuntimeManager runtimes, IProcessRunner runner, ILoginInteraction login) : IUsageProvider
{
    public ProviderKind Kind => ProviderKind.Claude;

    public async Task<ConnectionResult> ConnectAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken)
    {
        var executable = await PrepareAsync(account, progress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new("Claude 공식 로그인 화면을 여는 중…"));
        var result = await BrowserCliLogin.RunAsync(Request(executable, account, ["auth", "login"], TimeSpan.FromMinutes(5)), Kind, login, progress, cancellationToken).ConfigureAwait(false);
        ProviderErrors.EnsureSuccess(result);
        return await ReadIdentityAsync(executable, account, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UsageSnapshot> FetchAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken)
    {
        var executable = await PrepareAsync(account, progress, cancellationToken).ConfigureAwait(false);
        var identity = await ReadIdentityAsync(executable, account, cancellationToken).ConfigureAwait(false);
        ProviderIdentity.EnsureMatches(account, identity.IdentityKey);
        progress?.Report(new("Claude 구독 사용량을 확인하는 중…"));
        var result = await ProviderProcess.RunAsync(runner, Request(executable, account,
            ["--safe-mode", "-p", "/usage", "--output-format", "stream-json", "--verbose", "--no-session-persistence"], TimeSpan.FromSeconds(60)), cancellationToken).ConfigureAwait(false);
        ProviderErrors.EnsureSuccess(result);
        return ClaudeUsageParser.Parse(result.StandardOutput, account) with { IdentityKey = identity.IdentityKey, Plan = identity.Plan ?? account.Plan };
    }

    public async Task DisconnectAsync(AccountSettings account, CancellationToken cancellationToken)
    {
        var executable = await runtimes.EnsureAsync(Kind, null, cancellationToken).ConfigureAwait(false);
        var result = await ProviderProcess.RunAsync(runner, Request(executable, account, ["auth", "logout"], TimeSpan.FromSeconds(30)), cancellationToken).ConfigureAwait(false);
        ProviderErrors.EnsureSuccess(result);
    }

    private async Task<string> PrepareAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken token)
    {
        ValidateAccount(account);
        Directory.CreateDirectory(paths.ProfileDirectory(account));
        Directory.CreateDirectory(paths.WorkDirectory);
        var executable = await runtimes.EnsureAsync(Kind, progress, token).ConfigureAwait(false);
        var version = await ProviderProcess.RunAsync(runner, Request(executable, account, ["--version"], TimeSpan.FromSeconds(15)), token).ConfigureAwait(false);
        ProviderErrors.EnsureSuccess(version);
        var match = VersionPattern().Match(version.StandardOutput);
        if (!match.Success || !Version.TryParse(match.Value, out var parsed) || parsed < new Version(2, 1, 277))
            throw new ProviderException(UsageState.UpdateRequired, "Claude 사용량 조회에는 2.1.277 이상의 연결 구성요소가 필요합니다.");
        return executable;
    }

    private async Task<ConnectionResult> ReadIdentityAsync(string executable, AccountSettings account, CancellationToken token)
    {
        var result = await ProviderProcess.RunAsync(runner, Request(executable, account, ["auth", "status"], TimeSpan.FromSeconds(30)), token).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new ProviderException(UsageState.SignInRequired, "Claude 계정에 로그인해 주세요.");
        var root = ProviderJson.ReadObject(result.StandardOutput);
        if (ProviderJson.Boolean(root, "loggedIn") != true || ProviderJson.String(root, "authMethod") != "claude.ai")
            throw new ProviderException(UsageState.SignInRequired, "API 키가 아닌 Claude 구독 계정으로 로그인해 주세요.");
        // Newer CLI versions expose the directory explicitly. Reject a fallback to a global profile.
        var configured = ProviderJson.String(root, "configDirectory");
        if (configured == null || !string.Equals(Path.GetFullPath(configured).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(paths.ProfileDirectory(account)).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ProviderException(UsageState.Unavailable, "Claude 계정별 저장소를 확인하지 못했습니다. 다시 연결해 주세요.");
        var email = ProviderJson.String(root, "email");
        var id = ProviderJson.String(root, "accountId") ?? email;
        if (string.IsNullOrWhiteSpace(id)) throw ProviderJson.Unavailable("Claude 계정 정보를 확인하지 못했습니다.");
        var org = ProviderJson.String(root, "orgId");
        return new(ProviderIdentity.Create(Kind, id + (org == null ? "" : ":" + org)), email, ProviderJson.String(root, "subscriptionType"));
    }

    private ProcessRequest Request(string executable, AccountSettings account, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        ValidateAccount(account);
        return new() { FileName = executable, Arguments = arguments, WorkingDirectory = paths.WorkDirectory,
            Environment = ProviderProcess.EnvironmentFor(Kind, paths.ProfileDirectory(account)), Timeout = timeout };
    }
    private void ValidateAccount(AccountSettings account)
    { if (account.Provider != Kind) throw new ArgumentException("Provider mismatch.", nameof(account)); }
    [GeneratedRegex(@"\d+\.\d+\.\d+", RegexOptions.CultureInvariant)] private static partial Regex VersionPattern();
}

public sealed partial class CodexUsageProvider(AppPaths paths, IRuntimeManager runtimes, IProcessRunner runner, ILoginInteraction login) : IUsageProvider
{
    public ProviderKind Kind => ProviderKind.Codex;

    public async Task<ConnectionResult> ConnectAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await using var session = await OpenAsync(account, progress, timeout.Token).ConfigureAwait(false);
            progress?.Report(new("ChatGPT 공식 로그인 화면을 여는 중…"));
            var result = await session.RequestAsync("account/login/start", new { type = "chatgpt" }, timeout.Token).ConfigureAwait(false);
            var loginId = ProviderJson.String(result, "loginId") ?? throw ProviderJson.Unsupported();
            var authUrl = ProviderJson.String(result, "authUrl");
            if (!Uri.TryCreate(authUrl, UriKind.Absolute, out var uri) || !BrowserCliLogin.IsTrustedLoginUri(uri, Kind))
                throw new ProviderException(UsageState.SignInRequired, "공식 로그인 주소를 확인하지 못했습니다.");
            await login.OpenBrowserAsync(uri, timeout.Token).ConfigureAwait(false);
            progress?.Report(new("브라우저에서 ChatGPT 로그인을 완료해 주세요."));
            var completed = await session.WaitForNotificationAsync("account/login/completed", loginId, timeout.Token).ConfigureAwait(false);
            if (ProviderJson.Boolean(completed, "success") != true)
                throw new ProviderException(UsageState.SignInRequired, "ChatGPT 로그인이 완료되지 않았습니다. 다시 시도해 주세요.");
            return ParseAccount(await session.RequestAsync("account/read", new { refreshToken = false }, timeout.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ProviderException(UsageState.SignInRequired, "로그인 대기 시간이 초과되었습니다. 다시 연결해 주세요."); }
        catch (IOException) { throw new ProviderException(UsageState.Offline, "Codex 로그인 구성요소와 연결하지 못했습니다."); }
    }

    public async Task<UsageSnapshot> FetchAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        try
        {
            await using var session = await OpenAsync(account, progress, timeout.Token).ConfigureAwait(false);
            var identity = ParseAccount(await session.RequestAsync("account/read", new { refreshToken = false }, timeout.Token).ConfigureAwait(false));
            ProviderIdentity.EnsureMatches(account, identity.IdentityKey);
            progress?.Report(new("Codex 구독 사용량을 확인하는 중…"));
            var usage = await session.RequestAsync("account/rateLimits/read", new { }, timeout.Token).ConfigureAwait(false);
            return CodexUsageParser.Parse(usage.GetRawText(), account) with { IdentityKey = identity.IdentityKey, Plan = identity.Plan ?? account.Plan };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ProviderException(UsageState.Offline, "Codex 사용량 조회 시간이 초과되었습니다."); }
        catch (IOException) { throw new ProviderException(UsageState.Offline, "Codex 연결 구성요소와 통신하지 못했습니다."); }
    }

    public async Task DisconnectAsync(AccountSettings account, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await using var session = await OpenAsync(account, null, timeout.Token).ConfigureAwait(false);
            await session.RequestAsync("account/logout", new { }, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ProviderException(UsageState.Offline, "Codex 로그아웃 시간이 초과되었습니다."); }
    }

    public static ConnectionResult ParseAccount(JsonElement result)
    {
        if (!result.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object || ProviderJson.String(account, "type") != "chatgpt")
            throw new ProviderException(UsageState.SignInRequired, "API 키가 아닌 ChatGPT 계정으로 로그인해 주세요.");
        var email = ProviderJson.String(account, "email");
        var id = ProviderJson.String(account, "accountId") ?? email;
        if (string.IsNullOrWhiteSpace(id)) throw ProviderJson.Unavailable("ChatGPT 계정 식별 정보를 확인하지 못했습니다.");
        return new(ProviderIdentity.Create(ProviderKind.Codex, id), email, ProviderJson.String(account, "planType"));
    }

    private async Task<CodexRpcSession> OpenAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken token)
    {
        if (account.Provider != Kind) throw new ArgumentException("Provider mismatch.", nameof(account));
        Directory.CreateDirectory(paths.ProfileDirectory(account));
        Directory.CreateDirectory(paths.WorkDirectory);
        var executable = await runtimes.EnsureAsync(Kind, progress, token).ConfigureAwait(false);
        _ = runner; // Shared one-shot runner is intentionally not used for duplex JSON-RPC.
        CodexRpcSession? session = null;
        try
        {
            session = new(new ProcessRequest
            {
                FileName = executable,
                Arguments = ["-c", "cli_auth_credentials_store=\"keyring\"", "app-server", "--listen", "stdio://"],
                WorkingDirectory = paths.WorkDirectory, Environment = ProviderProcess.EnvironmentFor(Kind, paths.ProfileDirectory(account))
            });
            await session.InitializeAsync(token).ConfigureAwait(false);
            return session;
        }
        catch
        {
            if (session != null) await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

public sealed partial class AntigravityUsageProvider(AppPaths paths, IRuntimeManager runtimes, IProcessRunner runner, ILoginInteraction login) : IUsageProvider
{
    public ProviderKind Kind => ProviderKind.Antigravity;
    public const string SupportedRuntimeVersion = "1.3.1";
    private static readonly SemaphoreSlim CurrentAccountGate = new(1, 1);
    private string? _creditsRetryIdentity;
    private DateTimeOffset? _creditsRetryAt;

    public async Task<ConnectionResult> ConnectAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken)
    {
        await CurrentAccountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var executable = await PrepareAsync(account, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("Antigravity 현재 로그인 계정을 연결하는 중…"));
            // Official CLI owns Google OAuth and the OS keyring. No token copy or browser-cookie access.
            // The version-pinned vendor auth action exits after login, without creating an agent turn.
            // A hidden pseudoconsole supplies the controlling terminal required for first-time OAuth.
            var result = await BrowserCliLogin.RunTerminalAsync(Request(executable, account, [], TimeSpan.FromMinutes(5), "login"), Kind, login, progress, cancellationToken).ConfigureAwait(false);
            ProviderErrors.EnsureSuccess(result);
            var loggedIn = AntigravityUsageParser.ParseIdentity(result.StandardOutput);
            var confirmed = await ReadIdentityAsync(executable, account, cancellationToken).ConfigureAwait(false);
            if (loggedIn.IdentityKey != confirmed.IdentityKey) throw IdentityChanged();
            return confirmed;
        }
        finally { CurrentAccountGate.Release(); }
    }

    public async Task<UsageSnapshot> FetchAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken)
    {
        await CurrentAccountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var executable = await PrepareAsync(account, progress, cancellationToken).ConfigureAwait(false);
            var before = await ReadIdentityAsync(executable, account, cancellationToken).ConfigureAwait(false);
            ProviderIdentity.EnsureMatches(account, before.IdentityKey);
            progress?.Report(new("Antigravity 사용량을 확인하는 중…"));
            var result = await ProviderProcess.RunAsync(runner, Request(executable, account,
                ["-p", "/usage", "--output-format", "json"], TimeSpan.FromSeconds(60)), cancellationToken).ConfigureAwait(false);
            ProviderErrors.EnsureSuccess(result);
            var snapshot = AntigravityUsageParser.Parse(result.StandardOutput, account);
            var (credit, creditMessage) = await ReadCreditsAsync(executable, account, before.IdentityKey, cancellationToken).ConfigureAwait(false);
            var after = await ReadIdentityAsync(executable, account, cancellationToken).ConfigureAwait(false);
            if (before.IdentityKey != after.IdentityKey) throw IdentityChanged();
            ProviderIdentity.EnsureMatches(account, after.IdentityKey);
            return snapshot with { IdentityKey = after.IdentityKey, Credits = credit is null ? null : [credit], CreditsMessage = creditMessage };
        }
        finally { CurrentAccountGate.Release(); }
    }

    // Antigravity's keyring is shared with its official apps. Unlink only; never log those apps out.
    public Task DisconnectAsync(AccountSettings account, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

    private async Task<(AccountCredit? Credit, string? Message)> ReadCreditsAsync(string executable, AccountSettings account,
        string identity, CancellationToken token)
    {
        // The surrounding account gate and before/after auth checks also cover this native command.
        // A partial credit error must not discard a successful subscription quota response.
        var retryIdentity = account.Id + ":" + identity;
        if (_creditsRetryIdentity != retryIdentity) { _creditsRetryIdentity = retryIdentity; _creditsRetryAt = null; }
        if (_creditsRetryAt > DateTimeOffset.UtcNow) return (null, "크레딧 조회 제한 · 재시도 대기");
        try
        {
            var result = await ProviderProcess.RunAsync(runner, Request(executable, account,
                ["-p", "/credits", "--output-format", "json"], TimeSpan.FromSeconds(30)), token).ConfigureAwait(false);
            ProviderErrors.EnsureSuccess(result);
            var root = ProviderJson.ReadObject(result.StandardOutput);
            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null && error.ToString().Length > 0)
                throw ProviderErrors.FromText(error.ToString());
            var credit = AccountCreditParsers.Antigravity(root);
            _creditsRetryAt = null;
            return (credit, credit is null ? "크레딧 정보 미제공" : null);
        }
        catch (ProviderException error)
        {
            if (error.State == UsageState.RateLimited)
            {
                var delay = error.RetryAfter ?? TimeSpan.FromMinutes(5);
                try { _creditsRetryAt = DateTimeOffset.UtcNow.Add(delay > TimeSpan.Zero ? delay : TimeSpan.Zero); }
                catch (ArgumentOutOfRangeException) { _creditsRetryAt = DateTimeOffset.MaxValue; }
                return (null, "크레딧 조회 제한 · 재시도 대기");
            }
            return (null, "크레딧을 확인하지 못했습니다.");
        }
    }

    private async Task<string> PrepareAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken token)
    {
        if (account.Provider != Kind) throw new ArgumentException("Provider mismatch.", nameof(account));
        Directory.CreateDirectory(paths.WorkDirectory);
        var executable = await runtimes.EnsureAsync(Kind, progress, token).ConfigureAwait(false);
        var result = await ProviderProcess.RunAsync(runner, Request(executable, account, ["--version"], TimeSpan.FromSeconds(15)), token).ConfigureAwait(false);
        ProviderErrors.EnsureSuccess(result);
        if (VersionPattern().Match(result.StandardOutput).Value != SupportedRuntimeVersion)
            throw new ProviderException(UsageState.UpdateRequired, "Antigravity 연결 구성요소의 버전이 호환되지 않습니다. 앱을 업데이트해 주세요.");
        return executable;
    }

    private async Task<ConnectionResult> ReadIdentityAsync(string executable, AccountSettings account, CancellationToken token)
    {
        var result = await ProviderProcess.RunAsync(runner, Request(executable, account, [], TimeSpan.FromSeconds(30), "check"), token).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new ProviderException(UsageState.SignInRequired, "Antigravity 계정으로 로그인해 주세요.");
        return AntigravityUsageParser.ParseIdentity(result.StandardOutput + "\n" + result.StandardError);
    }

    private ProcessRequest Request(string executable, AccountSettings account, IReadOnlyList<string> arguments, TimeSpan timeout, string? authAction = null)
    {
        var environment = ProviderProcess.EnvironmentFor(Kind, paths.ProfileDirectory(account));
        if (authAction != null) environment["AGY_CLI_CDE_AUTH_ACTION"] = authAction;
        return new() { FileName = executable, Arguments = arguments, WorkingDirectory = paths.WorkDirectory, Environment = environment, Timeout = timeout };
    }
    private static ProviderException IdentityChanged() => new(UsageState.SignInRequired,
        "Antigravity 로그인 계정이 조회 중 변경되었습니다. 계정을 다시 연결해 주세요.") { IdentityChanged = true };
    [GeneratedRegex(@"\d+\.\d+\.\d+", RegexOptions.CultureInvariant)] private static partial Regex VersionPattern();
}
