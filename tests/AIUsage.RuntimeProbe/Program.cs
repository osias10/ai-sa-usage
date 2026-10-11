using AIUsage.Core;
using AIUsage.Infrastructure;

// Explicit integration probe, never run by dotnet test. Runs only --version/--help:
// no login, token access, usage requests, model inference, or global PATH changes.
if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This runtime provisioning probe requires Windows x64.");
    return 1;
}

var repository = FindRepositoryRoot();
var root = Path.Combine(repository, ".artifacts", $"runtime-probe-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
Console.WriteLine($"Fresh provisioning directory: {root}");
var paths = new AppPaths(root);
Directory.CreateDirectory(paths.WorkDirectory);
using var manager = new RuntimeManager(paths);
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    await Task.WhenAll(Enum.GetValues<ProviderKind>().Select(async provider =>
    {
        var progress = new ProbeProgress(provider);
        var executable = await manager.EnsureAsync(provider, progress, cancellation.Token);
        if (progress.DownloadStarts == 0)
            throw new InvalidOperationException($"{provider}: expected a fresh official download.");

        var cacheProgress = new ProbeProgress(provider);
        var cached = await manager.EnsureAsync(provider, cacheProgress, cancellation.Token);
        if (cached != executable || cacheProgress.DownloadStarts != 0)
            throw new InvalidOperationException($"{provider}: cached verification unexpectedly downloaded a replacement.");

        // Empty child PATH proves that the provider executable is addressed directly and does
        // not depend on a globally installed CLI for this safe help/version startup check.
        var result = await new ProcessRunner().RunAsync(new()
        {
            FileName = executable,
            WorkingDirectory = paths.WorkDirectory,
            Arguments = provider == ProviderKind.Antigravity ? ["--help"] : ["--version"],
            Environment = new Dictionary<string, string?>
            {
                ["PATH"] = "",
                ["DISABLE_AUTOUPDATER"] = "1",
                ["AGY_CLI_DISABLE_AUTO_UPDATE"] = "1"
            },
            Timeout = TimeSpan.FromSeconds(30)
        }, cancellation.Token);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{provider}: help/version startup failed with exit code {result.ExitCode}.");

        Console.WriteLine($"PASS {provider} {RuntimeCatalog.Get(provider).Version}: official download, pinned checksum, cached validation, empty-PATH startup.");
        Console.WriteLine($"  {executable}");
    }));
    Console.WriteLine("All runtime provisioning checks passed. Authenticated usage and browser login were not exercised.");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Probe cancelled or exceeded its 15-minute deadline. Partial downloads were not executed.");
    return 2;
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAIL: {error.GetType().Name}: {error.Message}");
    return 1;
}
finally { Console.CancelKeyPress -= cancel; }

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "AIUsageWidget.slnx"))) return directory.FullName;
    throw new DirectoryNotFoundException("Run this probe from the AIUsageWidget repository or one of its subdirectories.");
}

sealed class ProbeProgress(ProviderKind provider) : IProgress<ProviderProgress>
{
    public int DownloadStarts { get; private set; }

    public void Report(ProviderProgress progress)
    {
        if (progress.Percent != 0) return;
        DownloadStarts++;
        Console.WriteLine($"{provider}: downloading the pinned original official executable (attempt {DownloadStarts}).");
    }
}
