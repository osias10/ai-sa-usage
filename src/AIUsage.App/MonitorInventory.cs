using System.Runtime.InteropServices;
using AIUsage.Core;

namespace AIUsage.App;

/// <summary>Fresh monitor geometry in physical desktop pixels, without Screen's cached topology.</summary>
internal static class MonitorInventory
{
    internal static IReadOnlyList<MonitorLayout> Capture()
    {
        var monitors = new List<MonitorLayout>();
        try
        {
            MonitorEnumProc callback = (monitor, _, _, _) =>
            {
                var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(monitor, ref info) || string.IsNullOrWhiteSpace(info.DeviceName)
                    || info.Bounds.Right <= info.Bounds.Left || info.Bounds.Bottom <= info.Bounds.Top
                    || info.WorkArea.Right <= info.WorkArea.Left || info.WorkArea.Bottom <= info.WorkArea.Top)
                    return false;

                // An interface path identifies the monitor independently of DISPLAY1/2 ordering.
                // Only the Windows interface name is read; no EDID or serial data is collected.
                var identity = ReadIdentity(info.DeviceName);
                _ = GetScaleFactorForMonitor(monitor, out var scalePercent);
                // The API supplies a usable fallback factor even when it returns an error.
                var scale = scalePercent > 0 ? scalePercent / 100d : 1d;
                monitors.Add(new MonitorLayout(identity, info.DeviceName,
                    ToDesktopRect(info.Bounds), ToDesktopRect(info.WorkArea), scale,
                    (info.Flags & 1) != 0));
                return true;
            };

            // Do not expose a partial topology during a driver/display transition.
            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
                return Array.Empty<MonitorLayout>();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return Array.Empty<MonitorLayout>();
        }

        return Array.AsReadOnly(monitors.OrderByDescending(monitor => monitor.IsPrimary)
            .ThenBy(monitor => monitor.Id, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static string ReadIdentity(string deviceName)
    {
        const uint getDeviceInterfaceName = 1;
        const uint active = 1;
        const uint mirroringDriver = 8;
        string? identity = null;
        for (uint index = 0; ; index++)
        {
            var device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(deviceName, index, ref device, getDeviceInterfaceName)) break;
            if ((device.StateFlags & active) == 0 || (device.StateFlags & mirroringDriver) != 0
                || string.IsNullOrWhiteSpace(device.DeviceId)) continue;
            // Mirrored displays can share one GDI view. Pick a deterministic actual interface
            // rather than depending on the driver's enumeration order or composing an ID.
            if (identity is null || StringComparer.OrdinalIgnoreCase.Compare(device.DeviceId, identity) < 0)
                identity = device.DeviceId;
        }
        return identity ?? deviceName;
    }

    private static DesktopRect ToDesktopRect(NativeRect rect)
        => new(rect.Left, rect.Top, (double)rect.Right - rect.Left, (double)rect.Bottom - rect.Top);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Bounds;
        public NativeRect WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, IntPtr bounds, IntPtr data);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clipRect, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string deviceName, uint index, ref DisplayDevice device, uint flags);

    [DllImport("shcore.dll", ExactSpelling = true)]
    private static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scale);
}
