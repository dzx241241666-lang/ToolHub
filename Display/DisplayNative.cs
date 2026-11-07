using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Switchboard.Display;

internal static class DisplayNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct MonitorInfo
    {
        public int Size; public Rect Bounds, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }
    internal sealed record LogicalMonitor(IntPtr Handle, string Device, bool Primary, int Width, int Height, int Left, int Top, List<DisplayDevice> Devices);
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr hdc, ref Rect bounds, IntPtr data);
    public static List<LogicalMonitor> Enumerate()
    {
        var result = new List<LogicalMonitor>();
        MonitorCallback callback = (IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            var devices = new List<DisplayDevice>();
            for (uint i = 0; i < 32; i++)
            {
                var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
                if (!EnumDisplayDevices(info.Device, i, ref device, 1)) break;
                if ((device.Flags & 1) != 0) devices.Add(device);
            }
            result.Add(new(monitor, info.Device, (info.Flags & 1) != 0, info.Bounds.Right - info.Bounds.Left, info.Bounds.Bottom - info.Bounds.Top, info.Bounds.Left, info.Bounds.Top, devices));
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return result;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")] private static extern bool EnumDisplayDevices(string device, uint number, ref DisplayDevice display, uint flags);
    [DllImport("dxva2.dll", SetLastError = true)] internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll", SetLastError = true)] internal static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] physical);
    [DllImport("dxva2.dll", SetLastError = true)] internal static extern bool DestroyPhysicalMonitor(IntPtr monitor);
    [DllImport("dxva2.dll", SetLastError = true)] internal static extern bool GetVCPFeatureAndVCPFeatureReply(PhysicalHandle monitor, byte code, out uint type, out uint current, out uint maximum);
    [DllImport("dxva2.dll", SetLastError = true)] internal static extern bool SetVCPFeature(PhysicalHandle monitor, byte code, uint value);
    [DllImport("dxva2.dll", SetLastError = true)] internal static extern bool GetMonitorBrightness(PhysicalHandle monitor, out uint minimum, out uint current, out uint maximum);
    [DllImport("dxva2.dll", SetLastError = true)] internal static extern bool SetMonitorBrightness(PhysicalHandle monitor, uint value);
}

internal sealed class PhysicalHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public PhysicalHandle(IntPtr value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle() => DisplayNative.DestroyPhysicalMonitor(handle);
}
