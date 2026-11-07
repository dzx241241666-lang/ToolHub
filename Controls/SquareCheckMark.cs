using System;
using System.Windows;
using System.Windows.Media;

namespace Switchboard.Controls;

public sealed class SquareCheckMark : FrameworkElement
{
    public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(nameof(IsChecked), typeof(bool?), typeof(SquareCheckMark), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(SquareCheckMark), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FrameBrushProperty = DependencyProperty.Register(nameof(FrameBrush), typeof(Brush), typeof(SquareCheckMark), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool? IsChecked { get => (bool?)GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Brush FrameBrush { get => (Brush)GetValue(FrameBrushProperty); set => SetValue(FrameBrushProperty, value); }

    public SquareCheckMark()
    {
        Width = Height = 12;
        SnapsToDevicePixels = UseLayoutRounding = true;
        IsHitTestVisible = false;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    { base.OnDpiChanged(oldDpi, newDpi); InvalidateVisual(); }
    protected override void OnRender(DrawingContext drawing)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY));
        var borderX = Math.Max(1, (int)Math.Round(dpi.DpiScaleX));
        var borderY = Math.Max(1, (int)Math.Round(dpi.DpiScaleY));
        var markWidth = Math.Max(1, (int)Math.Round(6 * dpi.DpiScaleX));
        var markHeight = Math.Max(1, (int)Math.Round(6 * dpi.DpiScaleY));
        // Matching parity keeps both margins equal after fractional DPI scaling.
        if ((width - markWidth) % 2 != 0) markWidth--;
        if ((height - markHeight) % 2 != 0) markHeight--;
        var left = (width - markWidth) / 2;
        var top = (height - markHeight) / 2;
        double X(int pixels) => pixels / dpi.DpiScaleX;
        double Y(int pixels) => pixels / dpi.DpiScaleY;
        drawing.PushGuidelineSet(new GuidelineSet(new[] { 0d, X(borderX), X(left), X(left + markWidth), X(width - borderX), X(width) }, new[] { 0d, Y(borderY), Y(top), Y(top + markHeight), Y(height - borderY), Y(height) }));
        drawing.DrawRectangle(FrameBrush, null, new Rect(0, 0, X(width), Y(borderY)));
        drawing.DrawRectangle(FrameBrush, null, new Rect(0, Y(height - borderY), X(width), Y(borderY)));
        drawing.DrawRectangle(FrameBrush, null, new Rect(0, 0, X(borderX), Y(height)));
        drawing.DrawRectangle(FrameBrush, null, new Rect(X(width - borderX), 0, X(borderX), Y(height)));
        if (IsChecked != false)
        {
            drawing.PushOpacity(IsChecked == true ? 1 : 0.45);
            drawing.DrawRectangle(Foreground, null, new Rect(X(left), Y(top), X(markWidth), Y(markHeight)));
            drawing.Pop();
        }
        drawing.Pop();
    }
}
