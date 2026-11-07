using System;
using System.Drawing;
using System.Windows.Forms;

namespace Switchboard.Controls;

internal sealed class SquareCheckRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color Paper = Color.FromArgb(245, 244, 239);
    private static readonly Color Ink = Color.FromArgb(28, 29, 27);
    private static readonly Color Muted = Color.FromArgb(116, 117, 111);
    private static readonly Color Rule = Color.FromArgb(217, 218, 210);
    private static readonly Color Hover = Color.FromArgb(231, 231, 222);
    private static readonly Color Accent = Color.FromArgb(223, 58, 40);
    public SquareCheckRenderer() { RoundedEdges = false; }
    public static void Apply(ContextMenuStrip menu)
    {
        menu.Renderer = new SquareCheckRenderer(); menu.BackColor = Paper; menu.ForeColor = Ink;
        menu.Font = new Font("Segoe UI", 9.75f, FontStyle.Regular, GraphicsUnit.Point);
        menu.ShowImageMargin = false; menu.ShowCheckMargin = true; menu.DropShadowEnabled = true;
        menu.Opening += (_, _) =>
        {
            int Scale(int value) => (int)Math.Round(value * menu.DeviceDpi / 96d);
            menu.Padding = new Padding(Scale(5)); menu.MinimumSize = new Size(Scale(220), 0);
            foreach (ToolStripItem item in menu.Items)
            {
                item.ForeColor = Ink;
                item.Padding = item is ToolStripSeparator ? new Padding(0, Scale(5), 0, Scale(5)) : new Padding(Scale(10), Scale(7), Scale(14), Scale(7));
            }
        };
    }
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    { using var brush = new SolidBrush(Paper); e.Graphics.FillRectangle(brush, e.AffectedBounds); }
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    { using var brush = new SolidBrush(Paper); e.Graphics.FillRectangle(brush, e.AffectedBounds); }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    { using var pen = new Pen(Rule); e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1); }
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Enabled || !e.Item.Selected) return;
        var scale = (e.Item.Owner?.DeviceDpi ?? 96) / 96d;
        using var brush = new SolidBrush(Hover);
        e.Graphics.FillRectangle(brush, new Rectangle(0, 0, e.Item.Width, e.Item.Height));
        using var marker = new SolidBrush(Accent);
        var height = (int)Math.Round(14 * scale);
        e.Graphics.FillRectangle(marker, 0, (e.Item.Height - height) / 2, Math.Max(2, (int)Math.Round(2 * scale)), height);
    }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Ink : Muted;
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, e.TextColor, e.TextFormat);
    }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var inset = (int)Math.Round(10 * (e.Item.Owner?.DeviceDpi ?? 96) / 96d);
        using var pen = new Pen(Rule);
        e.Graphics.DrawLine(pen, inset, e.Item.Height / 2, e.Item.Width - inset, e.Item.Height / 2);
    }
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        var scale = (e.Item?.Owner?.DeviceDpi ?? 96) / 96f;
        var x = e.ArrowRectangle.Left + e.ArrowRectangle.Width / 2f;
        var y = e.ArrowRectangle.Top + e.ArrowRectangle.Height / 2f;
        var direction = e.Direction == ArrowDirection.Left ? -1 : 1;
        using var pen = new Pen(e.Item?.Enabled != false ? Ink : Muted, Math.Max(1, scale));
        e.Graphics.DrawLines(pen, new[] { new PointF(x - 2 * scale * direction, y - 4 * scale), new PointF(x + 2 * scale * direction, y), new PointF(x - 2 * scale * direction, y + 4 * scale) });
    }
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var size = Math.Max(5, (int)Math.Round(6 * (e.Item.Owner?.DeviceDpi ?? 96) / 96d));
        var bounds = e.ImageRectangle;
        if ((bounds.Width - size) % 2 != 0) size--;
        using var brush = new SolidBrush(e.Item.Enabled ? Accent : Muted);
        e.Graphics.FillRectangle(brush, bounds.Left + (bounds.Width - size) / 2, bounds.Top + (bounds.Height - size) / 2, size, size);
    }
}
