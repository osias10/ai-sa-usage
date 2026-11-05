namespace AIUsage.Core;

/// <summary>Pins one verified executable for the entire provider operation.</summary>
public sealed class RuntimeLease(string executablePath, string? version = null, Action? release = null)
    : IDisposable, IAsyncDisposable
{
    private Action? _release = release;
    public string ExecutablePath { get; } = executablePath;
    public string? Version { get; } = version;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

public enum RuntimeToolPhase
{
    NotInstalled, Ready, UpdateAvailable, Checking, Downloading, Verifying,
    WaitingForIdle, Applying, Failed, Cancelled
}

public sealed record RuntimeToolStatus(
    ProviderKind Provider, string TargetVersion, string? InstalledVersion = null,
    RuntimeToolPhase Phase = RuntimeToolPhase.NotInstalled, string? Message = null,
    double? Percent = null, DateTimeOffset? LastCheckedAt = null, bool IsInstalled = false)
{
    public bool IsBusy => Phase is RuntimeToolPhase.Checking or RuntimeToolPhase.Downloading
        or RuntimeToolPhase.Verifying or RuntimeToolPhase.WaitingForIdle or RuntimeToolPhase.Applying;
    public bool CanUpdate => !IsBusy && (!IsInstalled || InstalledVersion != TargetVersion);
    public bool CanReinstall => !IsBusy && IsInstalled;
}

/// <summary>Checks only the compatibility catalog embedded in this application.</summary>
public interface IRuntimeToolService
{
    event EventHandler? Changed;
    IReadOnlyList<RuntimeToolStatus> GetStatuses();
    Task CheckAsync(CancellationToken cancellationToken);
    Task UpdateAsync(ProviderKind provider, bool reinstall, CancellationToken cancellationToken);
    void Cancel(ProviderKind provider);
}

/// <summary>Finishes tool preparation before the durable one-shot message reservation.</summary>
public interface IPreparedResetMessageProvider
{
    Task<IPreparedResetMessage> PrepareResetMessageAsync(AccountSettings account, CancellationToken cancellationToken);
}

public interface IPreparedResetMessage : IAsyncDisposable
{
    Task ValidateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task SendAsync(CancellationToken cancellationToken);
}

/// <summary>Download/activation waits do not occupy usage-request concurrency slots.</summary>
public interface IPreparedUsageProvider
{
    Task<IPreparedUsageRead> PrepareUsageReadAsync(AccountSettings account,
        IProgress<ProviderProgress>? progress, CancellationToken cancellationToken);
}

public interface IPreparedUsageRead : IAsyncDisposable
{
    Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken);
}
