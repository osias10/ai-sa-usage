using System.Net;
using System.Security.Cryptography;
using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class UpdateDownloaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AIUsage-UpdateTests", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Payload = "verified installer fixture"u8.ToArray();

    [Fact]
    public async Task PublishesOnlyACompleteVerifiedFileAndCanReverifyBeforeExecution()
    {
        using var client = Client(Payload);
        var progress = new CaptureProgress();
        var downloader = new UpdateDownloader(client, _directory);
        var path = await downloader.DownloadAsync(Release(), progress, default);
        Assert.Equal("AISA-0.4.6-win-x64-setup.exe", Path.GetFileName(path));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
        Assert.DoesNotContain(Directory.GetFiles(_directory, "*", SearchOption.AllDirectories), file => file.EndsWith(".part", StringComparison.Ordinal));
        Assert.Equal(100d, progress.Values[^1].Percent);
        Assert.Equal(Payload.Length, progress.Values[^1].ReceivedBytes);
        await downloader.VerifyAsync(path, Release(), default);
    }

    [Fact]
    public async Task SameVersionDownloadsUseIndependentAttemptDirectories()
    {
        using var client = Client(Payload);
        var downloader = new UpdateDownloader(client, _directory);
        var first = await downloader.DownloadAsync(Release(), null, default);
        var second = await downloader.DownloadAsync(Release(), null, default);
        Assert.NotEqual(first, second);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(first));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(second));
    }

    [Fact]
    public async Task CorruptHashNeverPublishesAnInstallerAndCleansPartialFiles()
    {
        using var client = Client(Payload);
        var release = Release();
        release = release with { Asset = release.Asset! with { Sha256 = new string('0', 64) } };
        await Assert.ThrowsAsync<GitHubUpdateException>(() => new UpdateDownloader(client, _directory).DownloadAsync(release, null, default));
        AssertNoFiles();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task RejectsContentLengthMismatch(int difference)
    {
        using var client = Client(Payload);
        var release = Release();
        release = release with { Asset = release.Asset! with { Size = Payload.Length + difference } };
        await Assert.ThrowsAsync<GitHubUpdateException>(() => new UpdateDownloader(client, _directory).DownloadAsync(release, null, default));
        AssertNoFiles();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task EnforcesActualStreamSizeWhenNoLengthHeaderExists(int difference)
    {
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StreamContent(new NonSeekableStream(Payload))
        })));
        var release = Release();
        release = release with { Asset = release.Asset! with { Size = Payload.Length + difference } };
        await Assert.ThrowsAsync<GitHubUpdateException>(() => new UpdateDownloader(client, _directory).DownloadAsync(release, null, default));
        AssertNoFiles();
    }

    [Fact]
    public async Task CancelingAnActiveTransferRemovesItsPartialFile()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = Client(Payload);
        var progress = new CaptureProgress(value => { if (value.ReceivedBytes > 0) cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UpdateDownloader(client, _directory).DownloadAsync(Release(), progress, cancellation.Token));
        AssertNoFiles();
    }

    [Fact]
    public async Task MissingDigestMakesNoHttpRequestAndCreatesNoFiles()
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((_, _) => { requests++; throw new InvalidOperationException("Unexpected HTTP request."); }));
        var release = Release();
        release = release with { Asset = release.Asset! with { Sha256 = null } };
        await Assert.ThrowsAsync<GitHubUpdateException>(() => new UpdateDownloader(client, _directory).DownloadAsync(release, null, default));
        Assert.Equal(0, requests);
        Assert.False(Directory.Exists(_directory));
    }

    [Theory]
    [InlineData("https://example.test/installer.exe")]
    [InlineData("http://github.com/example/ai-sa/releases/download/v0.4.6/AISA-0.4.6-win-x64-setup.exe")]
    [InlineData("https://github.com/example/other/releases/download/v0.4.6/AISA-0.4.6-win-x64-setup.exe")]
    public async Task ConstructedReleaseCannotBypassTheRepositoryOriginContract(string address)
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((_, _) => { requests++; throw new InvalidOperationException("Unexpected HTTP request."); }));
        var release = Release();
        release = release with { Asset = release.Asset! with { DownloadUrl = new(address) } };
        await Assert.ThrowsAsync<GitHubUpdateException>(() => new UpdateDownloader(client, _directory).DownloadAsync(release, null, default));
        Assert.Equal(0, requests);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task AllowsGitHubAssetCdnRedirectAndStillVerifiesBytes()
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            requests++;
            if (requests == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request };
                response.Headers.Location = new("https://release-assets.githubusercontent.com/github-production-release-asset/fixture");
                return Task.FromResult(response);
            }
            Assert.Equal("release-assets.githubusercontent.com", request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(Payload) });
        }));
        var path = await new UpdateDownloader(client, _directory).DownloadAsync(Release(), null, default);
        Assert.Equal(2, requests);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/fixture")]
    [InlineData("https://example.test/fixture")]
    public async Task RejectsRedirectBeforeRequestingAnUntrustedDestination(string location)
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request };
            response.Headers.Location = new(location);
            return Task.FromResult(response);
        }));
        await Assert.ThrowsAsync<GitHubUpdateException>(() => new UpdateDownloader(client, _directory).DownloadAsync(Release(), null, default));
        Assert.Equal(1, requests);
        AssertNoFiles();
    }

    [Fact]
    public async Task ReverificationDetectsChangesEvenWhenFileLengthIsUnchanged()
    {
        using var client = Client(Payload);
        var downloader = new UpdateDownloader(client, _directory);
        var path = await downloader.DownloadAsync(Release(), null, default);
        var changed = Payload.ToArray();
        changed[0] ^= 1;
        await File.WriteAllBytesAsync(path, changed);
        await Assert.ThrowsAsync<GitHubUpdateException>(() => downloader.VerifyAsync(path, Release(), default));
    }

    [Fact]
    public async Task ReverificationDetectsTruncation()
    {
        using var client = Client(Payload);
        var downloader = new UpdateDownloader(client, _directory);
        var path = await downloader.DownloadAsync(Release(), null, default);
        await File.WriteAllBytesAsync(path, Payload[..^1]);
        await Assert.ThrowsAsync<GitHubUpdateException>(() => downloader.VerifyAsync(path, Release(), default));
    }

    private static ReleaseInfo Release() => new("0.4.6", new("https://github.com/example/ai-sa/releases/tag/v0.4.6"), null,
        new("AISA-0.4.6-win-x64-setup.exe", new("https://github.com/example/ai-sa/releases/download/v0.4.6/AISA-0.4.6-win-x64-setup.exe"), Payload.Length, Convert.ToHexString(SHA256.HashData(Payload))));
    private static HttpClient Client(byte[] payload) => new(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        RequestMessage = request, Content = new ByteArrayContent(payload)
    })));
    private void AssertNoFiles() => Assert.True(!Directory.Exists(_directory) || Directory.GetFiles(_directory, "*", SearchOption.AllDirectories).Length == 0);
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }
    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
    private sealed class CaptureProgress(Action<UpdateDownloadProgress>? onReport = null) : IProgress<UpdateDownloadProgress>
    {
        public List<UpdateDownloadProgress> Values { get; } = [];
        public void Report(UpdateDownloadProgress value) { Values.Add(value); onReport?.Invoke(value); }
    }
}
