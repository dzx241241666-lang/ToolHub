using Switchboard.Audio;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace Switchboard;

public partial class MainWindow
{
    private readonly VolumeBoost _volumeBoost = new();
    private bool _boostConfiguring;
    private int DeviceVolumeLimit(string id) => _settings.VolumeBoostEnabled && _volumeBoost.Ready(id) ? VolumeBoost.Maximum : 100;
    private int DisplayVolume(string id, int nativeLevel)
    {
        if (!_settings.VolumeBoostEnabled || !_volumeBoost.Ready(id)) return nativeLevel;
        var level = _volumeBoost.Level(id);
        // An external volume decrease also removes our extra gain. The next volume
        // increase must not unexpectedly bring back a previous amplification.
        if (nativeLevel < 100 && level > 100) { _volumeBoost.SetLevel(id, 100); return nativeLevel; }
        return nativeLevel == 100 ? level : nativeLevel;
    }
    private void SetDeviceVolume(string id, int level)
    {
        level = Math.Clamp(level, 0, DeviceVolumeLimit(id));
        if (!_settings.VolumeBoostEnabled) { _audio.SetVolume(id, level); return; }
        var before = _audio.Volume(id);
        var oldGain = _volumeBoost.Level(id);
        try
        {
            if (level > 100 || oldGain > 100) _volumeBoost.SetLevel(id, level);
            _audio.SetVolume(id, Math.Min(level, 100));
        }
        catch
        {
            try { _volumeBoost.SetLevel(id, oldGain); _audio.SetVolume(id, before.Level); } catch (Exception error) { SettingsStore.Log(error); }
            throw;
        }
    }
    private void InitializeVolumeBoost()
    {
        VolumeBoostCheck.IsChecked = _settings.VolumeBoostEnabled;
        if (!_settings.VolumeBoostEnabled)
        {
            try { _volumeBoost.Reset(); } catch (Exception error) { SettingsStore.Log(error); }
        }
        UpdateVolumeBoost();
    }
    private void UpdateVolumeBoost()
    {
        var unavailable = VolumeBoost.Availability(_activeDevice);
        VolumeBoostCheck.IsEnabled = !_boostConfiguring && unavailable.Length == 0;
        VolumeBoostSetup.IsEnabled = !_boostConfiguring;
        VolumeBoostSetup.Content = VolumeBoost.InstallPath == null ? "安装组件" : "设备配置";
        VolumeBoostStatus.Text = unavailable.Length > 0 ? unavailable : _settings.VolumeBoostEnabled ? "输出设备 · 最高 200%" : "最高 100%";
        VolumeSlider.Maximum = _activeDevice == null ? 100 : DeviceVolumeLimit(_activeDevice);
    }
    private void VolumeBoostChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing || _updating) return;
        var enabled = VolumeBoostCheck.IsChecked == true;
        try
        {
            if (enabled && !_volumeBoost.Ready(_activeDevice)) throw new InvalidOperationException(VolumeBoost.Availability(_activeDevice));
            if (enabled) VolumeBoost.Prepare();
            if (!enabled) _volumeBoost.Reset();
            _settings.VolumeBoostEnabled = enabled;
            Save(); RefreshVolume();
        }
        catch (Exception error)
        {
            _updating = true;
            try { VolumeBoostCheck.IsChecked = _settings.VolumeBoostEnabled; } finally { _updating = false; }
            Report(error, false);
        }
    }
    private async void VolumeBoostSetupClick(object sender, RoutedEventArgs e)
    {
        if (_boostConfiguring) return;
        _boostConfiguring = true; UpdateVolumeBoost();
        try
        {
            await VolumeBoost.ConfigureAsync(new Progress<string>(text => { if (!_disposed) StatusText.Text = text; }));
            if (!_disposed) StatusText.Text = "增强组件已配置";
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { if (!_disposed) StatusText.Text = "组件设置已取消"; }
        catch (Exception error) { if (!_disposed) Report(error, false); }
        finally { _boostConfiguring = false; if (!_disposed) RefreshVolume(); }
    }
}
