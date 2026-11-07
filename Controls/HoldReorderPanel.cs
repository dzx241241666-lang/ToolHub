using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Switchboard.Controls;

public sealed class HoldReorderPanel : StackPanel
{
    private readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private CheckBox? _pressed;
    private UIElement[] _original = Array.Empty<UIElement>();
    private Point _down;
    private double _grabOffset, _opacity;
    private bool _dragging, _moved;
    private Cursor? _previousCursor;
    private Transform? _previousTransform;
    private int _previousZ;
    public event EventHandler? OrderChanged;

    public HoldReorderPanel()
    {
        Background = Brushes.Transparent;
        Focusable = true;
        KeyboardNavigation.SetIsTabStop(this, false);
        _hold.Tick += (_, _) =>
        {
            _hold.Stop();
            if (_pressed == null || Mouse.LeftButton != MouseButtonState.Pressed) { Finish(false); return; }
            _dragging = true;
            _pressed.Opacity = 0.7;
            _previousCursor = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.SizeNS;
            Panel.SetZIndex(_pressed, 100);
            MoveItem(Mouse.GetPosition(this));
        };
        Unloaded += (_, _) => Finish(false);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (_pressed != null) return;
        var point = e.GetPosition(this);
        var item = Children.OfType<CheckBox>().FirstOrDefault(child =>
            new Rect(new Point(0, VisualTreeHelper.GetOffset(child).Y), new Size(ActualWidth, child.ActualHeight)).Contains(point));
        if (item == null || !item.IsEnabled || !CaptureMouse()) return;
        _pressed = item; _down = point; _moved = false; _dragging = false;
        _original = Children.Cast<UIElement>().ToArray();
        _grabOffset = point.Y - VisualTreeHelper.GetOffset(item).Y;
        _opacity = item.Opacity; _previousTransform = item.RenderTransform; _previousZ = Panel.GetZIndex(item);
        Focus(); _hold.Start(); e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (_pressed == null) return;
        var point = e.GetPosition(this);
        _moved |= (point - _down).Length > 6;
        if (Mouse.LeftButton != MouseButtonState.Pressed) { Finish(false); return; }
        if (_dragging) MoveItem(point);
        e.Handled = true;
    }

    private void MoveItem(Point point)
    {
        if (_pressed == null) return;
        var target = Children.OfType<CheckBox>().Count(child => child != _pressed &&
            point.Y > VisualTreeHelper.GetOffset(child).Y + child.ActualHeight / 2);
        if (target != Children.IndexOf(_pressed))
        {
            var positions = Children.OfType<CheckBox>().Where(c => c != _pressed)
                .ToDictionary(c => c, c => c.TranslatePoint(new Point(), this).Y);
            Children.Remove(_pressed); Children.Insert(target, _pressed); UpdateLayout();
            foreach (var pair in positions)
            {
                var transform = new TranslateTransform(); pair.Key.RenderTransform = transform;
                if (SystemParameters.ClientAreaAnimation)
                    transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(
                        pair.Value - VisualTreeHelper.GetOffset(pair.Key).Y, 0, TimeSpan.FromMilliseconds(130))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }
        var top = Math.Clamp(point.Y - _grabOffset, 0, Math.Max(0, ActualHeight - _pressed.ActualHeight));
        _pressed.RenderTransform = new TranslateTransform(0, top - VisualTreeHelper.GetOffset(_pressed).Y);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (_pressed == null) return;
        var item = _pressed; var point = e.GetPosition(this);
        var toggle = !_dragging && !_moved && new Rect(new Point(0, VisualTreeHelper.GetOffset(item).Y),
            new Size(ActualWidth, item.ActualHeight)).Contains(point);
        Finish(true);
        if (toggle) { item.Focus(); item.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, item.IsChecked != true); }
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_pressed != null && e.Key == Key.Escape) { Finish(false); e.Handled = true; }
        else if (_pressed == null && Keyboard.Modifiers == ModifierKeys.Control &&
            e.Key is Key.Up or Key.Down && Keyboard.FocusedElement is CheckBox item && Children.Contains(item))
        {
            var index = Children.IndexOf(item);
            var target = Math.Clamp(index + (e.Key == Key.Up ? -1 : 1), 0, Children.Count - 1);
            if (target != index)
            {
                Children.Remove(item); Children.Insert(target, item); item.Focus();
                OrderChanged?.Invoke(this, EventArgs.Empty);
            }
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    { base.OnLostMouseCapture(e); if (_pressed != null) Finish(false); }

    private void Finish(bool commit)
    {
        _hold.Stop(); if (_pressed == null) return;
        var item = _pressed; var wasDragging = _dragging;
        var changed = !Children.Cast<UIElement>().SequenceEqual(_original);
        _pressed = null; _dragging = false;
        if (!commit && changed) { Children.Clear(); foreach (var child in _original) Children.Add(child); }
        foreach (var child in Children.OfType<CheckBox>()) child.RenderTransform = Transform.Identity;
        item.RenderTransform = _previousTransform ?? Transform.Identity; item.Opacity = _opacity; Panel.SetZIndex(item, _previousZ);
        if (wasDragging) Mouse.OverrideCursor = _previousCursor;
        if (IsMouseCaptured) ReleaseMouseCapture();
        _original = Array.Empty<UIElement>();
        if (commit && changed) OrderChanged?.Invoke(this, EventArgs.Empty);
    }
}
