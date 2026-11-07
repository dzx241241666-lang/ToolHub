using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Switchboard.Audio;

public sealed record SessionInfo(uint ProcessId, string DeviceId, int State);
public sealed record SessionVolume(string InstanceId, float Level, bool Muted);
public sealed class AudioEngine : IDisposable
{
    private IMMDeviceEnumerator? _enumerator;
    private IMMDeviceEnumerator Enumerator => _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumerator();
    private readonly AudioPolicy _policy = new();
    public string? DefaultId(int role = 1)
    {
        if (Enumerator.GetDefaultAudioEndpoint(0, role, out var device) < 0) return null;
        try { Com.Check(device.GetId(out var id)); return id; } finally { Com.Release(device); }
    }
    public List<DeviceInfo> Devices()
    {
        var result = new List<DeviceInfo>();
        var defaultId = DefaultId();
        Com.Check(Enumerator.EnumAudioEndpoints(0, 1, out var collection));
        try
        {
            Com.Check(collection.GetCount(out var count));
            for (uint i = 0; i < count; i++)
            {
                Com.Check(collection.Item(i, out var device));
                try
                {
                    Com.Check(device.GetId(out var id));
                    var name = id;
                    Com.Check(device.OpenPropertyStore(0, out var store));
                    try
                    {
                        var key = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
                        Com.Check(store.GetValue(ref key, out var value));
                        try { if (value.Type == 31 && value.Pointer != IntPtr.Zero) name = Marshal.PtrToStringUni(value.Pointer) ?? id; }
                        finally { Com.PropVariantClear(ref value); }
                    }
                    finally { Com.Release(store); }
                    var volume = WithEndpoint(device, v => { Com.Check(v.GetMasterVolumeLevelScalar(out var level)); return (int)Math.Round(level * 100); });
                    result.Add(new() { Id = id, Name = name, IsDefault = id == defaultId, Volume = volume });
                }
                catch (COMException) { /* A device may disappear during enumeration. */ }
                finally { Com.Release(device); }
            }
        }
        finally { Com.Release(collection); }
        return result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name).ToList();
    }
    private static T WithEndpoint<T>(IMMDevice device, Func<IAudioEndpointVolume, T> action)
    {
        var iid = typeof(IAudioEndpointVolume).GUID;
        Com.Check(device.Activate(ref iid, 23, IntPtr.Zero, out var instance));
        try { return action((IAudioEndpointVolume)instance); } finally { Com.Release(instance); }
    }
    private T WithEndpoint<T>(string id, Func<IAudioEndpointVolume, T> action)
    {
        Com.Check(Enumerator.GetDevice(id, out var device));
        try { return WithEndpoint(device, action); } finally { Com.Release(device); }
    }
    public (int Level, bool Muted) Volume(string id) => WithEndpoint(id, v => { Com.Check(v.GetMasterVolumeLevelScalar(out var level)); Com.Check(v.GetMute(out var mute)); return ((int)Math.Round(level * 100), mute); });
    public void SetVolume(string id, int level) => WithEndpoint(id, v => { var context = Guid.Empty; Com.Check(v.SetMasterVolumeLevelScalar(Math.Clamp(level, 0, 100) / 100f, ref context)); return 0; });
    public void SetMute(string id, bool muted) => WithEndpoint(id, v => { var context = Guid.Empty; Com.Check(v.SetMute(muted, ref context)); return 0; });
    public List<SessionInfo> Sessions()
    {
        var result = new List<SessionInfo>();
        foreach (var device in Devices())
        {
            try { VisitSessions(device.Id, (session, state, pid) => { if (pid != 0 && state != 2) result.Add(new(pid, device.Id, state)); }); }
            catch (COMException) { }
        }
        return result;
    }
    private void VisitSessions(string id, Action<IAudioSessionControl2, int, uint> action)
    {
        Com.Check(Enumerator.GetDevice(id, out var device));
        object? manager = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            var iid = typeof(IAudioSessionManager2).GUID;
            Com.Check(device.Activate(ref iid, 23, IntPtr.Zero, out manager));
            Com.Check(((IAudioSessionManager2)manager).GetSessionEnumerator(out sessions));
            Com.Check(sessions.GetCount(out var count));
            for (var i = 0; i < count; i++)
            {
                Com.Check(sessions.GetSession(i, out var session));
                try { Com.Check(session.GetProcessId(out var pid)); Com.Check(session.GetState(out var state)); action(session, state, pid); }
                finally { Com.Release(session); }
            }
        }
        finally { Com.Release(sessions); Com.Release(manager); Com.Release(device); }
    }
    public string? ProcessDevice(uint pid, int role = 1) => _policy.GetProcess(pid, role);
    public void RouteProcess(uint pid, string? id, int? onlyRole = null) => _policy.SetProcess(pid, id, onlyRole);
    public void RouteSystem(string id, int? onlyRole = null) => _policy.SetSystem(id, onlyRole);
    public int AdjustSessions(IReadOnlySet<uint> pids, int delta)
    {
        int total = 0, count = 0;
        foreach (var device in Devices()) VisitSessions(device.Id, (session, state, pid) =>
        {
            if (!pids.Contains(pid) || state == 2) return;
            var volume = (ISimpleAudioVolume)session;
            Com.Check(volume.GetMasterVolume(out var current));
            var level = Math.Clamp((int)Math.Round(current * 100) + delta, 0, 100);
            var context = Guid.Empty;
            Com.Check(volume.SetMasterVolume(level / 100f, ref context));
            if (delta > 0) Com.Check(volume.SetMute(false, ref context));
            total += level; count++;
        });
        if (count == 0) throw new InvalidOperationException("前台应用没有音频会话");
        return total / count;
    }
    public List<SessionVolume> SessionVolumes(IReadOnlySet<uint> pids)
    {
        var result = new List<SessionVolume>();
        foreach (var device in Devices()) VisitSessions(device.Id, (session, state, pid) =>
        {
            if (!pids.Contains(pid) || state == 2) return;
            Com.Check(session.GetSessionInstanceIdentifier(out var instance));
            var volume = (ISimpleAudioVolume)session;
            Com.Check(volume.GetMasterVolume(out var level)); Com.Check(volume.GetMute(out var mute));
            result.Add(new(instance, level, mute));
        });
        return result;
    }
    public void RestoreSessionVolumes(IReadOnlyList<SessionVolume> snapshot)
    {
        foreach (var device in Devices()) VisitSessions(device.Id, (session, state, pid) =>
        {
            Com.Check(session.GetSessionInstanceIdentifier(out var instance));
            var saved = snapshot.FirstOrDefault(s => s.InstanceId == instance);
            if (saved == null) return;
            var volume = (ISimpleAudioVolume)session; var context = Guid.Empty;
            Com.Check(volume.SetMasterVolume(saved.Level, ref context)); Com.Check(volume.SetMute(saved.Muted, ref context));
        });
    }
    public void Dispose() { _policy.Dispose(); Com.Release(_enumerator); _enumerator = null; }
}
