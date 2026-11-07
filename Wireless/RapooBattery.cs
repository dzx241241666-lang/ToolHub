using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Switchboard.Wireless;

internal sealed record RapooInterface(string Path, string Identity, ushort ProductId, ushort UsagePage, ushort Usage, ushort InputLength, string Product);
internal sealed record RapooSample(RapooInterface Device, int? Percent, string Error, string[] Reports);

internal static class RapooBattery
{
    private static readonly Dictionary<string, (int Percent, DateTimeOffset At)> LastReadings = new();
    private static readonly object ReadLock = new();
    [StructLayout(LayoutKind.Sequential)] private struct InterfaceData { public uint Size; public Guid Class; public uint Flags; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct Attributes { public int Size; public ushort Vendor, Product, Version; }
    [StructLayout(LayoutKind.Sequential)] private struct Caps
    {
        public ushort Usage, UsagePage, Input, Output, Feature;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort Nodes, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
    }
    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetProductString(SafeFileHandle handle, [Out] byte[] buffer, uint length);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref InterfaceData info, IntPtr detail, uint size, out uint required, IntPtr device);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);

    public static List<RapooInterface> Enumerate()
    {
        HidD_GetHidGuid(out var guid);
        var devices = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0x12);
        var result = new List<RapooInterface>();
        if (devices == new IntPtr(-1)) return result;
        try
        {
            for (uint i = 0; i < 2048; i++)
            {
                var data = new InterfaceData { Size = (uint)Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref guid, i, ref data)) break;
                SetupDiGetDeviceInterfaceDetailW(devices, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required is < 8 or > 32768) continue;
                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(devices, ref data, buffer, required, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(buffer + 4)!;
                    if (!path.Contains("vid_24ae", StringComparison.OrdinalIgnoreCase)) continue;
                    using var handle = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid) continue;
                    var attributes = new Attributes { Size = Marshal.SizeOf<Attributes>() };
                    if (!HidD_GetAttributes(handle, ref attributes) || attributes.Vendor != 0x24ae || !HidD_GetPreparsedData(handle, out var preparsed)) continue;
                    Caps caps;
                    try { if (HidP_GetCaps(preparsed, out caps) != 0x110000) continue; }
                    finally { HidD_FreePreparsedData(preparsed); }
                    if (caps.UsagePage != 0xff00 || caps.Input is < 7 or > 128) continue;
                    var text = new byte[512];
                    var product = HidD_GetProductString(handle, text, (uint)text.Length) ? Encoding.Unicode.GetString(text).TrimEnd('\0') : "Rapoo";
                    var identity = BatteryService.ContainerId(path) ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.RegularExpressions.Regex.Replace(path.ToLowerInvariant(), @"&col[0-9a-f]+|&[0-9a-f]{4}(?=#)", ""))))[..24];
                    result.Add(new(path, identity, attributes.Product, caps.UsagePage, caps.Usage, caps.Input, product));
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
        return result;
    }
    public static int? Parse(ReadOnlySpan<byte> report)
    {
        // VT3 firmware uses status type 10 00; some Rapoo firmware uses 20 00.
        // Only status reports are accepted, with a validated percentage at byte 8.
        if (report.Length == 13 && report[0] is 0x07 or 0x00 && report[1] is 0x10 or 0x20 && report[2] == 0 && report[8] <= 100) return report[8];
        return null;
    }
    private static async Task<RapooSample> ListenAsync(RapooInterface device, bool diagnostic)
    {
        var reports = new List<string>();
        try
        {
            using var handle = CreateFileW(device.Path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid) return new(device, null, "接收器暂不可读取", reports.ToArray());
            using var input = new FileStream(handle, FileAccess.Read, device.InputLength, true);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var bytes = new byte[device.InputLength];
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var length = await input.ReadAsync(bytes, cancellation.Token).ConfigureAwait(false);
                    if (length == 0) break;
                    if (diagnostic && reports.Count < 8) reports.Add(Convert.ToHexString(bytes.AsSpan(0, length)));
                    var percent = Parse(bytes.AsSpan(0, length));
                    if (percent != null) return new(device, percent, "", reports.ToArray());
                }
            }
            catch (OperationCanceledException) { }
            return new(device, null, "等待鼠标上报", reports.ToArray());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(device, null, "接收器暂不可读取", reports.ToArray()); }
    }
    public static async Task<RapooSample[]> ProbeAsync(bool diagnostic = false)
    {
        var devices = Enumerate();
        return await Task.WhenAll(devices.Take(16).Select(device => ListenAsync(device, diagnostic))).ConfigureAwait(false);
    }
    public static List<BatteryDevice> Read()
    {
        lock (ReadLock)
        {
            // This receiver ID and status frame were verified on a VT3. Other Rapoo
            // protocols stay outside this reader until their reports are verified.
            var devices = Enumerate().Where(d => d.ProductId == 0x1412 && d.Usage == 2 && d.InputLength == 13).Take(8).ToList();
            var samples = Task.WhenAll(devices.Select(device => ListenAsync(device, false))).GetAwaiter().GetResult();
            var result = new List<BatteryDevice>();
            var now = DateTimeOffset.Now;
            foreach (var group in samples.GroupBy(s => s.Device.Identity))
            {
                var id = "rapoo:" + group.Key;
                var sample = group.FirstOrDefault(s => s.Percent != null) ?? group.First();
                var source = "雷柏接收器 · 2.4G";
                if (sample.Percent is int percent)
                {
                    LastReadings[id] = (percent, now);
                    result.Add(new(id, "雷柏 VT3", percent, "", true, "mouse", source));
                }
                else if (LastReadings.TryGetValue(id, out var previous))
                    result.Add(new(id, "雷柏 VT3", previous.Percent, "", null, "mouse", source, $"等待更新 · 上次报告 {previous.At:HH:mm:ss}"));
                else result.Add(new(id, "雷柏 VT3", null, "", null, "mouse", source, sample.Error));
            }
            foreach (var removed in LastReadings.Keys.Where(k => result.All(d => d.Id != k)).ToArray()) LastReadings.Remove(removed);
            return result;
        }
    }
}
