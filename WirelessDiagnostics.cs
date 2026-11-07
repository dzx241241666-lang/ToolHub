using Switchboard.Wireless;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Switchboard;
internal static class WirelessDiagnostics
{
    public static void Run(string output)
    {
        var checks = new List<string>();
        void Check(bool valid, string label) { if (!valid) throw new Exception(label); checks.Add(label); }
        Check(BatteryService.ParsePercent(3, new byte[] { 0 }) == 0 && BatteryService.ParsePercent(3, new byte[] { 100 }) == 100 && BatteryService.ParsePercent(3, new byte[] { 255 }) == null, "Unknown and invalid battery data never becomes a percentage");
        Check(BatteryService.Address(@"BTHENUM\DEV_AABBCCDDEEFF\node") == "AABBCCDDEEFF" && BatteryService.Address(@"BTHENUM\{service}\7&node&0&AABBCCDDEEFF_C00000000") == "AABBCCDDEEFF", "Bluetooth service and parent nodes share a stable device identity");
        Check(RapooBattery.Parse(Convert.FromHexString("071000B004B004011F00038400")) == 31, "VT3 receiver status decodes the 31% reading verified against A Hub");
        Check(RapooBattery.Parse(Convert.FromHexString("072000B004B004010000038400")) == 0 && RapooBattery.Parse(Convert.FromHexString("072000B004B004016400038400")) == 100, "Rapoo battery endpoints are accepted");
        Check(RapooBattery.Parse(Convert.FromHexString("071000B004B00401FF00038400")) == null && RapooBattery.Parse(Convert.FromHexString("070400B004B004011F00038400")) == null && RapooBattery.Parse(Convert.FromHexString("071000B004B004011F")) == null && RapooBattery.Parse(new byte[] { 7, 0x10, 0 }) == null, "Invalid, truncated and unrelated HID packets never become a battery percentage");
        var stale = new BatteryViewModel(new("test-stale", "雷柏 VT3", 31, "", null, "mouse", "2.4G", "等待更新 · 上次报告 12:00:00"), new());
        Check(stale.Level == "31%" && stale.Status.StartsWith("等待更新"), "A previous report is distinguished from a current reading");
        var offline = new BatteryViewModel(new("test-offline", "离线设备", 81, "", false, "mouse", "test", "上次报告 12:00:00"), new());
        Check(offline.Level == "未连接" && offline.Percent == 0 && offline.MeterVisibility == System.Windows.Visibility.Collapsed && !offline.HasBattery && offline.Status == "未连接 · test", "Disconnected devices hide previous percentages, meters and report timestamps");
        var offlineLevel = new BatteryViewModel(new("test-offline-level", "离线手柄", null, "满电", false, "gamepad", "test"), new());
        Check(offlineLevel.Level == "未连接" && !offlineLevel.HasBattery, "Disconnected controllers hide previous battery tiers");
        var emptyBattery = new BatteryViewModel(new("test-empty", "已连接设备", 0, "", true, "mouse", "test"), new());
        Check(emptyBattery.Level == "0%" && emptyBattery.HasBattery && emptyBattery.MeterVisibility == System.Windows.Visibility.Visible, "A connected empty battery remains visible as zero percent");
        var fake = new BatteryDevice("test", "原始名称", null, "", true, "mouse", "test");
        var model = new BatteryViewModel(fake, new() { Icon = "mouse" });
        Check(model.Level == "未报告", "Missing battery data is explicitly shown");
        foreach (var icon in BatteryViewModel.Presets) { model.Choose(icon.Key); Check(model.Icon.IsFrozen, "Preset icon renders: " + icon.Name); }
        var fixture = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "icon-test-" + Guid.NewGuid().ToString("N") + ".png");
        var pixels = Enumerable.Repeat((byte)255, 16 * 16 * 4).ToArray();
        var image = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(fixture)) encoder.Save(stream);
        string? imported = null;
        try
        {
            model.Name = "我的无线鼠标"; model.Upload(fixture); imported = model.Preference.Image;
            Check(File.Exists(fixture) && imported != fixture && File.Exists(imported), "Custom icons are copied without changing the source file");
            var restored = new BatteryViewModel(fake, JsonSerializer.Deserialize<WirelessPreference>(JsonSerializer.Serialize(model.Preference))!);
            Check(restored.Name == model.Name && restored.IconKey == "custom" && restored.Icon is BitmapImage, "Names and custom icons survive settings serialization");
        }
        finally { if (imported != null && File.Exists(imported)) File.Delete(imported); }
        var devices = BatteryService.Read();
        Check(devices.Select(d => d.Id).Distinct().Count() == devices.Count && devices.All(d => d.Percent == null || d.Percent is >= 0 and <= 100), "Real hardware scan returns unique devices and valid reported levels");
        File.WriteAllText(output, JsonSerializer.Serialize(new { At = DateTimeOffset.Now, Checks = checks, Devices = devices }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
