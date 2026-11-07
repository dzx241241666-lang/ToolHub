using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Switchboard.Display;

internal sealed record WmiPanel(string Instance, int Current, int[] Levels);
internal static class WmiBrightness
{
    public static string Normalize(string instance)
    {
        var text = instance.Trim();
        if (text.StartsWith(@"\\?\", StringComparison.Ordinal)) text = text[4..];
        var parts = text.Split('#');
        if (parts.Length >= 3) text = string.Join('\\', parts.Take(3));
        if (text.StartsWith(@"MONITOR\", StringComparison.OrdinalIgnoreCase)) text = "DISPLAY" + text[7..];
        return Regex.Replace(text, @"_\d+$", "").ToUpperInvariant();
    }
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    private static object Property(object row, string name)
    {
        object? properties = null, property = null;
        try { properties = ((dynamic)row).Properties_; property = ((dynamic)properties).Item(name, 0); return ((dynamic)property).Value; }
        finally { Release(property); Release(properties); }
    }
    private static void SetProperty(object row, string name, object value)
    {
        object? properties = null, property = null;
        try { properties = ((dynamic)row).Properties_; property = ((dynamic)properties).Item(name, 0); ((dynamic)property).Value = value; }
        finally { Release(property); Release(properties); }
    }
    private static T Connected<T>(Func<object, T> action)
    {
        object? locator = null, service = null, security = null;
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!);
            service = ((dynamic)locator!).ConnectServer(".", @"root\wmi");
            security = ((dynamic)service).Security_; ((dynamic)security).ImpersonationLevel = 3;
            return action(service);
        }
        finally { Release(security); Release(service); Release(locator); }
    }
    private static void Query(object service, string query, Action<dynamic> visit)
    {
        object? rows = null;
        try
        {
            rows = ((dynamic)service).ExecQuery(query, "WQL", 0);
            foreach (var row in (IEnumerable)rows) try { visit((dynamic)row); } finally { Release(row); }
        }
        finally { Release(rows); }
    }
    private static string Characters(object? value)
    {
        if (value is not IEnumerable characters) return "";
        var result = new StringBuilder();
        foreach (var item in characters) { var c = Convert.ToUInt16(item); if (c != 0) result.Append((char)c); }
        return result.ToString().Trim();
    }
    public static (List<WmiPanel> Panels, Dictionary<string, string> Names, string? Error) Discover()
    {
        var panels = new List<WmiPanel>(); var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? error = null;
        try
        {
            Connected(service =>
            {
                try { Query(service, "SELECT * FROM WmiMonitorID WHERE Active=TRUE", row => { var name = Characters(Property((object)row, "UserFriendlyName")); if (name.Length > 0) names[Normalize((string)Property((object)row, "InstanceName"))] = name; }); }
                catch (Exception e) { SettingsStore.Log(e); }
                Query(service, "SELECT * FROM WmiMonitorBrightness WHERE Active=TRUE", row =>
                {
                    var levels = ((IEnumerable)Property((object)row, "Level")).Cast<object>().Select(Convert.ToInt32).Distinct().OrderBy(v => v).ToArray();
                    panels.Add(new((string)Property((object)row, "InstanceName"), Convert.ToInt32(Property((object)row, "CurrentBrightness")), levels));
                });
                return 0;
            });
        }
        catch (Exception e) { error = e.Message; SettingsStore.Log(e); }
        return (panels, names, error);
    }
    public static int Read(string instance) => Connected<int>(service =>
    {
        int? current = null;
        Query(service, "SELECT * FROM WmiMonitorBrightness WHERE Active=TRUE", row => { if (string.Equals((string)Property((object)row, "InstanceName"), instance, StringComparison.OrdinalIgnoreCase)) current = Convert.ToInt32(Property((object)row, "CurrentBrightness")); });
        return current ?? throw new InvalidOperationException("显示器已断开");
    });
    public static void Write(string instance, int level) => Connected(service =>
    {
        var found = false;
        Query(service, "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE", row =>
        {
            if (!string.Equals((string)Property((object)row, "InstanceName"), instance, StringComparison.OrdinalIgnoreCase)) return;
            object? methods = null, method = null, signature = null, input = null, output = null;
            try
            {
                methods = row.Methods_; method = ((dynamic)methods).Item("WmiSetBrightness", 0);
                signature = ((dynamic)method).InParameters; input = ((dynamic)signature).SpawnInstance_(0);
                SetProperty(input!, "Timeout", (uint)0); SetProperty(input!, "Brightness", (byte)Math.Clamp(level, 0, 100));
                output = row.ExecMethod_("WmiSetBrightness", input, 0);
                // Some display drivers return no output object. The caller
                // still reads the actual panel brightness after the write.
                var result = output == null ? 0u : Convert.ToUInt32(Property(output, "ReturnValue"));
                if (result != 0) throw new InvalidOperationException("显示器拒绝亮度设置");
                found = true;
            }
            finally { Release(output); Release(input); Release(signature); Release(method); Release(methods); }
        });
        if (!found) throw new InvalidOperationException("显示器已断开");
        return 0;
    });
}
