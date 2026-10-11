using System.Net;
using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class UpdateCheckerTests
{
    [Fact]
    public async Task ComparesVersionsAndReturnsDownloadPage()
    {
        using var client = new HttpClient(new ResponseHandler("""{"version":"0.2.0","downloadUrl":"https://example.test/releases/0.2.0"}"""));
        var result = await new UpdateChecker(client).CheckAsync(new("https://example.test/releases.json"), new Version(0, 1, 0), default);
        Assert.True(result.IsNewer);
        Assert.Equal("/releases/0.2.0", result.Release.DownloadUrl.AbsolutePath);
    }
    [Fact]
    public async Task RejectsExecutableSchemesFromReleaseFeed()
    {
        using var client = new HttpClient(new ResponseHandler("""{"version":"0.2.0","downloadUrl":"file:///C:/malicious.exe"}"""));
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateChecker(client).CheckAsync(new("https://example.test/releases.json"), new Version(0, 1), default));
    }
    private sealed class ResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(json) });
    }
}
