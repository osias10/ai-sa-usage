using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AIUsage.Core;

namespace AIUsage.Infrastructure;

/// <summary>Provisions untouched official binaries in this application's private directory.</summary>
public sealed class RuntimeManager : IRuntimeManager, IDisposable
{
    private const long MaximumDownloadBytes = 512L * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InstallLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly AppPaths _paths;
    private readonly HttpClient _http;

    public RuntimeManager(AppPaths paths)
    {
        _paths = paths;
        _http = new HttpClient(new HttpClientHandler
        {
            // Check every redirect before following it, including HTTPS and the target host.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AIUsageWidget/1.0");
    }

    public async Task<string> EnsureAsync(ProviderKind provider, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new ProviderException(UsageState.UpdateRequired, "Windows x64에서 지원하는 실행 파일입니다.");

        var release = RuntimeCatalog.Get(provider);
        var directory = Path.GetFullPath(Path.Combine(_paths.Runtimes, provider.ToString().ToLowerInvariant(), release.Version));
        var target = Path.Combine(directory, release.FileName);
        var installLock = InstallLocks.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        await installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            progress?.Report(new($"{provider} 실행 파일 확인 중"));
            if (await IsValidAsync(target, release, cancellationToken).ConfigureAwait(false)) return target;
            Directory.CreateDirectory(directory);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var temporaryPath = Path.Combine(directory, $".{release.FileName}.{Guid.NewGuid():N}.partial");
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromMinutes(15));
                    progress?.Report(new($"{provider} {release.Version} 다운로드 중", 0));
                    await DownloadAsync(release, temporaryPath, progress, timeout.Token).ConfigureAwait(false);
                    progress?.Report(new($"{provider} 다운로드 검증 중"));
                    if (!await IsValidAsync(temporaryPath, release, timeout.Token).ConfigureAwait(false))
                        throw new ProviderException(UsageState.UpdateRequired, "공식 체크섬과 일치하지 않아 다운로드한 실행 파일을 사용하지 않았습니다. 다시 시도해 주세요.");

                    // A .partial file is never returned. The rename happens only after a complete hash check.
                    File.Move(temporaryPath, target, overwrite: true);
                    progress?.Report(new($"{provider} 준비 완료", 100));
                    return target;
                }
                catch (RetryableDownloadException ex) when (attempt < 2)
                {
                    var delay = ex.RetryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                    // A long server delay is reported to the scheduler instead of keeping a login spinner alive.
                    if (delay > TimeSpan.FromSeconds(30))
                        throw new ProviderException(UsageState.RateLimited, "공식 다운로드 서버의 요청 제한입니다. 잠시 후 다시 시도해 주세요.", delay, ex);
                    progress?.Report(new($"다운로드 재시도 중 ({attempt + 2}/3)"));
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException) when (attempt < 2)
                {
                    progress?.Report(new($"연결 재시도 중 ({attempt + 2}/3)"));
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new ProviderException(UsageState.Offline, "실행 파일 다운로드 시간이 초과되었습니다. 연결을 확인하고 다시 시도해 주세요.", inner: ex);
                }
                finally
                {
                    // This is the exact temporary file created above, never a caller-supplied path.
                    TryDelete(temporaryPath);
                }
            }
            throw new InvalidOperationException("Unreachable download state.");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException(UsageState.Offline, "공식 다운로드 서버에 연결할 수 없습니다. 연결을 확인하고 다시 시도해 주세요.", inner: ex);
        }
        catch (RetryableDownloadException ex)
        {
            throw new ProviderException(ex.StatusCode == HttpStatusCode.TooManyRequests ? UsageState.RateLimited : UsageState.Offline,
                "공식 다운로드 서버가 일시적으로 응답하지 않습니다. 다시 시도해 주세요.", ex.RetryAfter, ex);
        }
        catch (IOException ex)
        {
            throw new ProviderException(UsageState.Unavailable, "실행 파일을 저장할 수 없습니다. 디스크 공간과 파일 접근 권한을 확인해 주세요.", inner: ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ProviderException(UsageState.Unavailable, "앱의 실행 파일 폴더에 접근할 수 없습니다.", inner: ex);
        }
        finally { installLock.Release(); }
    }

    private async Task DownloadAsync(RuntimeRelease release, string destination, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken)
    {
        using var response = await GetDownloadResponseAsync(release, cancellationToken).ConfigureAwait(false);
        var length = response.Content.Headers.ContentLength;
        if (length > MaximumDownloadBytes || (release.Size.HasValue && length.HasValue && release.Size != length))
            throw new ProviderException(UsageState.UpdateRequired, "다운로드 파일 크기가 호환성 목록과 다릅니다. 앱 업데이트를 확인해 주세요.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destinationStream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        long downloaded = 0;
        var lastReport = Environment.TickCount64;
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            downloaded += count;
            if (downloaded > MaximumDownloadBytes || (release.Size.HasValue && downloaded > release.Size))
                throw new ProviderException(UsageState.UpdateRequired, "다운로드 파일이 허용된 크기를 초과했습니다.");
            await destinationStream.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            if (Environment.TickCount64 - lastReport >= 200)
            {
                lastReport = Environment.TickCount64;
                progress?.Report(new($"{release.Provider} 다운로드 중 ({downloaded / 1048576d:F1} MB)",
                    length > 0 ? Math.Min(100, 100d * downloaded / length.Value) : null));
            }
        }
        await destinationStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (length.HasValue && downloaded != length.Value)
            throw new HttpRequestException("The official download ended before its advertised length.");
    }

    private async Task<HttpResponseMessage> GetDownloadResponseAsync(RuntimeRelease release, CancellationToken cancellationToken)
    {
        var uri = release.DownloadUri;
        for (var redirect = 0; redirect < 6; redirect++)
        {
            if (!IsAllowedUri(release.Provider, uri))
                throw new ProviderException(UsageState.UpdateRequired, "공식 다운로드 경로가 변경되어 파일을 받지 않았습니다. 앱 업데이트를 확인해 주세요.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new HttpRequestException("The official download redirect has no location.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500)
            {
                var delay = response.Headers.RetryAfter?.Delta;
                if (!delay.HasValue && response.Headers.RetryAfter?.Date is { } retryDate)
                    delay = retryDate - DateTimeOffset.UtcNow;
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                var status = response.StatusCode;
                response.Dispose();
                throw new RetryableDownloadException(status, delay);
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new ProviderException(UsageState.UpdateRequired, $"공식 실행 파일을 다운로드할 수 없습니다 (HTTP {(int)status}). 앱 업데이트를 확인해 주세요.");
            }
            return response;
        }
        throw new ProviderException(UsageState.UpdateRequired, "공식 다운로드 리디렉션이 너무 많습니다.");
    }

    internal static bool IsAllowedUri(ProviderKind provider, Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        return provider switch
        {
            ProviderKind.Claude => uri.Host.Equals("downloads.claude.ai", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.StartsWith("/claude-code-releases/", StringComparison.Ordinal),
            ProviderKind.Codex => (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                    && uri.AbsolutePath.StartsWith("/openai/codex/releases/download/", StringComparison.Ordinal))
                || (uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                    && uri.AbsolutePath.StartsWith("/github-production-release-asset/", StringComparison.Ordinal)),
            ProviderKind.Antigravity => uri.Host.Equals("storage.googleapis.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.StartsWith("/antigravity-public/antigravity-cli/", StringComparison.Ordinal),
            _ => false
        };
    }

    private static async Task<bool> IsValidAsync(string path, RuntimeRelease release, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        if (info.Length == 0 || info.Length > MaximumDownloadBytes || (release.Size.HasValue && info.Length != release.Size)) return false;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = release.HashAlgorithm switch
        {
            "SHA256" => await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false),
            "SHA512" => await SHA512.HashDataAsync(file, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("The runtime catalog has an unsupported hash algorithm.")
        };
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(release.Digest));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => _http.Dispose();

    private sealed class RetryableDownloadException(HttpStatusCode statusCode, TimeSpan? retryAfter) : Exception("Official download is temporarily unavailable.")
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }
}
