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
    string? Message = null);

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
    private AppUpdateState _state = new();
    private DateTimeOffset? _nextAutomaticCheck;
    private Task? _initialization;
    private Task? _scheduler;
    private Operation? _operation;
    private bool _autoCheckEnabled;
    private bool _initialized;
    private bool _disposed;

    public AppUpdateService(UpdateChecker checker, UpdateDownloader downloader, GitHubReleaseSource source,
        Version currentVersion, string stateFile, TimeProvider? timeProvider = null)
    {
        _checker = checker;
        _downloader = downloader;
        _source = source;
        CurrentVersion = currentVersion;
        _stateFile = Path.GetFullPath(stateFile);
        _time = timeProvider ?? TimeProvider.System;
    }

    public Version CurrentVersion { get; }
    public AppUpdateState State { get { lock (_gate) return _state; } }
    public bool AutoCheckEnabled { get { lock (_gate) return _autoCheckEnabled; } }
    public event Action? Changed;
    public event Action<ReleaseInfo>? UpdateAvailable;

    public Task InitializeAsync(bool autoCheckEnabled, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _autoCheckEnabled = autoCheckEnabled;
            _initialization ??= Task.Run(InitializeCoreAsync);
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
                var release = IsUsableCachedRelease(saved.Release) ? saved.Release : null;
                _state = new(release is null ? AppUpdatePhase.Idle : IsNewerRelease(release)
                    ? AppUpdatePhase.Available : AppUpdatePhase.UpToDate,
                    release, saved.LastCheckedAt, saved.RetryAfterUtc, Message: warning);
                var now = _time.GetUtcNow();
                _nextAutomaticCheck = saved.LastCheckedAt > now || saved.NextAutomaticCheckAt > now + CheckInterval
                    ? null : saved.NextAutomaticCheckAt;
                foreach (var version in saved.NotifiedVersions ?? [])
                    if (Version.TryParse(version, out var parsed) && parsed > CurrentVersion)
                        _notifiedVersions.Add(version);
            }
            else _state = _state with { Message = warning };
            _initialized = true;
            _scheduler = Task.Run(SchedulerAsync);
        }
        NotifyChanged();
    }

    private bool IsUsableCachedRelease(ReleaseInfo? release)
    {
        if (release is null || !Version.TryParse(release.Version, out _))
            return false;
        var uri = release.PageUrl;
        return uri is { IsAbsoluteUri: true, Scheme: "https", Host: "github.com" } &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            uri.AbsolutePath.StartsWith($"/{_source.Owner}/{_source.Repository}/releases/tag/", StringComparison.OrdinalIgnoreCase);
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
                operation = new(OperationKind.Check, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
                _operation = operation;
                start = true;
            }
        }
        if (start) _ = CheckCoreAsync(operation);
        return operation.Completion.Task.WaitAsync(cancellationToken);
    }

    private async Task CheckCoreAsync(Operation operation)
    {
        var previous = State;
        SetState(state => state with { Phase = AppUpdatePhase.Checking, Message = null });
        try
        {
            var result = await _checker.CheckAsync(_source, CurrentVersion, operation.Cancellation.Token).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            var release = result.Release;
            var newer = result.IsNewer && release is not null;
            var preserveInstaller = newer && release == previous.Release && previous.InstallerPath is not null;
            lock (_gate)
            {
                _state = new(!newer ? AppUpdatePhase.UpToDate : preserveInstaller
                    ? AppUpdatePhase.ReadyToInstall : AppUpdatePhase.Available, release, now,
                    DownloadPercent: preserveInstaller ? 100 : null,
                    InstallerPath: preserveInstaller ? previous.InstallerPath : null);
                _nextAutomaticCheck = now + CheckInterval;
            }
            var notify = newer && !_notifiedVersions.Contains(release!.Version);
            if (notify) _notifiedVersions.Add(release!.Version);
            var persisted = await PersistAsync(operation.Cancellation.Token).ConfigureAwait(false);
            if (notify && !persisted) _notifiedVersions.Remove(release!.Version);
            NotifyChanged();
            if (notify && persisted && !operation.Cancellation.IsCancellationRequested) NotifyAvailable(release!);
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
            SetState(_ => previous);
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(ex, operation.Cancellation.Token).ConfigureAwait(false);
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
                if (existing.Kind != OperationKind.Download) return DownloadAfterAsync(existing.Completion.Task, cancellationToken);
                operation = existing;
                release = null;
            }
            else
            {
                release = _state.Release;
                if (_state.Phase == AppUpdatePhase.ReadyToInstall && _state.InstallerPath is not null)
                    return Task.CompletedTask;
                if (release?.CanDownload != true || !IsNewerRelease(release))
                    throw new InvalidOperationException("검증 가능한 설치 파일이 없습니다. 릴리스 페이지를 확인해 주세요.");
                if (_state.RetryAfterUtc > _time.GetUtcNow()) return Task.CompletedTask;
                operation = new(OperationKind.Download,
                    CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken));
                _operation = operation;
                start = true;
            }
        }
        if (start) _ = DownloadCoreAsync(operation, release!);
        return operation.Completion.Task.WaitAsync(cancellationToken);
    }

    private async Task DownloadAfterAsync(Task previous, CancellationToken cancellationToken)
    {
        await previous.WaitAsync(cancellationToken).ConfigureAwait(false);
        await DownloadAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadCoreAsync(Operation operation, ReleaseInfo release)
    {
        SetState(state => state with { Phase = AppUpdatePhase.Downloading, DownloadPercent = 0, InstallerPath = null, Message = null });
        try
        {
            var progress = new InlineProgress<UpdateDownloadProgress>(value =>
            {
                lock (_gate)
                {
                    if (_operation != operation || _state.Phase != AppUpdatePhase.Downloading) return;
                    _state = _state with { DownloadPercent = double.IsFinite(value.Percent) ? Math.Clamp(value.Percent, 0, 100) : null };
                }
                NotifyChanged();
            });
            var path = await _downloader.DownloadAsync(release, progress, operation.Cancellation.Token).ConfigureAwait(false);
            operation.Cancellation.Token.ThrowIfCancellationRequested();
            SetState(state => state with { Phase = AppUpdatePhase.ReadyToInstall, DownloadPercent = 100,
                InstallerPath = path, RetryAfterUtc = null, Message = null });
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
            SetState(state => state with { Phase = AppUpdatePhase.Available, DownloadPercent = null,
                InstallerPath = null, Message = "다운로드를 취소했습니다. 다시 다운로드할 수 있습니다." });
        }
        catch (Exception ex) { await RecordFailureAsync(ex, _shutdown.Token).ConfigureAwait(false); }
        finally { Complete(operation); }
    }

    public void CancelDownload()
    {
        CancellationTokenSource? cancellation;
        lock (_gate) cancellation = _operation?.Kind == OperationKind.Download ? _operation.Cancellation : null;
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async Task<string> GetVerifiedInstallerAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operation operation;
            string? path = null;
            ReleaseInfo? release = null;
            lock (_gate)
            {
                ThrowIfUnavailable();
                if (_operation is { } existing) operation = existing;
                else
                {
                    if (_state.Phase != AppUpdatePhase.ReadyToInstall || _state.Release is null || _state.InstallerPath is null)
                        throw new InvalidOperationException("설치 파일을 먼저 다운로드해 주세요.");
                    path = _state.InstallerPath;
                    release = _state.Release;
                    operation = new(OperationKind.Verify,
                        CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken));
                    _operation = operation;
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
                return path;
            }
            catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                SetState(state => state with { Phase = AppUpdatePhase.Error, InstallerPath = null,
                    DownloadPercent = null, Message = FailureMessage(ex) });
                throw new InvalidDataException("설치 파일 검증에 실패했습니다. 다시 다운로드해 주세요.", ex);
            }
            finally { Complete(operation); }
        }
    }

    private async Task RecordFailureAsync(Exception error, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var retryAfter = (error as GitHubUpdateException)?.RetryAfterUtc;
        if (retryAfter <= now) retryAfter = null;
        lock (_gate)
        {
            _state = _state with { Phase = AppUpdatePhase.Error, LastCheckedAt = _operation?.Kind == OperationKind.Check
                ? now : _state.LastCheckedAt, RetryAfterUtc = retryAfter, DownloadPercent = null,
                InstallerPath = null, Message = FailureMessage(error) };
            _nextAutomaticCheck = retryAfter > now + FailureInterval ? retryAfter : now + FailureInterval;
        }
        await PersistAsync(cancellationToken).ConfigureAwait(false);
        NotifyChanged();
    }

    private static string FailureMessage(Exception error) => error switch
    {
        GitHubUpdateException => error.Message,
        InvalidDataException => "설치 파일 검증에 실패했습니다. 다시 다운로드해 주세요.",
        _ => "업데이트 작업을 완료하지 못했습니다. 연결을 확인한 뒤 다시 시도해 주세요."
    };

    private async Task<bool> PersistAsync(CancellationToken cancellationToken)
    {
        PersistedState saved;
        lock (_gate) saved = new()
        {
            Source = SourceKey, LastCheckedAt = _state.LastCheckedAt, RetryAfterUtc = _state.RetryAfterUtc,
            NextAutomaticCheckAt = _nextAutomaticCheck, Release = _state.Release,
            NotifiedVersions = _notifiedVersions.Order(StringComparer.Ordinal).ToArray()
        };
        var temporary = _stateFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, saved, cancellationToken: cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _stateFile, overwrite: true);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            lock (_gate) _state = _state with
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
        lock (_gate) { if (_operation == operation) _operation = null; }
        operation.Completion.TrySetResult();
        operation.Cancellation.Dispose();
    }

    private void SetState(Func<AppUpdateState, AppUpdateState> update)
    {
        lock (_gate) _state = update(_state);
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
    private bool IsNewerRelease(ReleaseInfo release) => Version.TryParse(release.Version, out var version) && version > CurrentVersion;
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
        Task? operation;
        lock (_gate) { scheduler = _scheduler; operation = _operation?.Completion.Task; }
        if (scheduler is not null) await scheduler.ConfigureAwait(false);
        if (operation is not null) await operation.ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private enum OperationKind { Check, Download, Verify }
    private sealed class Operation(OperationKind kind, CancellationTokenSource cancellation)
    {
        public OperationKind Kind { get; } = kind;
        public CancellationTokenSource Cancellation { get; } = cancellation;
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
    }
}
