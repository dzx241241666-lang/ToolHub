using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Runtime.InteropServices;

namespace Switchboard;

public sealed class OverlayWindow : Window
{
    private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _detail = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(180, 181, 173)), Margin = new(0, 7, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private long _presentation;
    private double _restingTop;
    public OverlayWindow()
    {
        Width = 360; Height = 92; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowActivated = false; ShowInTaskbar = false; Topmost = true; IsHitTestVisible = false;
        AllowsTransparency = true; Background = Brushes.Transparent;
        var stack = new StackPanel { Margin = new(20, 18, 20, 16) }; stack.Children.Add(_title); stack.Children.Add(_detail);
        Content = new Border { Background = new SolidColorBrush(Color.FromRgb(28, 29, 27)), BorderBrush = new SolidColorBrush(Color.FromRgb(223, 58, 40)), BorderThickness = new(4, 0, 0, 0), Child = stack };
        SourceInitialized += (_, _) => { var handle = new WindowInteropHelper(this).Handle; SetWindowLongPtr(handle, -20, new IntPtr(GetWindowLongPtr(handle, -20).ToInt64() | 0x08000000L | 0x20L | 0x80L)); };
        _timer.Tick += (_, _) => Dismiss();
    }
    public void Present(string title, string detail)
    {
        _presentation++;
        _timer.Stop();
        _title.Text = title; _detail.Text = detail;
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - 24;
        _restingTop = work.Bottom - Height - 24;
        var wasVisible = IsVisible;
        var fromTop = wasVisible ? Top : _restingTop + 16;
        var fromOpacity = wasVisible ? Opacity : 0;
        BeginAnimation(TopProperty, null); BeginAnimation(OpacityProperty, null);
        Top = _restingTop; Opacity = 1;
        if (!wasVisible) Show();
        if (SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(TopProperty, new DoubleAnimation(fromTop, _restingTop, TimeSpan.FromMilliseconds(220))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            BeginAnimation(OpacityProperty, new DoubleAnimation(fromOpacity, 1, TimeSpan.FromMilliseconds(160)));
        }
        _timer.Start();
    }
    private void Dismiss()
    {
        _timer.Stop();
        if (!IsVisible) return;
        if (!SystemParameters.ClientAreaAnimation) { Hide(); return; }
        var presentation = _presentation;
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(170));
        fade.Completed += (_, _) =>
        {
            if (presentation != _presentation) return;
            Hide();
            BeginAnimation(TopProperty, null); BeginAnimation(OpacityProperty, null);
        };
        BeginAnimation(TopProperty, new DoubleAnimation(Top, _restingTop + 10, TimeSpan.FromMilliseconds(170))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        BeginAnimation(OpacityProperty, fade);
    }
    protected override void OnClosed(EventArgs e) { _timer.Stop(); _presentation++; base.OnClosed(e); }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
}
