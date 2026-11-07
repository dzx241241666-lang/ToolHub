using Switchboard.Audio;
using System.Media;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

// A real, silent WaveOut audio session for end-to-end foreground hotkey tests.
// Closing restores this process's original audio preferences in all three roles.
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var audio = new AudioEngine();
        var pid = (uint)Environment.ProcessId;
        var saved = Enumerable.Range(0, 3).Select(role => audio.ProcessDevice(pid, role)).ToArray();
        using var stream = SilentWave();
        using var player = new SoundPlayer(stream);
        var output = args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "probe.json");
        var text = new TextBlock { Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap, FontSize = 16 };
        var window = new Window { Title = "Switchboard 音频测试", Width = 520, Height = 220, Content = text, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var app = new System.Windows.Application();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) =>
        {
            var route = audio.ProcessDevice(pid);
            var sessions = audio.Sessions().Where(s => s.ProcessId == pid).ToArray();
            var levels = audio.SessionVolumes(new HashSet<uint> { pid });
            var state = new { ProcessId = pid, Route = route, Sessions = sessions, Levels = levels, Default = audio.DefaultId(), At = DateTimeOffset.Now };
            File.WriteAllText(output, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            text.Text = $"静音音频会话\n\n设备：{audio.Devices().FirstOrDefault(d => d.Id == (route ?? audio.DefaultId()))?.Name}\n应用音量：{string.Join(", ", levels.Select(v => Math.Round(v.Level * 100) + "%"))}";
        };
        window.Loaded += (_, _) => { player.PlayLooping(); timer.Start(); };
        window.Closed += (_, _) => { timer.Stop(); player.Stop(); for (var role = 0; role < 3; role++) audio.RouteProcess(pid, saved[role], role); };
        try { app.Run(window); }
        finally { for (var role = 0; role < 3; role++) audio.RouteProcess(pid, saved[role], role); }
    }
    private static MemoryStream SilentWave()
    {
        const int rate = 44100;
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, true))
        {
            writer.Write("RIFF"u8); writer.Write(36 + rate * 2); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(rate * 2); writer.Write(new byte[rate * 2]);
        }
        stream.Position = 0; return stream;
    }
}
