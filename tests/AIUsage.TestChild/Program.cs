using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace AIUsage.TestChild;

// Harmless child process fixture: no provider executables, credentials, or network access.
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        switch (args[0])
        {
            case "echo":
                Console.WriteLine("redirected=" + Console.IsInputRedirected);
                Console.WriteLine("ready_for_input");
                // ReadConsoleW tests the PTY's Unicode input directly. ReadFile-based readers on
                // some Windows builds still apply the console OEM code page despite UTF-8 settings.
                var input = new StringBuilder(1024);
                if (!ReadConsoleW(GetStdHandle(-10), input, (uint)input.Capacity, out _, IntPtr.Zero))
                    return Marshal.GetLastWin32Error();
                Console.WriteLine("received=" + input.ToString().TrimEnd('\r', '\n'));
                return 7;
            case "output":
                Console.WriteLine(new string('x', 2048));
                Console.WriteLine("last_line");
                return 0;
            case "tree":
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                start.ArgumentList.Add("sleep");
                using (var child = Process.Start(start)!)
                    Console.WriteLine($"owned_pids={Environment.ProcessId},{child.Id};");
                await Task.Delay(TimeSpan.FromMinutes(1));
                return 0;
            case "sleep":
                Console.WriteLine("ready");
                await Task.Delay(TimeSpan.FromMinutes(1));
                return 0;
            default: return 2;
        }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int identifier);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadConsoleW(IntPtr input, StringBuilder buffer, uint count, out uint read, IntPtr reserved);
}
