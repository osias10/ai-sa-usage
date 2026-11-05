using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIUsage.Core;

namespace AIUsage.Infrastructure;

public sealed partial class RuntimeManager
{
    private sealed class ToolState(RuntimeRelease target)
    {
        public RuntimeToolStatus Status = new(target.Provider, target.Version);
        public Task? Pending;
        public CancellationTokenSource? Cancellation;
        public DateTimeOffset RetryAfter;
        public DateTimeOffset? ProviderRetryAt;
        public Exception? Failure;
        public int Leases;
        public bool Activating;
    }
    private sealed record InstalledRuntime(RuntimeRelease Release, string Path);
    private sealed record RuntimePointer(string Version, string RelativePath, string Digest);
    private sealed record ActivationState(RuntimePointer Active, RuntimePointer? Previous = null);
    private readonly object _stateLock = new();
    private readonly Dictionary<ProviderKind, RuntimeRelease[]> _releases;
    private readonly Dictionary<ProviderKind, ToolState> _tools;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<Task> _operations = [];
    private readonly bool _requireWindows;
    private Task? _checking;
    private Task? _disposeTask;
    private bool _disposed;
    public event EventHandler? Changed;

    /// <summary>The injected handler/catalog permit fixture tests without executing a vendor binary.</summary>
    public RuntimeManager(AppPaths paths, HttpMessageHandler? handler, IEnumerable<RuntimeRelease>? releases = null)
    {
        _paths = paths;
        _requireWindows = handler is null;
        var catalog = (releases ?? RuntimeCatalog.Releases).ToArray();
        foreach (var release in catalog) ValidateRelease(release);
        if (catalog.GroupBy(release => (release.Provider, release.Version)).Any(group => group.Count() > 1))
            throw new ArgumentException("The runtime catalog contains duplicate versions.", nameof(releases));
        _releases = catalog.GroupBy(release => release.Provider).ToDictionary(group => group.Key,
            group => group.OrderByDescending(release => Version.Parse(release.Version)).ToArray());
        _tools = _releases.ToDictionary(pair => pair.Key, pair => new ToolState(pair.Value[0]));
        _http = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AIUsageWidget/1.0");
    }

    public IReadOnlyList<RuntimeToolStatus> GetStatuses()
    {
        lock (_stateLock) return _tools.OrderBy(pair => pair.Key).Select(pair => pair.Value.Status).ToArray();
    }

    public async Task<string> EnsureAsync(ProviderKind provider, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken)
    {
        await using var lease = await AcquireAsync(provider, progress, cancellationToken).ConfigureAwait(false);
        // Compatibility for older callers. Provider operations use AcquireAsync to pin their whole lifetime.
        return lease.ExecutablePath;
    }

    public async Task<RuntimeLease> AcquireAsync(ProviderKind provider, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken)
    {
        var state = State(provider);
        CheckPlatform();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var token = linked.Token;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task? activation;
            lock (_stateLock) activation = state.Activating ? state.Pending : null;
            if (activation is not null)
            {
                try { await activation.WaitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { /* Recheck the unchanged installation after a failed/cancelled activation. */ }
                continue;
            }
            using (await MutationLockAsync(provider, token).ConfigureAwait(false))
            {
                lock (_stateLock) if (state.Activating) continue;
                var installed = await FindInstalledAsync(provider, token).ConfigureAwait(false);
                DateTimeOffset retryAfter;
                Exception? failure;
                lock (_stateLock) { retryAfter = state.RetryAfter; failure = state.Failure; }
                if (installed is not null)
                {
                    bool reportInstalled;
                    lock (_stateLock) reportInstalled = state.Pending is not { IsCompleted: false } && state.Failure is null;
                    if (reportInstalled) SetInstalledStatus(provider, installed);
                    FileStream handle;
                    lock (_stateLock)
                    {
                        token.ThrowIfCancellationRequested();
                        ObjectDisposedException.ThrowIf(_disposed, this);
                        handle = OpenLeaseFile(provider);
                        state.Leases++;
                        if (installed.Release.Version != _releases[provider][0].Version &&
                            DateTimeOffset.UtcNow >= state.RetryAfter && state.Pending is not { IsCompleted: false })
                            _ = ObserveAutomaticUpdateAsync(StartUpdate(provider, false, null, _shutdown.Token));
                    }
                    return new RuntimeLease(installed.Path, installed.Release.Version, () =>
                    {
                        handle.Dispose();
                        lock (_stateLock) state.Leases--;
                    });
                }
                if (installed is null && DateTimeOffset.UtcNow < retryAfter && failure is not null) throw failure;
            }
            try { await StartUpdate(provider, false, progress, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                // A failed upgrade must not prevent reads through a previously verified compatible runtime.
                using var mutation = await MutationLockAsync(provider, token).ConfigureAwait(false);
                if (await FindInstalledAsync(provider, token).ConfigureAwait(false) is null) throw;
            }
        }
    }

    public Task CheckAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_checking is { IsCompleted: false }) return _checking.WaitAsync(cancellationToken);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _checking = completion.Task;
            _operations.Add(completion.Task);
            _ = CheckTrackedAsync(completion, cancellationToken);
            return completion.Task;
        }
    }

    private async Task CheckTrackedAsync(TaskCompletionSource completion, CancellationToken cancellationToken)
    {
        await Task.Yield();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try
        {
            foreach (var provider in _tools.Keys)
            {
                linked.Token.ThrowIfCancellationRequested();
                lock (_stateLock) if (_tools[provider].Pending is { IsCompleted: false }) continue;
                SetStatus(provider, status => status with { Phase = RuntimeToolPhase.Checking, Message = "설치 파일 확인 중", Percent = null }, checking: true);
                try
                {
                    using var mutation = await MutationLockAsync(provider, linked.Token).ConfigureAwait(false);
                    var installed = await FindInstalledAsync(provider, linked.Token).ConfigureAwait(false);
                    SetInstalledStatus(provider, installed, checking: true);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    SetStatus(provider, status => status with { Phase = RuntimeToolPhase.Failed, Message = FriendlyError(error), Percent = null }, checking: true);
                }
            }
            completion.TrySetResult();
        }
        catch (OperationCanceledException error)
        {
            foreach (var provider in _tools.Keys)
                SetStatus(provider, status => status.Phase == RuntimeToolPhase.Checking
                    ? status with { Phase = RuntimeToolPhase.Cancelled, Message = "확인을 취소했습니다.", Percent = null } : status, checking: true);
            completion.TrySetCanceled(error.CancellationToken);
        }
        catch (Exception error) { completion.TrySetException(error); }
        finally { lock (_stateLock) _operations.Remove(completion.Task); }
    }

    public Task UpdateAsync(ProviderKind provider, bool reinstall, CancellationToken cancellationToken)
        => StartUpdate(provider, reinstall, null, cancellationToken);

    private static async Task ObserveAutomaticUpdateAsync(Task operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch { /* UpdateTrackedAsync publishes the failure and backoff; the existing lease remains usable. */ }
    }

    private Task StartUpdate(ProviderKind provider, bool reinstall, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken)
    {
        var state = State(provider);
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (state.Pending is { IsCompleted: false }) return state.Pending.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (state.ProviderRetryAt is { } retryAt && retryAt > DateTimeOffset.UtcNow)
            {
                var remaining = retryAt - DateTimeOffset.UtcNow;
                var wait = remaining.TotalSeconds < 60 ? $"{Math.Max(1, Math.Ceiling(remaining.TotalSeconds)):0}초"
                    : $"{Math.Ceiling(remaining.TotalMinutes):0}분";
                return Task.FromException(new ProviderException(UsageState.RateLimited,
                    $"공식 다운로드 서버의 요청 제한으로 대기 중입니다. {wait} 뒤 다시 시도해 주세요. 기존 도구는 유지합니다.", remaining));
            }
            state.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Pending = completion.Task;
            _operations.Add(completion.Task);
            _ = UpdateTrackedAsync(provider, reinstall, progress, state.Cancellation, completion);
            return completion.Task;
        }
    }

    private async Task UpdateTrackedAsync(ProviderKind provider, bool reinstall, IProgress<ProviderProgress>? progress,
        CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        await Task.Yield();
        var state = _tools[provider];
        var token = cancellation.Token;
        Exception? failure = null;
        try
        {
            CheckPlatform();
            var target = _releases[provider][0];
            InstalledRuntime? previous;
            SetStatus(provider, status => status with { Phase = RuntimeToolPhase.Checking, Message = "설치 파일 확인 중", Percent = null });
            using (await MutationLockAsync(provider, token).ConfigureAwait(false))
            {
                previous = await FindInstalledAsync(provider, token).ConfigureAwait(false);
                SetStatus(provider, status => status with { InstalledVersion = previous?.Release.Version, IsInstalled = previous is not null });
                if (!reinstall && previous?.Release.Version == target.Version)
                {
                    SetInstalledStatus(provider, previous);
                    lock (_stateLock) { state.Failure = null; state.RetryAfter = default; state.ProviderRetryAt = null; }
                    return;
                }
            }

            var directory = Path.Combine(ProviderDirectory(provider), target.Version, "generation-" + Guid.NewGuid().ToString("N"));
            EnsureSafePath(directory);
            var downloadProgress = new InlineProgress(value =>
            {
                SetStatus(provider, status => status with
                {
                    Phase = value.Message.Contains("검증", StringComparison.Ordinal) ? RuntimeToolPhase.Verifying : RuntimeToolPhase.Downloading,
                    Message = value.Message, Percent = value.Percent
                });
                progress?.Report(value);
            });
            SetStatus(provider, status => status with { Phase = RuntimeToolPhase.Downloading, Message = "공식 실행 파일 다운로드 중", Percent = 0 });
            var path = await DownloadNewAsync(target, directory, downloadProgress, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (_stateLock) state.Activating = true;
            SetStatus(provider, status => status with { Phase = RuntimeToolPhase.WaitingForIdle, Message = "진행 중인 작업이 끝나면 적용합니다.", Percent = null });

            // Writer intent blocks new leases across processes. Existing operations keep their shared handles.
            using (await MutationLockAsync(provider, token).ConfigureAwait(false))
            using (await ExclusiveLeaseFileAsync(provider, token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                previous = await FindInstalledAsync(provider, token).ConfigureAwait(false);
                if (!reinstall && previous?.Release.Version == target.Version)
                {
                    SetInstalledStatus(provider, previous);
                }
                else
                {
                    SetStatus(provider, status => status with { Phase = RuntimeToolPhase.Applying, Message = "검증한 실행 파일 적용 중", Percent = null });
                    EnsureSafePath(path);
                    if (!await IsValidAsync(path, target, token).ConfigureAwait(false))
                        throw new ProviderException(UsageState.UpdateRequired, "검증된 실행 파일이 변경되어 적용하지 않았습니다. 기존 도구는 유지합니다.");
                    var installed = new InstalledRuntime(target, path);
                    await WriteActivationAsync(provider, installed, previous, token).ConfigureAwait(false);
                    SetInstalledStatus(provider, installed);
                }
                lock (_stateLock) { state.Failure = null; state.RetryAfter = default; state.ProviderRetryAt = null; }
            }
        }
        catch (OperationCanceledException error)
        {
            failure = error;
            lock (_stateLock) { state.Failure = error; state.RetryAfter = DateTimeOffset.UtcNow.AddSeconds(30); }
            SetStatus(provider, status => status with { Phase = RuntimeToolPhase.Cancelled, Message = "작업을 취소했습니다. 기존 도구는 그대로 유지합니다.", Percent = null });
        }
        catch (Exception error)
        {
            failure = error;
            lock (_stateLock)
            {
                state.Failure = error;
                state.RetryAfter = FailureRetryAt(error);
            }
            SetStatus(provider, status => status with { Phase = RuntimeToolPhase.Failed, Message = FriendlyError(error), Percent = null });
        }
        finally
        {
            lock (_stateLock)
            {
                state.Cancellation = null;
                state.Activating = false;
                _operations.Remove(completion.Task);
            }
            cancellation.Dispose();
            if (failure is OperationCanceledException cancelled) completion.TrySetCanceled(cancelled.CancellationToken);
            else if (failure is not null) completion.TrySetException(failure);
            else completion.TrySetResult();
        }
    }

    public void Cancel(ProviderKind provider)
    {
        lock (_stateLock) if (_tools.TryGetValue(provider, out var state)) state.Cancellation?.Cancel();
    }

    private ToolState State(ProviderKind provider)
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _tools.TryGetValue(provider, out var state) ? state : throw new ArgumentOutOfRangeException(nameof(provider));
        }
    }

    private void SetInstalledStatus(ProviderKind provider, InstalledRuntime? installed, bool checking = false)
        => SetStatus(provider, status => status with
        {
            InstalledVersion = installed?.Release.Version, IsInstalled = installed is not null,
            Phase = installed is null ? RuntimeToolPhase.NotInstalled : installed.Release.Version == status.TargetVersion
                ? RuntimeToolPhase.Ready : RuntimeToolPhase.UpdateAvailable,
            Message = installed is null ? "설치된 도구가 없습니다." : installed.Release.Version == status.TargetVersion
                ? "이 앱에서 지원하는 최신 버전입니다." : "이 앱에서 지원하는 새 버전을 설치할 수 있습니다.",
            Percent = null, LastCheckedAt = DateTimeOffset.UtcNow
        }, checking);

    private void SetStatus(ProviderKind provider, Func<RuntimeToolStatus, RuntimeToolStatus> update, bool checking = false)
    {
        bool changed;
        lock (_stateLock)
        {
            var state = _tools[provider];
            if (checking && state.Pending is { IsCompleted: false }) return;
            var next = update(state.Status);
            if (checking && state.Failure is not null && state.Status.Phase is (RuntimeToolPhase.Failed or RuntimeToolPhase.Cancelled))
                next = next with { Phase = state.Status.Phase, Message = state.Status.Message, Percent = null };
            changed = next != state.Status;
            state.Status = next;
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<InstalledRuntime?> FindInstalledAsync(ProviderKind provider, CancellationToken token)
    {
        var candidates = new List<InstalledRuntime>();
        var activeFile = Path.Combine(ProviderDirectory(provider), "active.json");
        try
        {
            EnsureSafePath(activeFile);
            if (File.Exists(activeFile) && new FileInfo(activeFile).Length <= 16384)
            {
                var saved = JsonSerializer.Deserialize<ActivationState>(await File.ReadAllTextAsync(activeFile, token).ConfigureAwait(false));
                foreach (var pointer in new[] { saved?.Active, saved?.Previous })
                    if (pointer is not null && ResolvePointer(provider, pointer) is { } candidate) candidates.Add(candidate);
            }
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or ArgumentException) { }
        candidates.AddRange(_releases[provider].Select(release =>
            new InstalledRuntime(release, Path.Combine(ProviderDirectory(provider), release.Version, release.FileName))));
        foreach (var candidate in candidates.DistinctBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(candidate => Version.Parse(candidate.Release.Version)))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                EnsureSafePath(candidate.Path);
                if (await IsValidAsync(candidate.Path, candidate.Release, token).ConfigureAwait(false)) return candidate;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return null;
    }

    private InstalledRuntime? ResolvePointer(ProviderKind provider, RuntimePointer pointer)
    {
        var release = _releases[provider].FirstOrDefault(release => release.Version == pointer.Version &&
            string.Equals(release.Digest, pointer.Digest, StringComparison.OrdinalIgnoreCase));
        if (release is null || string.IsNullOrWhiteSpace(pointer.RelativePath) || Path.IsPathRooted(pointer.RelativePath)) return null;
        var parts = pointer.RelativePath.Replace('\\', '/').Split('/');
        if (parts.Length is not (2 or 3) || parts[0] != release.Version || parts[^1] != release.FileName ||
            parts.Length == 3 && (!parts[1].StartsWith("generation-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(parts[1]["generation-".Length..], "N", out _))) return null;
        var path = Path.GetFullPath(Path.Combine(ProviderDirectory(provider), Path.Combine(parts)));
        EnsureSafePath(path);
        return new(release, path);
    }

    private async Task WriteActivationAsync(ProviderKind provider, InstalledRuntime active, InstalledRuntime? previous, CancellationToken token)
    {
        var directory = ProviderDirectory(provider);
        var destination = Path.Combine(directory, "active.json");
        var temporary = Path.Combine(directory, ".active-" + Guid.NewGuid().ToString("N") + ".partial");
        EnsureSafePath(destination);
        RuntimePointer Pointer(InstalledRuntime installed) => new(installed.Release.Version,
            Path.GetRelativePath(directory, installed.Path), installed.Release.Digest);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new ActivationState(Pointer(active), previous is null ? null : Pointer(previous)));
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, token).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private string ProviderDirectory(ProviderKind provider) => Path.GetFullPath(Path.Combine(_paths.Runtimes, provider.ToString().ToLowerInvariant()));

    private void EnsureSafePath(string path)
    {
        var baseRoot = Path.GetFullPath(_paths.Runtimes).TrimEnd(Path.DirectorySeparatorChar);
        var root = baseRoot + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Runtime path is outside the private runtime directory.");
        for (string? current = full; current is not null && (current.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(current, baseRoot, StringComparison.OrdinalIgnoreCase)); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Runtime paths cannot contain links.");
    }

    private async Task<FileStream> MutationLockAsync(ProviderKind provider, CancellationToken token)
    {
        var directory = ProviderDirectory(provider);
        EnsureSafePath(directory);
        Directory.CreateDirectory(directory);
        return await OpenExclusiveAsync(Path.Combine(directory, "mutation.lock"), token).ConfigureAwait(false);
    }

    private FileStream OpenLeaseFile(ProviderKind provider)
    {
        var path = Path.Combine(ProviderDirectory(provider), "leases.lock");
        EnsureSafePath(path);
        if (!File.Exists(path)) using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private Task<FileStream> ExclusiveLeaseFileAsync(ProviderKind provider, CancellationToken token)
        => OpenExclusiveAsync(Path.Combine(ProviderDirectory(provider), "leases.lock"), token);

    private async Task<FileStream> OpenExclusiveAsync(string path, CancellationToken token)
    {
        EnsureSafePath(path);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 11)
            { await Task.Delay(50, token).ConfigureAwait(false); }
        }
    }

    private static void ValidateRelease(RuntimeRelease release)
    {
        if (!Enum.IsDefined(release.Provider) || !Regex.IsMatch(release.Version, "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\z") ||
            !Version.TryParse(release.Version, out _) || string.IsNullOrWhiteSpace(release.FileName) ||
            release.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            release.FileName.IndexOfAny(['/', '\\', ':']) >= 0 || release.FileName != Path.GetFileName(release.FileName) ||
            !release.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !IsAllowedUri(release.Provider, release.DownloadUri) || release.Size is <= 0 or > MaximumDownloadBytes ||
            release.HashAlgorithm is not ("SHA256" or "SHA512") ||
            !Regex.IsMatch(release.Digest, release.HashAlgorithm == "SHA256" ? "^[0-9a-fA-F]{64}\\z" : "^[0-9a-fA-F]{128}\\z"))
            throw new ArgumentException("Invalid embedded runtime release metadata.", nameof(release));
    }

    private void CheckPlatform()
    {
        if (_requireWindows && (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64))
            throw new ProviderException(UsageState.UpdateRequired, "Windows x64에서 지원하는 실행 파일입니다.");
    }

    private static string FriendlyError(Exception error) => error is ProviderException ? error.Message
        : error is IOException or UnauthorizedAccessException ? "실행 파일을 저장하거나 확인할 수 없습니다. 기존 도구는 유지합니다."
        : "도구를 준비하지 못했습니다. 기존 도구는 유지합니다.";

    private static DateTimeOffset FailureRetryAt(Exception error)
    {
        var delay = error is ProviderException { RetryAfter: { } retry } && retry > TimeSpan.FromMinutes(5)
            ? retry : TimeSpan.FromMinutes(5);
        return RetryAt(delay);
    }

    private void RecordProviderRetryAfter(ProviderKind provider, TimeSpan? delay)
    {
        if (delay is not { } duration || duration <= TimeSpan.Zero) return;
        var retryAt = RetryAt(duration);
        lock (_stateLock)
        {
            var state = _tools[provider];
            if (state.ProviderRetryAt is null || state.ProviderRetryAt < retryAt) state.ProviderRetryAt = retryAt;
        }
    }

    private static DateTimeOffset RetryAt(TimeSpan delay)
    {
        var now = DateTimeOffset.UtcNow;
        return delay > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now.Add(delay);
    }

    private sealed class InlineProgress(Action<ProviderProgress> report) : IProgress<ProviderProgress>
    {
        public void Report(ProviderProgress value) => report(value);
    }

    public ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            _shutdown.Cancel();
            return new(_disposeTask = FinishDisposeAsync(_operations.ToArray()));
        }
    }

    private async Task FinishDisposeAsync(Task[] operations)
    {
        try { await Task.WhenAll(operations).ConfigureAwait(false); } catch { }
        _http.Dispose();
        _shutdown.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
