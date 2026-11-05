using System.Text.Json;
using AIUsage.Core;

namespace AIUsage.Providers;

// This seam keeps lease ownership testable without starting a real account process.
internal interface ICodexRpcConnection : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken token);
    Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken token);
    Task<JsonElement> WaitForNotificationAsync(string method, string? loginId, CancellationToken token);
}

internal sealed class NativeCodexRpcConnection(ProcessRequest request) : ICodexRpcConnection
{
    private readonly CodexRpcSession _session = new(request);
    public Task InitializeAsync(CancellationToken token) => _session.InitializeAsync(token);
    public Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken token) => _session.RequestAsync(method, parameters, token);
    public Task<JsonElement> WaitForNotificationAsync(string method, string? loginId, CancellationToken token) => _session.WaitForNotificationAsync(method, loginId, token);
    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
