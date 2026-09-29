using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DemoGraphics.Diagnostics;

namespace DemoGraphics.Views;

/// <summary>
/// The frame time trace: one column per rendered frame, newest on the right,
/// coloured by which budget it fits into. A number tells you the frame rate now;
/// this tells you whether it is steady, which is the thing you actually feel.
/// </summary>
public sealed class FrameTimeRibbon : Control
{
    private static readonly Color Under120 = Color.FromRgb(0x45, 0xC4, 0xCF);
    private static readonly Color Under60 = Color.FromRgb(0x8F, 0xCB, 0x6B);
    private static readonly Color Under30 = Color.FromRgb(0xE8, 0xA9, 0x3C);
    private static readonly Color Over30 = Color.FromRgb(0xE0, 0x56, 0x4E);

    private readonly IBrush _grid = new SolidColorBrush(Color.FromRgb(0x29, 0x32, 0x3F));
    private readonly IBrush _label = new SolidColorBrush(Color.FromRgb(0x5C, 0x6A, 0x79));
    private readonly IBrush _backdrop = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1A));
    private float[] _samples = new float[512];

    /// <summary>The log this ribbon draws. Call <see cref="Refresh"/> to redraw.</summary>
    public FrameLog? Log { get; set; }

    /// <summary>Longest frame the ribbon shows before clamping, in milliseconds.</summary>
    public double CeilingMs { get; set; } = 50;

    public void Refresh() => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        context.FillRectangle(_backdrop, bounds);
        if (bounds.Width < 8 || bounds.Height < 8)
        {
            return;
        }

        double Line(double ms) => bounds.Height - (Math.Clamp(ms / CeilingMs, 0, 1) * bounds.Height);

        // Rules at the frame rates worth hitting.
        foreach ((double ms, string text) in new[] { (33.33, "30"), (16.67, "60"), (8.33, "120") })
        {
            double y = Line(ms);
            context.FillRectangle(_grid, new Rect(0, y, bounds.Width, 1));
            FormattedText caption = new(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Cascadia Mono,Consolas,monospace"),
                9,
                _label);
            context.DrawText(caption, new Point(4, Math.Max(0, y - 11)));
        }

        if (Log is not { Count: > 1 } log)
        {
            return;
        }

        int columns = (int)Math.Min(bounds.Width / 2, _samples.Length);
        if (columns < 4)
        {
            return;
        }

        int taken = log.Recent(_samples.AsSpan(0, columns));
        double x = bounds.Width - (taken * 2);
        for (int i = 0; i < taken; i++)
        {
            float ms = _samples[i];
            if (ms <= 0f)
            {
                x += 2;
                continue;
            }

            double top = Line(ms);
            Color color = ms <= 8.34f ? Under120 : ms <= 16.7f ? Under60 : ms <= 33.4f ? Under30 : Over30;
            // A frame is one 2 px column: tall means slow, and a spike is a hitch.
            context.FillRectangle(
                new SolidColorBrush(color, ms <= 16.7f ? 0.85 : 1.0),
                new Rect(x, top, 1.6, bounds.Height - top));
            x += 2;
        }
    }
}
