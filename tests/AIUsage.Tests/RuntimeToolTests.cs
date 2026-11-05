using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AIUsage.Core;
using AIUsage.Infrastructure;
using Xunit;

namespace AIUsage.Tests;

public sealed class RuntimeToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AIUsage-RuntimeTools", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Payload = "synthetic runtime fixture only"u8.ToArray();
    private AppPaths Paths => new(_root);

    [Fact]
    public void EmbeddedCatalogKeepsReviewedVersionsAndSupportsNumericOrdering()
    {
        Assert.Equal("2.1.288", RuntimeCatalog.Get(ProviderKind.Claude).Version);
        Assert.Equal("0.157.0", RuntimeCatalog.Get(ProviderKind.Codex).Version);
        Assert.Equal("1.3.1", RuntimeCatalog.Get(ProviderKind.Antigravity).Version);
        using var manager = Manager(new Handler(), Release("2.1.9"), Release("2.1.100"), Release("2.1.20"));
        Assert.Equal("2.1.100", Assert.Single(manager.GetStatuses()).TargetVersion);
    }

    [Fact]
    public async Task AcquiresHighestCompatibleVersionAndNeverExecutesItsFile()
    {
        var handler = new Handler();
        await using var manager = Manager(handler, Release("2.1.9"), Release("2.1.100"));
        await using var lease = await manager.AcquireAsync(ProviderKind.Claude, null, default);
        Assert.Equal("2.1.100", lease.Version);
        Assert.Contains("generation-", lease.ExecutablePath);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(lease.ExecutablePath));
        Assert.Contains("/2.1.100/", Assert.Single(handler.Requests).AbsolutePath);
        Assert.Equal(RuntimeToolPhase.Ready, Assert.Single(manager.GetStatuses()).Phase);
        Assert.True(File.Exists(Path.Combine(Paths.Runtimes, "claude", "active.json")));
    }

    [Fact]
    public async Task LegacyHighestVersionIsReusedAndUnlistedDirectoriesAreIgnored()
    {
        var newest = Release("2.1.100");
        var legacy = await WriteLegacyAsync(newest);
        await WriteLegacyAsync(Release("999.1.0"));
        var handler = new Handler();
        await using var manager = Manager(handler, Release("2.1.9"), newest);
        await manager.CheckAsync(default);
        Assert.Equal("2.1.100", Assert.Single(manager.GetStatuses()).InstalledVersion);
        await using var lease = await manager.AcquireAsync(ProviderKind.Claude, null, default);
        Assert.Equal(legacy, lease.ExecutablePath);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UnknownOnlyDirectoryDoesNotBecomeAnInstalledRuntime()
    {
        await WriteLegacyAsync(Release("999.1.0"));
        var handler = new Handler();
        await using var manager = Manager(handler, Release());
        await manager.CheckAsync(default);
        var status = Assert.Single(manager.GetStatuses());
        Assert.False(status.IsInstalled);
        Assert.Null(status.InstalledVersion);
        Assert.Equal(RuntimeToolPhase.NotInstalled, status.Phase);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ReinstallWaitsForLeasesBlocksNewAdmissionsAndKeepsTheOriginalFile()
    {
        var original = await WriteLegacyAsync(Release());
        await using var manager = Manager(new Handler(), Release());
        var held = await manager.AcquireAsync(ProviderKind.Claude, null, default);
        try
        {
            var update = manager.UpdateAsync(ProviderKind.Claude, true, default);
            await WaitForAsync(() => Assert.Single(manager.GetStatuses()).Phase == RuntimeToolPhase.WaitingForIdle);
            var newcomer = manager.AcquireAsync(ProviderKind.Claude, null, default);
            Assert.False(update.IsCompleted);
            Assert.False(newcomer.IsCompleted);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(original));
            held.Dispose();
            held.Dispose();
            await update.WaitAsync(TimeSpan.FromSeconds(5));
            await using var replacement = await newcomer.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(original, replacement.ExecutablePath);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(original));
            Assert.Equal(Payload, await File.ReadAllBytesAsync(replacement.ExecutablePath));
        }
        finally { held.Dispose(); }
    }

    [Fact]
    public async Task ASecondManagerCannotActivateWhileAnotherManagerHoldsALease()
    {
        var original = await WriteLegacyAsync(Release());
        await using var first = Manager(new Handler(), Release());
        await using var second = Manager(new Handler(), Release());
        var held = await first.AcquireAsync(ProviderKind.Claude, null, default);
        try
        {
            var update = second.UpdateAsync(ProviderKind.Claude, true, default);
            await WaitForAsync(() => Assert.Single(second.GetStatuses()).Phase == RuntimeToolPhase.WaitingForIdle);
            Assert.False(update.IsCompleted);
            held.Dispose();
            await update.WaitAsync(TimeSpan.FromSeconds(5));
            await using var selected = await first.AcquireAsync(ProviderKind.Claude, null, default);
            Assert.NotEqual(original, selected.ExecutablePath);
        }
        finally { held.Dispose(); }
    }

    [Fact]
    public async Task CancellingAnActivationKeepsExistingFileAndReleasesWaitingReaders()
    {
        var original = await WriteLegacyAsync(Release());
        var handler = new Handler();
        await using var manager = Manager(handler, Release());
        await using var held = await manager.AcquireAsync(ProviderKind.Claude, null, default);
        var update = manager.UpdateAsync(ProviderKind.Claude, true, default);
        await WaitForAsync(() => Assert.Single(manager.GetStatuses()).Phase == RuntimeToolPhase.WaitingForIdle);
        var newcomer = manager.AcquireAsync(ProviderKind.Claude, null, default);
        manager.Cancel(ProviderKind.Claude);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
        await using var stillOriginal = await newcomer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(original, stillOriginal.ExecutablePath);
        Assert.Equal(RuntimeToolPhase.Cancelled, Assert.Single(manager.GetStatuses()).Phase);
        Assert.Single(handler.Requests);
        Assert.False(File.Exists(Path.Combine(Paths.Runtimes, "claude", "active.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidHashOrHttpFailureLeavesExistingInstallationAndBacksOff(bool httpFailure)
    {
        var previous = Release("2.1.9");
        var original = await WriteLegacyAsync(previous);
        var handler = new Handler((request, _) => Task.FromResult(new HttpResponseMessage(httpFailure ? HttpStatusCode.NotFound : HttpStatusCode.OK)
        {
            RequestMessage = request, Content = new ByteArrayContent("corrupt runtime fixture only!!"u8.ToArray())
        }));
        await using var manager = Manager(handler, previous, Release("2.1.100"));
        await using (var initial = await manager.AcquireAsync(ProviderKind.Claude, null, default))
        {
            Assert.Equal(original, initial.ExecutablePath);
            await WaitForAsync(() => Assert.Single(manager.GetStatuses()).Phase == RuntimeToolPhase.Failed);
        }
        for (var index = 0; index < 4; index++)
        {
            await using var lease = await manager.AcquireAsync(ProviderKind.Claude, null, default);
            Assert.Equal(original, lease.ExecutablePath);
            Assert.Equal("2.1.9", lease.Version);
        }
        Assert.Single(handler.Requests);
        var status = Assert.Single(manager.GetStatuses());
        Assert.Equal(RuntimeToolPhase.Failed, status.Phase);
        Assert.True(status.IsInstalled);
        Assert.Equal("2.1.9", status.InstalledVersion);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(original));
        Assert.False(File.Exists(Path.Combine(Paths.Runtimes, "claude", "active.json")));
        Assert.Empty(Directory.GetFiles(Paths.Runtimes, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InvalidHashDuringReinstallPreservesAnExistingActivePointer()
    {
        await using (var first = Manager(new Handler(), Release()))
            await first.UpdateAsync(ProviderKind.Claude, false, default);
        var pointer = Path.Combine(Paths.Runtimes, "claude", "active.json");
        var before = await File.ReadAllTextAsync(pointer);
        // The original catalog remains authoritative; corrupt only the bytes downloaded for the new generation.
        var handler = new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request, Content = new ByteArrayContent(Enumerable.Repeat((byte)0, Payload.Length).ToArray())
        }));
        await using var manager = Manager(handler, Release());
        await Assert.ThrowsAsync<ProviderException>(() => manager.UpdateAsync(ProviderKind.Claude, true, default));
        Assert.Equal(before, await File.ReadAllTextAsync(pointer));
        await using var lease = await manager.AcquireAsync(ProviderKind.Claude, null, default);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(lease.ExecutablePath));
    }

    [Fact]
    public async Task FirstInstallationFailureDoesNotRedownloadOnEveryEnsure()
    {
        var handler = new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request, Content = new ByteArrayContent(Enumerable.Repeat((byte)0, Payload.Length).ToArray())
        }));
        await using var manager = Manager(handler, Release());
        for (var index = 0; index < 3; index++)
            await Assert.ThrowsAsync<ProviderException>(() => manager.EnsureAsync(ProviderKind.Claude, null, default));
        Assert.Single(handler.Requests);
        Assert.False(Assert.Single(manager.GetStatuses()).IsInstalled);
        Assert.Equal(RuntimeToolPhase.Failed, Assert.Single(manager.GetStatuses()).Phase);
    }

    [Fact]
    public async Task ServerRetryAfterBlocksManualUpdateAndReinstallEvenAfterChecking()
    {
        var previous = Release("2.1.9");
        var original = await WriteLegacyAsync(previous);
        var handler = new Handler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(30));
            return Task.FromResult(response);
        });
        await using var manager = Manager(handler, previous, Release("2.1.100"));
        await Assert.ThrowsAsync<ProviderException>(() => manager.UpdateAsync(ProviderKind.Claude, false, default));
        var failed = Assert.Single(manager.GetStatuses());
        await manager.CheckAsync(default);
        var checkedStatus = Assert.Single(manager.GetStatuses());
        Assert.Equal(RuntimeToolPhase.Failed, checkedStatus.Phase);
        Assert.Equal(failed.Message, checkedStatus.Message);
        Assert.True(checkedStatus.IsInstalled);
        Assert.Equal(previous.Version, checkedStatus.InstalledVersion);
        Assert.NotNull(checkedStatus.LastCheckedAt);
        foreach (var reinstall in new[] { false, true })
        {
            var error = await Assert.ThrowsAsync<ProviderException>(() => manager.UpdateAsync(ProviderKind.Claude, reinstall, default));
            Assert.Equal(UsageState.RateLimited, error.State);
            Assert.True(error.RetryAfter > TimeSpan.FromMinutes(29));
            Assert.Contains("뒤 다시 시도", error.Message);
        }
        await using var lease = await manager.AcquireAsync(ProviderKind.Claude, null, default);
        Assert.Equal(original, lease.ExecutablePath);
        Assert.Single(handler.Requests);
        Assert.Equal(RuntimeToolPhase.Failed, Assert.Single(manager.GetStatuses()).Phase);
    }

    [Fact]
    public async Task CheckingPreservesFailureAndCancellationButManualRetryCanBypassLocalBackoff()
    {
        var previous = Release("2.1.9");
        var original = await WriteLegacyAsync(previous);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt = 0;
        var handler = new Handler(async (request, token) =>
        {
            switch (Interlocked.Increment(ref attempt))
            {
                case 1: return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
                case 2:
                    entered.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                    throw new InvalidOperationException("Unreachable cancelled fixture.");
                default: return Response(request);
            }
        });
        await using var manager = Manager(handler, previous, Release("2.1.100"));
        await Assert.ThrowsAsync<ProviderException>(() => manager.UpdateAsync(ProviderKind.Claude, false, default));
        var failed = Assert.Single(manager.GetStatuses());
        await manager.CheckAsync(default);
        Assert.Equal(RuntimeToolPhase.Failed, Assert.Single(manager.GetStatuses()).Phase);
        Assert.Equal(failed.Message, Assert.Single(manager.GetStatuses()).Message);

        var retry = manager.UpdateAsync(ProviderKind.Claude, false, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        manager.Cancel(ProviderKind.Claude);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);
        var cancelled = Assert.Single(manager.GetStatuses());
        await manager.CheckAsync(default);
        var checkedStatus = Assert.Single(manager.GetStatuses());
        Assert.Equal(RuntimeToolPhase.Cancelled, checkedStatus.Phase);
        Assert.Equal(cancelled.Message, checkedStatus.Message);
        Assert.True(checkedStatus.IsInstalled);
        Assert.Equal(previous.Version, checkedStatus.InstalledVersion);

        await manager.UpdateAsync(ProviderKind.Claude, false, default).WaitAsync(TimeSpan.FromSeconds(5));
        await manager.CheckAsync(default);
        var ready = Assert.Single(manager.GetStatuses());
        Assert.Equal(RuntimeToolPhase.Ready, ready.Phase);
        Assert.Equal("2.1.100", ready.InstalledVersion);
        Assert.Equal(3, handler.Requests.Count);
        Assert.True(File.Exists(original));
    }

    [Fact]
    public async Task CancellingAShortServerRetryDelayDoesNotRemoveTheServerLimit()
    {
        var original = await WriteLegacyAsync(Release());
        var handler = new Handler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        });
        await using var manager = Manager(handler, Release());
        var update = manager.UpdateAsync(ProviderKind.Claude, true, default);
        await WaitForAsync(() => Assert.Single(manager.GetStatuses()).Message?.Contains("다운로드 재시도", StringComparison.Ordinal) == true);
        manager.Cancel(ProviderKind.Claude);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
        await manager.CheckAsync(default);
        Assert.Equal(RuntimeToolPhase.Cancelled, Assert.Single(manager.GetStatuses()).Phase);
        var error = await Assert.ThrowsAsync<ProviderException>(() => manager.UpdateAsync(ProviderKind.Claude, true, default));
        Assert.Equal(UsageState.RateLimited, error.State);
        Assert.True(error.RetryAfter > TimeSpan.Zero);
        await using var lease = await manager.AcquireAsync(ProviderKind.Claude, null, default);
        Assert.Equal(original, lease.ExecutablePath);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ConcurrentAutomaticAndManualRequestsShareOneDownloadAndCheckDoesNotOverwriteProgress()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var respond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (request, token) =>
        {
            entered.TrySetResult();
            await respond.Task.WaitAsync(token);
            return Response(request);
        });
        await using var manager = Manager(handler, Release());
        var update = manager.UpdateAsync(ProviderKind.Claude, false, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var acquisitions = Enumerable.Range(0, 8).Select(_ => manager.AcquireAsync(ProviderKind.Claude, null, default)).ToArray();
        var secondUpdate = manager.UpdateAsync(ProviderKind.Claude, false, default);
        await manager.CheckAsync(default);
        Assert.Equal(RuntimeToolPhase.Downloading, Assert.Single(manager.GetStatuses()).Phase);
        respond.TrySetResult();
        await Task.WhenAll(update, secondUpdate).WaitAsync(TimeSpan.FromSeconds(5));
        var leases = await Task.WhenAll(acquisitions).WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Single(handler.Requests);
            Assert.Single(leases.Select(lease => lease.ExecutablePath).Distinct());
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }

    [Fact]
    public async Task CancelledDownloadKeepsLowerCompatibleVersionWithoutImmediatelyDownloadingAgain()
    {
        var previous = Release("2.1.9");
        var original = await WriteLegacyAsync(previous);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable cancelled fixture.");
        });
        await using var manager = Manager(handler, previous, Release("2.1.100"));
        var update = manager.UpdateAsync(ProviderKind.Claude, false, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        manager.Cancel(ProviderKind.Claude);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
        for (var index = 0; index < 3; index++)
        {
            await using var lease = await manager.AcquireAsync(ProviderKind.Claude, null, default);
            Assert.Equal(original, lease.ExecutablePath);
        }
        Assert.Single(handler.Requests);
        Assert.Equal(RuntimeToolPhase.Cancelled, Assert.Single(manager.GetStatuses()).Phase);
    }

    [Fact]
    public async Task AutomaticUpgradeDownloadsAlongsideAnImmediatelyUsableOlderLease()
    {
        var previous = Release("2.1.9");
        var original = await WriteLegacyAsync(previous);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var respond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (request, token) =>
        {
            entered.TrySetResult();
            await respond.Task.WaitAsync(token);
            return Response(request);
        });
        await using var manager = Manager(handler, previous, Release("2.1.100"));
        var held = await manager.AcquireAsync(ProviderKind.Claude, null, default).WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(original, held.ExecutablePath);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using (var second = await manager.AcquireAsync(ProviderKind.Claude, null, default).WaitAsync(TimeSpan.FromSeconds(5)))
                Assert.Equal(original, second.ExecutablePath);
            respond.TrySetResult();
            await WaitForAsync(() => Assert.Single(manager.GetStatuses()).Phase == RuntimeToolPhase.WaitingForIdle);
            var newcomer = manager.AcquireAsync(ProviderKind.Claude, null, default);
            Assert.False(newcomer.IsCompleted);
            held.Dispose();
            await using var upgraded = await newcomer.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("2.1.100", upgraded.Version);
            Assert.NotEqual(original, upgraded.ExecutablePath);
            Assert.Single(handler.Requests);
        }
        finally { held.Dispose(); }
    }

    [Fact]
    public async Task ReturningToAnOlderAppIgnoresANewerUnsupportedActiveVersion()
    {
        var previous = Release("2.1.9");
        var newer = Release("2.1.100");
        await using (var newerApp = Manager(new Handler(), previous, newer))
        {
            await using var installed = await newerApp.AcquireAsync(ProviderKind.Claude, null, default);
            Assert.Equal(newer.Version, installed.Version);
        }
        var handler = new Handler();
        await using var olderApp = Manager(handler, previous);
        await olderApp.CheckAsync(default);
        Assert.False(Assert.Single(olderApp.GetStatuses()).IsInstalled);
        await using var compatible = await olderApp.AcquireAsync(ProviderKind.Claude, null, default);
        Assert.Equal(previous.Version, compatible.Version);
        Assert.Contains("/2.1.9/", Assert.Single(handler.Requests).AbsolutePath);
        Assert.True(Directory.Exists(Path.Combine(Paths.Runtimes, "claude", newer.Version)));
    }

    [Theory]
    [InlineData("../outside.exe")]
    [InlineData("2.1.288/generation-not-a-guid/claude.exe")]
    [InlineData("999.1.0/claude.exe")]
    public async Task UntrustedActivePathsAreIgnored(string relativePath)
    {
        var release = Release();
        var directory = Path.Combine(Paths.Runtimes, "claude");
        Directory.CreateDirectory(directory);
        var untrustedTarget = Path.GetFullPath(Path.Combine(directory, relativePath));
        Assert.StartsWith(Path.GetFullPath(_root), untrustedTarget, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.GetDirectoryName(untrustedTarget)!);
        await File.WriteAllBytesAsync(untrustedTarget, Payload);
        await File.WriteAllTextAsync(Path.Combine(directory, "active.json"), JsonSerializer.Serialize(new
        {
            Active = new { release.Version, RelativePath = relativePath, release.Digest }
        }));
        await using var manager = Manager(new Handler(), release);
        await manager.CheckAsync(default);
        Assert.False(Assert.Single(manager.GetStatuses()).IsInstalled);
    }

    [Fact]
    public async Task DisposeCancelsAndAwaitsTheOutstandingDownload()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { exited.TrySetResult(); }
            throw new InvalidOperationException("Unreachable cancelled fixture.");
        });
        var manager = Manager(handler, Release());
        var update = manager.UpdateAsync(ProviderKind.Claude, false, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(exited.Task.IsCompletedSuccessfully);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
        await manager.DisposeAsync();
    }

    private RuntimeManager Manager(HttpMessageHandler handler, params RuntimeRelease[] releases) => new(Paths, handler, releases);
    private static RuntimeRelease Release(string version = "2.1.288") => new(ProviderKind.Claude, version, "claude.exe",
        new Uri($"https://downloads.claude.ai/claude-code-releases/{version}/win32-x64/claude.exe"),
        "SHA256", Convert.ToHexString(SHA256.HashData(Payload)), Payload.Length,
        new Uri($"https://downloads.claude.ai/claude-code-releases/{version}/manifest.json"));

    private async Task<string> WriteLegacyAsync(RuntimeRelease release)
    {
        var directory = Path.Combine(Paths.Runtimes, release.Provider.ToString().ToLowerInvariant(), release.Version);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, release.FileName);
        await File.WriteAllBytesAsync(path, Payload);
        return path;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static HttpResponseMessage Response(HttpRequestMessage request) => new(HttpStatusCode.OK)
    {
        RequestMessage = request, Content = new ByteArrayContent(Payload)
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? handle = null) : HttpMessageHandler
    {
        public System.Collections.Concurrent.ConcurrentQueue<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            return handle is null ? Task.FromResult(Response(request)) : handle(request, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
