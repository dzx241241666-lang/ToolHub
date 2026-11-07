using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Switchboard.Services;

public sealed record HotkeyBinding(int Id, string Chord, bool Repeat = false);
public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly HashSet<int> _registered = new();
    public event Action<int>? Pressed;
    public HotkeyService(IntPtr handle) { _source = HwndSource.FromHwnd(handle); _source.AddHook(Hook); }
    public List<string> Register(IEnumerable<HotkeyBinding> bindings)
    {
        Clear();
        var errors = new List<string>();
        foreach (var binding in bindings)
        {
            try
            {
                var (modifiers, key) = Parse(binding.Chord);
                if (RegisterHotKey(_source.Handle, binding.Id, modifiers | (binding.Repeat ? 0u : 0x4000u), key)) _registered.Add(binding.Id);
                else errors.Add(binding.Chord + " 已被占用");
            }
            catch { errors.Add(binding.Chord + " 无效"); }
        }
        return errors;
    }
    public static (uint Modifiers, uint Key) Parse(string chord)
    {
        var pieces = (chord ?? "").Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length < 2) throw new ArgumentException();
        uint modifiers = 0;
        foreach (var piece in pieces[..^1]) modifiers |= piece.ToLowerInvariant() switch { "alt" => 1u, "ctrl" or "control" => 2u, "shift" => 4u, "win" => 8u, _ => throw new ArgumentException() };
        if ((modifiers & 11) == 0) throw new ArgumentException();
        var last = pieces[^1];
        var key = last.Length == 1 && char.IsDigit(last[0]) ? (Key)((int)Key.D0 + last[0] - '0') : Enum.Parse<Key>(last, true);
        if (key is Key.None or Key.System or Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) throw new ArgumentException();
        return (modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
    }
    public static string? Capture(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return null;
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & (ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Windows)) == 0) return null;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key >= Key.D0 && key <= Key.D9 ? ((int)key - (int)Key.D0).ToString() : key.ToString());
        return string.Join('+', parts);
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    {
        if (message == 0x0312) { handled = true; Pressed?.Invoke(wparam.ToInt32()); }
        return IntPtr.Zero;
    }
    public void Clear() { foreach (var id in _registered) UnregisterHotKey(_source.Handle, id); _registered.Clear(); }
    public void Dispose() { Clear(); _source.RemoveHook(Hook); }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
}
