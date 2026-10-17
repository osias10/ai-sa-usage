using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIUsage.Core;

public sealed record GitHubReleaseSource(string Owner, string Repository);
public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size, string? Sha256);
public sealed record ReleaseInfo(string Version, Uri PageUrl, string? Notes, ReleaseAsset? Asset, bool IsPrerelease = false)
{
    public bool CanDownload => Asset is { Size: > 0 } asset && ReleaseValidation.IsSha256(asset.Sha256);
}
public sealed record UpdateCheckResult(bool IsNewer, ReleaseInfo? Release, UpdateAction Action = UpdateAction.None);

public sealed class GitHubUpdateException(string message, DateTimeOffset? retryAfterUtc = null, Exception? inner = null)
    : Exception(message, inner)
{
    public DateTimeOffset? RetryAfterUtc { get; } = retryAfterUtc;
}

/// <summary>Reads public releases without account data. The supplied client must disable automatic redirects.</summary>
public sealed class UpdateChecker(HttpClient http)
{
    private const int MaximumMetadataBytes = 1024 * 1024;
    private const int ReleasesPerPage = 100;
    private const int MaximumReleasePages = 20;

    public Task<UpdateCheckResult> CheckAsync(GitHubReleaseSource source, Version currentVersion, CancellationToken cancellationToken)
        => CheckAsync(source, ReleaseVersion.FromVersion(currentVersion), UpdateChannel.Stable, cancellationToken);

    public async Task<UpdateCheckResult> CheckAsync(GitHubReleaseSource source, ReleaseVersion currentVersion,
        UpdateChannel channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(currentVersion);
        ReleaseValidation.ValidateSource(source);
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            ReleaseInfo? release = null;
            if (channel == UpdateChannel.Stable)
            {
                var uri = new Uri($"https://api.github.com/repos/{source.Owner}/{source.Repository}/releases/latest");
                var metadata = await ReadMetadataAsync(uri, timeout.Token).ConfigureAwait(false);
                using var document = metadata.Document;
                if (document is null) return new(false, null);
                release = ParseRelease(document.RootElement, source, channel);
            }
            else
            {
                var seenVersions = new Dictionary<string, ReleaseInfo>(StringComparer.Ordinal);
                for (var pageNumber = 1; pageNumber <= MaximumReleasePages; pageNumber++)
                {
                    // Construct every page URL ourselves; never follow an untrusted Link header URL.
                    var uri = new Uri($"https://api.github.com/repos/{source.Owner}/{source.Repository}/releases?per_page={ReleasesPerPage}&page={pageNumber}");
                    var metadata = await ReadMetadataAsync(uri, timeout.Token).ConfigureAwait(false);
                    using var document = metadata.Document;
                    if (document is null)
                    {
                        if (pageNumber == 1) return new(false, null);
                        throw new GitHubUpdateException("릴리스 목록을 끝까지 확인하지 못했습니다. 다시 시도해 주세요.");
                    }
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > ReleasesPerPage) throw InvalidMetadata();
                    foreach (var item in root.EnumerateArray())
                    {
                        var candidate = ParseRelease(item, source, channel);
                        if (candidate is null) continue;
                        if (seenVersions.TryGetValue(candidate.Version, out var previous) && previous != candidate)
                            throw new GitHubUpdateException("같은 버전에 서로 다른 릴리스 정보가 있습니다. 배포 정보를 확인해 주세요.");
                        seenVersions[candidate.Version] = candidate;
                        if (release is null || ReleaseVersion.Parse(candidate.Version).CompareTo(ReleaseVersion.Parse(release.Version)) > 0)
                            release = candidate;
                    }
                    if (root.GetArrayLength() < ReleasesPerPage && !metadata.HasNextPage) break;
                    if (pageNumber == MaximumReleasePages)
                        throw new GitHubUpdateException("릴리스 목록이 조회 범위를 초과했습니다. 최신 버전을 확정할 수 없습니다.");
                }
            }
            if (release is null) return new(false, null);
            var action = UpdateDecision.GetAction(currentVersion, channel, release);
            return new(action == UpdateAction.Upgrade, release, action);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubUpdateException("업데이트 확인 시간이 초과되었습니다. 잠시 후 다시 시도해 주세요.");
        }
        catch (JsonException error) { throw InvalidMetadata(error); }
        catch (HttpRequestException error) { throw new GitHubUpdateException("업데이트 서버에 연결하지 못했습니다. 연결 상태를 확인해 주세요.", inner: error); }
        catch (IOException error) { throw new GitHubUpdateException("업데이트 정보를 읽지 못했습니다. 다시 시도해 주세요.", inner: error); }
        catch (InvalidDataException error) { throw InvalidMetadata(error); }
    }

    private async Task<(JsonDocument? Document, bool HasNextPage)> ReadMetadataAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(http, uri, download: false, cancellationToken).ConfigureAwait(false);
        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        if (!string.Equals(finalUri.AbsoluteUri, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            throw new GitHubUpdateException("업데이트 저장소 주소가 변경되었습니다. 배포 주소를 확인해 주세요.");
        if (response.StatusCode == HttpStatusCode.NotFound) return (null, false);
        ThrowForFailure(response);
        if (response.Content.Headers.ContentLength > MaximumMetadataBytes)
            throw new GitHubUpdateException("업데이트 정보가 너무 큽니다.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (data.Length + count > MaximumMetadataBytes) throw new GitHubUpdateException("업데이트 정보가 너무 큽니다.");
            data.Write(buffer, 0, count);
        }
        var hasNextPage = response.Headers.TryGetValues("Link", out var links) && links.Any(link =>
            Regex.IsMatch(link, "(?:^|;)\\s*rel\\s*=\\s*(?:\"[^\"]*\\bnext\\b[^\"]*\"|next)(?:\\s*[,;]|\\s*$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        return (JsonDocument.Parse(data.ToArray(), new JsonDocumentOptions { MaxDepth = 32 }), hasNextPage);
    }

    private static ReleaseInfo? ParseRelease(JsonElement root, GitHubReleaseSource source, UpdateChannel channel)
    {
        if (root.ValueKind != JsonValueKind.Object) throw InvalidMetadata();
        if (RequiredBoolean(root, "draft")) return null;
        var prerelease = RequiredBoolean(root, "prerelease");
        if (channel == UpdateChannel.Stable && prerelease) return null;
        var tag = RequiredString(root, "tag_name");
        if (!ReleaseValidation.TryVersion(tag, out var version, out var normalized))
        {
            if (channel == UpdateChannel.Preview) return null;
            throw new GitHubUpdateException("릴리스 버전을 확인할 수 없습니다.");
        }
        if (version.IsPrerelease != prerelease)
            throw new GitHubUpdateException("릴리스 태그와 프리릴리스 표시가 일치하지 않습니다.");
        if (!Uri.TryCreate(RequiredString(root, "html_url"), UriKind.Absolute, out var page)) throw InvalidMetadata();
        ReleaseValidation.RequireGitHubPath(page, $"/{source.Owner}/{source.Repository}/releases/tag/{tag}");
        string? notes = null;
        if (root.TryGetProperty("body", out var body) && body.ValueKind != JsonValueKind.Null)
            notes = body.ValueKind == JsonValueKind.String ? body.GetString() : throw InvalidMetadata();
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) throw InvalidMetadata();
        var expectedName = $"AISA-{normalized}-win-x64-setup.exe";
        ReleaseAsset? asset = null;
        var found = false;
        foreach (var item in assets.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw InvalidMetadata();
            if (RequiredString(item, "name") != expectedName) continue;
            if (found) throw new GitHubUpdateException("설치 파일 정보가 중복되어 업데이트할 수 없습니다.");
            found = true;
            if (!Uri.TryCreate(RequiredString(item, "browser_download_url"), UriKind.Absolute, out var download)) throw InvalidMetadata();
            ReleaseValidation.RequireGitHubPath(download, $"/{source.Owner}/{source.Repository}/releases/download/{tag}/{expectedName}");
            if (!item.TryGetProperty("size", out var sizeNode) || sizeNode.ValueKind != JsonValueKind.Number || !sizeNode.TryGetInt64(out var size) || size < 0)
                throw InvalidMetadata();
            string? sha256 = null;
            if (item.TryGetProperty("digest", out var digestNode) && digestNode.ValueKind != JsonValueKind.Null)
            {
                if (digestNode.ValueKind != JsonValueKind.String) throw InvalidMetadata();
                var digest = digestNode.GetString();
                if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && ReleaseValidation.IsSha256(digest[7..]))
                    sha256 = digest[7..].ToLowerInvariant();
            }
            asset = new(expectedName, download, size, sha256);
        }
        return new(normalized, page, notes, asset, prerelease);
    }

    // A client with AllowAutoRedirect=false is required so no unvalidated redirect is followed.
    internal static async Task<HttpResponseMessage> SendAsync(HttpClient http, Uri uri, bool download, CancellationToken cancellationToken)
    {
        var originalUri = uri;
        for (var redirect = 0; ; redirect++)
        {
            RequireAllowedTransport(uri, download);
            if (!download && !string.Equals(uri.AbsoluteUri, originalUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                throw new GitHubUpdateException("업데이트 저장소 주소가 변경되었습니다. 배포 주소를 확인해 주세요.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("AI-SA-Updater/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(download ? "application/octet-stream" : "application/vnd.github+json"));
            if (!download) request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.RequestMessage ??= request;
            try
            {
                RequireAllowedTransport(response.RequestMessage?.RequestUri ?? uri, download);
                if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                    return response;
                if (redirect >= 5 || response.Headers.Location is not { } location)
                    throw new GitHubUpdateException("업데이트 서버의 이동 주소를 확인할 수 없습니다.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            }
            catch { response.Dispose(); throw; }
            response.Dispose();
        }
    }

    private static void RequireAllowedTransport(Uri uri, bool download)
    {
        RequireHttps(uri);
        var allowed = download
            ? uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"
            : uri.Host == "api.github.com";
        if (!allowed || !uri.IsDefaultPort)
            throw new GitHubUpdateException("GitHub 배포 서버가 아닌 주소에서 업데이트를 받을 수 없습니다.");
    }

    internal static void ThrowForFailure(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            var now = DateTimeOffset.UtcNow;
            DateTimeOffset? retry = response.Headers.RetryAfter?.Date;
            if (response.Headers.RetryAfter?.Delta is { } delta) retry = now.Add(delta);
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values))
            {
                foreach (var value in values)
                    if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 0 and <= 253402300799)
                    {
                        var reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
                        if (retry is null || reset > retry) retry = reset;
                    }
            }
            if (retry is null || retry <= now) retry = now.AddMinutes(5);
            throw new GitHubUpdateException("GitHub 요청 한도에 도달했습니다. 잠시 후 다시 확인해 주세요.", retry);
        }
        throw new GitHubUpdateException("업데이트 서버에서 요청을 처리하지 못했습니다. 잠시 후 다시 시도해 주세요.");
    }

    public static void RequireHttps(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("HTTPS 업데이트 주소가 필요합니다.");
    }

    private static string RequiredString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw InvalidMetadata();
    private static bool RequiredBoolean(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : throw InvalidMetadata();
    private static GitHubUpdateException InvalidMetadata(Exception? inner = null)
        => new("업데이트 정보 형식이 올바르지 않습니다.", inner: inner);
}

internal static class ReleaseValidation
{
    internal static void ValidateSource(GitHubReleaseSource source)
    {
        if (string.IsNullOrWhiteSpace(source.Owner) || string.IsNullOrWhiteSpace(source.Repository) ||
            !Regex.IsMatch(source.Owner, "^[A-Za-z0-9][A-Za-z0-9-]{0,38}\\z", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(source.Repository, "^[A-Za-z0-9_.-]{1,100}\\z", RegexOptions.CultureInvariant) || source.Repository is "." or "..")
            throw new GitHubUpdateException("GitHub 업데이트 저장소가 올바르지 않습니다.");
    }

    internal static bool TryVersion(string text, out ReleaseVersion version, out string normalized)
    {
        version = null!;
        normalized = "";
        if (string.IsNullOrEmpty(text) || text.Contains('+') || !ReleaseVersion.TryParse(text, out version) ||
            version.IsPrerelease && !Regex.IsMatch(version.Prerelease!, "^(?:beta|rc)\\.(?:0|[1-9][0-9]*)\\z", RegexOptions.CultureInvariant)) return false;
        normalized = version.ToString();
        return true;
    }

    internal static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static void RequireGitHubPath(Uri uri, string expectedPath)
    {
        RequireHttpsAddress(uri);
        if (!uri.IsDefaultPort || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.AbsolutePath, expectedPath, StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new GitHubUpdateException("릴리스 주소가 지정된 GitHub 저장소와 일치하지 않습니다.");
    }

    internal static ReleaseAsset RequireDownload(ReleaseInfo release)
    {
        if (!release.CanDownload || release.Asset is not { } asset)
            throw new GitHubUpdateException("설치 파일의 크기와 SHA-256 검증 정보가 준비되지 않았습니다.");
        if (!TryVersion(release.Version, out var releaseVersion, out var version) || releaseVersion.IsPrerelease != release.IsPrerelease || asset.Name != $"AISA-{version}-win-x64-setup.exe")
            throw new GitHubUpdateException("현재 Windows 환경에 맞는 설치 파일이 아닙니다.");
        RequireHttpsAddress(release.PageUrl);
        var parts = release.PageUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || parts[2] != "releases" || parts[3] != "tag" ||
            !TryVersion(parts[4], out _, out var tagVersion) || tagVersion != version)
            throw new GitHubUpdateException("릴리스 버전과 다운로드 정보가 일치하지 않습니다.");
        ValidateSource(new(parts[0], parts[1]));
        RequireGitHubPath(release.PageUrl, $"/{parts[0]}/{parts[1]}/releases/tag/{parts[4]}");
        RequireGitHubPath(asset.DownloadUrl, $"/{parts[0]}/{parts[1]}/releases/download/{parts[4]}/{asset.Name}");
        return asset;
    }

    private static void RequireHttpsAddress(Uri uri)
    {
        try { UpdateChecker.RequireHttps(uri); }
        catch (InvalidDataException error) { throw new GitHubUpdateException("HTTPS 업데이트 주소가 필요합니다.", inner: error); }
    }
}
