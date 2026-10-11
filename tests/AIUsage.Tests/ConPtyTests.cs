using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AIUsage.Core;
using AIUsage.Infrastructure;
using Xunit;

namespace AIUsage.Tests;

public sealed class ConPtyTests
{
    private static readonly string Dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));

    private static ProcessRequest Child(string mode, TimeSpan? timeout = null) => new()
    {
        FileName = Dotnet,
        WorkingDirectory = Path.GetTempPath(),
        Arguments = [typeof(TestChild.Program).Assembly.Location, mode],
        Timeout = timeout ?? TimeSpan.FromSeconds(15)
    };

    [Fact]
    public async Task ChildHasControllingTerminalAndReceivesUnicodeInput()
    {
        await using var session = new ConPtySession(Child("echo"));
        var output = new StringBuilder(await ReadUntilAsync(session, "ready_for_input"));
        Assert.Contains("redirected=False", Clean(output.ToString()));
        await session.WriteAsync("한글 입력 ✓\r", CancellationToken.None);
        while (await session.ReadAsync(CancellationToken.None) is { } chunk) output.Append(chunk);
        await session.WaitForExitAsync(CancellationToken.None);
        Assert.Equal(7, session.ExitCode);
        Assert.True(Clean(output.ToString()).Contains("received=한글 입력 ✓"), System.Text.Json.JsonSerializer.Serialize(output.ToString()));
    }

    [Fact]
    public async Task FinalOutputIsDrainedAfterFastProcessExit()
    {
        var expected = new string('x', 2048);
        await using var session = new ConPtySession(Child("output"));
        var output = new StringBuilder();
        while (await session.ReadAsync(CancellationToken.None) is { } chunk) output.Append(chunk);
        Assert.Contains(expected, Clean(output.ToString()));
        Assert.Contains("last_line", Clean(output.ToString()));
        Assert.Equal(0, session.ExitCode);
    }

    [Fact]
    public async Task DeadlineStopsTheOwnedProcess()
    {
        await using var session = new ConPtySession(Child("sleep", TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            while (await session.ReadAsync(CancellationToken.None) is not null) { }
        });
        Assert.True(session.HasExited);
    }

    [Fact]
    public async Task CallerCancellationPreservesCancellationSemantics()
    {
        await using var session = new ConPtySession(Child("sleep"));
        await ReadUntilAsync(session, "ready");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            while (await session.ReadAsync(cancel.Token) is not null) { }
        });
    }

    [Fact]
    public async Task DisposingSessionStopsItsChildAndGrandchild()
    {
        var session = new ConPtySession(Child("tree"));
        int[] pids;
        try
        {
            var text = Clean(await ReadUntilAsync(session, ";"));
            var match = Regex.Match(text, @"owned_pids=(\d+),(\d+);");
            Assert.True(match.Success, text);
            pids = [int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)];
        }
        finally { await session.DisposeAsync(); }

        foreach (var pid in pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                Assert.True(process.HasExited);
            }
            catch (ArgumentException) { /* Already reaped by Windows. */ }
        }
    }

    private static async Task<string> ReadUntilAsync(ConPtySession session, string marker)
    {
        var output = new StringBuilder();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!Clean(output.ToString()).Contains(marker, StringComparison.Ordinal))
        {
            var text = await session.ReadAsync(deadline.Token);
            Assert.NotNull(text);
            output.Append(text);
        }
        return output.ToString();
    }

    private static string Clean(string value) => Regex.Replace(value, @"\x1B\][^\x07]*(?:\x07|\x1B\\)|\x1B\[[0-?]*[ -/]*[@-~]", "");
}
