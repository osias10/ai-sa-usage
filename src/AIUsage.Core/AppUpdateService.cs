using System.Text.Json;

namespace AIUsage.Core;

public enum AppUpdatePhase { Idle, Checking, Available, UpToDate, Downloading, ReadyToInstall, Error }

public sealed record AppUpdateState(
    AppUpdatePhase Phase = AppUpdatePhase.Idle,
    ReleaseInfo? Release = null,
    DateTimeOffset? LastCheckedAt = null,
    DateTimeOffset? RetryAfterUtc = null,
    double? DownloadPercent = null,
    string? InstallerPath = null,
    string? Message = null,
    UpdateAction Action = UpdateAction.None,
    UpdateChannel Channel = UpdateChannel.Stable);

/// <summary>
/// App-lifetime update orchestration. Events may run on a worker thread; UI consumers dispatch them.
/// A caller cancelling a check stops waiting, without cancelling a check shared with other callers.
/// The first download caller's token and CancelDownload both cancel the owned download.
/// HttpClient ownership remains with the caller of the checker/downloader constructors.
/// </summary>
public sealed class AppUpdateService : IAsyncDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan FailureInterval = TimeSpan.FromHours(1);
    private readonly UpdateChecker _checker;
    private readonly UpdateDownloader _downloader;
    private readonly GitHubReleaseSource _source;
    private readonly string _stateFile;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<string> _notifiedVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<UpdateChannel, ChannelState> _channels = [];
    private readonly HashSet<Operation> _operations = [];
    private AppUpdateState _state = new();
    private DateTimeOffset? _nextAutomaticCheck;
    private Task? _initialization;
    private Task? _scheduler;
    private Operation? _operation;
    private bool _autoCheckEnabled;
    private bool _initialized;
    private bool _disposed;
    private long _generation;
    private long _persistenceRevision;

    public AppUpdateService(UpdateChecker checker, UpdateDownloader downloader, GitHubReleaseSource source,
        Version currentVersion, string stateFile, TimeProvider? timeProvider = null)
        : this(checker, downloader, source, ReleaseVersion.FromVersion(currentVersion), stateFile, timeProvider)
    {
        CurrentVersion = currentVersion;
    }

    public AppUpdateService(UpdateChecker checker, UpdateDownloader downloader, GitHubReleaseSource source,
        ReleaseVersion currentVersion, string stateFile, TimeProvider? timeProvider = null)
    {
        _checker = checker;
        _downloader = downloader;
        _source = source;
        CurrentReleaseVersion = currentVersion;
        CurrentVersion = Version.Parse(currentVersion.ToString().Split('-', '+')[0]);
        _stateFile = Path.GetFullPath(stateFile);
        _time = timeProvider ?? TimeProvider.System;
    }

    public Version CurrentVersion { get; }
    public ReleaseVersion CurrentReleaseVersion { get; }
    public AppUpdateState State { get { lock (_gate) return _state; } }
    public UpdateChannel Channel { get { lock (_gate) return _state.Channel; } }
    public bool AutoCheckEnabled { get { lock (_gate) return _autoCheckEnabled; } }
    public event Action? Changed;
    public event Action<ReleaseInfo>? UpdateAvailable;

    public Task InitializeAsync(bool autoCheckEnabled, CancellationToken cancellationToken = default)
        => InitializeAsync(autoCheckEnabled, UpdateChannel.Stable, cancellationToken);

    public Task InitializeAsync(bool autoCheckEnabled, UpdateChannel channel, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _autoCheckEnabled = autoCheckEnabled;
            if (_initialization is null)
            {
                _state = _state with { Channel = channel };
                _initialization = Task.Run(InitializeCoreAsync);
            }
            return _initialization.WaitAsync(cancellationToken);
        }
    }

    private async Task InitializeCoreAsync()
    {
        string? warning = null;
        PersistedState? saved = null;
        try
        {
            if (File.Exists(_stateFile))
            {
                await using var stream = new FileStream(_stateFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous);
                if (stream.Length > 1024 * 1024) throw new InvalidDataException();
                saved = await JsonSerializer.DeserializeAsync<PersistedState>(stream, cancellationToken: _shutdown.Token)
                    .ConfigureAwait(false);
                if (saved is null || saved.SchemaVersion != 1) throw new InvalidDataException();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            saved = null;
            warning = "저장된 업데이트 정보를 읽지 못했습니다. 다시 확인할 수 있습니다.";
        }

        lock (_gate)
        {
            if (_disposed) return;
            if (saved?.Source == SourceKey)
            {
                foreach (var cache in saved.Channels ?? [])
                    if (cache is not null && Enum.IsDefined(cache.Channel)) _channels[cache.Channel] = cache;
                if (!_channels.ContainsKey(UpdateChannel.Stable))
                    _channels[UpdateChannel.Stable] = new() { Channel = UpdateChannel.Stable,
                        Release = saved.Release, LastCheckedAt = saved.LastCheckedAt, NextAutomaticCheckAt = saved.NextAutomaticCheckAt };
                RestoreChannel(_state.Channel, saved.RetryAfterUtc, warning);
                foreach (var key in saved.NotifiedUpdates ?? [])
                    if (IsNotificationKey(key)) _notifiedVersions.Add(key);
                foreach (var version in saved.NotifiedVersions ?? [])
                    if (ReleaseVersion.TryParse(version, out var parsed) && !parsed.IsPrerelease)
                        _notifiedVersions.Add(NotificationKey(UpdateChannel.Stable, UpdateAction.Upgrade, parsed.ToString()));
            }
            else _state = _state with { Message = warning };
            _initialized = true;
            _scheduler = Task.Run(SchedulerAsync);
        }
        NotifyChanged();
    }

    private bool IsUsableCachedRelease(ReleaseInfo? release)
    {
        if (release is null || !ReleaseVersion.TryParse(release.Version, out var version) || version.IsPrerelease != release.IsPrerelease)
            return false;
        var uri = release.PageUrl;
        return uri is { IsAbsoluteUri: true, Scheme: "https", Host: "github.com" } &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            uri.AbsolutePath.StartsWith($"/{_source.Owner}/{_source.Repository}/releases/tag/", StringComparison.OrdinalIgnoreCase);
    }

    private void RememberChannel()
        => _channels[_state.Channel] = new() { Channel = _state.Channel, Release = _state.Release,
            LastCheckedAt = _state.LastCheckedAt, NextAutomaticCheckAt = _nextAutomaticCheck };

    private void RestoreChannel(UpdateChannel channel, DateTimeOffset? retryAfter, string? message = null)
    {
        _channels.TryGetValue(channel, out var cache);
        var release = IsUsableCachedRelease(cache?.Release) &&
            (channel == UpdateChannel.Preview || cache!.Release?.IsPrerelease != true) ? cache?.Release : null;
        var action = GetAction(channel, release);
        _state = new(release is null ? AppUpdatePhase.Idle : action != UpdateAction.None
            ? AppUpdatePhase.Available : AppUpdatePhase.UpToDate,
            release, cache?.LastCheckedAt, retryAfter, Message: message, Action: action, Channel: channel);
        var now = _time.GetUtcNow();
        _nextAutomaticCheck = cache?.LastCheckedAt > now || cache?.NextAutomaticCheckAt > now + CheckInterval
            ? null : cache?.NextAutomaticCheckAt;
    }

    public async Task SetChannelAsync(UpdateChannel channel, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        Operation? previous;
        long generation;
        lock (_gate)
        {
            ThrowIfUnavailable();
            if (_state.Channel == channel) return;
            RememberChannel();
            generation = ++_generation;
            previous = _operation;
            _operation = null;
            RestoreChannel(channel, _state.RetryAfterUtc);
        }
        TryCancel(previous);
        NotifyChanged();
        await PersistAsync(generation, _shutdown.Token).ConfigureAwait(false);
        lock (_gate) if (_disposed || generation != _generation) return;
        // A user changing channels requests a check even when automatic checking is disabled.
        // The shared server retry deadline is still enforced by CheckAsync.
        await CheckAsync(manual: true, cancellationToken).ConfigureAwait(false);
    }

    public void SetAutoCheckEnabled(bool enabled)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _autoCheckEnabled = enabled;
        }
        NotifyChanged();
        // The minute scheduler observes this value. Toggling does not bypass a saved due/rate-limit time.
    }

    public Task CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Operation operation;
        var start = false;
        lock (_gate)
        {
            ThrowIfUnavailable();
            if (_operation is { } existing) operation = existing;
            else
            {
                var now = _time.GetUtcNow();
                if (_state.RetryAfterUtc > now || (!manual && (!_autoCheckEnabled || _nextAutomaticCheck > now)))
                    return Task.CompletedTask;
                operation = StartOperation(OperationKind.Check, _shutdown.Token);
                start = true;
            }
        }
        if (start) _ = CheckCoreAsync(operation);
        return operation.Completion.Task.WaitAsync(cancellationToken);
    }

    private async Task CheckCoreAsync(Operation operation)
    {
        AppUpdateState previous;
        lock (_gate) previous = _state;
        SetState(operation, state => state with { Phase = AppUpdatePhase.Checking, Message = null });
        try
        {
            var result = await _checker.CheckAsync(_source, CurrentReleaseVersion, operation.Channel, operation.Cancellation.Token).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            var release = result.Release;
            var action = GetAction(operation.Channel, release);
            var available = action != UpdateAction.None;
            var preserveInstaller = available && release == previous.Release && previous.InstallerPath is not null;
            var key = release is null ? null : NotificationKey(operation.Channel, action, release.Version);
            bool notify;
            lock (_gate)
            {
                if (!IsCurrent(operation)) return;
                _state = new(!available ? AppUpdatePhase.UpToDate : preserveInstaller
                    ? AppUpdatePhase.ReadyToInstall : AppUpdatePhase.Available, release, now,
                    RetryAfterUtc: _state.RetryAfterUtc > now ? _state.RetryAfterUtc : null,
                    DownloadPercent: preserveInstaller ? 100 : null,
                    InstallerPath: preserveInstaller ? previous.InstallerPath : null,
                    Action: action, Channel: operation.Channel);
                _nextAutomaticCheck = _state.RetryAfterUtc > now + CheckInterval ? _state.RetryAfterUtc : now + CheckInterval;
                RememberChannel();
                notify = available && _notifiedVersions.Add(key!);
            }
            var persisted = await PersistAsync(operation.Generation, operation.Cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!IsCurrent(operation)) return;
                if (notify && !persisted) _notifiedVersions.Remove(key!);
                NotifyChanged();
                if (notify && persisted && !operation.Cancellation.IsCancellationRequested) NotifyAvailable(release!);
            }
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
            SetState(operation, _ => previous);
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(operation, ex).ConfigureAwait(false);
        }
        finally { Complete(operation); }
    }

    public Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Operation operation;
        ReleaseInfo? release;
        var start = false;
        lock (_gate)
        {
            ThrowIfUnavailable();
            if (_operation is { } existing)
            {
                if (existing.Kind != OperationKind.Download) return DownloadAfterAsync(existing, cancellationToken);
                operation = existing;
                release = null;
            }
            else
            {
                release = _state.Release;
                if (_state.Phase == AppUpdatePhase.ReadyToInstall && _state.InstallerPath is not null)
                    return Task.CompletedTask;
                if (release?.CanDownload != true || GetAction(_state.Channel, release) == UpdateAction.None)
                    throw new InvalidOperationException("검증 가능한 설치 파일이 없습니다. 릴리스 페이지를 확인해 주세요.");
                if (_state.RetryAfterUtc > _time.GetUtcNow()) return Task.CompletedTask;
                operation = StartOperation(OperationKind.Download, cancellationToken);
                start = true;
            }
        }
        if (start) _ = DownloadCoreAsync(operation, release!);
        return operation.Completion.Task.WaitAsync(cancellationToken);
    }

    private async Task DownloadAfterAsync(Operation previous, CancellationToken cancellationToken)
    {
        await previous.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task next;
        lock (_gate)
        {
            if (_disposed || previous.Generation != _generation) return;
            next = DownloadAsync(cancellationToken);
        }
        await next.ConfigureAwait(false);
    }

    private async Task DownloadCoreAsync(Operation operation, ReleaseInfo release)
    {
        SetState(operation, state => state with { Phase = AppUpdatePhase.Downloading, DownloadPercent = 0, InstallerPath = null, Message = null });
        try
        {
            var progress = new InlineProgress<UpdateDownloadProgress>(value =>
            {
                lock (_gate)
                {
                    if (!IsCurrent(operation) || _state.Phase != AppUpdatePhase.Downloading) return;
                    _state = _state with { DownloadPercent = double.IsFinite(value.Percent) ? Math.Clamp(value.Percent, 0, 100) : null };
                }
                NotifyChanged();
            });
            var path = await _downloader.DownloadAsync(release, progress, operation.Cancellation.Token).ConfigureAwait(false);
            operation.Cancellation.Token.ThrowIfCancellationRequested();
            SetState(operation, state => state with { Phase = AppUpdatePhase.ReadyToInstall, DownloadPercent = 100,
                InstallerPath = path, RetryAfterUtc = state.RetryAfterUtc > _time.GetUtcNow() ? state.RetryAfterUtc : null, Message = null });
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
            SetState(operation, state => state with { Phase = AppUpdatePhase.Available, DownloadPercent = null,
                InstallerPath = null, Message = "다운로드를 취소했습니다. 다시 다운로드할 수 있습니다." });
        }
        catch (Exception ex) { await RecordFailureAsync(operation, ex).ConfigureAwait(false); }
        finally { Complete(operation); }
    }

    public void CancelDownload()
    {
        Operation? operation;
        lock (_gate) operation = _operation?.Kind == OperationKind.Download ? _operation : null;
        TryCancel(operation);
    }

    public async Task<string> GetVerifiedInstallerAsync(CancellationToken cancellationToken = default)
    {
        long generation;
        lock (_gate) { ThrowIfUnavailable(); generation = _generation; }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operation operation;
            string? path = null;
            ReleaseInfo? release = null;
            lock (_gate)
            {
                ThrowIfUnavailable();
                if (generation != _generation)
                    throw new OperationCanceledException("업데이트 채널이 변경되었습니다.", cancellationToken);
                if (_operation is { } existing) operation = existing;
                else
                {
                    if (_state.Phase != AppUpdatePhase.ReadyToInstall || _state.Release is null || _state.InstallerPath is null ||
                        GetAction(_state.Channel, _state.Release) == UpdateAction.None)
                        throw new InvalidOperationException("설치 파일을 먼저 다운로드해 주세요.");
                    path = _state.InstallerPath;
                    release = _state.Release;
                    operation = StartOperation(OperationKind.Verify, cancellationToken);
                }
            }
            if (path is null)
            {
                await operation.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }
            try
            {
                await _downloader.VerifyAsync(path, release!, operation.Cancellation.Token).ConfigureAwait(false);
                operation.Cancellation.Token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (!IsCurrent(operation) || _state.Release != release || _state.InstallerPath != path ||
                        GetAction(_state.Channel, release) == UpdateAction.None)
                        throw new OperationCanceledException("업데이트 채널이 변경되었습니다.", operation.Cancellation.Token);
                    return path;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SetState(operation, state => state with { Phase = AppUpdatePhase.Error, InstallerPath = null,
                    DownloadPercent = null, Message = FailureMessage(ex) });
                throw new InvalidDataException("설치 파일 검증에 실패했습니다. 다시 다운로드해 주세요.", ex);
            }
            finally { Complete(operation); }
        }
    }

    private async Task RecordFailureAsync(Operation operation, Exception error)
    {
        var now = _time.GetUtcNow();
        var retryAfter = (error as GitHubUpdateException)?.RetryAfterUtc;
        if (retryAfter <= now) retryAfter = null;
        long generation;
        lock (_gate)
        {
            if (_disposed) return;
            var current = IsCurrent(operation);
            if (!current)
            {
                // A detached request may still receive a server-wide rate limit. Keep only its
                // newer deadline; its phase, release, error text and installation state are obsolete.
                if (retryAfter is null || _state.RetryAfterUtc >= retryAfter) return;
                _state = _state with { RetryAfterUtc = retryAfter };
                if (_nextAutomaticCheck is null || _nextAutomaticCheck < retryAfter) _nextAutomaticCheck = retryAfter;
            }
            else
            {
                if (_state.RetryAfterUtc > now && (retryAfter is null || _state.RetryAfterUtc > retryAfter))
                    retryAfter = _state.RetryAfterUtc;
                _state = _state with { Phase = AppUpdatePhase.Error, LastCheckedAt = operation.Kind == OperationKind.Check
                    ? now : _state.LastCheckedAt, RetryAfterUtc = retryAfter, DownloadPercent = null,
                    InstallerPath = null, Message = FailureMessage(error) };
                _nextAutomaticCheck = retryAfter > now + FailureInterval ? retryAfter : now + FailureInterval;
            }
            generation = _generation;
            RememberChannel();
        }
        // Detached operations have a cancelled token, but a learned retry deadline must survive restart.
        await PersistAsync(generation, _shutdown.Token).ConfigureAwait(false);
        NotifyChanged();
    }

    private static string FailureMessage(Exception error) => error switch
    {
        GitHubUpdateException => error.Message,
        InvalidDataException => "설치 파일 검증에 실패했습니다. 다시 다운로드해 주세요.",
        _ => "업데이트 작업을 완료하지 못했습니다. 연결을 확인한 뒤 다시 시도해 주세요."
    };

    private async Task<bool> PersistAsync(long generation, CancellationToken cancellationToken)
    {
        PersistedState saved;
        long revision;
        lock (_gate)
        {
            if (_disposed || generation != _generation) return false;
            revision = ++_persistenceRevision;
            RememberChannel();
            _channels.TryGetValue(UpdateChannel.Stable, out var stable);
            saved = new()
            {
                // Keep legacy fields stable-only so older stable apps do not restore a preview selection.
                Source = SourceKey, LastCheckedAt = stable?.LastCheckedAt, RetryAfterUtc = _state.RetryAfterUtc,
                NextAutomaticCheckAt = stable?.NextAutomaticCheckAt, Release = stable?.Release,
                Channels = _channels.Values.OrderBy(cache => cache.Channel).ToArray(),
                NotifiedUpdates = _notifiedVersions.Order(StringComparer.Ordinal).ToArray(),
                NotifiedVersions = _notifiedVersions.Where(key => key.StartsWith("Stable|Upgrade|", StringComparison.Ordinal))
                    .Select(key => key["Stable|Upgrade|".Length..]).Order(StringComparer.Ordinal).ToArray()
            };
        }
        var temporary = _stateFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, saved, cancellationToken: cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || generation != _generation || revision != _persistenceRevision || cancellationToken.IsCancellationRequested) return false;
                File.Move(temporary, _stateFile, overwrite: true);
                return true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            lock (_gate)
                if (!_disposed && generation == _generation && revision == _persistenceRevision) _state = _state with
                {
                    Message = "업데이트 정보를 저장하지 못했습니다. 다음 실행에서 확인 일정과 알림 기록이 복원되지 않을 수 있습니다."
                };
            return false;
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task SchedulerAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), _time);
            do { await CheckAsync(manual: false, _shutdown.Token).ConfigureAwait(false); }
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { }
    }

    private void Complete(Operation operation)
    {
        lock (_gate)
        {
            if (_operation == operation) _operation = null;
            _operations.Remove(operation);
        }
        operation.Completion.TrySetResult();
        operation.Cancellation.Dispose();
    }

    private bool IsCurrent(Operation operation)
        => !_disposed && _operation == operation && operation.Generation == _generation && operation.Channel == _state.Channel;

    // Called while holding _gate. Detached operations remain tracked until their cancellation finishes.
    private Operation StartOperation(OperationKind kind, CancellationToken cancellationToken)
    {
        var operation = new Operation(kind, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken),
            _generation, _state.Channel);
        _operation = operation;
        _operations.Add(operation);
        return operation;
    }

    private static void TryCancel(Operation? operation)
    {
        try { operation?.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void SetState(Operation operation, Func<AppUpdateState, AppUpdateState> update)
    {
        lock (_gate)
        {
            if (!IsCurrent(operation)) return;
            _state = update(_state);
        }
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch { /* A UI consumer cannot break scheduling or operation cleanup. */ }
    }

    private void NotifyAvailable(ReleaseInfo release)
    {
        if (UpdateAvailable is not { } handlers) return;
        foreach (Action<ReleaseInfo> handler in handlers.GetInvocationList())
            try { handler(release); } catch { }
    }

    private string SourceKey => $"{_source.Owner}/{_source.Repository}";
    private UpdateAction GetAction(UpdateChannel channel, ReleaseInfo? release)
        => release is null ? UpdateAction.None : UpdateDecision.GetAction(CurrentReleaseVersion, channel, release);
    private static string NotificationKey(UpdateChannel channel, UpdateAction action, string version)
        => $"{channel}|{action}|{version}";
    private static bool IsNotificationKey(string? key)
    {
        if (key is null) return false;
        var parts = key.Split('|');
        return parts.Length == 3 && Enum.TryParse<UpdateChannel>(parts[0], out var channel) && Enum.IsDefined(channel) &&
            Enum.TryParse<UpdateAction>(parts[1], out var action) && action != UpdateAction.None && Enum.IsDefined(action) &&
            ReleaseVersion.TryParse(parts[2], out _);
    }
    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException("InitializeAsync must complete before using updates.");
    }

    public async ValueTask DisposeAsync()
    {
        Task? initialization;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            initialization = _initialization;
        }
        _shutdown.Cancel();
        if (initialization is not null) await initialization.ConfigureAwait(false);
        Task? scheduler;
        Task[] operations;
        lock (_gate) { scheduler = _scheduler; operations = _operations.Select(operation => operation.Completion.Task).ToArray(); }
        if (scheduler is not null) await scheduler.ConfigureAwait(false);
        await Task.WhenAll(operations).ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private enum OperationKind { Check, Download, Verify }
    private sealed class Operation(OperationKind kind, CancellationTokenSource cancellation, long generation, UpdateChannel channel)
    {
        public OperationKind Kind { get; } = kind;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public long Generation { get; } = generation;
        public UpdateChannel Channel { get; } = channel;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
    private sealed class PersistedState
    {
        public PersistedState() { }
        public int SchemaVersion { get; set; } = 1;
        public string Source { get; set; } = "";
        public DateTimeOffset? LastCheckedAt { get; set; }
        public DateTimeOffset? NextAutomaticCheckAt { get; set; }
        public DateTimeOffset? RetryAfterUtc { get; set; }
        public ReleaseInfo? Release { get; set; }
        public string[]? NotifiedVersions { get; set; }
        public ChannelState[]? Channels { get; set; }
        public string[]? NotifiedUpdates { get; set; }
    }
    private sealed class ChannelState
    {
        public ChannelState() { }
        public UpdateChannel Channel { get; set; }
        public ReleaseInfo? Release { get; set; }
        public DateTimeOffset? LastCheckedAt { get; set; }
        public DateTimeOffset? NextAutomaticCheckAt { get; set; }
    }
}
