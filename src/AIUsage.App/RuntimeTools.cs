using AIUsage.Core;
using AIUsage.Infrastructure;

namespace AIUsage.App;

public sealed partial class AppController
{
    private async Task InitializeRuntimeToolsAsync()
    {
        var providers = Settings.Accounts.Where(account => account.IdentityKey is not null)
            .Select(account => account.Provider).Distinct().ToArray();
        // The catalog changes only with an app update. Checking it never contacts an update feed.
        try { await RuntimeTools.CheckAsync(_shutdown.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
        catch { /* The tool service reports its own error without changing account usage. */ }

        foreach (var provider in providers)
        {
            if (_shutdown.IsCancellationRequested) return;
            if (RuntimeTools.GetStatuses().Single(status => status.Provider == provider).Phase
                is RuntimeToolPhase.NotInstalled or RuntimeToolPhase.UpdateAvailable)
            {
                try { await RuntimeTools.UpdateAsync(provider, false, _shutdown.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
                catch { /* Keep the last good tool and leave the error in the tools section. */ }
            }
        }
    }
}

/// <summary>Settings previews never touch a real runtime, account, or network.</summary>
internal sealed class DemoRuntimeToolService : IRuntimeToolService
{
    private readonly object _sync = new();
    private readonly Dictionary<ProviderKind, RuntimeToolStatus> _states = Enum.GetValues<ProviderKind>()
        .ToDictionary(kind => kind, kind => new RuntimeToolStatus(kind, RuntimeCatalog.Get(kind).Version,
            RuntimeCatalog.Get(kind).Version, RuntimeToolPhase.Ready, "데모 · 호환 최고 버전", LastCheckedAt: DateTimeOffset.UtcNow, IsInstalled: true));
    private readonly Dictionary<ProviderKind, CancellationTokenSource> _pending = [];
    public event EventHandler? Changed;
    public IReadOnlyList<RuntimeToolStatus> GetStatuses() { lock (_sync) return _states.Values.ToArray(); }
    public Task CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            foreach (var kind in _states.Keys.ToArray())
                if (!_states[kind].IsBusy) _states[kind] = _states[kind] with { LastCheckedAt = DateTimeOffset.UtcNow };
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
    public async Task UpdateAsync(ProviderKind provider, bool reinstall, CancellationToken cancellationToken)
    {
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_sync)
        {
            if (_pending.ContainsKey(provider)) return;
            _pending[provider] = pending;
        }
        try
        {
            for (var percent = 0; percent <= 100; percent += 25)
            {
                Set(provider, RuntimeToolPhase.Downloading, "데모 · 다운로드 중", percent);
                await Task.Delay(100, pending.Token).ConfigureAwait(false);
            }
            Set(provider, RuntimeToolPhase.Verifying, "데모 · 검증 중");
            await Task.Delay(100, pending.Token).ConfigureAwait(false);
            Set(provider, RuntimeToolPhase.Ready, "데모 · 호환 최고 버전");
        }
        catch (OperationCanceledException)
        {
            Set(provider, RuntimeToolPhase.Cancelled, "취소됨 · 기존 버전 유지");
        }
        finally { lock (_sync) _pending.Remove(provider); }
    }
    public void Cancel(ProviderKind provider) { lock (_sync) if (_pending.TryGetValue(provider, out var source)) source.Cancel(); }
    private void Set(ProviderKind provider, RuntimeToolPhase phase, string message, double? percent = null)
    {
        lock (_sync) _states[provider] = _states[provider] with
        { Phase = phase, Message = message, Percent = percent, LastCheckedAt = DateTimeOffset.UtcNow };
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
