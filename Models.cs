using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Switchboard;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Notify(name); return true; }
}

public sealed class DeviceInfo : Observable
{
    public string Id { get; init; } = "";
    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Index { get; set; } = "";
    private bool _isDefault;
    public bool IsDefault { get => _isDefault; set => Set(ref _isDefault, value); }
    private bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    private bool _cycle;
    public bool InCycle { get => _cycle; set => Set(ref _cycle, value); }
    private int _volume;
    public int Volume { get => _volume; set => Set(ref _volume, value); }
    public override string ToString() => Name;
}
public sealed class DeviceChoice
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public override string ToString() => Name;
}
public sealed class Preset
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Shortcut { get; set; } = "";
}
public sealed class AppSettings
{
    public ConversionPreferences Conversion { get; set; } = new();
    public Dictionary<string, WirelessPreference> Wireless { get; set; } = new();
    public SectionPreferences Sections { get; set; } = new();
    public TransferPreferences Transfer { get; set; } = new();
    public ShutdownPreferences Shutdown { get; set; } = new();
    public int Version { get; set; } = 1;
    public bool Initialized { get; set; }
    public bool AllApplications { get; set; }
    public bool HotkeysEnabled { get; set; } = true;
    public bool ShowOverlay { get; set; } = true;
    public int VolumeStep { get; set; } = 2;
    public List<string> ExcludedFromCycle { get; set; } = new();
    public List<Preset> Presets { get; set; } = Enumerable.Range(1, 6).Select(i => new Preset { Shortcut = $"Alt+{i}" }).ToList();
    public string PreviousShortcut { get; set; } = "Alt+Left";
    public string NextShortcut { get; set; } = "Alt+Right";
    public string VolumeUpShortcut { get; set; } = "Alt+Up";
    public string VolumeDownShortcut { get; set; } = "Alt+Down";
}
public sealed class SectionPreferences
{
    public List<int> Order { get; set; } = new() { 0, 1, 2, 3, 5, 6 };
    public bool Conversion { get; set; } = true;
    public bool Wireless { get; set; } = true;
    public bool Audio { get; set; } = true;
    public bool Shutdown { get; set; } = true;
    public bool Brightness { get; set; } = true;
    public bool Transfer { get; set; } = true;
}
public sealed class ConversionPreferences
{
    public int Category { get; set; }
    public int Quality { get; set; } = 90;
    public string Directory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ConvertedFiles");
}
public sealed class WirelessPreference
{
    public bool Hidden { get; set; }
    public string LastKnownName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "device";
    public string Image { get; set; } = "";
}
public sealed class TransferPreferences
{
    public string Name { get; set; } = Environment.MachineName;
    public string ReceiveDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ToolHub");
    public bool Enabled { get; set; }
    public bool CopyToClipboard { get; set; }
}
public sealed class ShutdownPreferences
{
    public bool Force { get; set; }
    public bool Countdown { get; set; } = true;
    public int Hours { get; set; }
    public int Minutes { get; set; } = 30;
    public int Seconds { get; set; }
    public int ClockHour { get; set; } = 23;
    public int ClockMinute { get; set; }
}
public sealed class SettingsStore
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Switchboard");
    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string? Warning { get; private set; }
    public AppSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        try { return Validate(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException()); }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            Warning = "设置文件无法读取";
            try { File.Copy(FilePath, FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
            return new();
        }
    }
    private static AppSettings Validate(AppSettings settings)
    {
        settings.VolumeStep = Math.Clamp(settings.VolumeStep, 1, 20);
        settings.Sections ??= new();
        settings.Sections.Order = (settings.Sections.Order ?? new()).Where(page => page is 0 or 1 or 2 or 3 or 5 or 6)
            .Concat(new[] { 0, 1, 2, 3, 5, 6 }).Distinct().ToList();
        settings.Conversion ??= new();
        settings.Conversion.Category = Math.Clamp(settings.Conversion.Category, 0, 2);
        settings.Conversion.Quality = Math.Clamp(settings.Conversion.Quality, 1, 100);
        if (string.IsNullOrWhiteSpace(settings.Conversion.Directory)) settings.Conversion.Directory = new ConversionPreferences().Directory;
        settings.Wireless ??= new();
        settings.Transfer ??= new();
        if (string.IsNullOrWhiteSpace(settings.Transfer.Name)) settings.Transfer.Name = Environment.MachineName;
        if (settings.Transfer.Name.Length > 64) settings.Transfer.Name = settings.Transfer.Name[..64];
        if (string.IsNullOrWhiteSpace(settings.Transfer.ReceiveDirectory)) settings.Transfer.ReceiveDirectory = new TransferPreferences().ReceiveDirectory;
        settings.Shutdown ??= new();
        settings.Shutdown.Hours = Math.Clamp(settings.Shutdown.Hours, 0, 999);
        settings.Shutdown.Minutes = Math.Clamp(settings.Shutdown.Minutes, 0, 59);
        settings.Shutdown.Seconds = Math.Clamp(settings.Shutdown.Seconds, 0, 59);
        settings.Shutdown.ClockHour = Math.Clamp(settings.Shutdown.ClockHour, 0, 23);
        settings.Shutdown.ClockMinute = Math.Clamp(settings.Shutdown.ClockMinute, 0, 59);
        if (settings.Presets == null || settings.Presets.Count != 6 || settings.Presets.Any(p => p == null)) throw new InvalidDataException();
        settings.ExcludedFromCycle ??= new();
        foreach (var p in settings.Presets) { p.DeviceId ??= ""; p.DeviceName ??= ""; p.Shortcut ??= ""; }
        return settings;
    }
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, FilePath, true);
    }
    public static void Log(Exception error)
    {
        try { Directory.CreateDirectory(DirectoryPath); var path = Path.Combine(DirectoryPath, "error.log"); if (File.Exists(path) && new FileInfo(path).Length > 512000) File.Move(path, path + ".old", true); File.AppendAllText(path, $"{DateTime.Now:O} {error}\n"); } catch { }
    }
}
