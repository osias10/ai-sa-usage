namespace AIUsage.Core;

public sealed class UsageAlertState
{
    public int SchemaVersion { get; set; } = 1;
    public List<UsageAlertDelivery> Deliveries { get; set; } = [];
}

public sealed record UsageAlertDelivery(
    ProviderKind Provider, string AccountId, string IdentityKey, string WindowId,
    DateTimeOffset? ResetsAt, DateTimeOffset NotifiedAt);

public sealed record UsageAlert(
    ProviderKind Provider, string AccountId, string IdentityKey, string WindowId,
    string WindowName, double UsedPercent, int ThresholdPercent, DateTimeOffset? ResetsAt);
