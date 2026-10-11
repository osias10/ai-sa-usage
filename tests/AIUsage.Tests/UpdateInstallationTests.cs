using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class UpdateInstallationTests
{
    [Theory]
    [InlineData("verify")]
    [InlineData("save")]
    [InlineData("launch")]
    public async Task FailureKeepsApplicationOpen(string failure)
    {
        var launched = false;
        var exited = false;
        var flow = new UpdateInstallation();
        await Assert.ThrowsAsync<IOException>(() => flow.RunAsync(
            () => failure == "verify" ? throw new IOException() : Task.FromResult("verified.exe"),
            () => failure == "save" ? throw new IOException() : Task.CompletedTask,
            _ => { if (failure == "launch") throw new IOException(); launched = true; },
            () => { exited = true; return Task.CompletedTask; }));
        Assert.False(launched);
        Assert.False(exited);
        // A failed attempt must not leave installation permanently locked.
        await flow.RunAsync(() => Task.FromResult("verified.exe"), () => Task.CompletedTask,
            _ => launched = true, () => { exited = true; return Task.CompletedTask; });
        Assert.True(launched);
        Assert.True(exited);
    }

    [Fact]
    public async Task ConcurrentClicksLaunchOnlyAfterSaveAndOnlyOnce()
    {
        var save = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launches = 0;
        var exits = 0;
        var flow = new UpdateInstallation();
        Task Run() => flow.RunAsync(() => Task.FromResult("verified.exe"), () => save.Task,
            _ => Interlocked.Increment(ref launches), () => { Interlocked.Increment(ref exits); return Task.CompletedTask; });
        var first = Run();
        await Run();
        Assert.Equal(0, launches);
        Assert.Equal(0, exits);
        save.SetResult();
        await first;
        Assert.Equal(1, launches);
        Assert.Equal(1, exits);
    }
}
