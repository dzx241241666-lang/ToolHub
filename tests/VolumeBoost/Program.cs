using System.Globalization;
using Switchboard.Audio;

var checks = new List<string>();
void Check(bool pass, string name) { if (!pass) throw new Exception(name); checks.Add(name); }
void Reject(Action action, string name) { try { action(); } catch (Exception e) when (e is ArgumentException or InvalidDataException) { checks.Add(name); return; } throw new Exception(name); }
var guid = "{d2faf00c-6fc7-4ae7-a4cf-523d33887585}";
var second = "{ef9f984f-e5db-4f3e-b6b5-c19098a8c543}";
Check(Math.Abs(VolumeBoost.Decibels(200) - 6.020599913) < 1e-8, "200 percent is a real 2x linear gain (+6.0206 dB)");
Check(VolumeBoost.Decibels(100) == 0, "100 percent is unity gain");
Check(VolumeBoost.Decibels(900) == VolumeBoost.Decibels(200), "Gain has an explicit upper bound");
Check(VolumeBoost.IsDefaultSample("Preamp: -6 dB\r\nInclude: example.txt\r\nGraphicEQ: 25 0; 40 0; 63 0; 100 0; 160 0; 250 0; 400 0; 630 0; 1000 0; 1600 0; 2500 0; 4000 0; 6300 0; 10000 0; 16000 0\r\n"), "Fresh upstream sample EQ is recognized before neutral initialization");
Check(!VolumeBoost.IsDefaultSample("Preamp: -5 dB\nInclude: example.txt"), "Personalized EQ is never mistaken for the installer sample");
Check(VolumeBoost.DeviceGuid("{0.0.0.00000000}." + guid) == guid, "Output endpoint matches only its exact GUID");
Reject(() => VolumeBoost.DeviceGuid("{0.0.1.00000000}." + guid), "Capture endpoints cannot be boosted");
Reject(() => VolumeBoost.DeviceGuid("Device: all\nPreamp: 99 dB"), "Endpoint text cannot inject APO commands");
Reject(() => VolumeBoost.Render(new Dictionary<string, int> { ["all"] = 200 }), "A config entry cannot target every device");
var root = Path.Combine(Path.GetTempPath(), "ToolHub-boost-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var main = Path.Combine(root, "config.txt");
    var original = "# Existing personal equalizer\r\nDevice: Other speakers\r\nFilter: ON PK Fc 80 Hz Gain -2 dB Q 1\r\n";
    File.WriteAllText(main, original);
    var oldCulture = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        VolumeBoost.SaveLevels(root, new() { [guid] = 150, [second] = 200 });
        var config = File.ReadAllText(Path.Combine(root, VolumeBoost.FileName));
        Check(config.Contains("Preamp: 3.521825181 dB"), "Decimal formatting is independent of Windows region");
        Check(config.Contains("Stage: post-mix\nChannel: all"), "Gain runs once after mixing on every channel");
    }
    finally { CultureInfo.CurrentCulture = oldCulture; }
    var attached = File.ReadAllText(main);
    Check(attached.StartsWith(original), "Existing EQ settings remain byte-for-byte unchanged as text");
    Check(File.ReadAllText(main + ".toolhub-backup") == original, "Original main configuration is backed up");
    var levels = VolumeBoost.ReadLevels(root);
    Check(levels.Count == 2 && levels[guid] == 150 && levels[second] == 200, "Output devices retain independent amplification");
    VolumeBoost.SaveLevels(root, new() { [guid] = 125 });
    Check(File.ReadAllText(main) == attached, "Repeated adjustments do not duplicate include blocks");
    Check(VolumeBoost.ReadLevels(root).Count == 1, "Removing another device also removes its gain");
    VolumeBoost.SaveLevels(root, new());
    Check(!File.ReadAllText(Path.Combine(root, VolumeBoost.FileName)).Contains("Preamp:"), "Disabling boost removes every amplification filter");
    File.WriteAllText(Path.Combine(root, VolumeBoost.FileName), "# Personal file\nPreamp: 2 dB\n");
    Reject(() => VolumeBoost.SaveLevels(root, new()), "Foreign files are never overwritten");
    Check(File.ReadAllText(main) == attached, "Rejected update preserves the main configuration");
    Reject(() => VolumeBoost.Attach(VolumeBoost.Begin + "\n"), "Incomplete managed blocks are rejected");
    Reject(() => VolumeBoost.Attach("Include: toolhub-volume.txt\n"), "Foreign include references are preserved");
}
finally { Directory.Delete(root, true); }
if (args.Length == 2 && args[0] == "--engine")
{
    var configDirectory = VolumeBoost.ConfigPath ?? throw new Exception("Equalizer APO is not installed");
    var benchmark = Path.Combine(args[1], "Benchmark.exe");
    var directory = Path.Combine(Path.GetTempPath(), "ToolHub-boost-engine-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var saved = VolumeBoost.ReadLevels(configDirectory);
    var fakeDevice = Guid.NewGuid().ToString("B");
    try
    {
        var input = Path.Combine(directory, "input.wav");
        using (var writer = new BinaryWriter(File.Create(input)))
        {
            writer.Write("RIFF"u8); writer.Write(36 + 48000 * 4); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((short)1); writer.Write((short)2); writer.Write(48000); writer.Write(192000); writer.Write((short)4); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(48000 * 4);
            for (var frame = 0; frame < 48000; frame++) { var sample = (short)Math.Round(1000 * Math.Sin(frame * 2 * Math.PI * 1000 / 48000)); writer.Write(sample); writer.Write(sample); }
        }
        void Run(string output)
        {
            var start = new System.Diagnostics.ProcessStartInfo(benchmark) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = args[1] };
            foreach (var argument in new[] { "--guid", fakeDevice, "-i", input, "-o", output, "--nopause" }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start) ?? throw new Exception("Cannot launch Equalizer APO benchmark");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000)) { process.Kill(); throw new Exception("Equalizer APO benchmark timed out"); }
            if (process.ExitCode != 0) throw new Exception(stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }
        double Rms(string file)
        {
            using var reader = new BinaryReader(File.OpenRead(file));
            if (new string(reader.ReadChars(4)) != "RIFF") throw new Exception("Invalid benchmark output");
            reader.ReadInt32(); reader.ReadBytes(4);
            while (reader.BaseStream.Position < reader.BaseStream.Length - 8)
            {
                var name = new string(reader.ReadChars(4)); var length = reader.ReadInt32();
                if (name == "data") { double sum = 0; for (var i = 0; i < length / 2; i++) { var sample = reader.ReadInt16(); sum += (double)sample * sample; } return Math.Sqrt(sum / (length / 2)); }
                reader.BaseStream.Seek(length + (length & 1), SeekOrigin.Current);
            }
            throw new Exception("Benchmark output has no PCM data");
        }
        var baseline = Path.Combine(directory, "baseline.wav"); var boosted = Path.Combine(directory, "boosted.wav");
        Run(baseline);
        var modified = new Dictionary<string, int>(saved) { [fakeDevice] = 200 };
        VolumeBoost.SaveLevels(configDirectory, modified);
        Run(boosted);
        var ratio = Rms(boosted) / Rms(baseline);
        Check(double.IsFinite(ratio) && Math.Abs(ratio - 2) < .02, $"Real Equalizer APO engine amplifies PCM by 2x (measured {ratio:0.0000}x)");
    }
    finally { VolumeBoost.SaveLevels(configDirectory, saved); Directory.Delete(directory, true); }
}
foreach (var check in checks) Console.WriteLine("PASS " + check);
Console.WriteLine($"{checks.Count} checks passed");

namespace Switchboard
{
    // No installation or machine audio settings are accessed by these config tests.
    internal static class SettingsStore { public static string DirectoryPath => Path.GetTempPath(); }
}
