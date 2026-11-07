using Microsoft.Win32;
using Switchboard.Display;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Switchboard;

public partial class MainWindow
{
    private readonly BrightnessService _brightnessService = new();
    private readonly ObservableCollection<BrightnessViewModel> _brightnessMonitors = new();
    private readonly Dictionary<string, BrightnessViewModel> _brightnessPending = new();
    private readonly DispatcherTimer _brightnessDebounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _brightnessDisplayDebounce = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly DispatcherTimer _brightnessPoll = new() { Interval = TimeSpan.FromSeconds(4) };
    private bool _brightnessRefreshing, _brightnessApplying, _brightnessPolling, _brightnessDirty = true;
    private int _brightnessGeneration;
    private DateTimeOffset _brightnessLastRead;

    private void InitializeBrightness()
    {
        BrightnessDevices.ItemsSource = _brightnessMonitors;
        _brightnessDebounce.Tick += ApplyBrightness;
        _brightnessDisplayDebounce.Tick += (_, _) => { _brightnessDisplayDebounce.Stop(); if (BrightnessPage.IsVisible) _ = RefreshBrightnessAsync(); };
        _brightnessPoll.Tick += PollBrightness;
        _brightnessPoll.Start();
        SystemEvents.DisplaySettingsChanged += BrightnessDisplayChanged;
    }
    private void BrightnessDisplayChanged(object? sender, EventArgs e)
    {
        if (_disposed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            _brightnessDirty = true;
            _brightnessGeneration++;
            _brightnessDisplayDebounce.Stop(); _brightnessDisplayDebounce.Start();
        }));
    }
    private async Task RefreshBrightnessAsync()
    {
        if (_brightnessRefreshing || _disposed) return;
        _brightnessRefreshing = true;
        var generation = _brightnessGeneration;
        _brightnessDebounce.Stop(); _brightnessPending.Clear();
        BrightnessRefresh.IsEnabled = BrightnessDevices.IsEnabled = false;
        BrightnessPageError.Text = ""; BrightnessCount.Text = "检测中";
        BrightnessEmpty.Visibility = Visibility.Collapsed;
        try
        {
            var monitors = await _brightnessService.DiscoverAsync();
            if (_disposed) return;
            _brightnessMonitors.Clear();
            for (var i = 0; i < monitors.Count; i++) _brightnessMonitors.Add(new(monitors[i], i + 1, QueueBrightness));
            _brightnessDirty = generation != _brightnessGeneration; _brightnessLastRead = DateTimeOffset.UtcNow;
            BrightnessCount.Text = $"{monitors.Count:00} 台显示器 · {monitors.Count(m => m.CanControl):00} 台可调节";
            BrightnessEmpty.Visibility = monitors.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception error)
        {
            SettingsStore.Log(error);
            if (!_disposed) { BrightnessCount.Text = "检测失败"; BrightnessPageError.Text = "显示器信息无法读取"; }
        }
        finally
        {
            _brightnessRefreshing = false;
            if (!_disposed) BrightnessRefresh.IsEnabled = BrightnessDevices.IsEnabled = true;
            if (!_disposed && generation != _brightnessGeneration) { _brightnessDisplayDebounce.Stop(); _brightnessDisplayDebounce.Start(); }
        }
    }
    private async void PollBrightness(object? sender, EventArgs e)
    {
        if (_disposed || !BrightnessPage.IsVisible || _brightnessRefreshing || _brightnessApplying || _brightnessPolling || _brightnessPending.Count > 0 || BrightnessDevices.IsMouseCaptureWithin) return;
        _brightnessPolling = true;
        var versions = _brightnessMonitors.ToDictionary(m => m.Id, m => (Monitor: m, m.Version));
        try
        {
            var snapshots = await _brightnessService.ReadAsync();
            if (_disposed || _brightnessRefreshing) return;
            foreach (var snapshot in snapshots.Where(s => s.CanControl))
            {
                var monitor = _brightnessMonitors.FirstOrDefault(m => m.Id == snapshot.Id);
                if (monitor == null || !versions.TryGetValue(monitor.Id, out var previous) || previous.Monitor != monitor || previous.Version != monitor.Version) continue;
                if (snapshot.Status.Length > 0) monitor.State(snapshot.Status, true, false); else monitor.Applied(snapshot.Percent);
            }
        }
        catch (Exception error) { if (!_disposed) SettingsStore.Log(error); }
        finally { _brightnessPolling = false; }
    }
    private void QueueBrightness(BrightnessViewModel monitor)
    {
        if (_disposed || _brightnessRefreshing || !monitor.CanControl) return;
        monitor.Version++; monitor.State("", false, true);
        _brightnessPending[monitor.Id] = monitor;
        _brightnessDebounce.Stop(); _brightnessDebounce.Start();
    }
    private async void ApplyBrightness(object? sender, EventArgs e)
    {
        _brightnessDebounce.Stop();
        if (_brightnessApplying || _disposed) return;
        _brightnessApplying = true;
        try
        {
            while (_brightnessPending.Count > 0 && !_disposed)
            {
                var next = _brightnessPending.First(); _brightnessPending.Remove(next.Key);
                var monitor = next.Value; var version = monitor.Version;
                try
                {
                    var result = await _brightnessService.SetAsync(monitor.Id, (int)monitor.Level);
                    if (_disposed) return;
                    if (monitor.Version == version && _brightnessMonitors.Contains(monitor))
                    {
                        monitor.Applied(result.Percent);
                        StatusText.Text = monitor.Name + $" · 亮度 {result.Percent}%";
                    }
                }
                catch (Exception error)
                {
                    SettingsStore.Log(error);
                    if (!_disposed && monitor.Version == version && _brightnessMonitors.Contains(monitor)) monitor.State(error.Message, true, false);
                }
            }
        }
        finally { _brightnessApplying = false; }
    }
    private async void BrightnessRefreshClick(object sender, RoutedEventArgs e) => await RefreshBrightnessAsync();
    private void BrightnessPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BrightnessViewModel monitor, Tag: string percent } && monitor.CanControl) monitor.Level = int.Parse(percent);
    }
    private void BrightnessStepClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BrightnessViewModel monitor, Tag: string delta } && monitor.CanControl) monitor.Level += int.Parse(delta);
    }
    private void BrightnessNavClick(object sender, RoutedEventArgs e) => Navigate(2);
    private void BrightnessOpened()
    {
        if (_brightnessDirty || DateTimeOffset.UtcNow - _brightnessLastRead > TimeSpan.FromSeconds(10)) _ = RefreshBrightnessAsync();
    }
    private void DisposeBrightness()
    {
        SystemEvents.DisplaySettingsChanged -= BrightnessDisplayChanged;
        _brightnessDebounce.Stop(); _brightnessDisplayDebounce.Stop(); _brightnessPoll.Stop(); _brightnessPending.Clear(); _brightnessService.Dispose();
    }
}
