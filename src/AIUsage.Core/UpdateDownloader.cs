using System.Security.Cryptography;

namespace AIUsage.Core;

public sealed record UpdateDownloadProgress(long ReceivedBytes, long TotalBytes)
{
    public double Percent => TotalBytes <= 0 ? 0 : Math.Clamp((double)ReceivedBytes / TotalBytes * 100, 0, 100);
}

/// <summary>Stages a verified installer without execution. The supplied client must disable automatic redirects.</summary>
public sealed class UpdateDownloader(HttpClient http, string updateDirectory)
{
    public async Task<string> DownloadAsync(ReleaseInfo release, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var asset = ReleaseValidation.RequireDownload(release);
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(Path.GetFullPath(updateDirectory), Guid.NewGuid().ToString("N"));
        var finalPath = Path.Combine(directory, asset.Name);
        var partialPath = finalPath + ".part";
        var complete = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            Directory.CreateDirectory(directory);
            using var response = await UpdateChecker.SendAsync(http, asset.DownloadUrl, download: true, timeout.Token).ConfigureAwait(false);
            UpdateChecker.ThrowForFailure(response);
            if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
                throw new GitHubUpdateException("설치 파일 크기가 릴리스 정보와 다릅니다. 다시 다운로드해 주세요.");
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                long received = 0;
                var buffer = new byte[81920];
                progress?.Report(new(0, asset.Size));
                while (true)
                {
                    var count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (count > asset.Size - received)
                        throw new GitHubUpdateException("설치 파일 크기가 릴리스 정보와 다릅니다. 다시 다운로드해 주세요.");
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, count);
                    received += count;
                    progress?.Report(new(received, asset.Size));
                }
                if (received != asset.Size || !MatchesHash(hash.GetHashAndReset(), asset.Sha256!))
                    throw new GitHubUpdateException("설치 파일 검증에 실패했습니다. 다시 다운로드해 주세요.");
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            timeout.Token.ThrowIfCancellationRequested();
            File.Move(partialPath, finalPath);
            complete = true;
            return finalPath;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubUpdateException("설치 파일 다운로드 시간이 초과되었습니다. 다시 시도해 주세요.");
        }
        catch (HttpRequestException error) { throw new GitHubUpdateException("설치 파일을 다운로드하지 못했습니다. 연결 상태를 확인해 주세요.", inner: error); }
        catch (IOException error) { throw new GitHubUpdateException("설치 파일을 저장하지 못했습니다. 저장 공간과 연결 상태를 확인해 주세요.", inner: error); }
        catch (UnauthorizedAccessException error) { throw new GitHubUpdateException("업데이트 폴더에 설치 파일을 저장할 수 없습니다.", inner: error); }
        catch (InvalidDataException error) { throw new GitHubUpdateException("설치 파일 주소가 올바르지 않습니다.", inner: error); }
        finally
        {
            if (!complete)
            {
                // Only the uniquely created attempt directory is eligible for cleanup.
                TryDelete(partialPath);
                TryDelete(finalPath);
                try { if (Directory.Exists(directory)) Directory.Delete(directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    public async Task VerifyAsync(string path, ReleaseInfo release, CancellationToken cancellationToken)
    {
        var asset = ReleaseValidation.RequireDownload(release);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != asset.Size) throw new GitHubUpdateException("설치 파일이 변경되었습니다. 다시 다운로드해 주세요.");
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!MatchesHash(hash, asset.Sha256!)) throw new GitHubUpdateException("설치 파일이 변경되었습니다. 다시 다운로드해 주세요.");
        }
        catch (IOException error) { throw new GitHubUpdateException("설치 파일을 확인하지 못했습니다. 다시 다운로드해 주세요.", inner: error); }
        catch (UnauthorizedAccessException error) { throw new GitHubUpdateException("설치 파일을 읽을 수 없습니다. 다시 다운로드해 주세요.", inner: error); }
    }

    private static bool MatchesHash(byte[] hash, string expected) => CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(expected));
    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
