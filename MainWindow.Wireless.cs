using Switchboard.Wireless;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Switchboard;

public partial class MainWindow
{
    private readonly ObservableCollection<BatteryViewModel> _wirelessDevices = new();
    private readonly DispatcherTimer _wirelessTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _wirelessRefreshing;
    private int _wirelessEditing;
    private List<BatteryDevice> _wirelessSnapshot = new();
    private DateTime _wirelessReadAt;
    private string _wirelessSignature = "";
    private void InitializeWireless()
    {
        WirelessDevices.ItemsSource = _wirelessDevices;
        var hidden = _settings.Wireless.Values.Count(p => p != null && p.Hidden);
        WirelessHidden.Content = $"已隐藏 ({hidden})"; WirelessHidden.Visibility = hidden == 0 ? Visibility.Collapsed : Visibility.Visible;
        _wirelessTimer.Tick += async (_, _) => { if (WirelessPage.IsVisible && _wirelessEditing == 0 && !WirelessDevices.IsKeyboardFocusWithin) await RefreshWirelessAsync(); };
        _wirelessTimer.Start();
    }
    private async Task RefreshWirelessAsync()
    {
        if (_disposed || _wirelessRefreshing) return;
        _wirelessRefreshing = true; WirelessRefresh.IsEnabled = false; WirelessError.Text = "";
        try
        {
            var devices = await Task.Run(BatteryService.Read);
            if (_disposed) return;
            _wirelessSnapshot = devices; _wirelessReadAt = DateTime.Now;
            RenderWirelessDevices();
        }
        catch (Exception error) { SettingsStore.Log(error); WirelessError.Text = error.Message; }
        finally { _wirelessRefreshing = false; if (!_disposed) WirelessRefresh.IsEnabled = true; }
    }
    private void RenderWirelessDevices()
    {
            var devices = _wirelessSnapshot.Where(d => !_settings.Wireless.TryGetValue(d.Id, out var preference) || preference == null || !preference.Hidden).ToList();
            var signature = string.Join("|", devices.Select(d => d.ToString()));
            if (_wirelessSignature != signature)
            {
                _wirelessSignature = signature; _wirelessDevices.Clear();
                foreach (var device in devices)
                {
                    if (!_settings.Wireless.TryGetValue(device.Id, out var preference) || preference == null) _settings.Wireless[device.Id] = preference = new() { Icon = device.Kind };
                    _wirelessDevices.Add(new(device, preference));
                }
            }
            var hidden = _settings.Wireless.Values.Count(p => p != null && p.Hidden);
            WirelessHidden.Content = $"已隐藏 ({hidden})"; WirelessHidden.Visibility = hidden == 0 ? Visibility.Collapsed : Visibility.Visible;
            WirelessCount.Text = $"{devices.Count} 台设备 · {_wirelessDevices.Count(d => d.HasBattery)} 台提供电量 · 读取于 {_wirelessReadAt:HH:mm:ss}";
            WirelessEmpty.Text = _wirelessSnapshot.Count > 0 ? "设备已全部隐藏" : "没有发现设备";
            WirelessEmpty.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void WirelessRefreshClick(object sender, RoutedEventArgs e) => await RefreshWirelessAsync();
    private void WirelessNavClick(object sender, RoutedEventArgs e) => Navigate(5);
    private ContextMenu WirelessMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => _wirelessEditing++;
        menu.Closed += (_, _) => _wirelessEditing--;
        return menu;
    }
    private void WirelessDeviceMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BatteryViewModel device } card) return;
        var menu = card.ContextMenu!; menu.Items.Clear();
        var rename = new MenuItem { Header = "修改名称", Icon = MenuGlyph("M2,14 L3,10 L11,2 L14,5 L6,13 Z M10,3 L13,6") }; rename.Click += (_, _) => RenameWireless(device); menu.Items.Add(rename);
        var icons = new MenuItem { Header = "预设图标", Icon = MenuGlyph("M2,2 H6 V6 H2 Z M10,2 H14 V6 H10 Z M2,10 H6 V14 H2 Z M10,10 H14 V14 H10 Z") };
        foreach (var preset in BatteryViewModel.Presets.Where(p => p.Key != "custom"))
        {
            var item = new MenuItem { Header = preset.Name, IsCheckable = true, IsChecked = device.IconKey == preset.Key, Style = (Style)FindResource("SquareCheckMenuItem"), Icon = new Image { Source = new BatteryViewModel(device.Device, new() { Icon = preset.Key }).Icon, Width = 18, Height = 18 } };
            item.Click += (_, _) => { device.Choose(preset.Key); Save(); }; icons.Items.Add(item);
        }
        menu.Items.Add(icons);
        var upload = new MenuItem { Header = "上传图标…", Icon = MenuGlyph("M8,11 V2 M4,6 L8,2 L12,6 M2,10 V14 H14 V10") }; upload.Click += (_, _) => UploadWirelessIcon(device); menu.Items.Add(upload);
        menu.Items.Add(new Separator());
        var hide = new MenuItem { Header = "隐藏设备", Icon = MenuGlyph("M1,8 C4,2 12,2 15,8 C12,14 4,14 1,8 M6,6 L10,10 M2,2 L14,14") }; hide.Click += (_, _) => { device.Preference.Hidden = true; device.Preference.LastKnownName = device.NativeName; Save(); RenderWirelessDevices(); }; menu.Items.Add(hide);
    }
    private static System.Windows.Shapes.Path MenuGlyph(string geometry) => new()
    {
        Data = System.Windows.Media.Geometry.Parse(geometry), Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 29, 27)),
        StrokeThickness = 1.25, Width = 16, Height = 16, Stretch = System.Windows.Media.Stretch.Uniform,
        StrokeStartLineCap = System.Windows.Media.PenLineCap.Square, StrokeEndLineCap = System.Windows.Media.PenLineCap.Square
    };
    private void WirelessCardLoaded(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement card && card.ContextMenu == null) card.ContextMenu = WirelessMenu(); }
    private void WirelessHiddenClick(object sender, RoutedEventArgs e)
    {
        var menu = WirelessMenu();
        foreach (var pair in _settings.Wireless.Where(p => p.Value != null && p.Value.Hidden))
        {
            var preference = pair.Value;
            var name = string.IsNullOrWhiteSpace(preference.Name) ? preference.LastKnownName : preference.Name;
            var item = new MenuItem { Header = "恢复 · " + (string.IsNullOrWhiteSpace(name) ? "设备" : name), Icon = MenuGlyph("M3,6 A6,6 0 1 1 3,11 M3,2 V6 H7") };
            item.Click += (_, _) => { preference.Hidden = false; Save(); RenderWirelessDevices(); }; menu.Items.Add(item);
        }
        menu.PlacementTarget = WirelessHidden; menu.IsOpen = true;
    }
    private void RenameWireless(BatteryViewModel device)
    {
        var dialog = new Window { Title = "设备名称", Owner = this, Width = 380, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "设备名称", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        var name = new TextBox { Text = device.Name, MaxLength = 64 }; panel.Children.Add(name);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        var save = new Button { Content = "保存", IsDefault = true };
        save.Click += (_, _) => { device.Name = name.Text.Trim(); Save(); dialog.DialogResult = true; };
        actions.Children.Add(cancel); actions.Children.Add(save); panel.Children.Add(actions); dialog.Content = panel;
        dialog.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        _wirelessEditing++; try { dialog.ShowDialog(); } finally { _wirelessEditing--; }
    }
    private void UploadWirelessIcon(BatteryViewModel device)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "设备图标", Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico" };
        _wirelessEditing++;
        try { if (dialog.ShowDialog(this) != true) return; device.Upload(dialog.FileName); Save(); WirelessError.Text = ""; }
        catch (Exception error) { WirelessError.Text = "图标不可用 · " + error.Message; }
        finally { _wirelessEditing--; }
    }
}
