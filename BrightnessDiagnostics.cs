using Switchboard.Display;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Switchboard;

internal static class BrightnessDiagnostics
{
    public static async Task Run(string output, bool writeTest)
    {
        using var service = new BrightnessService();
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        Check(BrightnessService.Native(25, 20, 180) == 60 && BrightnessService.Percent(60, 20, 180) == 25, "Native brightness range scaling");
        Check(BrightnessService.Native(-1, 0, 255) == 0 && BrightnessService.Native(101, 0, 255) == 255, "Brightness values stay within hardware range");
        Check(WmiBrightness.Normalize(@"\\?\DISPLAY#BOE0B68#5&bc44db7&1&UID4355#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}") == WmiBrightness.Normalize(@"DISPLAY\BOE0B68\5&bc44db7&1&UID4355_0"), "WMI and physical monitor identifiers match");
        var monitors = await service.DiscoverAsync().ConfigureAwait(false);
        Check(monitors.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == monitors.Count, "Per-monitor identifiers are unique");
        var roundTrips = new List<BrightnessRoundTrip>();
        if (writeTest)
            foreach (var monitor in monitors.Where(m => m.CanControl))
            {
                var result = await service.TestAsync(monitor.Id).ConfigureAwait(false);
                roundTrips.Add(result);
                Check(result.Original == result.Restored, "Restored brightness: " + monitor.Name);
                Check(result.OtherMonitorsUnchanged, "Isolated brightness control: " + monitor.Name);
            }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { At = DateTimeOffset.Now, Checks = checks, Monitors = monitors, RoundTrips = roundTrips, service.WmiError }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
