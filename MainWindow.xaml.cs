using Microsoft.Win32;
using Switchboard.Audio;
using Switchboard.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Switchboard;

public partial class MainWindow : Window
{
    private readonly AudioEngine _audio = new();
    private readonly SettingsStore _store = new();
    private readonly AppSettings _settings;
    private readonly ForegroundTracker _foreground = new();
    private readonly ObservableCollection<DeviceInfo> _devices = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<int, TextBox> _shortcutBoxes = new();
    private int _currentPage;
    private readonly OverlayWindow _overlay = new();
    private Forms.NotifyIcon? _tray;
    private HotkeyService? _hotkeys;
    private bool _initializing = true, _updating, _exit, _disposed;
    private string? _activeDevice, _deviceSignature;
    private uint _targetPid;
    private int _ticks;
    private string _conflicts = "";
    private readonly bool _background;

    public MainWindow(bool background)
    {
        _background = background;
        _settings = _store.Load();
        InitializeComponent();
        DevicesList.ItemsSource = _devices;
        EnabledCheck.IsChecked = _settings.HotkeysEnabled;
        ForegroundRadio.IsChecked = !_settings.AllApplications; AllRadio.IsChecked = _settings.AllApplications;
        OverlayCheck.IsChecked = _settings.ShowOverlay;
        StartupCheck.IsChecked = IsStartupEnabled();
        foreach (ComboBoxItem item in StepCombo.Items) if (int.Parse((string)item.Tag) == _settings.VolumeStep) StepCombo.SelectedItem = item;
        if (StepCombo.SelectedIndex < 0) StepCombo.SelectedIndex = 1;
        InitializeShutdown();
        InitializeBrightness();
        InitializeTransfer();
        InitializeWireless();
        InitializeConversion();
        ShowAudio.IsChecked = _settings.Sections.Audio;
        ShowShutdown.IsChecked = _settings.Sections.Shutdown;
        ShowBrightness.IsChecked = _settings.Sections.Brightness;
        ShowTransfer.IsChecked = _settings.Sections.Transfer;
        ShowWireless.IsChecked = _settings.Sections.Wireless;
        ShowConversion.IsChecked = _settings.Sections.Conversion;
        ApplySectionVisibility();
        Loaded += OnLoaded;
        Closing += OnClosing;
        _timer.Tick += OnTick;
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hotkeys != null) return;
        _hotkeys = new(new WindowInteropHelper(this).EnsureHandle());
        _hotkeys.Pressed += HotkeyPressed;
        try { RefreshDevices(); }
        catch (Exception error) { Report(error); }
        BuildShortcuts();
        MigrateStartupRegistration();
        CreateTray();
        _initializing = false;
        Navigate(OrderedSections.First(SectionVisible));
        LoadShutdown();
        if (_settings.Transfer.Enabled) _ = StartTransferAsync();
        RegisterHotkeys();
        UpdateHints();
        if (_store.Warning != null) StatusText.Text = _store.Warning;
        try { _foreground.Poll(); SyncTarget(); RefreshVolume(); } catch (Exception error) { Report(error, false); }
        _timer.Start();
        if (_background) Hide();
    }
    private void CreateTray()
    {
        _tray = new Forms.NotifyIcon { Text = "ToolHub", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!), Visible = true };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMain);
        _tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() => { Navigate(3); ShowMain(); });
        var menu = new Forms.ContextMenuStrip();
        Controls.SquareCheckRenderer.Apply(menu);
        var navigation = new Dictionary<int, Forms.ToolStripItem>();
        foreach (var page in OrderedSections)
        {
            var target = page;
            var name = page == 0 ? "音频输出" : (string)((TextBlock)((Grid)SectionControls(page).Nav.Content).Children[0]).Text;
            navigation[page] = menu.Items.Add(name, null, (_, _) => Dispatcher.Invoke(() => { Navigate(target); ShowMain(); }));
        }
        menu.Opening += (_, _) =>
        {
            var index = 0;
            foreach (var page in OrderedSections)
            {
                var item = navigation[page]; menu.Items.Remove(item); menu.Items.Insert(index++, item);
                item.Visible = SectionVisible(page);
            }
        };
        _shutdownTrayCancel = new Forms.ToolStripMenuItem("取消定时关机") { Enabled = false };
        _shutdownTrayCancel.Click += (_, _) => Dispatcher.Invoke(CancelShutdown);
        menu.Items.Add(_shutdownTrayCancel);
        var scope = new Forms.ToolStripMenuItem("全部应用") { Checked = _settings.AllApplications, CheckOnClick = true };
        scope.CheckedChanged += (_, _) => Dispatcher.Invoke(() => { AllRadio.IsChecked = scope.Checked; ForegroundRadio.IsChecked = !scope.Checked; });
        menu.Opening += (_, _) => scope.Checked = _settings.AllApplications;
        menu.Items.Add(scope); menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Exit));
        _tray.ContextMenuStrip = menu;
    }
    public void ShowMain() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    private void OnTick(object? sender, EventArgs e)
    {
        UpdateShutdown();
        try
        {
            _foreground.Poll();
            if (++_ticks % 4 == 0) { RefreshDevices(); ResolveDevice(); }
            SyncTarget();
            if (IsVisible && !VolumeSlider.IsMouseCaptureWithin) RefreshVolume();
        }
        catch (Exception error) { Report(error, false); }
    }
    private void RefreshDevices()
    {
        var found = _audio.Devices();
        if (!_settings.Initialized && found.Count > 0)
        {
            for (var i = 0; i < Math.Min(6, found.Count); i++) { _settings.Presets[i].DeviceId = found[i].Id; _settings.Presets[i].DeviceName = found[i].Name; }
            _settings.Initialized = true; Save();
        }
        var signature = string.Join('|', found.OrderBy(d => d.Id).Select(d => d.Id + d.Name));
        var changed = signature != _deviceSignature;
        _updating = true;
        try
        {
            foreach (var old in _devices.Where(d => found.All(n => n.Id != d.Id)).ToList()) _devices.Remove(old);
            foreach (var item in found)
            {
                var old = _devices.FirstOrDefault(d => d.Id == item.Id);
                if (old == null) { item.InCycle = !_settings.ExcludedFromCycle.Contains(item.Id); _devices.Add(item); }
                else { old.IsDefault = item.IsDefault; old.Volume = item.Volume; old.Name = item.Name; }
            }
            for (var i = 0; i < _devices.Count; i++) _devices[i].Index = (i + 1).ToString("00");
            if (_settings.AllApplications || !_devices.Any(d => d.Id == _activeDevice)) _activeDevice = _audio.DefaultId();
            DeviceCount.Text = _devices.Count.ToString("00");
            EmptyDevices.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (changed) { _deviceSignature = signature; BuildPresets(); }
        }
        finally { _updating = false; }
    }
    private void BuildPresets()
    {
        PresetsPanel.Children.Clear();
        for (var i = 0; i < 6; i++)
        {
            var preset = _settings.Presets[i];
            var row = new Grid { Margin = new(0, 4, 0, 6) };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new());
            var label = CreateShortcutBox(i, $"预设 {i + 1}");
            label.Margin = new(0, 0, 8, 0); row.Children.Add(label);
            var combo = new ComboBox { Tag = i, FontSize = 11, DisplayMemberPath = "Name", SelectedValuePath = "Id", HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var choices = new List<DeviceChoice> { new() { Name = "未分配" } };
            choices.AddRange(_devices.Select(d => new DeviceChoice { Id = d.Id, Name = d.Name }));
            if (!string.IsNullOrEmpty(preset.DeviceId) && _devices.All(d => d.Id != preset.DeviceId)) choices.Add(new() { Id = preset.DeviceId, Name = preset.DeviceName + " · 离线" });
            combo.ItemsSource = choices; combo.SelectedValue = preset.DeviceId;
            combo.SetValue(AutomationProperties.NameProperty, $"预设 {i + 1} 输出设备");
            combo.SelectionChanged += PresetChanged;
            Grid.SetColumn(combo, 1); row.Children.Add(combo); PresetsPanel.Children.Add(row);
        }
    }
    private void PresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || _initializing || sender is not ComboBox { SelectedItem: DeviceChoice choice } combo) return;
        var preset = _settings.Presets[(int)combo.Tag];
        preset.DeviceId = choice.Id; preset.DeviceName = choice.Name;
        Save(); RegisterHotkeys();
    }
    private void BuildShortcuts()
    {
        CycleShortcuts.Children.Clear(); VolumeShortcuts.Children.Clear();
        foreach (var id in new[] { 100, 101, 103, 102 })
        {
            var name = id switch { 100 => "上一个设备", 101 => "下一个设备", 102 => "音量 +", _ => "音量 −" };
            var panel = id < 102 ? CycleShortcuts : VolumeShortcuts;
            if (id >= 102) panel.Children.Add(new TextBlock { Text = name, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 4, 0) });
            var box = CreateShortcutBox(id, name); box.Margin = new(0, 0, 10, 0); panel.Children.Add(box);
        }
    }
    private TextBox CreateShortcutBox(int id, string name)
    {
        var box = new TextBox { Text = DisplayChord(GetChord(id)), Tag = id, IsReadOnly = true, FontFamily = new("Consolas"), FontSize = 11, Cursor = Cursors.Hand, Padding = new(5, 4, 5, 4), BorderThickness = new(0, 0, 0, 1), MinWidth = 65, MaxWidth = 170, VerticalAlignment = VerticalAlignment.Center, ToolTip = name + " · 单击改键" };
        box.SetValue(AutomationProperties.NameProperty, name + " 快捷键");
        box.GotKeyboardFocus += (_, _) => { _hotkeys?.Clear(); HotkeyStatus.Text = "快捷键录入中"; box.Text = "组合键…"; };
        box.LostKeyboardFocus += (_, _) => { box.Text = DisplayChord(GetChord(id)); RegisterHotkeys(); };
        box.PreviewKeyDown += CaptureShortcut;
        _shortcutBoxes[id] = box;
        return box;
    }
    private string GetChord(int id) => id < 6 ? _settings.Presets[id].Shortcut : id switch { 100 => _settings.PreviousShortcut, 101 => _settings.NextShortcut, 102 => _settings.VolumeUpShortcut, _ => _settings.VolumeDownShortcut };
    private void SetChord(int id, string chord)
    {
        if (id < 6) _settings.Presets[id].Shortcut = chord;
        else switch (id) { case 100: _settings.PreviousShortcut = chord; break; case 101: _settings.NextShortcut = chord; break; case 102: _settings.VolumeUpShortcut = chord; break; case 103: _settings.VolumeDownShortcut = chord; break; }
    }
    private IEnumerable<HotkeyBinding> Bindings(bool includeUnassigned = false)
    {
        for (var i = 0; i < 6; i++) if (includeUnassigned || !string.IsNullOrEmpty(_settings.Presets[i].DeviceId)) yield return new(i, GetChord(i));
        for (var i = 100; i <= 103; i++) yield return new(i, GetChord(i), i >= 102);
    }
    private void RegisterHotkeys()
    {
        if (_hotkeys == null || _initializing) return;
        _hotkeys.Clear();
        var errors = _settings.HotkeysEnabled ? _hotkeys.Register(Bindings()) : new List<string>();
        _conflicts = string.Join(" / ", errors);
        HotkeyStatus.Text = !_settings.HotkeysEnabled ? "已暂停" : errors.Count > 0 ? $"{errors.Count} 个冲突" : "已启用";
        HotkeyStatus.ToolTip = _conflicts;
        if (errors.Count > 0) StatusText.Text = _conflicts;
    }
    private void CaptureShortcut(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Tab) return;
        e.Handled = true;
        if (e.Key == Key.Escape) { box.Text = DisplayChord(GetChord((int)box.Tag)); Keyboard.ClearFocus(); RegisterHotkeys(); return; }
        var chord = HotkeyService.Capture(e);
        if (chord == null) return;
        var id = (int)box.Tag;
        try
        {
            var parsed = HotkeyService.Parse(chord);
            if (Bindings(true).Any(b => b.Id != id && HotkeyService.Parse(b.Chord) == parsed)) throw new InvalidOperationException("快捷键重复");
            var old = GetChord(id); SetChord(id, chord);
            var errors = _hotkeys!.Register(new[] { new HotkeyBinding(id, chord) });
            if (errors.Count > 0) { SetChord(id, old); _hotkeys.Clear(); throw new InvalidOperationException(string.Join(" / ", errors)); }
            _hotkeys.Clear(); box.Text = DisplayChord(chord);
            Save(); UpdateHints(); StatusText.Text = "快捷键已保存";
            Keyboard.ClearFocus(); RegisterHotkeys();
        }
        catch (Exception error) { Report(error, false); }
    }
    private static string DisplayChord(string chord) => chord.Replace("Left", "←").Replace("Right", "→").Replace("Up", "↑").Replace("Down", "↓");
    private void UpdateHints()
    {
        VolumeHint.Text = DisplayChord(_settings.VolumeDownShortcut) + " / " + DisplayChord(_settings.VolumeUpShortcut) + $"  ·  {_settings.VolumeStep}%";
    }
    private void SyncTarget()
    {
        var target = _foreground.Last;
        TargetName.Text = _settings.AllApplications ? "系统与所有音频应用" : target?.Name ?? "没有前台应用";
        if (_targetPid != (target?.ProcessId ?? 0)) { _targetPid = target?.ProcessId ?? 0; ResolveDevice(); }
        foreach (var d in _devices) d.Selected = d.Id == _activeDevice;
    }
    private void ResolveDevice()
    {
        var target = _foreground.Last;
        if (_settings.AllApplications || target == null) { _activeDevice = _audio.DefaultId(); return; }
        var ids = ForegroundTracker.Family(target.ProcessId);
        var policy = _audio.ProcessDevice(target.ProcessId);
        if (policy == null) foreach (var pid in ids.Where(p => p != target.ProcessId)) { try { policy = _audio.ProcessDevice(pid); if (policy != null) break; } catch { } }
        _activeDevice = _devices.Any(d => d.Id == policy) ? policy : _audio.DefaultId();
    }
    private void HotkeyPressed(int id)
    {
        try
        {
            if (!_settings.HotkeysEnabled) return;
            _foreground.Poll();
            RefreshDevices();
            if (id < 6)
            {
                var preset = _settings.Presets[id];
                if (string.IsNullOrEmpty(preset.DeviceId)) throw new InvalidOperationException("预设未分配");
                Route(preset.DeviceId); return;
            }
            if (id is 100 or 101)
            {
                ResolveDevice();
                var cycle = _devices.Where(d => d.InCycle).ToList();
                if (cycle.Count == 0) throw new InvalidOperationException("没有参与循环的设备");
                var current = cycle.FindIndex(d => d.Id == _activeDevice);
                var index = current < 0 ? (id == 101 ? 0 : cycle.Count - 1) : (current + (id == 101 ? 1 : -1) + cycle.Count) % cycle.Count;
                Route(cycle[index].Id); return;
            }
            var delta = _settings.VolumeStep * (id == 102 ? 1 : -1);
            if (_settings.AllApplications)
            {
                var device = _audio.DefaultId() ?? throw new InvalidOperationException("没有默认输出设备");
                var before = _audio.Volume(device); var after = Math.Clamp(before.Level + delta, 0, 100);
                _audio.SetVolume(device, after);
                if (delta > 0 && before.Muted) _audio.SetMute(device, false);
                _activeDevice = device; Present($"音量 {after}%", _devices.FirstOrDefault(d => d.Id == device)?.Name ?? "系统");
            }
            else
            {
                var target = _foreground.RequireTarget();
                var volume = _audio.AdjustSessions(ForegroundTracker.Family(target.ProcessId), delta);
                Present($"音量 {volume}%", target.Name);
            }
            RefreshVolume();
        }
        catch (Exception error) { Report(error); }
    }
    private void Route(string id)
    {
        var device = _devices.FirstOrDefault(d => d.Id == id) ?? throw new InvalidOperationException("预设设备离线");
        var failures = new List<uint>();
        HashSet<uint> routed;
        string detail;
        if (_settings.AllApplications)
        {
            // Snapshot first: switching the default may immediately move existing sessions.
            var sessions = _audio.Sessions();
            routed = sessions.Select(s => s.ProcessId).ToHashSet();
            _audio.RouteSystem(id);
            foreach (var pid in routed) try { _audio.RouteProcess(pid, id); } catch (Exception error) { failures.Add(pid); SettingsStore.Log(error); }
            detail = "全部应用";
        }
        else
        {
            var target = _foreground.RequireTarget();
            var ids = ForegroundTracker.Family(target.ProcessId);
            routed = ids;
            var succeeded = 0;
            foreach (var pid in ids) try { _audio.RouteProcess(pid, id); succeeded++; } catch (Exception error) { failures.Add(pid); SettingsStore.Log(error); }
            if (succeeded == 0) throw new InvalidOperationException("应用音频切换失败");
            detail = target.Name;
        }
        _activeDevice = id;
        SyncTarget(); RefreshVolume();
        Present(device.Name, detail);
        if (failures.Count > 0) StatusText.Text = detail + $" · {failures.Count} 个进程未切换";
        var expectedStatus = StatusText.Text;
        var check = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        check.Tick += (_, _) =>
        {
            check.Stop();
            if (_disposed || _activeDevice != id || StatusText.Text != expectedStatus) return;
            try
            {
                var pending = _audio.Sessions().Count(s => routed.Contains(s.ProcessId) && s.State == 1 && s.DeviceId != id);
                if (pending > 0) StatusText.Text = $"输出已设置 · {pending} 个播放流仍在原设备";
            }
            catch (Exception error) { SettingsStore.Log(error); }
        };
        check.Start();
    }
    private void RefreshVolume()
    {
        var device = _devices.FirstOrDefault(d => d.Id == _activeDevice);
        _updating = true;
        try
        {
            VolumeSlider.IsEnabled = MuteCheck.IsEnabled = device != null;
            VolumeDeviceName.Text = device?.Name ?? "—";
            if (device == null) { VolumeNumber.Text = "—"; return; }
            var volume = _audio.Volume(device.Id);
            VolumeSlider.Value = volume.Level; MuteCheck.IsChecked = volume.Muted; VolumeNumber.Text = volume.Level + "%";
        }
        finally { _updating = false; }
    }
    private void Present(string title, string detail)
    {
        StatusText.Text = detail + " · " + title;
        if (_settings.ShowOverlay) _overlay.Present(title, detail);
    }
    private void Report(Exception error, bool overlay = true)
    {
        SettingsStore.Log(error);
        var message = error is System.Runtime.InteropServices.COMException ? $"音频接口不可用 · 0x{error.HResult:X8}" : error.Message;
        StatusText.Text = message;
        if (overlay && !_initializing && _settings.ShowOverlay) _overlay.Present(message, "ToolHub");
    }
    private void Save() { try { _store.Save(_settings); } catch (Exception error) { Report(error, false); } }
    private void ScopeChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.AllApplications = AllRadio.IsChecked == true; Save();
        try { ResolveDevice(); SyncTarget(); RefreshVolume(); } catch (Exception error) { Report(error); }
    }
    private void EnabledChanged(object sender, RoutedEventArgs e) { if (_initializing) return; _settings.HotkeysEnabled = EnabledCheck.IsChecked == true; Save(); RegisterHotkeys(); }
    private void CycleChanged(object sender, RoutedEventArgs e)
    {
        if (_updating || _initializing || sender is not CheckBox { DataContext: DeviceInfo device } box) return;
        if (box.IsChecked == true) _settings.ExcludedFromCycle.Remove(device.Id); else if (!_settings.ExcludedFromCycle.Contains(device.Id)) _settings.ExcludedFromCycle.Add(device.Id);
        Save();
    }
    private void RouteClick(object sender, RoutedEventArgs e) { try { Route((string)((Button)sender).Tag); } catch (Exception error) { Report(error); } }
    private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || _initializing || _activeDevice == null) return;
        try { var level = (int)Math.Round(e.NewValue); _audio.SetVolume(_activeDevice, level); VolumeNumber.Text = level + "%"; } catch (Exception error) { Report(error); }
    }
    private void MuteChanged(object sender, RoutedEventArgs e) { if (_updating || _initializing || _activeDevice == null) return; try { _audio.SetMute(_activeDevice, MuteCheck.IsChecked == true); } catch (Exception error) { Report(error); } }
    private void OverlayChanged(object sender, RoutedEventArgs e) { if (_initializing) return; _settings.ShowOverlay = OverlayCheck.IsChecked == true; Save(); }
    private void StepChanged(object sender, SelectionChangedEventArgs e) { if (_initializing || StepCombo.SelectedItem is not ComboBoxItem item) return; _settings.VolumeStep = int.Parse((string)item.Tag); Save(); UpdateHints(); }
    private static bool IsStartupEnabled() { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("ToolHub") != null || key?.GetValue("Switchboard") != null; }
    private void MigrateStartupRegistration()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key?.GetValue("Switchboard") == null) return;
            key.SetValue("ToolHub", $"\"{Environment.ProcessPath}\" --background");
            key.DeleteValue("Switchboard", false);
        }
        catch (Exception error) { SettingsStore.Log(error); }
    }
    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        try { using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); if (StartupCheck.IsChecked == true) key.SetValue("ToolHub", $"\"{Environment.ProcessPath}\" --background"); else key.DeleteValue("ToolHub", false); key.DeleteValue("Switchboard", false); }
        catch (Exception error) { Report(error, false); _initializing = true; StartupCheck.IsChecked = IsStartupEnabled(); _initializing = false; }
    }
    private void AudioNavClick(object sender, RoutedEventArgs e) => Navigate(0);
    private void SettingsNavClick(object sender, RoutedEventArgs e) => Navigate(4);
    private bool SectionVisible(int page) => page switch { 0 => _settings.Sections.Audio, 1 => _settings.Sections.Shutdown, 2 => _settings.Sections.Brightness, 3 => _settings.Sections.Transfer, 5 => _settings.Sections.Wireless, 6 => _settings.Sections.Conversion, _ => true };
    private IEnumerable<int> OrderedSections => _settings.Sections.Order.Concat(new[] { 4 });
    private (Button Nav, CheckBox? Toggle, TextBlock Heading, string Label) SectionControls(int page) => page switch
    {
        0 => (AudioNav, ShowAudio, AudioSectionLabel, "AUDIO"),
        1 => (ShutdownNav, ShowShutdown, ShutdownSectionLabel, "TIMER"),
        2 => (BrightnessNav, ShowBrightness, BrightnessSectionLabel, "DISPLAY"),
        3 => (TransferNav, ShowTransfer, TransferSectionLabel, "TRANSFER"),
        5 => (WirelessNav, ShowWireless, WirelessSectionLabel, "BATTERY"),
        6 => (ConversionNav, ShowConversion, ConversionSectionLabel, "CONVERT"),
        _ => (SettingsNav, null, SettingsSectionLabel, "SETTINGS")
    };
    private void ApplySectionVisibility()
    {
        var number = 0; var row = 1; var option = 0;
        foreach (var page in OrderedSections)
        {
            var section = SectionControls(page); var visible = SectionVisible(page);
            Grid.SetRow(section.Nav, row++);
            section.Nav.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            section.Nav.Margin = new Thickness(0, number == 0 ? 0 : 4, 0, 0);
            section.Heading.Text = visible ? $"{++number:00} / {section.Label}" : "";
            if (section.Toggle != null)
            {
                if (SectionOptions.Children.IndexOf(section.Toggle) != option)
                { SectionOptions.Children.Remove(section.Toggle); SectionOptions.Children.Insert(option, section.Toggle); }
                option++;
            }
        }
    }
    private void SectionOrderChanged(object? sender, EventArgs e)
    {
        _settings.Sections.Order = SectionOptions.Children.OfType<CheckBox>().Select(item => int.Parse((string)item.Tag)).ToList();
        ApplySectionVisibility(); Save();
    }
    private void SectionVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.Sections.Audio = ShowAudio.IsChecked == true;
        _settings.Sections.Shutdown = ShowShutdown.IsChecked == true;
        _settings.Sections.Brightness = ShowBrightness.IsChecked == true;
        _settings.Sections.Transfer = ShowTransfer.IsChecked == true;
        _settings.Sections.Wireless = ShowWireless.IsChecked == true;
        _settings.Sections.Conversion = ShowConversion.IsChecked == true;
        ApplySectionVisibility(); Save();
        if (!SectionVisible(_currentPage)) Navigate(4);
    }
    private void Navigate(int page)
    {
        if (!SectionVisible(page)) page = 4;
        _currentPage = page;
        Keyboard.ClearFocus(); RegisterHotkeys();
        AudioPage.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShutdownPage.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        BrightnessPage.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        TransferPage.Visibility = page == 3 ? Visibility.Visible : Visibility.Collapsed;
        WirelessPage.Visibility = page == 5 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == 4 ? Visibility.Visible : Visibility.Collapsed;
        ConversionPage.Visibility = page == 6 ? Visibility.Visible : Visibility.Collapsed;
        VolumeHint.Visibility = Visibility.Collapsed;
        var navigation = new[] { AudioNav, ShutdownNav, BrightnessNav, TransferNav, SettingsNav, WirelessNav, ConversionNav };
        for (var i = 0; i < navigation.Length; i++) { navigation[i].Background = i == page ? (Brush)FindResource("Ink") : Brushes.Transparent; navigation[i].Foreground = i == page ? Brushes.White : (Brush)FindResource("Ink"); }
        Title = "ToolHub · " + (page == 0 ? "音频输出" : page == 1 ? "定时关机" : page == 2 ? "显示器亮度" : page == 3 ? "局域网互传" : page == 5 ? "设备电量" : page == 6 ? "格式转换" : "设置");
        if (page == 1) { LoadShutdown(); UpdateShutdownUI(); }
        if (page == 2) BrightnessOpened();
        if (page == 3) _ = StartTransferAsync();
        if (page == 5) _ = RefreshWirelessAsync();
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void HideClick(object sender, RoutedEventArgs e) { Keyboard.ClearFocus(); RegisterHotkeys(); Hide(); }
    private void ExitClick(object sender, RoutedEventArgs e) => Exit();
    public void Exit() { _exit = true; Close(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exit) { e.Cancel = true; Keyboard.ClearFocus(); RegisterHotkeys(); Hide(); return; }
        if (WaitForConversionExit()) { e.Cancel = true; return; }
        if (_disposed) return;
        _disposed = true; _wirelessTimer.Stop(); DisposeTransfer(); DisposeBrightness(); _timer.Stop(); _hotkeys?.Dispose(); _tray?.Dispose(); _overlay.Close(); _audio.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}
