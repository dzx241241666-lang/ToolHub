using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Switchboard.Wireless;

public sealed record BatteryDevice(string Id, string Name, int? Percent, string Level, bool? Connected, string Kind, string Source, string State = "");
public static class BatteryService
{
    [StructLayout(LayoutKind.Sequential)] private struct Key { public Guid Format; public uint Id; public Key(string guid, uint id) { Format = new(guid); Id = id; } }
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID_List_SizeW(out uint size, string? filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_IDW(uint node, StringBuilder buffer, uint length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_DevNode_PropertyW(uint node, ref Key key, out uint type, [Out] byte[] buffer, ref uint length, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct Battery { public byte Type, Level; }
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetBatteryInformation(uint index, byte type, out Battery battery);
    private static byte[]? Property(uint node, string guid, uint id, out uint type)
    {
        var key = new Key(guid, id); var bytes = new byte[4096]; uint length = (uint)bytes.Length;
        return CM_Get_DevNode_PropertyW(node, ref key, out type, bytes, ref length, 0) == 0 ? bytes[..(int)length] : null;
    }
    private static string Text(uint node, uint id)
    { var bytes = Property(node, "a45c254e-df1c-4efd-8020-67d146a850e0", id, out var type); return bytes != null && type == 18 ? Encoding.Unicode.GetString(bytes).TrimEnd('\0') : ""; }
    public static int? ParsePercent(uint type, byte[]? bytes)
    { if (bytes == null) return null; var value = type == 3 && bytes.Length == 1 ? bytes[0] : type == 7 && bytes.Length == 4 ? (long)BitConverter.ToUInt32(bytes) : -1; return value is >= 0 and <= 100 ? (int)value : null; }
    public static string? Address(string id)
    { var match = Regex.Match(id, @"(?:DEV_|BLUETOOTHDEVICE_|[_&])([A-F0-9]{12})(?:[\\_]|$)", RegexOptions.IgnoreCase); return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null; }
    internal static string? ContainerId(string interfacePath)
    {
        var parts = interfacePath[4..].Split('#');
        if (parts.Length < 3 || CM_Locate_DevNodeW(out var node, string.Join('\\', parts.Take(3)), 0) != 0) return null;
        var bytes = Property(node, "8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2, out var type);
        return type == 13 && bytes?.Length == 16 ? new Guid(bytes).ToString("N") : null;
    }
    public static string Kind(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("xbox") || n.Contains("controller") || n.Contains("手柄") ? "gamepad" : n.Contains("mouse") || n.Contains("鼠标") ? "mouse" : n.Contains("keyboard") || n.Contains("键盘") || n.StartsWith("k1") ? "keyboard" : n.Contains("tws") || n.Contains("buds") || n.Contains("head") || n.Contains("耳") || n.Contains("aero") ? "headphones" : n.Contains("phone") || n.Contains("s24") || n.Contains("tab") ? "phone" : n.Contains("speaker") || n.Contains("音箱") ? "speaker" : "device";
    }
    public static List<BatteryDevice> Read()
    {
        if (CM_Get_Device_ID_List_SizeW(out var length, null, 0x100) != 0 || length > 4_000_000) throw new InvalidOperationException("设备列表不可用");
        var buffer = new char[length];
        if (CM_Get_Device_ID_ListW(null, buffer, length, 0x100) != 0) throw new InvalidOperationException("设备列表读取失败");
        var nodes = new List<(string Address, string Name, int? Percent, bool? Connected, bool Root)>();
        foreach (var id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) continue;
            if (CM_Locate_DevNodeW(out var node, id, 0) != 0) continue;
            var address = Address(id); var parent = node;
            for (var depth = 0; address == null && depth < 8 && CM_Get_Parent(out parent, parent, 0) == 0; depth++)
            { var parentId = new StringBuilder(1024); if (CM_Get_Device_IDW(parent, parentId, 1024, 0) == 0) address = Address(parentId.ToString()); }
            if (address == null) continue;
            var root = id.StartsWith("BTHENUM\\DEV_", StringComparison.OrdinalIgnoreCase) || id.StartsWith("BTHLE\\DEV_", StringComparison.OrdinalIgnoreCase);
            var data = Property(node, "104ea319-6ee2-4701-bd47-8ddbf425bbe5", 2, out var type);
            var percent = ParsePercent(type, data);
            if (!root && percent == null) continue;
            var connected = Property(node, "83da6326-97a6-4088-9453-a1923f573b29", 15, out var connectedType);
            var name = Text(node, 14); if (name.Length == 0) name = Text(node, 2);
            nodes.Add((address, name, percent, connectedType == 17 && connected?.Length == 1 ? connected[0] != 0 : null, root));
        }
        var result = nodes.GroupBy(n => n.Address).Select(group =>
        {
            var root = group.OrderByDescending(n => n.Root).First();
            var reported = group.Where(n => n.Percent != null).OrderByDescending(n => n.Connected == true).FirstOrDefault();
            bool? connected = group.Any(n => n.Connected == true) ? true : group.Any(n => n.Connected == false) ? false : null;
            return new BatteryDevice("bt:" + group.Key, string.IsNullOrWhiteSpace(root.Name) ? "蓝牙设备" : root.Name, reported.Percent, "", connected, Kind(root.Name), "Windows 蓝牙报告");
        }).ToList();
        for (uint i = 0; i < 4; i++)
        {
            if (XInputGetBatteryInformation(i, 0, out var battery) != 0 || battery.Type is 0 or 1) continue;
            var level = battery.Type is 2 or 3 && battery.Level <= 3 ? new[] { "电量耗尽", "低电量", "中等", "充足" }[battery.Level] : "未报告";
            var controllers = result.Where(d => d.Kind == "gamepad").ToList();
            if (controllers.Count == 1 && i == 0) { var existing = controllers[0]; result[result.IndexOf(existing)] = existing with { Connected = true, Level = existing.Percent == null ? level : "", Source = existing.Percent == null ? "XInput 电量档位" : existing.Source }; }
            else result.Add(new("xinput:" + i, "无线手柄 · 玩家 " + (i + 1), null, level, true, "gamepad", "XInput 电量档位"));
        }
        try { result.AddRange(RapooBattery.Read()); }
        catch (Exception error) { SettingsStore.Log(error); }
        return result.OrderByDescending(d => d.Connected == true).ThenBy(d => d.Name).ToList();
    }
}
