namespace AIUsage.Core;

/// <summary>Only hand off to the installer after verification and durable state saves.</summary>
public sealed class UpdateInstallation
{
    private int _running;

    public async Task RunAsync(Func<Task<string>> verifyInstaller, Func<Task> saveState,
        Action<string> launchInstaller, Func<Task> exitApplication)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        try
        {
            var installer = await verifyInstaller();
            await saveState();
            launchInstaller(installer);
            await exitApplication();
        }
        finally { Volatile.Write(ref _running, 0); }
    }
}
