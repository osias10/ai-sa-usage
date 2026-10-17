using System.Text.Json;

namespace AIUsage.Core;

/// <summary>Short-lived, app-owned state copies. Provider credentials are never copied or restored.</summary>
public sealed class UpdateDataBackup(AppPaths paths, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private static readonly string[] StateFiles = ["settings.json", "usage-cache.json", "usage-alerts.json",
        "usage-statistics.json", "usage-forecast.json"];
    private static readonly string[] BackupFiles = [.. StateFiles, "manifest.json"];

    public async Task<string> CreateAsync(string fromVersion, string toVersion, CancellationToken cancellationToken = default)
    {
        ReleaseVersion.Parse(fromVersion);
        ReleaseVersion.Parse(toVersion);
        CleanupExpired();
        var root = Path.GetFullPath(paths.UpdateBackupsDirectory);
        Directory.CreateDirectory(root);
        var pending = Path.Combine(root, ".pending-" + Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(root, "backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pending);
        try
        {
            var copied = new List<string>();
            foreach (var name in StateFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.Combine(paths.Root, name);
                if (!File.Exists(source)) continue;
                var bytes = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
                using (var document = JsonDocument.Parse(bytes))
                {
                    if (document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.TryGetProperty("SchemaVersion", out var schema) &&
                        (schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version > 1))
                        throw new InvalidDataException("정식 버전과 호환되지 않는 저장 형식입니다. 설치를 중단했습니다.");
                }
                await WriteDurablyAsync(Path.Combine(pending, name), bytes, cancellationToken).ConfigureAwait(false);
                copied.Add(name);
            }
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new BackupManifest(1, _time.GetUtcNow(), fromVersion, toVersion, copied));
            await WriteDurablyAsync(Path.Combine(pending, "manifest.json"), manifest, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(pending, destination);
            return destination;
        }
        finally
        {
            // An incomplete backup never becomes a restore candidate.
            if (Directory.Exists(pending)) DeleteOwnedDirectory(pending);
        }
    }

    public void CleanupExpired()
    {
        foreach (var directory in OwnedDirectories())
        {
            var expired = Path.GetFileName(directory).StartsWith(".pending-", StringComparison.Ordinal);
            if (!expired)
            {
                try
                {
                    var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(directory, "manifest.json")));
                    expired = manifest is null || manifest.CreatedAt > _time.GetUtcNow() ||
                        manifest.CreatedAt <= _time.GetUtcNow().AddHours(-24);
                }
                catch (JsonException) { expired = true; }
                catch (FileNotFoundException) { expired = true; }
            }
            if (expired) DeleteOwnedDirectory(directory);
        }
    }

    public void ClearStatistics()
    {
        foreach (var directory in OwnedDirectories())
        {
            File.Delete(Path.Combine(directory, "usage-statistics.json"));
            File.Delete(Path.Combine(directory, "usage-forecast.json"));
        }
    }

    private IEnumerable<string> OwnedDirectories()
    {
        var root = Path.GetFullPath(paths.UpdateBackupsDirectory);
        if (!Directory.Exists(root)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            var id = name.StartsWith("backup-", StringComparison.Ordinal) ? name[7..]
                : name.StartsWith(".pending-", StringComparison.Ordinal) ? name[9..] : "";
            if (Guid.TryParseExact(id, "N", out _) &&
                !File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) yield return directory;
        }
    }

    private void DeleteOwnedDirectory(string directory)
    {
        var root = Path.GetFullPath(paths.UpdateBackupsDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), root, StringComparison.OrdinalIgnoreCase) ||
            File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("백업 경로를 확인할 수 없습니다.");
        foreach (var name in BackupFiles) File.Delete(Path.Combine(directory, name));
        // Do not recursively delete unexpected files, subdirectories or user-created backups.
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    private static async Task WriteDurablyAsync(string file, byte[] bytes, CancellationToken token)
    {
        await using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private sealed record BackupManifest(int SchemaVersion, DateTimeOffset CreatedAt, string FromVersion,
        string ToVersion, IReadOnlyList<string> Files);
}
