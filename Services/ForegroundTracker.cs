using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Switchboard.Services;

public sealed record AppTarget(uint ProcessId, long Started, string Name, string Title);
public sealed class ForegroundTracker
{
    public AppTarget? Last { get; private set; }
    private static readonly uint OwnPid = (uint)Environment.ProcessId;
    public void Poll()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return;
        GetWindowThreadProcessId(window, out var pid);
        if (pid == OwnPid)
        {
            if (Last != null) { try { using var previous = Process.GetProcessById((int)Last.ProcessId); if (previous.StartTime.ToUniversalTime().Ticks != Last.Started) Last = null; } catch { Last = null; } }
            return;
        }
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (process.ProcessName is "explorer" or "ShellExperienceHost" or "StartMenuExperienceHost" or "SearchHost") { Last = null; return; }
            if (process.ProcessName == "ApplicationFrameHost")
            {
                EnumChildWindows(window, (child, _) => { GetWindowThreadProcessId(child, out var childPid); if (childPid != pid && childPid > 0) { pid = childPid; return false; } return true; }, IntPtr.Zero);
            }
            using var actual = Process.GetProcessById((int)pid);
            var title = new StringBuilder(512);
            GetWindowText(window, title, title.Capacity);
            var name = actual.ProcessName;
            try { name = actual.MainModule?.FileVersionInfo.FileDescription ?? name; } catch { }
            Last = new(pid, actual.StartTime.ToUniversalTime().Ticks, name, title.ToString());
        }
        catch { Last = null; }
    }
    public AppTarget RequireTarget()
    {
        Poll();
        var target = Last ?? throw new InvalidOperationException("没有前台应用");
        try { using var p = Process.GetProcessById((int)target.ProcessId); if (p.StartTime.ToUniversalTime().Ticks != target.Started) throw new InvalidOperationException(); }
        catch { Last = null; throw new InvalidOperationException("前台应用已退出"); }
        return target;
    }
    public static HashSet<uint> Family(uint targetPid)
    {
        var all = Snapshot();
        var ids = new HashSet<uint> { targetPid };
        var parent = targetPid;
        // A browser's foreground window can belong to a child process.
        var path = ProcessPath(targetPid);
        for (var depth = 0; depth < 16; depth++)
        {
            var entry = all.FirstOrDefault(p => p.Pid == parent);
            if (entry.Parent == 0 || path == null || !string.Equals(ProcessPath(entry.Parent), path, StringComparison.OrdinalIgnoreCase)) break;
            if (!ids.Add(entry.Parent)) break;
            parent = entry.Parent;
        }
        bool changed;
        do { changed = false; foreach (var p in all) if (ids.Contains(p.Parent) && ids.Add(p.Pid)) changed = true; } while (changed);
        return ids;
    }
    private static string? ProcessPath(uint pid)
    {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) return null;
        try { var capacity = 32768; var buffer = new StringBuilder(capacity); return QueryFullProcessImageName(handle, 0, buffer, ref capacity) ? buffer.ToString() : null; }
        finally { CloseHandle(handle); }
    }
    private static List<(uint Pid, uint Parent)> Snapshot()
    {
        var result = new List<(uint, uint)>();
        var handle = CreateToolhelp32Snapshot(2, 0);
        if (handle == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception();
        try { var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() }; if (Process32First(handle, ref entry)) do { result.Add((entry.Pid, entry.Parent)); } while (Process32Next(handle, ref entry)); }
        finally { CloseHandle(handle); }
        return result;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ProcessEntry { public uint Size, Usage, Pid; public UIntPtr DefaultHeap; public uint ModuleId, Threads, Parent; public int Priority; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe; }
    private delegate bool EnumWindow(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, EnumWindow callback, IntPtr parameter);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr handle, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr handle, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder name, ref int size);
}
