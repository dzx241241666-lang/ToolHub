using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Switchboard.Controls;

public sealed class NumberWheel : UserControl
{
    private const double RowHeight = 36;
    private readonly Canvas _numbers = new() { IsHitTestVisible = false };
    private readonly TextBlock[] _rows = new TextBlock[7];
    private readonly TextBox _editor;
    private readonly Border _selection;
    private readonly Brush _ink = new SolidColorBrush(Color.FromRgb(28, 29, 27));
    private readonly Brush _rule = new SolidColorBrush(Color.FromRgb(211, 212, 203));
    private readonly Brush _accent = new SolidColorBrush(Color.FromRgb(223, 58, 40));
    private bool _ready, _pressed, _dragging, _committing;
    private double _startY;
    private int _startValue, _wheelDelta;

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(int), typeof(NumberWheel), new PropertyMetadata(0, RangeChanged));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(int), typeof(NumberWheel), new PropertyMetadata(59, RangeChanged));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(int), typeof(NumberWheel), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed, CoerceNumber));
    public static readonly DependencyProperty DigitsProperty = DependencyProperty.Register(nameof(Digits), typeof(int), typeof(NumberWheel), new PropertyMetadata(2, AppearanceChanged));
    public static readonly DependencyProperty WrapProperty = DependencyProperty.Register(nameof(Wrap), typeof(bool), typeof(NumberWheel), new PropertyMetadata(true, AppearanceChanged));
    private static readonly DependencyProperty OffsetProperty = DependencyProperty.Register("Offset", typeof(double), typeof(NumberWheel), new PropertyMetadata(0d, AppearanceChanged));
    public static readonly RoutedEvent ValueChangedEvent = EventManager.RegisterRoutedEvent(nameof(ValueChanged), RoutingStrategy.Bubble, typeof(RoutedPropertyChangedEventHandler<int>), typeof(NumberWheel));
    public int Minimum { get => (int)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public int Maximum { get => (int)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Digits { get => (int)GetValue(DigitsProperty); set => SetValue(DigitsProperty, value); }
    public bool Wrap { get => (bool)GetValue(WrapProperty); set => SetValue(WrapProperty, value); }
    public event RoutedPropertyChangedEventHandler<int> ValueChanged { add => AddHandler(ValueChangedEvent, value); remove => RemoveHandler(ValueChangedEvent, value); }
    private bool Editing => _editor.Visibility == Visibility.Visible;
    private double Offset => (double)GetValue(OffsetProperty);

    public NumberWheel()
    {
        Height = RowHeight * 5; MinWidth = 64; Focusable = true; FocusVisualStyle = null; Cursor = Cursors.Hand;
        Background = Brushes.Transparent;
        var grid = new Grid { ClipToBounds = true, Background = Brushes.Transparent };
        _selection = new Border { Height = RowHeight, VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.FromRgb(232, 233, 224)), BorderBrush = _rule, BorderThickness = new(0, 1, 0, 1) };
        grid.Children.Add(_selection); grid.Children.Add(_numbers);
        for (var i = 0; i < _rows.Length; i++)
        {
            _rows[i] = new TextBlock { TextAlignment = TextAlignment.Center, Height = RowHeight, FontFamily = new("Segoe UI"), VerticalAlignment = VerticalAlignment.Center, Padding = new(0, 1, 0, 0) };
            _numbers.Children.Add(_rows[i]);
        }
        _editor = new TextBox { Visibility = Visibility.Collapsed, Height = RowHeight, VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, FontFamily = new("Segoe UI"), FontSize = 27, Padding = new(0), BorderThickness = new(1), BorderBrush = _accent, Background = _selection.Background, Cursor = Cursors.IBeam };
        _editor.PreviewTextInput += (_, e) => e.Handled = !IsDigits(e.Text);
        DataObject.AddPastingHandler(_editor, (_, e) => { if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText) || !IsDigits(e.DataObject.GetData(DataFormats.UnicodeText) as string ?? "")) e.CancelCommand(); });
        _editor.TextChanged += (_, _) => { if (!_committing) ValidateEditor(); };
        _editor.PreviewKeyDown += EditorKeyDown;
        _editor.LostKeyboardFocus += (_, _) => { if (!_committing) CommitInput(); };
        grid.Children.Add(_editor); Content = grid;
        Loaded += (_, _) => { _ready = true; Render(); };
        SizeChanged += (_, _) => Render();
        IsEnabledChanged += (_, _) => { Opacity = IsEnabled ? 1 : 0.45; if (!IsEnabled) CancelInput(); };
        IsKeyboardFocusWithinChanged += (_, _) => _selection.BorderBrush = IsKeyboardFocusWithin ? _accent : _rule;
        PreviewMouseWheel += Wheel;
        MouseLeftButtonDown += Down; MouseMove += Move; MouseLeftButtonUp += Up;
        LostMouseCapture += (_, _) => { if (_pressed) { _pressed = _dragging = false; Settle(); } };
        PreviewKeyDown += WheelKeyDown;
    }
    private static bool IsDigits(string text) { foreach (var c in text) if (c is < '0' or > '9') return false; return text.Length > 0; }
    private static object CoerceNumber(DependencyObject owner, object value)
    { var wheel = (NumberWheel)owner; return Math.Clamp((int)value, wheel.Minimum, Math.Max(wheel.Minimum, wheel.Maximum)); }
    private static void RangeChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    { var wheel = (NumberWheel)owner; wheel.CoerceValue(ValueProperty); wheel.SetCurrentValue(ValueProperty, wheel.Value); if (wheel._ready) { wheel.Render(); if (wheel.Editing) wheel.ValidateEditor(); } }
    private static void AppearanceChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e) => ((NumberWheel)owner).Render();
    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        var wheel = (NumberWheel)owner;
        if (wheel._ready && !wheel._dragging)
        {
            var delta = (int)e.NewValue - (int)e.OldValue;
            var count = wheel.Maximum - wheel.Minimum + 1;
            if (wheel.Wrap && Math.Abs(delta) > count / 2) delta += delta > 0 ? -count : count;
            var from = Math.Abs(delta) <= 3 ? Math.Clamp(wheel.Offset + delta * RowHeight, -RowHeight * 3, RowHeight * 3) : 0;
            wheel.Settle(from);
        }
        wheel.Render();
        wheel.RaiseEvent(new RoutedPropertyChangedEventArgs<int>((int)e.OldValue, (int)e.NewValue, ValueChangedEvent));
    }
    private int Normalize(int value)
    {
        var count = Math.Max(1, Maximum - Minimum + 1);
        return Wrap ? Minimum + ((value - Minimum) % count + count) % count : Math.Clamp(value, Minimum, Math.Max(Minimum, Maximum));
    }
    private string Format(int value) => value.ToString(Digits >= 4 ? "0000" : "00", CultureInfo.InvariantCulture);
    private void Render()
    {
        if (_rows[0] == null) return;
        for (var i = 0; i < _rows.Length; i++)
        {
            var relative = i - 3;
            var raw = Value + relative;
            var row = _rows[i];
            row.Text = !Wrap && (raw < Minimum || raw > Maximum) ? "" : Format(Normalize(raw));
            var distance = Math.Abs(relative + Offset / RowHeight);
            row.FontSize = 28 - Math.Min(distance, 2) * 4;
            row.FontWeight = distance < 0.5 ? FontWeights.SemiBold : FontWeights.Normal;
            row.Foreground = _ink; row.Opacity = Math.Max(0.12, 1 - distance * 0.35);
            row.Width = Math.Max(0, ActualWidth);
            row.Visibility = Editing && relative == 0 ? Visibility.Hidden : Visibility.Visible;
            Canvas.SetTop(row, ActualHeight / 2 - RowHeight / 2 + relative * RowHeight + Offset);
        }
    }
    private void Settle(double? from = null)
    {
        var offset = from ?? Offset;
        BeginAnimation(OffsetProperty, null); SetValue(OffsetProperty, 0d);
        if (_ready && SystemParameters.ClientAreaAnimation && Math.Abs(offset) > 0.01)
            BeginAnimation(OffsetProperty, new DoubleAnimation(offset, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Render();
    }
    public int InputValue
    {
        get
        {
            if (!Editing) return Value;
            if (int.TryParse(_editor.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= Minimum && number <= Maximum) return number;
            throw new ArgumentException($"{AutomationProperties.GetName(this)}范围 {Minimum}–{Maximum}");
        }
    }
    private void ValidateEditor()
    {
        if (!Editing) return;
        try { _ = InputValue; _editor.Foreground = _ink; _editor.ToolTip = null; }
        catch (ArgumentException error) { _editor.Foreground = _accent; _editor.ToolTip = error.Message; }
    }
    private void BeginInput(string? seed = null)
    {
        if (!IsEnabled) return;
        BeginAnimation(OffsetProperty, null); SetValue(OffsetProperty, 0d);
        _editor.SetValue(AutomationProperties.NameProperty, AutomationProperties.GetName(this) + " 数字输入");
        _editor.MaxLength = Digits >= 4 ? 4 : 2;
        _committing = true; _editor.Text = seed ?? Format(Value); _editor.Visibility = Visibility.Visible; _committing = false;
        ValidateEditor(); Render(); _editor.Focus();
        if (seed == null) _editor.SelectAll(); else _editor.CaretIndex = _editor.Text.Length;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (seed == null && Editing) _editor.SelectAll(); }));
    }
    public bool CommitInput()
    {
        if (!Editing || _committing) return true;
        int number;
        try { number = InputValue; } catch (ArgumentException) { ValidateEditor(); return false; }
        _committing = true; _editor.Visibility = Visibility.Collapsed;
        Value = number; _committing = false; Render(); return true;
    }
    private void CancelInput()
    { _committing = true; _editor.Visibility = Visibility.Collapsed; _committing = false; Render(); }
    private void EditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; if (CommitInput()) Focus(); }
        else if (e.Key == Key.Escape) { e.Handled = true; CancelInput(); Focus(); }
        else if (e.Key == Key.Tab && !CommitInput()) e.Handled = true;
    }
    private void WheelKeyDown(object sender, KeyEventArgs e)
    {
        if (Editing || !IsEnabled) return;
        int? next = e.Key switch { Key.Up => Value - 1, Key.Down => Value + 1, Key.PageUp => Value - 10, Key.PageDown => Value + 10, Key.Home => Minimum, Key.End => Maximum, _ => null };
        if (next.HasValue) { Value = Normalize(next.Value); e.Handled = true; }
        else if (e.Key is Key.Enter or Key.Space or Key.F2) { BeginInput(); e.Handled = true; }
        else if (e.Key >= Key.D0 && e.Key <= Key.D9) { BeginInput(((int)e.Key - (int)Key.D0).ToString()); e.Handled = true; }
        else if (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9) { BeginInput(((int)e.Key - (int)Key.NumPad0).ToString()); e.Handled = true; }
    }
    private void Wheel(object sender, MouseWheelEventArgs e)
    {
        if (!IsEnabled) return;
        e.Handled = true;
        if (!CommitInput()) return;
        _wheelDelta += e.Delta;
        var steps = _wheelDelta / 120; _wheelDelta %= 120;
        if (steps != 0) Value = Normalize(Value - steps);
    }
    private void Down(object sender, MouseButtonEventArgs e)
    {
        if (!IsEnabled || (Editing && _editor.IsMouseOver) || !CommitInput()) return;
        Focus(); _startY = e.GetPosition(this).Y; _startValue = Value; _pressed = true; _dragging = false;
        CaptureMouse(); e.Handled = true;
    }
    private void Move(object sender, MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this).Y - _startY;
        if (!_dragging && Math.Abs(delta) < 5) return;
        _dragging = true; BeginAnimation(OffsetProperty, null);
        var steps = (int)Math.Round(-delta / RowHeight);
        Value = Normalize(_startValue + steps);
        var offset = delta + steps * RowHeight;
        if (!Wrap && _startValue + steps != Value) offset = Math.Clamp(delta + (Value - _startValue) * RowHeight, -10, 10);
        SetValue(OffsetProperty, offset); e.Handled = true;
    }
    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        var dragged = _dragging; _pressed = _dragging = false; ReleaseMouseCapture();
        if (dragged) Settle();
        else { Value = Normalize(Value + (int)Math.Round((e.GetPosition(this).Y - ActualHeight / 2) / RowHeight)); BeginInput(); }
        e.Handled = true;
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new WheelPeer(this);
    private sealed class WheelPeer : FrameworkElementAutomationPeer, IValueProvider, IRangeValueProvider
    {
        private NumberWheel Wheel => (NumberWheel)Owner;
        public WheelPeer(NumberWheel owner) : base(owner) { }
        protected override string GetClassNameCore() => "NumberWheel";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Spinner;
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface is PatternInterface.Value or PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);
        bool IValueProvider.IsReadOnly => !Wheel.IsEnabled;
        string IValueProvider.Value => Wheel.Value.ToString(CultureInfo.InvariantCulture);
        void IValueProvider.SetValue(string value) { if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) throw new ArgumentException(); Set(number); }
        bool IRangeValueProvider.IsReadOnly => !Wheel.IsEnabled;
        double IRangeValueProvider.Value => Wheel.Value;
        double IRangeValueProvider.Minimum => Wheel.Minimum;
        double IRangeValueProvider.Maximum => Wheel.Maximum;
        double IRangeValueProvider.SmallChange => 1;
        double IRangeValueProvider.LargeChange => 10;
        void IRangeValueProvider.SetValue(double value) { if (value != Math.Truncate(value)) throw new ArgumentException(); Set((int)value); }
        private void Set(int number) { if (!Wheel.IsEnabled) throw new ElementNotEnabledException(); if (number < Wheel.Minimum || number > Wheel.Maximum) throw new ArgumentOutOfRangeException(nameof(number)); Wheel.CancelInput(); Wheel.Value = number; }
    }
}
