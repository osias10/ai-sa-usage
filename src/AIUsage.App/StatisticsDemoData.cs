using AIUsage.Core;

namespace AIUsage.App;

internal static class StatisticsDemoData
{
    // Opt-in demo only; normal demo and live accounts never receive generated history.
    internal static UsageStatisticsHistory Create(IReadOnlyList<AccountSettings> accounts, IReadOnlyList<UsageSnapshot> snapshots, DateTimeOffset now)
    {
        var history = new UsageStatisticsHistory();
        foreach (var account in accounts)
        {
            var snapshot = snapshots.Single(sample => sample.AccountId == account.Id);
            foreach (var window in snapshot.Windows)
            {
                for (var step = 0; step < 144; step++)
                {
                    var time = now.AddMinutes(-1435 + step * 10);
                    // Leave a visible collection gap and preserve real reset boundaries in this fixture.
                    if (step is > 74 and < 86) continue;
                    var session = window.IsPrimary;
                    var cycle = session ? step / 30 : 0;
                    var used = session ? 10 + step % 30 * 2.4 : Math.Min(98, 12 + step * .35);
                    var reset = session ? now.AddMinutes(-1435 + (cycle + 1) * 300) : window.ResetsAt;
                    history.Samples.Add(new(account.Provider, account.Id, account.IdentityKey!, window.Id, window.Name, used, reset, time));
                }
            }
        }
        var first = accounts[0];
        history.Samples.Add(new(first.Provider, first.Id, first.IdentityKey!, "archived", "이전 한도", 42, now.AddHours(1), now.AddHours(-3)));
        return history;
    }
}
