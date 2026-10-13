namespace AIUsage.Core;

public sealed class UsageAlertState
{
    public int SchemaVersion { get; set; } = 1;
    public List<UsageAlertDelivery> Deliveries { get; set; } = [];
    public List<UsageResetObservation> ResetObservations { get; set; } = [];
}

public sealed record UsageAlertDelivery(
    ProviderKind Provider, string AccountId, string IdentityKey, string WindowId,
    DateTimeOffset? ResetsAt, DateTimeOffset NotifiedAt);

public enum UsageAlertKind { Threshold, Reset }

/// <summary>The latest confirmed future reset, with source and local clock high-water marks.</summary>
public sealed record UsageResetObservation(
    ProviderKind Provider, string AccountId, string IdentityKey, string WindowId,
    DateTimeOffset ResetsAt, DateTimeOffset FetchedAt, DateTimeOffset ObservedAt);

public sealed record UsageAlert(
    ProviderKind Provider, string AccountId, string IdentityKey, string WindowId,
    string WindowName, double UsedPercent, int ThresholdPercent, DateTimeOffset? ResetsAt,
    UsageAlertKind Kind = UsageAlertKind.Threshold);
