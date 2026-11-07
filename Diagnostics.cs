using Switchboard.Audio;
using Switchboard.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Switchboard;

internal static class Diagnostics
{
    public static void Run(string path, bool smoke)
    {
        using var audio = new AudioEngine();
        var checks = new List<string>();
        var devices = audio.Devices();
        var sessions = audio.Sessions();
        checks.Add("Core Audio enumeration");
        if (HotkeyService.Parse("Alt+1") != (1u, 49u) || HotkeyService.Parse("Ctrl+Alt+Right") != (3u, 39u)) throw new Exception("Hotkey parser");
        checks.Add("Hotkey parser");
        var family = ForegroundTracker.Family((uint)Environment.ProcessId);
        if (!family.Contains((uint)Environment.ProcessId)) throw new Exception("Process family");
        checks.Add("Process family");
        var originalPolicies = Enumerable.Range(0, 3).Select(role => audio.ProcessDevice((uint)Environment.ProcessId, role)).ToArray();
        var originalDefaults = Enumerable.Range(0, 3).Select(role => audio.DefaultId(role)).ToArray();
        checks.Add("Per-application policy read");
        if (smoke && devices.Count > 0)
        {
            var id = devices[0].Id;
            var volume = audio.Volume(id);
            try
            {
                var expected = volume.Level == 100 ? 99 : volume.Level + 1;
                audio.SetVolume(id, expected);
                if (audio.Volume(id).Level != expected) throw new Exception("Endpoint volume readback");
                audio.SetMute(id, !volume.Muted);
                if (audio.Volume(id).Muted == volume.Muted) throw new Exception("Endpoint mute readback");
                checks.Add("Endpoint volume and mute round trip");
            }
            finally { audio.SetVolume(id, volume.Level); audio.SetMute(id, volume.Muted); }
            // Uses this diagnostic process only, restoring its prior policy afterwards.
            try
            {
                audio.RouteProcess((uint)Environment.ProcessId, id);
                if (!string.Equals(audio.ProcessDevice((uint)Environment.ProcessId), id, StringComparison.OrdinalIgnoreCase)) throw new Exception("Process route readback");
                checks.Add("Per-application route round trip");
            }
            finally { for (var role = 0; role < 3; role++) audio.RouteProcess((uint)Environment.ProcessId, originalPolicies[role], role); }
            try
            {
                var other = devices.Last().Id;
                audio.RouteSystem(other);
                if (Enumerable.Range(0, 3).Any(role => audio.DefaultId(role) != other)) throw new Exception("System default roles readback");
                checks.Add("System default all three roles round trip");
            }
            finally { for (var role = 0; role < 3; role++) if (originalDefaults[role] is string saved) audio.RouteSystem(saved, role); }
        }
        var result = new { At = DateTimeOffset.Now, OS = Environment.OSVersion.VersionString, Checks = checks, Devices = devices.Select(d => new { d.Id, d.Name, d.IsDefault, d.Volume }), Default = audio.DefaultId(), Sessions = sessions, OriginalPolicies = originalPolicies, OriginalDefaults = originalDefaults };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
