using System.Diagnostics;
using System.Text;
using AIUsage.Core;

namespace AIUsage.Infrastructure;

/// <summary>Runs a directly addressed executable without a shell and owns only its process tree.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    private const int MaximumOutputCharacters = 4 * 1024 * 1024;

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Path.IsPathFullyQualified(request.FileName)) throw new ArgumentException("An absolute executable path is required.", nameof(request));
        if (!File.Exists(request.FileName)) throw new FileNotFoundException("The requested executable does not exist.", request.FileName);
        if (!Path.IsPathFullyQualified(request.WorkingDirectory) || !Directory.Exists(request.WorkingDirectory))
            throw new DirectoryNotFoundException("An existing absolute working directory is required.");
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(request), "Process timeouts must be between zero and one hour.");
        cancellationToken.ThrowIfCancellationRequested();

        var start = new ProcessStartInfo(request.FileName)
        {
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
        foreach (var variable in request.Environment)
        {
            if (variable.Value is null) start.Environment.Remove(variable.Key);
            else start.Environment[variable.Key] = variable.Value;
        }

        using var process = new Process { StartInfo = start };
        using var deadline = new CancellationTokenSource(request.Timeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        if (!process.Start()) throw new InvalidOperationException("The provider process could not be started.");
        using var stopRegistration = operation.Token.Register(() => KillOwnedProcess(process));
        var output = CaptureAsync(process.StandardOutput, request.OnOutput, process, operation.Token);
        var error = CaptureAsync(process.StandardError, request.OnError, process, operation.Token);

        try
        {
            if (request.StandardInput is not null)
                await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), operation.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(operation.Token).ConfigureAwait(false);
            await Task.WhenAll(output, error).WaitAsync(operation.Token).ConfigureAwait(false);
            return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("The provider process did not finish within its configured timeout.");
        }
        finally
        {
            KillOwnedProcess(process);
            // Closing readers releases blocked reads even if a detached child retained a pipe handle.
            process.StandardOutput.Close();
            process.StandardError.Close();
            try { await Task.WhenAll(output, error).ConfigureAwait(false); }
            catch { /* The primary execution exception is preserved; both tasks are observed. */ }
        }
    }

    private static async Task<string> CaptureAsync(StreamReader reader, Action<string>? callback, Process process, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var line = callback is null ? null : new StringBuilder();
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                if (output.Length + count > MaximumOutputCharacters)
                    throw new InvalidDataException("Provider output exceeded the allowed size.");
                output.Append(buffer, 0, count);
                if (line is null) continue;
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        callback!(line.ToString().TrimEnd('\r'));
                        line.Clear();
                    }
                    else line.Append(buffer[i]);
                }
            }
            if (line?.Length > 0) callback!(line.ToString().TrimEnd('\r'));
            return output.ToString();
        }
        catch
        {
            // Also stop promptly on output limits and exceptions in a caller's output callback.
            KillOwnedProcess(process);
            throw;
        }
    }

    private static void KillOwnedProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
