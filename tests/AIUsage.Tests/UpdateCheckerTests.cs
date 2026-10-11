using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class UpdateCheckerTests
{
    private static readonly GitHubReleaseSource Source = new("example", "ai-sa");

    [Theory]
    [InlineData("v0.4.6", "0.4.5.0", true, "0.4.6")]
    [InlineData("0.4.5", "0.4.5.0", false, "0.4.5")]
    [InlineData("v0.4.5.0", "0.4.5", false, "0.4.5")]
    [InlineData("0.4.5.1", "0.4.5.0", true, "0.4.5.1")]
    [InlineData("v0.4.4", "0.4.5", false, "0.4.4")]
    public async Task NormalizesVersionsAndSelectsTheMatchingInstaller(string tag, string current, bool newer, string normalized)
    {
        using var client = Client(ReleaseJson(tag, normalized));
        var result = await new UpdateChecker(client).CheckAsync(Source, Version.Parse(current), default);
        Assert.Equal(newer, result.IsNewer);
        Assert.Equal(normalized, result.Release!.Version);
        Assert.True(result.Release.CanDownload);
        Assert.Equal($"AISA-{normalized}-win-x64-setup.exe", result.Release.Asset!.Name);
        Assert.Equal(new string('a', 64), result.Release.Asset.Sha256);
    }

    [Fact]
    public async Task SendsOnlyPublicGitHubRequestWithRequiredHeaders()
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("https://api.github.com/repos/example/ai-sa/releases/latest", request.RequestUri!.AbsoluteUri);
            Assert.NotEmpty(request.Headers.UserAgent);
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/vnd.github+json");
            Assert.Equal("2026-03-10", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(Response(ReleaseJson(), request));
        }));
        Assert.True((await new UpdateChecker(client).CheckAsync(Source, new(0, 4, 5), default)).IsNewer);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task IgnoresUnpublishedAndPreviewReleases(bool draft, bool prerelease)
    {
        var json = JsonNode.Parse(ReleaseJson())!;
        json["draft"] = draft;
        json["prerelease"] = prerelease;
        using var client = Client(json.ToJsonString());
        var result = await new UpdateChecker(client).CheckAsync(Source, new(0, 4, 5), default);
        Assert.False(result.IsNewer);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task MissingPublicReleaseIsNotANetworkError()
    {
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request })));
        var result = await new UpdateChecker(client).CheckAsync(Source, new(0, 4, 5), default);
        Assert.False(result.IsNewer);
        Assert.Null(result.Release);
    }

    [Theory]
    [InlineData("AISA-0.4.6-win-arm64-setup.exe")]
    [InlineData("AISA-0.4.5-win-x64-setup.exe")]
    [InlineData("source.zip")]
    public async Task DoesNotSubstituteAnotherAsset(string name)
    {
        var json = JsonNode.Parse(ReleaseJson())!;
        json["assets"]![0]!["name"] = name;
        using var client = Client(json.ToJsonString());
        var result = await new UpdateChecker(client).CheckAsync(Source, new(0, 4, 5), default);
        Assert.True(result.IsNewer);
        Assert.False(result.Release!.CanDownload);
        Assert.Null(result.Release.Asset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sha256:broken")]
    [InlineData("md5:0123456789abcdef")]
    public async Task MissingOrInvalidDigestKeepsReleaseVisibleButNotInstallable(string? digest)
    {
        var json = JsonNode.Parse(ReleaseJson())!;
        json["assets"]![0]!["digest"] = digest;
        using var client = Client(json.ToJsonString());
        var result = await new UpdateChecker(client).CheckAsync(Source, new(0, 4, 5), default);
        Assert.True(result.IsNewer);
        Assert.NotNull(result.Release!.Asset);
        Assert.False(result.Release.CanDownload);
    }

    [Theory]
    [InlineData("v0.4")]
    [InlineData("0.4.6-preview")]
    [InlineData("v0.4.6+build")]
    [InlineData("0.4.6.0.1")]
    [InlineData("2147483648.0.0")]
    [InlineData("0.4.6\n")]
    public async Task RejectsVersionsOutsideTheNumericReleaseContract(string tag)
    {
        using var client = Client(ReleaseJson(tag));
        await Assert.ThrowsAsync<GitHubUpdateException>(() => new UpdateChecker(client).CheckAsync(Source, new(0, 4, 5), default));
    }

    [Theory]
    [InlineData("html_url", "https://github.com/other/repo/releases/tag/v0.4.6")]
    [InlineData("html_url", "http://github.com/example/ai-sa/releases/tag/v0.4.6")]
    [InlineData("browser_download_url", "https://example.test/setup.exe")]
    [InlineData("browser_download_url", "https://github.com/other/repo/releases/download/v0.4.6/AISA-0.4.6-win-x64-setup.exe")]
    [InlineData("browser_download_url", "file:///C:/setup.exe")]
    public async Task RejectsUnexpectedReleaseAndAssetOrigins(string property, string address)
    {
        var json = JsonNode.Parse(ReleaseJson())!;
        if (property == "html_url") json[property] = address;
        else json["assets"]![0]![property] = address;
        using var client = Client(json.ToJsonString());
        await Assert.ThrowsAsync<GitHubUpdateException>(() => new UpdateChecker(client).CheckAsync(Source, new(0, 4, 5), default));
    }

    [Theory]
    [InlineData("size", "\"3\"")]
    [InlineData("size", "-1")]
    [InlineData("size", "3.5")]
    [InlineData("digest", "[]")]
    public async Task RejectsWrongAssetMetadataTypes(string property, string value)
    {
        var json = JsonNode.Parse(ReleaseJson())!;
        json["assets"]![0]![property] = JsonNode.Parse(value);
        using var client = Client(json.ToJsonString());
        await Assert.ThrowsAsync<GitHubUpdateException>(() => new UpdateChecker(client).CheckAsync(Source, new(0, 4, 5), default));
    }

    [Fact]
    public async Task EmptyAssetListKeepsReleasePageAvailable()
    {
        var json = JsonNode.Parse(ReleaseJson())!;
        json["assets"] = new JsonArray();
        using var client = Client(json.ToJsonString());
        var result = await new UpdateChecker(client).CheckAsync(Source, new(0, 4, 5), default);
        Assert.True(result.IsNewer);
        Assert.False(result.Release!.CanDownload);
        Assert.Equal("github.com", result.Release.PageUrl.Host);
    }

    [Fact]
    public async Task ZeroSizeCannotBeInstalled()
    {
        var json = JsonNode.Parse(ReleaseJson())!;
        json["assets"]![0]!["size"] = 0;
        using var client = Client(json.ToJsonString());
        Assert.False((await new UpdateChecker(client).CheckAsync(Source, new(0, 4, 5), default)).Release!.CanDownload);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task PreservesTheLaterRateLimitDeadline(HttpStatusCode status)
    {
        var reset = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds());
        using var client = new HttpClient(new Handler((request, _) =>
        {
            var response = new HttpResponseMessage(status) { RequestMessage = request };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
            response.Headers.Add("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult(response);
        }));
        var error = await Assert.ThrowsAsync<GitHubUpdateException>(() => new UpdateChecker(client).CheckAsync(Source, new(0, 4, 5), default));
        Assert.Equal(reset, error.RetryAfterUtc);
    }

    [Fact]
    public async Task HonorsRetryAfterDate()
    {
        var retry = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(3).ToUnixTimeSeconds());
        using var client = new HttpClient(new Handler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(retry);
            return Task.FromResult(response);
        }));
        var error = await Assert.ThrowsAsync<GitHubUpdateException>(() => new UpdateChecker(client).CheckAsync(Source, new(0, 4, 5), default));
        Assert.Equal(retry, error.RetryAfterUtc);
    }

    [Theory]
    [InlineData("http://api.github.com/repos/example/ai-sa/releases/latest")]
    [InlineData("https://example.test/releases/latest")]
    [InlineData("https://api.github.com/repos/other/repo/releases/latest")]
    public async Task DoesNotRequestUntrustedRedirects(string location)
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request };
            response.Headers.Location = new(location);
            return Task.FromResult(response);
        }));
        await Assert.ThrowsAsync<GitHubUpdateException>(() => new UpdateChecker(client).CheckAsync(Source, new(0, 4, 5), default));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"draft\":\"false\",\"prerelease\":false}")]
    public async Task MalformedMetadataFailsClosed(string json)
    {
        using var client = Client(json);
        await Assert.ThrowsAsync<GitHubUpdateException>(() => new UpdateChecker(client).CheckAsync(Source, new(0, 4, 5), default));
    }

    [Fact]
    public async Task RejectsOversizedMetadataWithoutContentLength()
    {
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StreamContent(new NonSeekableStream(new byte[1024 * 1024 + 1]))
        })));
        await Assert.ThrowsAsync<GitHubUpdateException>(() => new UpdateChecker(client).CheckAsync(Source, new(0, 4, 5), default));
    }

    [Fact]
    public async Task UserCancellationIsPreserved()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UpdateChecker(client).CheckAsync(Source, new(0, 4, 5), cancellation.Token));
    }

    private static HttpClient Client(string json) => new(new Handler((request, _) => Task.FromResult(Response(json, request))));
    private static HttpResponseMessage Response(string json, HttpRequestMessage request) => new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(json) };
    private static string ReleaseJson(string tag = "v0.4.6", string version = "0.4.6") => JsonSerializer.Serialize(new
    {
        tag_name = tag, html_url = $"https://github.com/example/ai-sa/releases/tag/{tag}", body = "Release notes", draft = false, prerelease = false,
        assets = new[] { new { name = $"AISA-{version}-win-x64-setup.exe", browser_download_url = $"https://github.com/example/ai-sa/releases/download/{tag}/AISA-{version}-win-x64-setup.exe", size = 3, digest = "sha256:" + new string('a', 64) } }
    });
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }
    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
