using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Switchboard.Audio;

// Equalizer APO remains a separate, optional system component. ToolHub only writes
// its documented configuration format; it never replaces a device's APO registry data.
internal sealed class VolumeBoost
{
    public const int Maximum = 200;
    internal const string Header = "# ToolHub volume control v1";
    internal const string FileName = "toolhub-volume.txt";
    internal const string Begin = "# ToolHub volume begin";
    internal const string End = "# ToolHub volume end";
    public const string InstallerSha256 = "7403be7427bbe1936a40dded082829b6e217fc4f5990fee5cba501f0ae055afa";
    private const string InstallerUrl = "https://downloads.sourceforge.net/project/equalizerapo/1.4.2/EqualizerAPO-x64-1.4.2.exe";
    private const string PostMixClsid = "{ec1cc9ce-faed-4822-828a-82a81a6f018f}";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static string? _componentDirectory;
    private static string ComponentDirectory
    {
        get
        {
            if (_componentDirectory != null) return _componentDirectory;
            var preferred = Path.Combine(AppContext.BaseDirectory, "components");
            try { Directory.CreateDirectory(preferred); return _componentDirectory = preferred; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                var fallback = Path.Combine(SettingsStore.DirectoryPath, "components");
                Directory.CreateDirectory(fallback); return _componentDirectory = fallback;
            }
        }
    }
    public static string CachedInstaller => Path.Combine(ComponentDirectory, "EqualizerAPO-x64-1.4.2.exe");
    private static string PendingInstall => Path.Combine(ComponentDirectory, "toolhub-install-pending.txt");
    private static readonly Regex Endpoint = new(@"^\{0\.0\.0\.[0-9a-f]{8}\}\.(\{[0-9a-f-]{36}\})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string DeviceGuid(string endpoint)
    {
        var match = Endpoint.Match(endpoint);
        if (!match.Success || !Guid.TryParseExact(match.Groups[1].Value, "B", out var guid)) throw new ArgumentException("无效的输出设备");
        return guid.ToString("B");
    }
    public static double Decibels(int percent) => 20 * Math.Log10(Math.Clamp(percent, 100, Maximum) / 100d);
    public static string? InstallPath
    {
        get { using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO"); return key?.GetValue("InstallPath") as string; }
    }
    public static string? ConfigPath
    {
        get { using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO"); return key?.GetValue("ConfigPath") as string; }
    }
    public static string Availability(string? endpoint)
    {
        try
        {
            var install = InstallPath; var config = ConfigPath;
            if (install == null || config == null || !File.Exists(Path.Combine(install, "EqualizerAPO.dll"))) return "增强组件未安装";
            if (endpoint == null) return "没有输出设备";
            using var fx = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\" + DeviceGuid(endpoint) + @"\FxProperties");
            if (fx == null) return "此设备未启用增强";
            if (fx.GetValue("{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5") is int disabled && disabled != 0) return "此设备的音频增强已关闭";
            var installed = new[] { 2, 6, 7 }.Any(index => string.Equals(fx.GetValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}," + index) as string, PostMixClsid, StringComparison.OrdinalIgnoreCase));
            if (!installed) return "此设备未启用增强";
            return "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { return "增强组件不可用"; }
    }
    public bool Ready(string? endpoint) => Availability(endpoint).Length == 0;
    public int Level(string endpoint)
    {
        var path = ConfigPath;
        if (path == null) return 100;
        var level = ReadLevels(path).GetValueOrDefault(DeviceGuid(endpoint), 100);
        if (level > 100)
        {
            var main = Path.Combine(path, "config.txt");
            if (!File.Exists(main)) return 100;
            var text = ReadText(main);
            if (text != Attach(text)) return 100;
        }
        return level;
    }
    public void SetLevel(string endpoint, int percent)
    {
        if (percent > 100 && !Ready(endpoint)) throw new InvalidOperationException(Availability(endpoint));
        var path = ConfigPath;
        if (path == null) { if (percent > 100) throw new InvalidOperationException("增强组件未安装"); return; }
        if (File.Exists(PendingInstall)) Prepare();
        var levels = ReadLevels(path);
        var guid = DeviceGuid(endpoint);
        if (percent <= 100) levels.Remove(guid); else levels[guid] = Math.Clamp(percent, 100, Maximum);
        SaveLevels(path, levels);
    }
    public void Reset()
    {
        var path = ConfigPath;
        if (path != null && File.Exists(Path.Combine(path, FileName))) SaveLevels(path, new());
    }
    public static void Prepare()
    {
        var config = ConfigPath ?? throw new InvalidOperationException("增强组件尚未安装");
        if (File.Exists(PendingInstall) && File.ReadAllText(PendingInstall).Trim() == InstallerSha256)
        {
            var main = Path.Combine(config, "config.txt");
            // Only neutralize the exact sample shipped by our newly requested
            // installation. Existing custom equalizers are always retained.
            if (File.Exists(main) && ReadText(main).Replace("\r\n", "\n").Trim() == "Preamp: -6 dB\nInclude: example.txt")
            {
                if (!File.Exists(main + ".toolhub-original")) File.Copy(main, main + ".toolhub-original", false);
                AtomicWrite(main, "# ToolHub: neutral initial configuration\n");
            }
        }
        SaveLevels(config, ReadLevels(config));
        if (File.Exists(PendingInstall)) File.Delete(PendingInstall);
    }
    internal static Dictionary<string, int> ReadLevels(string directory)
    {
        var file = Path.Combine(directory, FileName);
        if (!File.Exists(file)) return new(StringComparer.OrdinalIgnoreCase);
        var text = ReadText(file);
        if (!text.StartsWith(Header + "\n", StringComparison.Ordinal) && !text.StartsWith(Header + "\r\n", StringComparison.Ordinal)) throw new InvalidDataException("增强配置文件已被其他程序修改");
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("# level ", StringComparison.Ordinal)))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4 || !Guid.TryParseExact(parts[2], "B", out var guid) || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var level) || level <= 100 || level > Maximum || !levels.TryAdd(guid.ToString("B"), level)) throw new InvalidDataException("增强配置无效");
        }
        if (Render(levels).Replace("\r\n", "\n").TrimEnd() != text.Replace("\r\n", "\n").TrimEnd()) throw new InvalidDataException("增强配置文件已被其他程序修改");
        return levels;
    }
    internal static string Render(IReadOnlyDictionary<string, int> levels)
    {
        var text = new StringBuilder(Header + "\n");
        foreach (var pair in levels.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!Guid.TryParseExact(pair.Key, "B", out var guid) || pair.Value <= 100 || pair.Value > Maximum) throw new InvalidDataException("增强配置无效");
            text.Append("# level ").Append(guid.ToString("B")).Append(' ').Append(pair.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.Append("Device: ").Append(guid.ToString("B")).Append("\nStage: post-mix\nChannel: all\nPreamp: ").Append(Decibels(pair.Value).ToString("0.000000000", CultureInfo.InvariantCulture)).Append(" dB\n");
        }
        text.Append("Device: all\nStage: post-mix\nChannel: all\n");
        return text.ToString();
    }
    internal static string Attach(string text)
    {
        var block = Begin + "\nDevice: all\nStage: post-mix\nChannel: all\nInclude: " + FileName + "\nDevice: all\nStage: post-mix\nChannel: all\n" + End;
        var start = text.IndexOf(Begin, StringComparison.Ordinal);
        var end = text.IndexOf(End, StringComparison.Ordinal);
        if (start < 0 && end < 0)
        {
            if (text.Contains(FileName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("主配置已有同名增强文件引用");
            return text + (text.Length > 0 && !text.EndsWith('\n') ? "\n" : "") + block + "\n";
        }
        if (start < 0 || end < start || text.IndexOf(Begin, start + Begin.Length, StringComparison.Ordinal) >= 0 || text.IndexOf(End, end + End.Length, StringComparison.Ordinal) >= 0) throw new InvalidDataException("主配置中的增强区块不完整");
        return text[..start] + block + text[(end + End.Length)..];
    }
    internal static void SaveLevels(string directory, Dictionary<string, int> levels)
    {
        // Refuse to overwrite a foreign file, and preserve every other existing filter.
        _ = ReadLevels(directory);
        var main = Path.Combine(directory, "config.txt");
        if (!File.Exists(main)) throw new FileNotFoundException("增强主配置不存在", main);
        var previous = ReadText(main);
        var updated = Attach(previous);
        var managed = Path.Combine(directory, FileName);
        var oldManaged = File.Exists(managed) ? ReadText(managed) : null;
        if (previous != updated && !File.Exists(main + ".toolhub-backup")) File.Copy(main, main + ".toolhub-backup", false);
        AtomicWrite(managed, Render(levels));
        try { if (previous != updated) AtomicWrite(main, updated); }
        catch { if (oldManaged != null) AtomicWrite(managed, oldManaged); else File.Delete(managed); throw; }
    }
    private static string ReadText(string file)
    {
        using var reader = new StreamReader(file, Utf8, true);
        return reader.ReadToEnd();
    }
    private static void AtomicWrite(string file, string text)
    {
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text, Utf8); File.Move(temp, file, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static async Task ConfigureAsync(IProgress<string> progress, CancellationToken cancellation = default)
    {
        var install = InstallPath;
        var fresh = install == null || !File.Exists(Path.Combine(install, "DeviceSelector.exe"));
        ProcessStartInfo info;
        if (fresh)
        {
            progress.Report("下载增强组件");
            var installer = CachedInstaller;
            if (!ValidInstaller(installer))
            {
                var temp = installer + "." + Guid.NewGuid().ToString("N") + ".download";
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                    using var response = await http.GetAsync(InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellation);
                    response.EnsureSuccessStatusCode();
                    await using var input = await response.Content.ReadAsStreamAsync(cancellation);
                    await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                    {
                        var buffer = new byte[65536]; long total = 0; int read;
                        while ((read = await input.ReadAsync(buffer, cancellation)) != 0)
                        { total += read; if (total > 64 * 1024 * 1024) throw new InvalidDataException("组件下载大小异常"); await output.WriteAsync(buffer.AsMemory(0, read), cancellation); }
                    }
                    if (!ValidInstaller(temp)) throw new InvalidDataException("增强组件校验失败");
                    File.Move(temp, installer, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            File.WriteAllText(PendingInstall, InstallerSha256, Utf8);
            info = new(installer) { Arguments = "/S", WorkingDirectory = Path.GetDirectoryName(installer)!, UseShellExecute = true, Verb = "runas" };
        }
        else info = new(Path.Combine(install!, "DeviceSelector.exe")) { WorkingDirectory = install!, UseShellExecute = true, Verb = "runas" };
        progress.Report("组件安装与设备选择中");
        Process? launched;
        try { launched = Process.Start(info); }
        catch (Win32Exception error) when (fresh && error.NativeErrorCode == 3)
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "/select,\"" + CachedInstaller + "\"", UseShellExecute = true });
            throw new InvalidOperationException("管理员安装未启动 · 安装包文件夹已打开", error);
        }
        using var process = launched ?? throw new IOException("无法打开增强组件设置");
        await process.WaitForExitAsync(cancellation);
        if (process.ExitCode != 0) throw new InvalidOperationException("增强组件设置未完成");
        Prepare();
        progress.Report("增强组件设置完成");
    }
    public static bool ValidInstaller(string path)
    {
        if (!File.Exists(path)) return false;
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).Equals(InstallerSha256, StringComparison.OrdinalIgnoreCase);
    }
}
