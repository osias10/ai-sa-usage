using AIUsage.Core;

namespace AIUsage.Providers;

/// <summary>Holds a runtime across the execution-slot wait without starting native processes.</summary>
internal sealed class PreparedUsageRead(RuntimeLease runtime, Func<CancellationToken, Task<UsageSnapshot>> fetch) : IPreparedUsageRead
{
    private readonly object _sync = new();
    private Task<UsageSnapshot>? _fetch;
    private bool _disposed;

    public Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_fetch is not null) throw new InvalidOperationException("준비한 사용량 조회는 한 번만 실행할 수 있습니다.");
            return _fetch = RunAsync(cancellationToken);
        }
    }

    private async Task<UsageSnapshot> RunAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return await fetch(token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Task? fetching;
        lock (_sync) { _disposed = true; fetching = _fetch; }
        if (fetching is not null) { try { await fetching.ConfigureAwait(false); } catch { } }
        await runtime.DisposeAsync().ConfigureAwait(false);
    }
}
