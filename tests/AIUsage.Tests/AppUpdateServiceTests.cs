using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class AppUpdateServiceTests
{
    [Fact]
    public async Task InitializationDoesNotWaitForNetworkAndChecksShareOneRequest()
    {
        using var files = new TestFiles();
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var http = Client(async (request, token) =>
        {
            Interlocked.Increment(ref calls);
            await response.Task.WaitAsync(token);
            return ReleaseResponse(request);
        });
        await using var service = Service(http, files);
        await service.InitializeAsync(true).WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => Volatile.Read(ref calls) == 1);
        var first = service.CheckAsync(manual: true);
        using var cancelledWaiter = new CancellationTokenSource();
        var second = service.CheckAsync(manual: true, cancelledWaiter.Token);
        cancelledWaiter.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(1, calls);
        Assert.False(first.IsCompleted);
        response.SetResult();
        await first;
        Assert.Equal(AppUpdatePhase.Available, service.State.Phase);
        Assert.Equal("0.4.7", service.State.Release!.Version);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AutomaticChecksWaitTwentyFourHoursAndManualChecksBypassTheSchedule()
    {
        using var files = new TestFiles();
        var clock = new ManualClock();
        var calls = 0;
        using var http = Client((request, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(ReleaseResponse(request));
        });
        await using var service = Service(http, files, clock);
        await service.InitializeAsync(true);
        await Until(() => service.State.Phase == AppUpdatePhase.Available);
        await service.CheckAsync(false);
        clock.Advance(TimeSpan.FromHours(23));
        await service.CheckAsync(false);
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromHours(1));
        await Until(() => Volatile.Read(ref calls) == 2);
        await service.CheckAsync(false);
        await service.CheckAsync(true);
        Assert.Equal(3, calls);
        service.SetAutoCheckEnabled(false);
        clock.Advance(TimeSpan.FromHours(25));
        await service.CheckAsync(false);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task SavedScheduleAndNotificationVersionSurviveRestart()
    {
        using var files = new TestFiles();
        var clock = new ManualClock();
        var version = "0.4.7";
        var calls = 0;
        using var http = Client((request, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(ReleaseResponse(request, version));
        });
        var notifications = new List<string>();
        await using (var first = Service(http, files, clock))
        {
            first.UpdateAvailable += release => notifications.Add(release.Version);
            await first.InitializeAsync(false);
            await first.CheckAsync(true);
            await first.CheckAsync(true);
            Assert.Single(notifications);
        }
        await using var restarted = Service(http, files, clock);
        restarted.UpdateAvailable += release => notifications.Add(release.Version);
        await restarted.InitializeAsync(true);
        Assert.Equal(AppUpdatePhase.Available, restarted.State.Phase);
        Assert.NotNull(restarted.State.LastCheckedAt);
        await restarted.CheckAsync(false);
        Assert.Equal(2, calls);
        await restarted.CheckAsync(true);
        Assert.Single(notifications);
        version = "0.4.8";
        await restarted.CheckAsync(true);
        Assert.Equal(["0.4.7", "0.4.8"], notifications);
    }

    [Fact]
    public async Task RateLimitRestrictsManualChecksAndPersistsAcrossRestart()
    {
        using var files = new TestFiles();
        var clock = new ManualClock();
        var retryAt = clock.GetUtcNow().AddHours(2);
        var calls = 0;
        using var http = Client((request, _) =>
        {
            var count = Interlocked.Increment(ref calls);
            if (count > 1) return Task.FromResult(ReleaseResponse(request));
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAt);
            return Task.FromResult(response);
        });
        await using (var first = Service(http, files, clock))
        {
            await first.InitializeAsync(false);
            await first.CheckAsync(true);
            Assert.Equal(AppUpdatePhase.Error, first.State.Phase);
            Assert.NotNull(first.State.RetryAfterUtc);
            await first.CheckAsync(true);
            Assert.Equal(1, calls);
        }
        await using var restarted = Service(http, files, clock);
        await restarted.InitializeAsync(false);
        await restarted.CheckAsync(true);
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(1));
        await restarted.CheckAsync(true);
        Assert.Equal(2, calls);
        Assert.Equal(AppUpdatePhase.Available, restarted.State.Phase);
        Assert.Null(restarted.State.RetryAfterUtc);
    }

    [Fact]
    public async Task AutomaticFailureRetriesAfterAnHourWhileManualRetryRemainsPossible()
    {
        using var files = new TestFiles();
        var clock = new ManualClock();
        var calls = 0;
        using var http = Client((request, _) =>
        {
            var count = Interlocked.Increment(ref calls);
            return Task.FromResult(count <= 2
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request }
                : ReleaseResponse(request));
        });
        await using var service = Service(http, files, clock);
        await service.InitializeAsync(true);
        await Until(() => service.State.Phase == AppUpdatePhase.Error);
        await service.CheckAsync(false);
        clock.Advance(TimeSpan.FromMinutes(59));
        await service.CheckAsync(false);
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Until(() => Volatile.Read(ref calls) == 2);
        await service.CheckAsync(false);
        await service.CheckAsync(true);
        Assert.Equal(3, calls);
        Assert.Equal(AppUpdatePhase.Available, service.State.Phase);
    }

    [Fact]
    public async Task DownloadMergesCallersBlocksRecheckAndCanBeCancelledThenRetried()
    {
        using var files = new TestFiles();
        var metadataCalls = 0;
        var downloadCalls = 0;
        using var http = Client(async (request, token) =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                Interlocked.Increment(ref metadataCalls);
                return ReleaseResponse(request);
            }
            if (Interlocked.Increment(ref downloadCalls) == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return AssetResponse(request);
        });
        await using var service = Service(http, files);
        await service.InitializeAsync(false);
        await service.CheckAsync(true);
        var first = service.DownloadAsync();
        await Until(() => Volatile.Read(ref downloadCalls) == 1);
        var duplicate = service.DownloadAsync();
        var check = service.CheckAsync(true);
        Assert.Equal(1, metadataCalls);
        Assert.Equal(1, downloadCalls);
        service.CancelDownload();
        await Task.WhenAll(first, duplicate, check);
        Assert.Equal(AppUpdatePhase.Available, service.State.Phase);
        Assert.Null(service.State.InstallerPath);
        Assert.Empty(Directory.EnumerateFiles(files.Downloads, "*", SearchOption.AllDirectories));
        await service.DownloadAsync();
        Assert.Equal(2, downloadCalls);
        Assert.Equal(AppUpdatePhase.ReadyToInstall, service.State.Phase);
        Assert.Equal(100d, service.State.DownloadPercent);
        Assert.Equal(service.State.InstallerPath, await service.GetVerifiedInstallerAsync());
    }

    [Fact]
    public async Task UpToDateRetainsLatestVersionButDoesNotDownloadOrNotify()
    {
        using var files = new TestFiles();
        using var http = Client((request, _) => Task.FromResult(ReleaseResponse(request, "0.4.6")));
        await using var service = Service(http, files);
        var notifications = 0;
        service.UpdateAvailable += _ => notifications++;
        await service.InitializeAsync(false);
        await service.CheckAsync(true);
        Assert.Equal(AppUpdatePhase.UpToDate, service.State.Phase);
        Assert.Equal("0.4.6", service.State.Release!.Version);
        Assert.Equal(0, notifications);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync());
    }

    [Fact]
    public async Task InstallerIsVerifiedAgainBeforeReturningItsPath()
    {
        using var files = new TestFiles();
        using var http = Client((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com"
            ? ReleaseResponse(request) : AssetResponse(request)));
        await using var service = Service(http, files);
        await service.InitializeAsync(false);
        await service.CheckAsync(true);
        await service.DownloadAsync();
        var path = service.State.InstallerPath!;
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetVerifiedInstallerAsync());
        Assert.Equal(AppUpdatePhase.Error, service.State.Phase);
        Assert.Null(service.State.InstallerPath);
        Assert.False(string.IsNullOrWhiteSpace(service.State.Message));
        await service.DownloadAsync();
        Assert.Equal(AppUpdatePhase.ReadyToInstall, service.State.Phase);
    }

    [Fact]
    public async Task DisposeCancelsAndWaitsForOwnedDownload()
    {
        using var files = new TestFiles();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        using var http = Client(async (request, token) =>
        {
            if (request.RequestUri!.Host == "api.github.com") return ReleaseResponse(request);
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return AssetResponse(request);
        });
        var service = Service(http, files);
        await service.InitializeAsync(false);
        await service.CheckAsync(true);
        var downloading = service.DownloadAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await downloading;
        Assert.True(cancelled);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.CheckAsync(true));
        // Caller still owns the client after service shutdown.
        using var response = await http.GetAsync("https://api.github.com/repos/example/ai-sa/releases/latest");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("future-schema")]
    [InlineData("oversize")]
    public async Task InvalidSavedStateDoesNotBlockInitializationOrRetry(string kind)
    {
        using var files = new TestFiles();
        await File.WriteAllTextAsync(files.State, kind switch
        {
            "malformed" => "{invalid-json",
            "future-schema" => "{\"SchemaVersion\":999,\"Source\":\"example/ai-sa\"}",
            _ => new string(' ', 1024 * 1024 + 1)
        });
        using var http = Client((request, _) => Task.FromResult(ReleaseResponse(request)));
        await using var service = Service(http, files);
        await service.InitializeAsync(false);
        Assert.Equal(AppUpdatePhase.Idle, service.State.Phase);
        Assert.Contains("읽지 못", service.State.Message);
        await service.CheckAsync(true);
        Assert.Equal(AppUpdatePhase.Available, service.State.Phase);
    }

    [Fact]
    public async Task PersistenceFailureKeepsReleaseUsableWithoutAnnouncingAnUnrecordedVersion()
    {
        using var files = new TestFiles();
        Directory.CreateDirectory(files.State); // A directory cannot be atomically replaced with the state JSON file.
        using var http = Client((request, _) => Task.FromResult(ReleaseResponse(request)));
        await using var service = Service(http, files);
        var notifications = 0;
        service.UpdateAvailable += _ => notifications++;
        await service.InitializeAsync(false);
        await service.CheckAsync(true);
        Assert.Equal(AppUpdatePhase.Available, service.State.Phase);
        Assert.Contains("저장하지 못", service.State.Message);
        Assert.Equal(0, notifications);
        Directory.Delete(files.State);
        await service.CheckAsync(true);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task ClockMovingBackwardsDoesNotPreserveAnUnboundedFutureSchedule()
    {
        using var files = new TestFiles();
        var clock = new ManualClock();
        var calls = 0;
        using var http = Client((request, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(ReleaseResponse(request));
        });
        await using (var first = Service(http, files, clock))
        {
            await first.InitializeAsync(false);
            await first.CheckAsync(true);
        }
        clock.Advance(TimeSpan.FromDays(-2));
        await using var restarted = Service(http, files, clock);
        await restarted.InitializeAsync(true);
        await Until(() => Volatile.Read(ref calls) == 2);
        await restarted.CheckAsync(false);
        Assert.Equal(clock.GetUtcNow(), restarted.State.LastCheckedAt);
    }

    [Fact]
    public async Task PreviewOptOutOffersAndVerifiesAnOlderStableInstaller()
    {
        using var files = new TestFiles();
        using var http = Client((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com"
            ? ReleaseResponse(request, request.RequestUri.AbsolutePath.EndsWith("/latest") ? "0.4.9" : "0.4.10-beta.2")
            : AssetResponse(request)));
        await using var service = Service(http, files, ReleaseVersion.Parse("0.4.10-beta.1"));
        await service.InitializeAsync(false, UpdateChannel.Preview);
        await service.CheckAsync(true);
        Assert.Equal(UpdateAction.Upgrade, service.State.Action);
        await service.DownloadAsync();
        var previewPath = await service.GetVerifiedInstallerAsync();
        await service.SetChannelAsync(UpdateChannel.Stable);
        Assert.Equal(UpdateChannel.Stable, service.Channel);
        Assert.Equal("0.4.9", service.State.Release!.Version);
        Assert.Equal(UpdateAction.ReturnToStable, service.State.Action);
        Assert.Equal(AppUpdatePhase.Available, service.State.Phase);
        Assert.Null(service.State.InstallerPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetVerifiedInstallerAsync());
        await service.DownloadAsync();
        var stablePath = await service.GetVerifiedInstallerAsync();
        Assert.NotEqual(previewPath, stablePath);
        Assert.EndsWith("AISA-0.4.9-win-x64-setup.exe", stablePath);
        Assert.Equal("0.4.10-beta.1", service.CurrentReleaseVersion.ToString());
        Assert.Equal(new Version(0, 4, 10), service.CurrentVersion);
    }

    [Fact]
    public async Task StableInstallationCannotUseChannelOptOutAsAnArbitraryDowngrade()
    {
        using var files = new TestFiles();
        using var http = Client((request, _) => Task.FromResult(ReleaseResponse(request, "0.4.8")));
        await using var service = Service(http, files, ReleaseVersion.Parse("0.4.9"));
        await service.InitializeAsync(false, UpdateChannel.Preview);
        await service.SetChannelAsync(UpdateChannel.Stable);
        Assert.Equal(UpdateAction.None, service.State.Action);
        Assert.Equal(AppUpdatePhase.UpToDate, service.State.Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync());
    }

    [Fact]
    public async Task ChannelSpecificCacheScheduleAndReturnNotificationSurviveRestart()
    {
        using var files = new TestFiles();
        var clock = new ManualClock();
        var calls = 0;
        using var http = Client((request, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(ReleaseResponse(request,
                request.RequestUri!.AbsolutePath.EndsWith("/latest") ? "0.4.9" : "0.4.10-beta.2"));
        });
        var notices = new List<string>();
        await using (var first = Service(http, files, ReleaseVersion.Parse("0.4.10-beta.1"), clock))
        {
            first.UpdateAvailable += release => notices.Add(release.Version);
            await first.InitializeAsync(false, UpdateChannel.Preview);
            await first.CheckAsync(true);
            await first.SetChannelAsync(UpdateChannel.Stable);
            await first.SetChannelAsync(UpdateChannel.Preview);
            await first.SetChannelAsync(UpdateChannel.Stable);
        }
        Assert.Equal(["0.4.10-beta.2", "0.4.9"], notices);
        await using (var stable = Service(http, files, ReleaseVersion.Parse("0.4.10-beta.1"), clock))
        {
            stable.UpdateAvailable += release => notices.Add(release.Version);
            await stable.InitializeAsync(true, UpdateChannel.Stable);
            Assert.Equal("0.4.9", stable.State.Release!.Version);
            Assert.Equal(UpdateAction.ReturnToStable, stable.State.Action);
            var callsBefore = calls;
            await stable.CheckAsync(false);
            Assert.Equal(callsBefore, calls);
            await stable.CheckAsync(true);
        }
        await using var preview = Service(http, files, ReleaseVersion.Parse("0.4.10-beta.1"), clock);
        preview.UpdateAvailable += release => notices.Add(release.Version);
        await preview.InitializeAsync(false, UpdateChannel.Preview);
        Assert.Equal("0.4.10-beta.2", preview.State.Release!.Version);
        Assert.Equal(UpdateAction.Upgrade, preview.State.Action);
        await preview.CheckAsync(true);
        Assert.Equal(["0.4.10-beta.2", "0.4.9"], notices);
    }

    [Fact]
    public async Task LateCancelledCheckCannotOverwriteTheNewChannelOrItsSavedState()
    {
        using var files = new TestFiles();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloads = 0;
        using var http = Client(async (request, _) =>
        {
            if (request.RequestUri!.Host != "api.github.com")
            {
                Interlocked.Increment(ref downloads);
                return AssetResponse(request);
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/latest"))
            {
                started.TrySetResult();
                await releaseOld.Task; // Deliberately uncooperative transport.
                return ReleaseResponse(request, "0.4.9");
            }
            return ReleaseResponse(request, "0.4.10-beta.2");
        });
        await using (var service = Service(http, files, ReleaseVersion.Parse("0.4.8")))
        {
            await service.InitializeAsync(false);
            var oldCheck = service.CheckAsync(true);
            var queuedDownload = service.DownloadAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await service.SetChannelAsync(UpdateChannel.Preview).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("0.4.10-beta.2", service.State.Release!.Version);
            }
            finally { releaseOld.TrySetResult(); }
            await oldCheck;
            await queuedDownload;
            Assert.Equal(UpdateChannel.Preview, service.Channel);
            Assert.Equal("0.4.10-beta.2", service.State.Release!.Version);
            Assert.Equal(AppUpdatePhase.Available, service.State.Phase);
            Assert.Equal(0, downloads);
        }
        await using var restarted = Service(http, files, ReleaseVersion.Parse("0.4.8"));
        await restarted.InitializeAsync(false, UpdateChannel.Preview);
        Assert.Equal("0.4.10-beta.2", restarted.State.Release!.Version);
    }

    [Fact]
    public async Task ChannelChangeCancelsAnActiveDownloadAndKeepsTheNewTarget()
    {
        using var files = new TestFiles();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadCancelled = false;
        using var http = Client(async (request, token) =>
        {
            if (request.RequestUri!.Host == "api.github.com")
                return ReleaseResponse(request, request.RequestUri.AbsolutePath.EndsWith("/latest") ? "0.4.9" : "0.4.10-beta.2");
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { downloadCancelled = true; throw; }
            return AssetResponse(request);
        });
        await using var service = Service(http, files, ReleaseVersion.Parse("0.4.10-beta.1"));
        await service.InitializeAsync(false, UpdateChannel.Preview);
        await service.CheckAsync(true);
        var download = service.DownloadAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.SetChannelAsync(UpdateChannel.Stable);
        await download;
        Assert.True(downloadCancelled);
        Assert.Equal("0.4.9", service.State.Release!.Version);
        Assert.Equal(UpdateAction.ReturnToStable, service.State.Action);
        Assert.Null(service.State.InstallerPath);
        Assert.Null(service.State.Message);
        Assert.Empty(Directory.EnumerateFiles(files.Downloads, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SwitchingChannelsDoesNotBypassAStoredServerRetryDeadline()
    {
        using var files = new TestFiles();
        var clock = new ManualClock();
        var calls = 0;
        var retryAt = clock.GetUtcNow().AddHours(2);
        using var http = Client((request, _) =>
        {
            if (Interlocked.Increment(ref calls) > 1) return Task.FromResult(ReleaseResponse(request, "0.4.10-beta.2"));
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAt);
            return Task.FromResult(response);
        });
        await using (var first = Service(http, files, ReleaseVersion.Parse("0.4.9"), clock))
        {
            await first.InitializeAsync(false);
            await first.CheckAsync(true);
            await first.SetChannelAsync(UpdateChannel.Preview);
            Assert.Equal(1, calls);
            Assert.NotNull(first.State.RetryAfterUtc);
        }
        await using var restarted = Service(http, files, ReleaseVersion.Parse("0.4.9"), clock);
        await restarted.InitializeAsync(false, UpdateChannel.Preview);
        await restarted.CheckAsync(true);
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(1));
        await restarted.CheckAsync(true);
        Assert.Equal(2, calls);
        Assert.Equal(UpdateAction.Upgrade, restarted.State.Action);
    }

    [Fact]
    public async Task LegacyStableCacheAndNotificationsMigrateWithoutBecomingPreviewCache()
    {
        using var files = new TestFiles();
        using var http = Client((request, _) => Task.FromResult(ReleaseResponse(request)));
        var result = await new UpdateChecker(http).CheckAsync(new("example", "ai-sa"), new Version(0, 4, 6), default);
        await File.WriteAllTextAsync(files.State, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, Source = "example/ai-sa", Release = result.Release,
            NotifiedVersions = new[] { "0.4.7" }, LastCheckedAt = DateTimeOffset.UtcNow,
            NextAutomaticCheckAt = DateTimeOffset.UtcNow.AddHours(24)
        }));
        await using var service = Service(http, files);
        var notifications = 0;
        service.UpdateAvailable += _ => notifications++;
        await service.InitializeAsync(false, UpdateChannel.Preview);
        Assert.Null(service.State.Release);
        await service.SetChannelAsync(UpdateChannel.Stable);
        Assert.Equal("0.4.7", service.State.Release!.Version);
        Assert.Equal(0, notifications);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(3, 2)]
    public async Task DetachedRateLimitPreservesNewChannelStateAndTheLongestDeadline(int currentRetryHours, int detachedRetryHours)
    {
        using var files = new TestFiles();
        var clock = new ManualClock();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var detachedDeadline = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().AddHours(detachedRetryHours).ToUnixTimeSeconds());
        var currentDeadline = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().AddHours(currentRetryHours).ToUnixTimeSeconds());
        using var http = Client(async (request, _) =>
        {
            Interlocked.Increment(ref calls);
            if (request.RequestUri!.AbsolutePath.EndsWith("/latest"))
            {
                started.TrySetResult();
                await finishOld.Task; // A transport that returns headers despite cancellation.
                return RateLimitResponse(request, detachedDeadline);
            }
            return currentRetryHours > 0 ? RateLimitResponse(request, currentDeadline)
                : ReleaseResponse(request, "0.4.10-beta.2");
        });
        DateTimeOffset expected;
        await using (var service = Service(http, files, ReleaseVersion.Parse("0.4.9"), clock))
        {
            await service.InitializeAsync(false);
            var detached = service.CheckAsync(true);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AppUpdateState before;
            try
            {
                await service.SetChannelAsync(UpdateChannel.Preview);
                before = service.State;
            }
            finally { finishOld.TrySetResult(); }
            await detached;
            expected = detachedDeadline > currentDeadline ? detachedDeadline : currentDeadline;
            Assert.Equal(before with { RetryAfterUtc = expected }, service.State);
            await service.CheckAsync(true);
            Assert.Equal(2, calls);
            await service.SetChannelAsync(UpdateChannel.Stable);
            Assert.Equal(expected, service.State.RetryAfterUtc);
            Assert.Equal(2, calls);
        }
        await using var restarted = Service(http, files, ReleaseVersion.Parse("0.4.9"), clock);
        await restarted.InitializeAsync(false, UpdateChannel.Preview);
        Assert.Equal(expected, restarted.State.RetryAfterUtc);
        await restarted.CheckAsync(true);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SuccessfulCurrentCheckCannotClearALateDetachedRateLimit()
    {
        using var files = new TestFiles();
        var clock = new ManualClock();
        var stableStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previewStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishStable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishPreview = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().AddHours(2).ToUnixTimeSeconds());
        var calls = 0;
        using var http = Client(async (request, _) =>
        {
            Interlocked.Increment(ref calls);
            if (request.RequestUri!.AbsolutePath.EndsWith("/latest"))
            {
                stableStarted.TrySetResult();
                await finishStable.Task;
                return RateLimitResponse(request, deadline);
            }
            previewStarted.TrySetResult();
            await finishPreview.Task;
            return ReleaseResponse(request, "0.4.10-beta.2");
        });
        await using var service = Service(http, files, ReleaseVersion.Parse("0.4.9"), clock);
        await service.InitializeAsync(false);
        var oldCheck = service.CheckAsync(true);
        await stableStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var switchChannel = service.SetChannelAsync(UpdateChannel.Preview);
        try
        {
            await previewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            finishStable.TrySetResult();
            await oldCheck;
            Assert.Equal(AppUpdatePhase.Checking, service.State.Phase);
            Assert.Equal(deadline, service.State.RetryAfterUtc);
        }
        finally { finishStable.TrySetResult(); finishPreview.TrySetResult(); }
        await switchChannel;
        Assert.Equal(AppUpdatePhase.Available, service.State.Phase);
        Assert.Equal("0.4.10-beta.2", service.State.Release!.Version);
        Assert.Equal(deadline, service.State.RetryAfterUtc);
        await service.CheckAsync(true);
        Assert.Equal(2, calls);
    }

    private static HttpResponseMessage RateLimitResponse(HttpRequestMessage request, DateTimeOffset deadline)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(deadline);
        return response;
    }

    private static AppUpdateService Service(HttpClient http, TestFiles files, ReleaseVersion version, TimeProvider? clock = null) => new(
        new UpdateChecker(http), new UpdateDownloader(http, files.Downloads), new("example", "ai-sa"), version, files.State, clock);

    private static AppUpdateService Service(HttpClient http, TestFiles files, TimeProvider? clock = null) => new(
        new UpdateChecker(http), new UpdateDownloader(http, files.Downloads), new("example", "ai-sa"),
        new Version(0, 4, 6), files.State, clock);

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle)
        => new(new ResponseHandler(handle));

    // Synthetic installer bytes exercise integrity validation without creating or executing a real binary.
    private static readonly byte[] Installer = [4, 5, 6];
    private static HttpResponseMessage AssetResponse(HttpRequestMessage request) => new(HttpStatusCode.OK)
    { RequestMessage = request, Content = new ByteArrayContent(Installer) };

    private static HttpResponseMessage ReleaseResponse(HttpRequestMessage request, string version = "0.4.7")
    {
        var name = $"AISA-{version}-win-x64-setup.exe";
        var json = JsonSerializer.Serialize(new
        {
            tag_name = "v" + version, html_url = $"https://github.com/example/ai-sa/releases/tag/v{version}",
            body = "Synthetic release notes", draft = false, prerelease = version.Contains('-'),
            assets = new[] { new { name, browser_download_url = $"https://github.com/example/ai-sa/releases/download/v{version}/{name}",
                size = Installer.Length, digest = "sha256:" + Convert.ToHexString(SHA256.HashData(Installer)).ToLowerInvariant() } }
        });
        if (request.RequestUri!.AbsolutePath.EndsWith("/releases")) json = "[" + json + "]";
        return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(json) };
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handle(request, cancellationToken);
    }

    private sealed class TestFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "AIUsage.UpdateTests", Guid.NewGuid().ToString("N"));
        public string State => Path.Combine(_directory, "updates.json");
        public string Downloads => Path.Combine(_directory, "downloads");
        public TestFiles() => Directory.CreateDirectory(_directory);
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => GetUtcNow().UtcTicks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate) _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            ManualTimer[] timers;
            lock (_gate) { _now += duration; timers = _timers.ToArray(); }
            foreach (var timer in timers) timer.FireIfDue();
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _due;
            private TimeSpan _period;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    if (_disposed) return false;
                    _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                    _period = period;
                    return true;
                }
            }
            public void FireIfDue()
            {
                lock (clock._gate)
                {
                    if (_disposed || _due is null || _due > clock._now) return;
                    _due = _period > TimeSpan.Zero ? clock._now + _period : null;
                }
                callback(state);
            }
            public void Dispose() { lock (clock._gate) { _disposed = true; clock._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
