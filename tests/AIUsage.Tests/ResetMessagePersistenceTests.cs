using System.Text.Json;
using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class ResetMessagePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AIUsage-reset-persistence-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset = Now.AddMinutes(10);
    private const string Target = "session::0";
    private static AccountSettings Account() => new()
    {
        Provider = ProviderKind.Claude, IdentityKey = "synthetic-reset-account",
        ResetMessageEnabled = true, ResetMessageWindowId = Target
    };
    private static UsageSnapshot Snapshot(AccountSettings account, DateTimeOffset fetched, DateTimeOffset? resetsAt, double used)
        => new(account.Provider, account.Id, account.IdentityKey, "test-plan", fetched,
            [new(Target, "Session", used, resetsAt)]);

    private static (ResetMessageEngine Engine, ResetMessageTicket Ticket) Reserve(AccountSettings account)
    {
        var engine = new ResetMessageEngine();
        engine.Observe(account, true, Target, Snapshot(account, Now, Reset, 25), Now, isFresh: true);
        var ticket = Assert.IsType<ResetMessageTicket>(engine.GetDue(account, true, Target, Reset));
        Assert.Equal(ResetMessageDecision.Reserved,
            engine.PrepareAttempt(ticket, Snapshot(account, Reset, null, 0), Reset, isFresh: true));
        return (engine, ticket);
    }

    [Fact]
    public async Task LegacyAccountStartsDisabledAndExplicitEnableAndTargetSurviveSettingsReload()
    {
        var paths = new AppPaths(_root);
        Directory.CreateDirectory(_root);
        var id = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(paths.SettingsFile,
            $$"""{"Accounts":[{"Id":"{{id}}","Provider":"Claude","IdentityKey":"synthetic-reset-account"}]}""");
        var store = new JsonSettingsStore(paths);
        var settings = await store.LoadSettingsAsync();
        var account = Assert.Single(settings.Accounts);
        Assert.False(account.ResetMessageEnabled);
        Assert.Null(account.ResetMessageWindowId);
        account.ResetMessageEnabled = true;
        account.ResetMessageWindowId = Target;
        await store.SaveSettingsAsync(settings);

        var restored = Assert.Single((await new JsonSettingsStore(paths).LoadSettingsAsync()).Accounts);
        Assert.True(restored.ResetMessageEnabled);
        Assert.Equal(Target, restored.ResetMessageWindowId);
        Assert.Equal(account.Id, restored.Id);
        Assert.Equal(account.IdentityKey, restored.IdentityKey);
    }

    [Fact]
    public async Task ReservedReceiptIsReloadableWithoutCompletionAndBlocksASecondSend()
    {
        var paths = new AppPaths(_root);
        var account = Account();
        var (engine, ticket) = Reserve(account);
        await new JsonSettingsStore(paths).SaveResetMessageStateAsync(engine.GetState());

        // Simulate termination after saving the reservation, before the provider outcome is known.
        var restoredState = await new JsonSettingsStore(paths).LoadResetMessageStateAsync();
        var receipt = Assert.Single(restoredState.Attempts);
        Assert.Equal(ResetMessageOutcome.Reserved, receipt.Outcome);
        Assert.Null(receipt.CompletedAt);
        Assert.Equal(ticket.ResetsAt, receipt.ResetsAt);
        var restarted = new ResetMessageEngine(restoredState);
        Assert.Equal(ResetMessageDecision.Invalid, restarted.PrepareAttempt(ticket,
            Snapshot(account, Reset.AddSeconds(10), null, 0), Reset.AddSeconds(10), isFresh: true));
        Assert.Null(restarted.GetDue(account, true, Target, Reset.AddSeconds(10)));
        Assert.Single(restarted.GetState().Attempts);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public async Task PreviewTransitionBacksUpResetReceiptsWithoutClearingThemWithStatistics()
    {
        var paths = new AppPaths(_root);
        var account = Account();
        var (engine, _) = Reserve(account);
        var store = new JsonSettingsStore(paths);
        await store.SaveResetMessageStateAsync(engine.GetState());
        await store.SaveSettingsAsync(new() { Accounts = [account] });
        var before = await File.ReadAllTextAsync(paths.ResetMessageStateFile);

        var backup = await store.BackupForVersionChangeAsync("0.4.11-beta.4", "0.4.10");
        var backedUpReceipt = Path.Combine(backup, "reset-messages.json");
        Assert.Equal(before, await File.ReadAllTextAsync(backedUpReceipt));
        Assert.Equal(before, await File.ReadAllTextAsync(paths.ResetMessageStateFile));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(backup, "manifest.json")));
        Assert.Contains("reset-messages.json", manifest.RootElement.GetProperty("Files").EnumerateArray().Select(item => item.GetString()));

        await store.ClearStatisticsBackupsAsync();
        Assert.Equal(before, await File.ReadAllTextAsync(backedUpReceipt));
        Assert.Single((await store.LoadResetMessageStateAsync()).Attempts);
    }

    [Fact]
    public async Task CorruptReceiptIsPreservedWithWarningAndCannotArmFromCachedOrExpiredReads()
    {
        var paths = new AppPaths(_root);
        var account = Account();
        var (_, ticket) = Reserve(account);
        Directory.CreateDirectory(_root);
        const string corrupt = "{\"SchemaVersion\":1,\"Attempts\":[{truncated";
        await File.WriteAllTextAsync(paths.ResetMessageStateFile, corrupt);
        var store = new JsonSettingsStore(paths);
        var recovered = await store.LoadResetMessageStateAsync();
        Assert.NotNull(store.LastWarning);
        Assert.Empty(recovered.Observations);
        Assert.Empty(recovered.Attempts);
        var preserved = Assert.Single(Directory.GetFiles(_root, "reset-messages.json.corrupt-*"));
        Assert.Equal(corrupt, await File.ReadAllTextAsync(preserved));

        var restarted = new ResetMessageEngine(recovered);
        restarted.Observe(account, true, Target, Snapshot(account, Now, Reset, 25), Now, isFresh: false);
        restarted.Observe(account, true, Target, Snapshot(account, Reset, Reset, 0), Reset, isFresh: true);
        Assert.Null(restarted.GetDue(account, true, Target, Reset));
        Assert.Equal(ResetMessageDecision.Invalid,
            restarted.PrepareAttempt(ticket, Snapshot(account, Reset, null, 0), Reset, isFresh: true));
        Assert.Empty(restarted.GetState().Attempts);
    }

    [Fact]
    public async Task DisablingPersistsCancellationOfNextReservationWithoutLosingPreviousReceipt()
    {
        var paths = new AppPaths(_root);
        var account = Account();
        var (engine, ticket) = Reserve(account);
        engine.CompleteAttempt(ticket, ResetMessageOutcome.Unknown, Reset.AddSeconds(1));
        engine.Observe(account, true, Target, Snapshot(account, Reset.AddMinutes(1), Reset.AddHours(5), 1),
            Reset.AddMinutes(1), isFresh: true);
        Assert.Single(engine.GetState().Observations);
        account.ResetMessageEnabled = false;
        engine.SynchronizeSettings(account, account.ResetMessageEnabled, account.ResetMessageWindowId);
        var store = new JsonSettingsStore(paths);
        await store.SaveSettingsAsync(new() { Accounts = [account] });
        await store.SaveResetMessageStateAsync(engine.GetState());

        var reopenedStore = new JsonSettingsStore(paths);
        var restoredAccount = Assert.Single((await reopenedStore.LoadSettingsAsync()).Accounts);
        var restored = await reopenedStore.LoadResetMessageStateAsync();
        Assert.False(restoredAccount.ResetMessageEnabled);
        Assert.Equal(Target, restoredAccount.ResetMessageWindowId);
        Assert.Empty(restored.Observations);
        Assert.Equal(ResetMessageOutcome.Unknown, Assert.Single(restored.Attempts).Outcome);
        var restarted = new ResetMessageEngine(restored);
        restarted.SynchronizeSettings(restoredAccount, true, Target);
        Assert.Null(restarted.GetDue(restoredAccount, true, Target, Reset.AddHours(5)));
        Assert.Single(restarted.GetState().Attempts);
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(_root);
        var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(resolved), expectedParent, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("AIUsage-reset-persistence-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside the owned temporary test directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
