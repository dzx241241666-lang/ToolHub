using System;
using System.Runtime.InteropServices;

namespace Switchboard.Audio;

// IInspectable has six base entries. Both Windows 10 and 11 policy factories
// expose Set/GetPersistedDefaultAudioEndpoint at entries 25/26.
internal sealed class AudioPolicy : IDisposable
{
    private IntPtr _factory;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetEndpoint(IntPtr self, uint process, int flow, int role, IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetEndpoint(IntPtr self, uint process, int flow, int role, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetDefault(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string device, int role);

    private void EnsureFactory()
    {
        if (_factory != IntPtr.Zero) return;
        const string name = "Windows.Media.Internal.AudioPolicyConfig";
        Com.Check(Com.WindowsCreateString(name, (uint)name.Length, out var hstring));
        try
        {
            var iid = new Guid("ab3d4648-e242-459f-b02f-541c70306324");
            var hr = Com.RoGetActivationFactory(hstring, ref iid, out _factory);
            if (hr < 0)
            {
                iid = new Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258");
                Com.Check(Com.RoGetActivationFactory(hstring, ref iid, out _factory));
            }
        }
        finally { Com.WindowsDeleteString(hstring); }
    }
    public void SetProcess(uint pid, string? endpoint, int? onlyRole = null)
    {
        EnsureFactory();
        var hstring = IntPtr.Zero;
        try
        {
            if (!string.IsNullOrEmpty(endpoint))
            {
                var path = @"\\?\SWD#MMDEVAPI#" + endpoint + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
                Com.Check(Com.WindowsCreateString(path, (uint)path.Length, out hstring));
            }
            var set = Com.Method<SetEndpoint>(_factory, 25);
            for (var role = 0; role < 3; role++) if (onlyRole == null || onlyRole == role) Com.Check(set(_factory, pid, 0, role, hstring));
        }
        finally { if (hstring != IntPtr.Zero) Com.WindowsDeleteString(hstring); }
    }
    public string? GetProcess(uint pid, int role = 1)
    {
        EnsureFactory();
        var hr = Com.Method<GetEndpoint>(_factory, 26)(_factory, pid, 0, role, out var hstring);
        // Processes without an audio identity (including some hosted windows)
        // return E_INVALIDARG instead of an empty persisted preference.
        if (hr == unchecked((int)0x80070057) || hr == unchecked((int)0x80070490)) return null;
        Com.Check(hr);
        try
        {
            if (hstring == IntPtr.Zero) return null;
            var pointer = Com.WindowsGetStringRawBuffer(hstring, out var length);
            var path = Marshal.PtrToStringUni(pointer, (int)length);
            if (string.IsNullOrEmpty(path)) return null;
            const string prefix = @"\\?\SWD#MMDEVAPI#";
            const string suffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) path = path[prefix.Length..];
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) path = path[..^suffix.Length];
            return path;
        }
        finally { if (hstring != IntPtr.Zero) Com.WindowsDeleteString(hstring); }
    }
    public void SetSystem(string id, int? onlyRole = null)
    {
        var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"))!)!;
        var unknown = Marshal.GetIUnknownForObject(instance);
        var policy = IntPtr.Zero;
        try
        {
            var iid = new Guid("f8679f50-850a-41cf-9c72-430f290290c8");
            Com.Check(Marshal.QueryInterface(unknown, ref iid, out policy));
            var set = Com.Method<SetDefault>(policy, 13);
            for (var role = 0; role < 3; role++) if (onlyRole == null || onlyRole == role) Com.Check(set(policy, id, role));
        }
        finally { if (policy != IntPtr.Zero) Marshal.Release(policy); Marshal.Release(unknown); Com.Release(instance); }
    }
    public void Dispose() { if (_factory != IntPtr.Zero) { Marshal.Release(_factory); _factory = IntPtr.Zero; } }
}
