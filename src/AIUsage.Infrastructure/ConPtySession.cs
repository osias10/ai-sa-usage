using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using AIUsage.Core;
using Microsoft.Win32.SafeHandles;

namespace AIUsage.Infrastructure;

/// <summary>
/// An invisible Windows pseudoconsole for official login flows that require a controlling terminal.
/// Output is raw UTF-8 terminal text (including escape sequences), never written to a log.
/// The owned job kills the child and all descendants when this session ends.
/// </summary>
public sealed class ConPtySession : IAsyncDisposable
{
    private const int OutputLimit = 4 * 1024 * 1024;
    private readonly SafeFileHandle _process;
    private readonly SafeFileHandle _job;
    private readonly SafeFileHandle _input;
    private readonly SafeFileHandle _output;
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenRegistration _deadlineRegistration;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly object _closeSync = new();
    private Task? _consoleCloseTask;
    private IntPtr _console;
    private int _disposed;
    private int _outputBytes;
    private bool _finishedReading;

    public ConPtySession(ProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) throw new PlatformNotSupportedException("Windows ConPTY is required.");
        if (!Path.IsPathFullyQualified(request.FileName) || !File.Exists(request.FileName))
            throw new ArgumentException("An existing absolute executable path is required.", nameof(request));
        if (!Path.IsPathFullyQualified(request.WorkingDirectory) || !Directory.Exists(request.WorkingDirectory))
            throw new DirectoryNotFoundException("An existing absolute working directory is required.");
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(request), "A timeout between zero and one hour is required.");

        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
        SafeFileHandle? process = null, thread = null, job = null;
        IntPtr console = IntPtr.Zero, attributes = IntPtr.Zero, environment = IntPtr.Zero;
        var attributesInitialized = false;
        try
        {
            Check(Native.CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0));
            Check(Native.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0));
            // Headless auth URLs can be long. Avoid inserting terminal line wraps into them.
            var hr = Native.CreatePseudoConsole(new(4096, 32), inputRead, outputWrite, 0, out console);
            Marshal.ThrowExceptionForHR(hr);

            nuint bytes = 0;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            if (bytes == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            attributes = Marshal.AllocHGlobal(checked((nint)bytes));
            Check(Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes));
            attributesInitialized = true;
            Check(Native.UpdateProcThreadAttribute(attributes, 0, 0x00020016, console, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));

            var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
                variables[(string)variable.Key] = (string)variable.Value!;
            foreach (var variable in request.Environment)
            {
                if (variable.Value is null) variables.Remove(variable.Key);
                else variables[variable.Key] = variable.Value;
            }
            var block = string.Concat(variables.Select(v => $"{v.Key}={v.Value}\0")) + "\0";
            environment = Marshal.StringToHGlobalUni(block);
            var commandLine = new StringBuilder(string.Join(" ", new[] { request.FileName }.Concat(request.Arguments).Select(QuoteArgument)));
            var startup = new Native.StartupInfoEx
            {
                // Null standard handles plus STARTF_USESTDHANDLES prevent a redirected parent
                // (including a test runner) from donating its pipe handles instead of the PTY.
                StartupInfo = new() { Size = Marshal.SizeOf<Native.StartupInfoEx>(), Flags = 0x00000100 },
                AttributeList = attributes
            };

            job = Native.CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new Native.JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new() { LimitFlags = 0x00002000 } // KILL_ON_JOB_CLOSE
            };
            Check(Native.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Native.JobObjectExtendedLimitInformation>()));

            // Suspended startup ensures no child runs before it belongs to the owned cleanup job.
            Check(Native.CreateProcessW(request.FileName, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                0x00080000 | 0x00000400 | 0x00000004, environment, request.WorkingDirectory, ref startup, out var info));
            process = new(info.Process, ownsHandle: true);
            thread = new(info.Thread, ownsHandle: true);
            Check(Native.AssignProcessToJobObject(job, process));
            if (Native.ResumeThread(thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());

            _process = process;
            _job = job;
            _input = inputWrite;
            _output = outputRead;
            _console = console;
            _deadline = new CancellationTokenSource(request.Timeout);
            _deadlineRegistration = _deadline.Token.Register(StopOwnedJob);
            process = null;
            job = null;
            inputWrite = null;
            outputRead = null;
            console = IntPtr.Zero;
        }
        catch
        {
            if (process is { IsInvalid: false }) Native.TerminateProcess(process, 1);
            throw;
        }
        finally
        {
            thread?.Dispose();
            process?.Dispose();
            job?.Dispose();
            inputRead?.Dispose();
            inputWrite?.Dispose();
            outputRead?.Dispose();
            outputWrite?.Dispose();
            if (console != IntPtr.Zero) Native.ClosePseudoConsole(console);
            if (attributesInitialized) Native.DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }

    public bool HasExited
    {
        get
        {
            if (_process.IsClosed) return true;
            var result = Native.WaitForSingleObject(_process, 0);
            if (result == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            return result == 0;
        }
    }

    public int ExitCode
    {
        get
        {
            if (!HasExited) throw new InvalidOperationException("The login process has not exited.");
            Check(Native.GetExitCodeProcess(_process, out var result));
            return unchecked((int)result);
        }
    }

    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
        using var cancelled = cancellationToken.Register(StopOwnedJob);
        await _readGate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            if (_finishedReading) return null;
            var buffer = new byte[16 * 1024];
            while (true)
            {
                operation.Token.ThrowIfCancellationRequested();
                if (!Native.PeekNamedPipe(_output, IntPtr.Zero, 0, IntPtr.Zero, out var available, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error is 109 or 233 || HasExited) return FinishReading();
                    throw new Win32Exception(error);
                }
                if (available > 0)
                {
                    Check(Native.ReadFile(_output, buffer, (uint)Math.Min(available, buffer.Length), out var read, IntPtr.Zero));
                    _outputBytes += checked((int)read);
                    if (_outputBytes > OutputLimit)
                    {
                        StopOwnedJob();
                        throw new InvalidDataException("Login terminal output exceeded the allowed size.");
                    }
                    var chars = new char[Encoding.UTF8.GetMaxCharCount((int)read)];
                    var decoded = _decoder.GetChars(buffer, 0, (int)read, chars, 0);
                    if (decoded > 0) return new string(chars, 0, decoded);
                }
                else if (HasExited && BeginCloseConsole().IsCompleted) return FinishReading();
                // Synchronous pipes cannot cancel a blocking ReadFile. Peek before reading and use
                // cancellable waits so closing a login dialog always stops promptly.
                await Task.Delay(25, operation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _deadline.IsCancellationRequested)
        {
            await StopAndWaitAsync().ConfigureAwait(false);
            throw new TimeoutException("The official login process timed out.");
        }
        finally { _readGate.Release(); }
    }

    public async Task WriteAsync(string text, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > 16 * 1024) throw new ArgumentException("Login input is too large.", nameof(text));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
        using var cancelled = cancellationToken.Register(StopOwnedJob);
        await _writeGate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            var bytes = Encoding.UTF8.GetBytes(text);
            // Console input consists of small URL/code responses. A worker keeps the UI thread free.
            await Task.Run(() =>
            {
                var offset = 0;
                while (offset < bytes.Length)
                {
                    operation.Token.ThrowIfCancellationRequested();
                    var part = bytes.AsSpan(offset, Math.Min(256, bytes.Length - offset)).ToArray();
                    Check(Native.WriteFile(_input, part, (uint)part.Length, out var written, IntPtr.Zero));
                    if (written == 0) throw new IOException("The login terminal accepted no input.");
                    offset += (int)written;
                }
            }, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _deadline.IsCancellationRequested)
        {
            await StopAndWaitAsync().ConfigureAwait(false);
            throw new TimeoutException("The official login process timed out.");
        }
        finally { _writeGate.Release(); }
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
        using var cancelled = cancellationToken.Register(StopOwnedJob);
        try
        {
            while (!HasExited) await Task.Delay(25, operation.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (_deadline.IsCancellationRequested) throw new TimeoutException("The official login process timed out.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _deadline.IsCancellationRequested)
        {
            await StopAndWaitAsync().ConfigureAwait(false);
            throw new TimeoutException("The official login process timed out.");
        }
    }

    private string? FinishReading()
    {
        _finishedReading = true;
        var chars = new char[4];
        var count = _decoder.GetChars([], 0, 0, chars, 0, flush: true);
        return count == 0 ? null : new string(chars, 0, count);
    }

    private void StopOwnedJob()
    {
        if (!_job.IsClosed && !_job.IsInvalid)
            Native.TerminateJobObject(_job, 1);
    }

    private Task StopAndWaitAsync()
    {
        StopOwnedJob();
        return Task.Run(() => Native.WaitForSingleObject(_process, 2000));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _deadline.Cancel();
        _deadlineRegistration.Dispose();
        StopOwnedJob();
        // ClosePseudoConsole can synchronously flush output. Continue draining its pipe on a
        // separate thread while it closes, avoiding the documented shutdown deadlock.
        var close = BeginCloseConsole();
        await _readGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var buffer = new byte[16 * 1024];
            var until = Environment.TickCount64 + 5000;
            while (!close.IsCompleted && Environment.TickCount64 < until)
            {
                if (Native.PeekNamedPipe(_output, IntPtr.Zero, 0, IntPtr.Zero, out var available, IntPtr.Zero) && available > 0)
                    Native.ReadFile(_output, buffer, (uint)Math.Min(available, buffer.Length), out _, IntPtr.Zero);
                else await Task.Delay(10).ConfigureAwait(false);
            }
            _output.Dispose();
            _input.Dispose();
            _process.Dispose();
            _job.Dispose();
        }
        finally { _readGate.Release(); _deadline.Dispose(); }
        try { await close.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (TimeoutException) { /* Closed pipes allow the background native close to finish. */ }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    private static void Check(bool result) { if (!result) throw new Win32Exception(Marshal.GetLastWin32Error()); }

    private Task BeginCloseConsole()
    {
        lock (_closeSync)
        {
            return _consoleCloseTask ??= Task.Run(() =>
            {
                var console = Interlocked.Exchange(ref _console, IntPtr.Zero);
                if (console != IntPtr.Zero) Native.ClosePseudoConsole(console);
            });
        }
    }

    internal static string QuoteArgument(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') result.Append('\\', slashes * 2 + 1).Append(c);
            else result.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal readonly struct Coord(short x, short y) { public readonly short X = x, Y = y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfo
        {
            public int Size; public IntPtr Reserved; public IntPtr Desktop; public IntPtr Title;
            public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public short ShowWindow, ReservedSize; public IntPtr Reserved2, StdInput, StdOutput, StdError;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] internal struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
            public nuint MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
            public nuint Affinity; public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] internal struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation; public IoCounters IoInfo;
            public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr attributes, uint size);
        [DllImport("kernel32.dll")] internal static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
        [DllImport("kernel32.dll")] internal static extern void ClosePseudoConsole(IntPtr console);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(SafeFileHandle thread);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref JobObjectExtendedLimitInformation information, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateJobObject(SafeFileHandle job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(SafeFileHandle process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PeekNamedPipe(SafeFileHandle pipe, IntPtr buffer, uint bufferSize, IntPtr bytesRead, out uint totalBytesAvailable, IntPtr bytesLeft);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ReadFile(SafeFileHandle file, byte[] buffer, uint bytes, out uint read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WriteFile(SafeFileHandle file, byte[] buffer, uint bytes, out uint written, IntPtr overlapped);
    }
}
