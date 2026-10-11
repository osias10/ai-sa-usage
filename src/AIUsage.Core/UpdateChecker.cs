using System.Net.Http.Json;
using System.Text.Json;

namespace AIUsage.Core;

public sealed record ReleaseInfo(string Version, Uri DownloadUrl, string? Notes = null);
public sealed record UpdateCheckResult(bool IsNewer, ReleaseInfo Release);

/// <summary>Optional public release metadata; no account or usage data is sent.</summary>
public sealed class UpdateChecker(HttpClient http)
{
    public async Task<UpdateCheckResult> CheckAsync(Uri feed, Version currentVersion, CancellationToken cancellationToken)
    {
        RequireHttps(feed);
        using var response = await http.GetAsync(feed, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } finalUri) RequireHttps(finalUri);
        if (response.Content.Headers.ContentLength > 65536) throw new InvalidDataException("Release metadata is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var data = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (data.Length + count > 65536) throw new InvalidDataException("Release metadata is too large.");
            data.Write(buffer, 0, count);
        }
        var release = JsonSerializer.Deserialize<ReleaseInfo>(data.ToArray(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Missing release metadata.");
        if (!Version.TryParse(release.Version, out var version)) throw new InvalidDataException("Invalid release version.");
        RequireHttps(release.DownloadUrl);
        return new(version > currentVersion, release);
    }
    public static void RequireHttps(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("An HTTPS release URL is required.");
    }
}
