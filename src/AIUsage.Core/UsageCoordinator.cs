namespace AIUsage.Core;

public sealed record AccountUsageStatus(string AccountId, UsageSnapshot? Snapshot, UsageState State,
    string? Message, bool IsRefreshing, DateTimeOffset? NextAttemptAt);
public sealed class AccountUsageChangedEventArgs(AccountUsageStatus status) : EventArgs
{
    public AccountUsageStatus Status { get; } = status;
}

/// <summary>Owns polling, account isolation, bounded concurrency, and last-good snapshots.</summary>
public sealed class UsageCoordinator : IAsyncDisposable
{
    private sealed class Entry(AccountSettings account, UsageSnapshot? snapshot)
    {
        public AccountSettings Account = account;
        public AccountUsageStatus Status = new(account.Id, snapshot,
            snapshot?.State ?? UsageState.SignInRequired, snapshot?.Message, false, null);
        public readonly SemaphoreSlim Gate = new(1, 1);
        public Task? Pending;
        public bool Removed;
        public int Failures;
        public readonly HashSet<string> ResetRequests = [];
    }
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly IReadOnlyDictionary<ProviderKind, IUsageProvider> _providers;
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly SemaphoreSlim _logins = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TimeProvider _time;
    private readonly HashSet<Task> _operations = [];
    private bool _disposing;
    private Task? _disposeTask;
    private Task? _loop;
    public event EventHandler<AccountUsageChangedEventArgs>? Changed;

    public UsageCoordinator(IEnumerable<IUsageProvider> providers, IEnumerable<AccountSettings> accounts,
        IEnumerable<UsageSnapshot>? snapshots = null, TimeProvider? timeProvider = null)
    {
        _providers = providers.ToDictionary(p => p.Kind);
        _time = timeProvider ?? TimeProvider.System;
        var cached = (snapshots ?? []).GroupBy(s => s.AccountId).ToDictionary(g => g.Key, g => g.Last());
        foreach (var account in accounts)
        {
            Validate(account);
            cached.TryGetValue(account.Id, out var snapshot);
            if (snapshot?.Provider != account.Provider || snapshot?.IdentityKey != account.IdentityKey) snapshot = null;
            var entry = new Entry(account, snapshot);
            if (account.IdentityKey is not null && snapshot is null)
                entry.Status = entry.Status with { State = UsageState.Unavailable, Message = "첫 사용량 조회를 기다리는 중" };
            if (snapshot is not null)
                entry.Status = entry.Status with { NextAttemptAt = snapshot.FetchedAt.AddMinutes(account.RefreshMinutes) };
            _entries.Add(account.Id, entry);
        }
    }

    public AccountUsageStatus GetStatus(string accountId) { lock (_sync) return Find(accountId).Status; }
    public IReadOnlyList<UsageSnapshot> GetSnapshots()
    {
        lock (_sync) return _entries.Values.Select(e => e.Status.Snapshot).OfType<UsageSnapshot>().ToArray();
    }
    public void AddAccount(AccountSettings account)
    {
        Validate(account);
        lock (_sync)
        {
            if (account.Provider == ProviderKind.Antigravity && _entries.Values.Any(e => e.Account.Provider == account.Provider))
                throw new InvalidOperationException("Antigravity는 현재 로그인한 계정 하나만 연결할 수 있습니다.");
            _entries.Add(account.Id, new Entry(account, null));
        }
        Publish(GetStatus(account.Id));
    }
    public void UpdateAccount(AccountSettings account)
    {
        Validate(account);
        AccountUsageStatus status;
        lock (_sync)
        {
            var entry = Find(account.Id);
            if (entry.Account.Provider != account.Provider) throw new InvalidOperationException("계정의 서비스는 변경할 수 없습니다.");
            entry.Account = account;
            entry.Status = entry.Status with
            {
                NextAttemptAt = entry.Status.State == UsageState.RateLimited ? entry.Status.NextAttemptAt
                    : entry.Status.Snapshot?.FetchedAt.AddMinutes(account.RefreshMinutes)
            };
            status = entry.Status;
        }
        Publish(status);
    }

    public Task ConnectAsync(string accountId, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            var entry = Find(accountId);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _operations.Add(completion.Task);
            _ = RunConnectTrackedAsync(entry, progress, cancellationToken, completion);
            return completion.Task;
        }
    }
    private async Task RunConnectTrackedAsync(Entry entry, IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken, TaskCompletionSource completion)
    {
        await Task.Yield();
        try { await RunConnectAsync(entry, progress, cancellationToken).ConfigureAwait(false); completion.TrySetResult(); }
        catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { completion.TrySetException(error); }
        finally { lock (_sync) _operations.Remove(completion.Task); }
    }
    private async Task RunConnectAsync(Entry entry, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var token = linked.Token;
        await _logins.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await entry.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (entry.Removed) return;
                SetStatus(entry, s => s with { IsRefreshing = true, Message = "계정 연결 중" });
                var result = await _providers[entry.Account.Provider].ConnectAsync(entry.Account, progress, token).ConfigureAwait(false);
                lock (_sync)
                {
                    var changedIdentity = entry.Account.IdentityKey != result.IdentityKey;
                    entry.Account.IdentityKey = result.IdentityKey;
                    entry.Account.Email = result.Email;
                    entry.Account.Plan = result.Plan;
                    entry.Failures = 0;
                    entry.ResetRequests.Clear();
                    entry.Status = entry.Status with { Snapshot = changedIdentity ? null : entry.Status.Snapshot,
                        State = UsageState.Ready, Message = null, NextAttemptAt = null };
                }
            }
            catch (OperationCanceledException) { SetStatus(entry, s => s with { Message = "계정 연결이 취소되었습니다." }); throw; }
            catch (Exception error) { RecordFailure(entry, error); throw; }
            finally { SetStatus(entry, s => s with { IsRefreshing = false }); entry.Gate.Release(); }
        }
        finally { _logins.Release(); }
        token.ThrowIfCancellationRequested();
        await RefreshAsync(entry.Account.Id, progress, token).ConfigureAwait(false);
    }

    public async Task RemoveAccountAsync(string accountId, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_sync) { entry = Find(accountId); entry.Removed = true; }
        try { await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch { lock (_sync) entry.Removed = false; throw; }
        try
        {
            await _providers[entry.Account.Provider].DisconnectAsync(entry.Account, cancellationToken).ConfigureAwait(false);
            lock (_sync) _entries.Remove(accountId);
        }
        catch { lock (_sync) entry.Removed = false; throw; }
        finally { entry.Gate.Release(); }
    }

    public Task RefreshAsync(string accountId, IProgress<ProviderProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Task task;
        lock (_sync)
        {
            // A settings change may remove an account after a polling batch took its snapshot.
            if (_disposing || !_entries.TryGetValue(accountId, out var entry) || entry.Removed) return Task.CompletedTask;
            if (entry.Status.State == UsageState.RateLimited && entry.Status.NextAttemptAt > _time.GetUtcNow())
                return Task.CompletedTask;
            if (entry.Pending is { IsCompleted: false }) task = entry.Pending;
            else
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                task = entry.Pending = completion.Task;
                _operations.Add(task);
                _ = RunRefreshAsync(entry, progress, completion);
            }
        }
        return task.WaitAsync(cancellationToken);
    }

    private async Task RunRefreshAsync(Entry entry, IProgress<ProviderProgress>? progress, TaskCompletionSource completion)
    {
        // Yield before touching entries: the caller installs the single-flight task under the lock.
        await Task.Yield();
        var token = _shutdown.Token;
        try
        {
            await entry.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (entry.Removed) return;
                SetStatus(entry, s => s with { IsRefreshing = true, Message = "사용량 조회 중" });
                await _slots.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var snapshot = await _providers[entry.Account.Provider].FetchAsync(entry.Account, progress, token).ConfigureAwait(false);
                    if (snapshot.AccountId != entry.Account.Id || snapshot.Provider != entry.Account.Provider)
                        throw new ProviderException(UsageState.Unavailable, "응답 계정이 일치하지 않습니다.");
                    if (snapshot.State != UsageState.Ready)
                        throw new ProviderException(snapshot.State, snapshot.Message ?? "사용량을 확인할 수 없습니다.");
                    if (string.IsNullOrWhiteSpace(snapshot.IdentityKey))
                        throw new ProviderException(UsageState.SignInRequired, "계정 정보를 확인할 수 없습니다. 다시 연결해 주세요.");
                    if (entry.Account.IdentityKey is not null && snapshot.IdentityKey != entry.Account.IdentityKey)
                    {
                        SetStatus(entry, s => s with { Snapshot = null });
                        throw new ProviderException(UsageState.SignInRequired, "로그인 계정이 변경되었습니다. 계정을 다시 연결해 주세요.");
                    }
                    lock (_sync)
                    {
                        entry.Account.IdentityKey ??= snapshot.IdentityKey;
                        entry.Account.Plan = snapshot.Plan;
                        entry.Failures = 0;
                        var keys = snapshot.Windows.Where(w => w.ResetsAt is not null)
                            .Select(w => ResetKey(w)).ToHashSet();
                        entry.ResetRequests.IntersectWith(keys);
                    }
                    SetStatus(entry, s => s with { Snapshot = snapshot, State = UsageState.Ready, Message = null,
                        NextAttemptAt = _time.GetUtcNow().AddMinutes(entry.Account.RefreshMinutes) });
                }
                finally { _slots.Release(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { RecordFailure(entry, error); }
            finally { SetStatus(entry, s => s with { IsRefreshing = false }); entry.Gate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { completion.TrySetResult(); lock (_sync) _operations.Remove(completion.Task); }
    }

    private void RecordFailure(Entry entry, Exception error)
    {
        var providerError = error as ProviderException;
        var state = providerError?.State ?? UsageState.Offline;
        var delay = providerError?.RetryAfter ?? TimeSpan.FromMinutes(Math.Min(15, Math.Pow(2, Math.Min(4, entry.Failures++))));
        if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
        SetStatus(entry, s => s with { State = state, Snapshot = providerError?.IdentityChanged == true ? null : s.Snapshot,
            Message = providerError?.Message ?? "연결을 확인한 뒤 다시 시도해 주세요.",
            NextAttemptAt = _time.GetUtcNow().Add(delay) });
    }

    public Task RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        string[] ids;
        lock (_sync) ids = _entries.Values.Where(e => !e.Removed).Select(e => e.Account.Id).ToArray();
        return Task.WhenAll(ids.Select(id => RefreshAsync(id, null, cancellationToken)));
    }

    /// <summary>Runs an explicitly enabled one-shot message under the same account and process gates as login/usage reads.</summary>
    public Task<bool> SendResetMessageAsync(string accountId, string expectedIdentity,
        Func<AccountUsageStatus, CancellationToken, Task<bool>> reserveAttempt, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_disposing || !_entries.TryGetValue(accountId, out var entry) || entry.Removed) return Task.FromResult(false);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _operations.Add(completion.Task);
            _ = SendResetMessageTrackedAsync(entry, expectedIdentity, reserveAttempt, cancellationToken, completion);
            return completion.Task;
        }
    }

    private async Task SendResetMessageTrackedAsync(Entry entry, string expectedIdentity,
        Func<AccountUsageStatus, CancellationToken, Task<bool>> reserveAttempt, CancellationToken cancellationToken,
        TaskCompletionSource<bool> completion)
    {
        await Task.Yield();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var token = linked.Token;
        try
        {
            await entry.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!Eligible()) { completion.TrySetResult(false); return; }
                if (_providers[entry.Account.Provider] is not IResetMessageProvider provider)
                    throw new InvalidOperationException("이 서비스는 자동 메시지를 지원하지 않습니다.");
                await _slots.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (!Eligible() || !await reserveAttempt(entry.Status, token).ConfigureAwait(false))
                    { completion.TrySetResult(false); return; }
                    token.ThrowIfCancellationRequested();
                    if (!Eligible()) { completion.TrySetResult(false); return; }
                    await provider.SendResetMessageAsync(entry.Account, token).ConfigureAwait(false);
                    completion.TrySetResult(true);
                }
                finally { _slots.Release(); }
            }
            finally { entry.Gate.Release(); }
        }
        catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { completion.TrySetException(error); }
        finally { lock (_sync) _operations.Remove(completion.Task); }

        bool Eligible() => !entry.Removed && entry.Account.ResetMessageEnabled &&
            StringComparer.Ordinal.Equals(entry.Account.IdentityKey, expectedIdentity) &&
            entry.Status.State == UsageState.Ready && !entry.Status.IsRefreshing && entry.Status.Snapshot?.IdentityKey == expectedIdentity;
    }
    public Task RefreshAfterClockChangeAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            foreach (var entry in _entries.Values)
                if (entry.Status.State is UsageState.Ready or UsageState.Offline or UsageState.Unavailable)
                    entry.Status = entry.Status with { NextAttemptAt = null };
        }
        // Keep provider Retry-After deadlines intact; a clock adjustment must not bypass a 429.
        return RefreshDueAsync(cancellationToken);
    }
    public Task RefreshDueAsync(CancellationToken cancellationToken = default)
    {
        List<string> due = [];
        lock (_sync)
        {
            var now = _time.GetUtcNow();
            foreach (var entry in _entries.Values)
            {
                if (entry.Removed || !entry.Account.AutoRefresh || entry.Account.IdentityKey is null ||
                    entry.Status.IsRefreshing || entry.Pending is { IsCompleted: false } ||
                    entry.Status.State is UsageState.SignInRequired or UsageState.UpdateRequired) continue;
                if (entry.Status.State != UsageState.Ready && entry.Status.NextAttemptAt > now) continue;
                var resetDue = false;
                foreach (var window in entry.Status.Snapshot?.Windows ?? [])
                    if (window.ResetsAt is { } reset && reset.AddSeconds(5) <= now && entry.ResetRequests.Add(ResetKey(window))) resetDue = true;
                if (resetDue || entry.Status.NextAttemptAt is null || entry.Status.NextAttemptAt <= now) due.Add(entry.Account.Id);
            }
        }
        return Task.WhenAll(due.Select(id => RefreshAsync(id, null, cancellationToken)));
    }
    private static string ResetKey(UsageWindow window) => $"{window.Id}:{window.ResetsAt:O}";

    public void Start()
    {
        lock (_sync) _loop ??= RunLoopAsync();
    }
    private async Task RunLoopAsync()
    {
        try
        {
            await RefreshDueAsync(_shutdown.Token).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
                await RefreshDueAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }
    private Entry Find(string id) => _entries.TryGetValue(id, out var entry) ? entry : throw new KeyNotFoundException(id);
    private void SetStatus(Entry entry, Func<AccountUsageStatus, AccountUsageStatus> update)
    {
        AccountUsageStatus status;
        lock (_sync) { if (entry.Removed) return; entry.Status = status = update(entry.Status); }
        Publish(status);
    }
    private void Publish(AccountUsageStatus status) => Changed?.Invoke(this, new(status));
    private static void Validate(AccountSettings account)
    {
        if (!Guid.TryParseExact(account.Id, "N", out _)) throw new ArgumentException("Invalid account id.");
        if (account.RefreshMinutes is < 1 or > 1440) throw new ArgumentOutOfRangeException(nameof(account.RefreshMinutes));
        if (!Enum.IsDefined(account.Provider)) throw new ArgumentException("Unknown provider.");
    }
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposing = true;
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }
    private async Task DisposeCoreAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        Task[] tasks;
        lock (_sync) tasks = _operations.Append(_loop ?? Task.CompletedTask).ToArray();
        // Connect callers retain their own errors; shutdown only waits for their owned processes to end.
        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
        _shutdown.Dispose();
    }
}
