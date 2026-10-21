namespace AIUsage.Core;

/// <summary>Optional, explicitly enabled generation after a confirmed subscription reset.</summary>
public interface IResetMessageProvider
{
    Task SendResetMessageAsync(AccountSettings account, CancellationToken cancellationToken);
}
