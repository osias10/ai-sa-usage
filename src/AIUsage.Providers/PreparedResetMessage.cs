using AIUsage.Core;

namespace AIUsage.Providers;

/// <summary>Owns the verified runtime until a prepared one-shot operation finishes or is abandoned.</summary>
internal sealed class PreparedResetMessage(RuntimeLease runtime, Func<CancellationToken, Task> send,
    Func<CancellationToken, Task>? validate = null) : IPreparedResetMessage
{
    private readonly object _sync = new();
    private Task? _send;
    private Task? _validation = validate is null ? Task.CompletedTask : null;
    private bool _disposed;

    public Task ValidateAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _validation ??= ValidateCoreAsync(cancellationToken);
        }
    }

    private async Task ValidateCoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (validate is not null) await validate(token).ConfigureAwait(false);
    }

    public Task SendAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_validation?.IsCompletedSuccessfully != true)
                throw new InvalidOperationException("계정 확인이 끝난 뒤 자동 메시지를 보낼 수 있습니다.");
            if (_send is not null) throw new InvalidOperationException("준비한 자동 메시지는 한 번만 보낼 수 있습니다.");
            return _send = RunAsync(cancellationToken);
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await send(token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Task? sending;
        Task? validating;
        lock (_sync) { _disposed = true; sending = _send; validating = _validation; }
        // A concurrent disposal must not unpin an executable while its process is still running.
        if (validating is not null) { try { await validating.ConfigureAwait(false); } catch { } }
        if (sending is not null) { try { await sending.ConfigureAwait(false); } catch { } }
        await runtime.DisposeAsync().ConfigureAwait(false);
    }
}
