using AIUsage.Core;
using AIUsage.Infrastructure;
using Xunit;

namespace AIUsage.Tests;

public sealed class ProcessRunnerTests
{
    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    private static ProcessRequest Request(string script, TimeSpan? timeout = null, Action<string>? output = null)
        => new() { FileName = PowerShell, WorkingDirectory = Path.GetTempPath(),
            Arguments = ["-NoProfile", "-NonInteractive", "-Command", script],
            Timeout = timeout ?? TimeSpan.FromSeconds(10), OnOutput = output };

    [Fact]
    public async Task CapturesSeparateStreamsAndNonzeroExitCode()
    {
        var result = await new ProcessRunner().RunAsync(Request("[Console]::Out.WriteLine('output'); [Console]::Error.WriteLine('error'); exit 7"), default);
        Assert.Equal(7, result.ExitCode);
        Assert.Contains("output", result.StandardOutput);
        Assert.Contains("error", result.StandardError);
    }
    [Fact]
    public async Task TimesOutAndStopsOwnedProcess()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => new ProcessRunner().RunAsync(Request("Start-Sleep -Seconds 30", TimeSpan.FromMilliseconds(300)), default));
    }
    [Fact]
    public async Task CancellationIsNotReportedAsTimeout()
    {
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new ProcessRunner().RunAsync(Request("[Console]::Out.WriteLine('ready'); Start-Sleep -Seconds 30", output: _ => started.TrySetResult()), cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
    [Fact]
    public async Task RelativeExecutableIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new ProcessRunner().RunAsync(new() { FileName = "powershell", WorkingDirectory = Path.GetTempPath() }, default));
    }
}
