using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Switchboard.Display;

public sealed record MonitorBrightness(string Id, string Name, string Display, bool Primary, int Width, int Height, bool CanControl, int Percent, string Kind, string Status);
public sealed record BrightnessResult(int Percent, uint NativeValue);
public sealed record BrightnessRoundTrip(string Id, uint Original, uint Tested, uint ReadBack, uint Restored, bool OtherMonitorsUnchanged);

public sealed class BrightnessService : IDisposable
{
    private enum Backend { Unsupported, Wmi, Vcp, HighLevel }
    private sealed class Endpoint : IDisposable
    {
        public required MonitorBrightness Info;
        public Backend Method;
        public PhysicalHandle? Handle;
        public string? Instance;
        public int[] Levels = Array.Empty<int>();
        public uint Minimum, Maximum;
        public void Dispose() => Handle?.Dispose();
    }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Endpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _disposed;
    public string? WmiError { get; private set; }
    public static int Percent(uint value, uint minimum, uint maximum) => maximum <= minimum ? 0 : (int)Math.Clamp(Math.Round(((double)value - minimum) * 100 / (maximum - minimum)), 0, 100);
    public static uint Native(int percent, uint minimum, uint maximum)
    {
        if (maximum <= minimum) throw new ArgumentException("显示器亮度范围无效");
        return (uint)Math.Round(minimum + (maximum - (double)minimum) * Math.Clamp(percent, 0, 100) / 100, MidpointRounding.AwayFromZero);
    }
    private async Task<T> Serialized<T>(Func<T> action)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_disposed, this); return await Task.Run(action).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    public Task<List<MonitorBrightness>> DiscoverAsync() => Serialized(Discover);
    private List<MonitorBrightness> Discover()
    {
        foreach (var endpoint in _endpoints.Values) endpoint.Dispose();
        _endpoints.Clear();
        var wmi = WmiBrightness.Discover(); WmiError = wmi.Error;
        var assignedWmi = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var logical in DisplayNative.Enumerate().OrderBy(m => DisplayNumber(m.Device)))
        {
            var matches = logical.Devices.Select(d => WmiBrightness.Normalize(d.DeviceId)).ToArray();
            var panels = wmi.Panels.Where(p => matches.Contains(WmiBrightness.Normalize(p.Instance), StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var panel in panels)
            {
                var key = WmiBrightness.Normalize(panel.Instance);
                if (!assignedWmi.Add(key)) continue;
                var name = wmi.Names.GetValueOrDefault(key, "内置显示器");
                Add(new Endpoint { Info = Info(key + "/WMI", name, logical, true, panel.Current, "内置屏幕", ""), Method = Backend.Wmi, Instance = panel.Instance, Levels = panel.Levels, Maximum = 100 });
            }
            // Integrated panels use WMI and generally do not implement DDC/CI.
            if (panels.Count > 0 && logical.Devices.Count <= 1) continue;
            var added = 0;
            if (DisplayNative.GetNumberOfPhysicalMonitorsFromHMONITOR(logical.Handle, out var count) && count > 0 && count <= 32)
            {
                var physical = new DisplayNative.PhysicalMonitor[count];
                if (DisplayNative.GetPhysicalMonitorsFromHMONITOR(logical.Handle, count, physical))
                {
                    for (var i = 0; i < physical.Length; i++)
                    {
                        var handle = new PhysicalHandle(physical[i].Handle);
                        var candidate = matches.Length == 1 ? matches[0] : logical.Device + "/" + i;
                        var name = wmi.Names.GetValueOrDefault(candidate, physical[i].Description);
                        if (string.IsNullOrWhiteSpace(name)) name = "外接显示器";
                        var endpoint = new Endpoint { Info = Info(candidate + "/DDC/" + i, name, logical, false, 0, "外接显示器", "不支持亮度调节或 DDC/CI 未开启"), Handle = handle };
                        try
                        {
                            if (DisplayNative.GetVCPFeatureAndVCPFeatureReply(handle, 0x10, out _, out var current, out var maximum) && maximum > 0 && current <= maximum)
                            {
                                endpoint.Method = Backend.Vcp; endpoint.Maximum = maximum;
                                endpoint.Info = endpoint.Info with { CanControl = true, Percent = Percent(current, 0, maximum), Status = "" };
                            }
                            else if (DisplayNative.GetMonitorBrightness(handle, out var minimum, out current, out maximum) && maximum > minimum && current >= minimum && current <= maximum)
                            {
                                endpoint.Method = Backend.HighLevel; endpoint.Minimum = minimum; endpoint.Maximum = maximum;
                                endpoint.Info = endpoint.Info with { CanControl = true, Percent = Percent(current, minimum, maximum), Status = "" };
                            }
                            if (panels.Count > 0 && endpoint.Method == Backend.Unsupported) { endpoint.Dispose(); continue; }
                            Add(endpoint); added++;
                        }
                        catch { endpoint.Dispose(); throw; }
                    }
                }
            }
            if (added == 0 && panels.Count == 0)
            {
                var candidate = matches.FirstOrDefault() ?? logical.Device;
                Add(new Endpoint { Info = Info(candidate + "/NONE", wmi.Names.GetValueOrDefault(candidate, "显示器 " + DisplayNumber(logical.Device)), logical, false, 0, "显示器", "不支持亮度调节或 DDC/CI 未开启") });
            }
        }
        foreach (var panel in wmi.Panels)
        {
            var key = WmiBrightness.Normalize(panel.Instance);
            if (assignedWmi.Contains(key)) continue;
            // Retains independently controllable panels when a display driver
            // does not expose a corresponding monitor interface path.
            Add(new Endpoint { Info = new(key + "/WMI", wmi.Names.GetValueOrDefault(key, "内置显示器"), "", false, 0, 0, true, panel.Current, "内置屏幕", ""), Method = Backend.Wmi, Instance = panel.Instance, Levels = panel.Levels, Maximum = 100 });
        }
        return _endpoints.Values.Select(e => e.Info).ToList();
    }
    private void Add(Endpoint endpoint)
    {
        if (_endpoints.TryGetValue(endpoint.Info.Id, out var previous)) previous.Dispose();
        _endpoints[endpoint.Info.Id] = endpoint;
    }
    private static int DisplayNumber(string name) => int.TryParse(new string(name.Where(char.IsDigit).ToArray()), out var number) ? number : 0;
    private static MonitorBrightness Info(string id, string name, DisplayNative.LogicalMonitor monitor, bool supported, int percent, string kind, string status) => new(id, name, monitor.Device.Replace(@"\\.\", ""), monitor.Primary, monitor.Width, monitor.Height, supported, percent, kind, status);
    private Endpoint Find(string id) => _endpoints.TryGetValue(id, out var endpoint) && endpoint.Method != Backend.Unsupported ? endpoint : throw new InvalidOperationException("显示器已断开或不支持亮度调节");
    private static uint Read(Endpoint endpoint)
    {
        if (endpoint.Method == Backend.Wmi) return (uint)WmiBrightness.Read(endpoint.Instance!);
        if (endpoint.Method == Backend.Vcp && DisplayNative.GetVCPFeatureAndVCPFeatureReply(endpoint.Handle!, 0x10, out _, out var value, out _)) return value;
        if (endpoint.Method == Backend.HighLevel && DisplayNative.GetMonitorBrightness(endpoint.Handle!, out _, out value, out _)) return value;
        throw new InvalidOperationException("显示器未响应，连接或亮度控制可能已变化");
    }
    private static void Write(Endpoint endpoint, uint value)
    {
        if (endpoint.Method == Backend.Wmi) { WmiBrightness.Write(endpoint.Instance!, (int)value); return; }
        var success = endpoint.Method == Backend.Vcp ? DisplayNative.SetVCPFeature(endpoint.Handle!, 0x10, value) : DisplayNative.SetMonitorBrightness(endpoint.Handle!, value);
        if (!success) throw new InvalidOperationException("显示器未响应，亮度未能设置");
    }
    private static uint ReadAfterWrite(Endpoint endpoint, uint expected)
    {
        var value = Read(endpoint);
        for (var attempt = 0; value != expected && attempt < 4; attempt++)
        {
            Thread.Sleep(60);
            value = Read(endpoint);
        }
        return value;
    }
    public Task<BrightnessResult> SetAsync(string id, int percent) => Serialized(() =>
    {
        var endpoint = Find(id);
        var native = Native(percent, endpoint.Minimum, endpoint.Maximum);
        if (endpoint.Method == Backend.Wmi && endpoint.Levels.Length > 0) native = (uint)endpoint.Levels.MinBy(level => Math.Abs(level - (int)native));
        Write(endpoint, native);
        var actual = ReadAfterWrite(endpoint, native);
        var level = Percent(actual, endpoint.Minimum, endpoint.Maximum);
        endpoint.Info = endpoint.Info with { Percent = level };
        return new BrightnessResult(level, actual);
    });
    public Task<List<MonitorBrightness>> ReadAsync() => Serialized(() =>
    {
        foreach (var endpoint in _endpoints.Values.Where(e => e.Method != Backend.Unsupported))
        {
            try { endpoint.Info = endpoint.Info with { Percent = Percent(Read(endpoint), endpoint.Minimum, endpoint.Maximum), Status = "" }; }
            catch (Exception e) { endpoint.Info = endpoint.Info with { Status = e.Message }; }
        }
        return _endpoints.Values.Select(e => e.Info).ToList();
    });
    internal Task<BrightnessRoundTrip> TestAsync(string id) => Serialized(() =>
    {
        var endpoint = Find(id);
        var others = _endpoints.Values.Where(e => e != endpoint && e.Method != Backend.Unsupported).ToDictionary(e => e.Info.Id, Read);
        var original = Read(endpoint);
        var tested = original < endpoint.Maximum ? original + 1 : Math.Max(endpoint.Minimum, original - 1);
        if (endpoint.Method == Backend.Wmi && endpoint.Levels.Length > 1)
        {
            var nearest = Array.FindIndex(endpoint.Levels, level => level >= original);
            if (nearest < 0) nearest = endpoint.Levels.Length - 1;
            tested = (uint)endpoint.Levels[nearest < endpoint.Levels.Length - 1 ? nearest + 1 : nearest - 1];
        }
        uint readback;
        uint restored = original;
        try { Write(endpoint, tested); readback = ReadAfterWrite(endpoint, tested); }
        finally { Write(endpoint, original); restored = ReadAfterWrite(endpoint, original); }
        if (readback != tested || restored != original) throw new InvalidOperationException($"亮度回读不一致 · {endpoint.Info.Name}: 原值 {original}, 测试 {tested}, 回读 {readback}, 恢复 {restored}");
        var unchanged = others.All(pair => Read(Find(pair.Key)) == pair.Value);
        return new BrightnessRoundTrip(id, original, tested, readback, restored, unchanged);
    });
    public void Dispose()
    {
        _disposed = true;
        _ = Task.Run(async () => { await _gate.WaitAsync().ConfigureAwait(false); try { foreach (var endpoint in _endpoints.Values) endpoint.Dispose(); _endpoints.Clear(); } finally { _gate.Release(); } });
    }
}
