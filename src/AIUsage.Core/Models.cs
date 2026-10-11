namespace AIUsage.Core;

public enum ProviderKind { Claude, Codex, Antigravity }
public enum UsageState { Ready, SignInRequired, Offline, RateLimited, UpdateRequired, Unavailable }

public sealed class AccountSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ProviderKind Provider { get; set; }
    public string Alias { get; set; } = "개인";
    public string? IdentityKey { get; set; }
    public string? Email { get; set; }
    public string? Plan { get; set; }
    public bool AutoRefresh { get; set; } = true;
    public int RefreshMinutes { get; set; } = 5;
    public bool Pinned { get; set; } = true;
    public string? SelectedWindowId { get; set; }
    public List<WidgetWindowSelection>? WidgetWindows { get; set; }
    public WidgetWindowSelection? TrayWindow { get; set; }
    public int Order { get; set; }
    public List<UsageAlertRule> UsageAlerts { get; set; } = [];
}

public sealed record WidgetWindowSelection(string Id, string Name);

public sealed class UsageAlertRule
{
    public string WindowId { get; set; } = "";
    public bool Enabled { get; set; }
    public int ThresholdPercent { get; set; } = 80;
}

public sealed record UsageWindow(string Id, string Name, double? UsedPercent, DateTimeOffset? ResetsAt, bool IsPrimary = false);
public sealed record UsageSnapshot(
    ProviderKind Provider, string AccountId, string? IdentityKey, string? Plan,
    DateTimeOffset FetchedAt, IReadOnlyList<UsageWindow> Windows,
    UsageState State = UsageState.Ready, string? Message = null);
public sealed record ConnectionResult(string IdentityKey, string? Email, string? Plan);
public sealed record ProviderProgress(string Message, double? Percent = null);

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool AlwaysOnTop { get; set; } = true;
    public bool PositionLocked { get; set; }
    public bool MiniMode { get; set; }
    public bool StartWithWindows { get; set; }
    public bool ShowRemaining { get; set; }
    public bool ShowLastChecked { get; set; } = true;
    public string? TrayAccountId { get; set; }
    public string Theme { get; set; } = "System";
    public int WidgetTransparencyPercent { get; set; } = 10;
    public List<AccountSettings> Accounts { get; set; } = [];
    public Dictionary<string, WindowPlacement> Placements { get; set; } = [];
}
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, double Scale = 1);

public sealed class ProviderException(UsageState state, string message, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    public UsageState State { get; } = state;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public bool IdentityChanged { get; init; }
}

public interface IUsageProvider
{
    ProviderKind Kind { get; }
    Task<ConnectionResult> ConnectAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken);
    Task<UsageSnapshot> FetchAsync(AccountSettings account, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken);
    Task DisconnectAsync(AccountSettings account, CancellationToken cancellationToken);
}

public interface IRuntimeManager
{
    Task<string> EnsureAsync(ProviderKind provider, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken);
}

public interface ILoginInteraction
{
    Task OpenBrowserAsync(Uri uri, CancellationToken cancellationToken);
    Task<string?> RequestInputAsync(string title, string message, CancellationToken cancellationToken);
}

public sealed class ProcessRequest
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public required string WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
    public string? StandardInput { get; init; }
    public Action<string>? OnOutput { get; init; }
    public Action<string>? OnError { get; init; }
}
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken);
}

public sealed class AppPaths
{
    public string Root { get; }
    public AppPaths(string? root = null) => Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageWidget");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string CacheFile => Path.Combine(Root, "usage-cache.json");
    public string AlertStateFile => Path.Combine(Root, "usage-alerts.json");
    public string Runtimes => Path.Combine(Root, "runtimes");
    public string WorkDirectory => Path.Combine(Root, "work");
    public string ProfileDirectory(AccountSettings account)
    {
        if (!Guid.TryParseExact(account.Id, "N", out _)) throw new ArgumentException("Invalid account identifier.");
        return Path.Combine(Root, "profiles", account.Id, account.Provider.ToString().ToLowerInvariant());
    }
}
