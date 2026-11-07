using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Switchboard.Wireless;

public sealed record IconPreset(string Key, string Name);
public sealed class BatteryViewModel : Observable
{
    public static IReadOnlyList<IconPreset> Presets { get; } = new[] { new IconPreset("headphones", "耳机"), new("mouse", "鼠标"), new("keyboard", "键盘"), new("gamepad", "手柄"), new("phone", "手机 / 平板"), new("speaker", "音箱"), new("device", "通用"), new("custom", "自定义") };
    public IReadOnlyList<IconPreset> IconOptions => Presets;
    public BatteryDevice Device { get; }
    public WirelessPreference Preference { get; }
    public string Id => Device.Id;
    public string NativeName => Device.Name;
    public string Name { get => string.IsNullOrWhiteSpace(Preference.Name) ? Device.Name : Preference.Name; set { Preference.Name = value[..Math.Min(64, value.Length)]; Notify(); } }
    public string IconKey => Preference.Icon;
    public bool HasBattery => Device.Connected != false && (Device.Percent != null || Device.Level.Length > 0);
    public string Level => Device.Connected == false ? "未连接" : Device.Percent is int percent ? percent + "%" : Device.Level.Length > 0 ? Device.Level : "未报告";
    public double Percent => Device.Connected == false ? 0 : Device.Percent ?? 0;
    public Visibility MeterVisibility => Device.Connected != false && Device.Percent != null ? Visibility.Visible : Visibility.Collapsed;
    public string Status => (Device.Connected == false ? "未连接" : Device.State.Length > 0 ? Device.State : Device.Connected == true ? "已连接" : "连接状态未知") + " · " + Device.Source;
    public ImageSource Icon { get; private set; } = null!;
    public BatteryViewModel(BatteryDevice device, WirelessPreference preference) { Device = device; Preference = preference; UpdateIcon(); }
    public void Choose(string key) { Preference.Icon = key; UpdateIcon(); Notify(nameof(IconKey)); }
    public static string IconDirectory => Path.Combine(SettingsStore.DirectoryPath, "wireless-icons");
    public static BitmapImage LoadImage(string file)
    {
        using (var stream = File.OpenRead(file))
        {
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            if (frame.PixelWidth > 16384 || frame.PixelHeight > 16384 || (long)frame.PixelWidth * frame.PixelHeight > 32_000_000) throw new InvalidOperationException("图片尺寸过大");
        }
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 256; image.UriSource = new Uri(Path.GetFullPath(file)); image.EndInit(); image.Freeze(); return image;
    }
    public void Upload(string file)
    {
        if (new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidOperationException("图标上限 8 MB");
        var image = LoadImage(file);
        Directory.CreateDirectory(IconDirectory);
        var target = Path.Combine(IconDirectory, Guid.NewGuid().ToString("N") + ".png");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(target)) encoder.Save(stream);
        Preference.Image = target; Choose("custom");
    }
    private void UpdateIcon()
    {
        if (Preference.Icon == "custom" && !string.IsNullOrEmpty(Preference.Image))
        {
            try { var path = Path.GetFullPath(Preference.Image); if (path.StartsWith(Path.GetFullPath(IconDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(path)) { Icon = LoadImage(path); Notify(nameof(Icon)); return; } } catch { }
        }
        var shape = Preference.Icon switch
        {
            "headphones" => "M 5,18 L5,13 C5,1 27,1 27,13 L27,18 M5,15 L10,15 L10,26 L5,26 Z M22,15 L27,15 L27,26 L22,26 Z",
            "mouse" => "M8,14 C8,2 24,2 24,14 L24,21 C24,33 8,33 8,21 Z M16,4 L16,15 M8,15 L24,15",
            "keyboard" => "M2,7 L30,7 L30,25 L2,25 Z M6,12 L9,12 M13,12 L16,12 M20,12 L23,12 M26,12 L27,12 M6,17 L9,17 M13,17 L16,17 M20,17 L26,17 M8,21 L24,21",
            "gamepad" => "M9,9 L23,9 C29,9 33,27 27,27 L21,21 L11,21 L5,27 C-1,27 3,9 9,9 Z M7,15 L13,15 M10,12 L10,18 M22,13 L24,13 M25,17 L27,17",
            "phone" => "M8,2 L24,2 L24,30 L8,30 Z M12,6 L20,6 M14,26 L18,26",
            "speaker" => "M7,2 L25,2 L25,30 L7,30 Z M19,8 A3,3 0 1 1 13,8 A3,3 0 1 1 19,8 M22,21 A6,6 0 1 1 10,21 A6,6 0 1 1 22,21",
            _ => "M6,4 L26,4 L26,28 L6,28 Z M11,10 L21,10 M11,16 L21,16 M11,22 L17,22"
        };
        var drawing = new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromRgb(24, 24, 22)), 1.5), Geometry.Parse(shape));
        var source = new DrawingImage(drawing); source.Freeze(); Icon = source; Notify(nameof(Icon));
    }
}
