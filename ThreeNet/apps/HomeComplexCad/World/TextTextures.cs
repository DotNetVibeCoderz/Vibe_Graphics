using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ThreeNet;

namespace HomeComplexCad.World;

/// <summary>
/// Renders text into textures with Avalonia, for street signs, shop fronts,
/// house numbers and the estate gate. Must run on the UI thread.
/// </summary>
public static class TextTextures
{
    public static Texture Create(
        Scene scene,
        string text,
        Color background,
        Color foreground,
        int width = 512,
        int height = 128,
        double fontSize = 64,
        string? subtitle = null,
        bool border = true)
    {
        using RenderTargetBitmap bitmap = new(new PixelSize(width, height), new Vector(96, 96));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(new SolidColorBrush(background), new Rect(0, 0, width, height));
            if (border)
            {
                double inset = height * 0.06;
                context.DrawRectangle(
                    null,
                    new Pen(new SolidColorBrush(foreground), height * 0.03),
                    new Rect(inset, inset, width - (inset * 2), height - (inset * 2)),
                    (float)(height * 0.06));
            }

            Typeface typeface = new("Inter", FontStyle.Normal, FontWeight.Bold);
            FormattedText title = new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize, new SolidColorBrush(foreground));
            // Shrink long names so they always fit on the board.
            double maxWidth = width * 0.9;
            if (title.Width > maxWidth)
            {
                title.SetFontSize(fontSize * maxWidth / title.Width);
            }

            double titleY = subtitle is null ? (height - title.Height) / 2 : (height * 0.42) - (title.Height / 2);
            context.DrawText(title, new Point((width - title.Width) / 2, titleY));

            if (subtitle is not null)
            {
                FormattedText small = new(subtitle, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Inter"), fontSize * 0.38, new SolidColorBrush(foreground));
                context.DrawText(small, new Point((width - small.Width) / 2, height * 0.72 - (small.Height / 2)));
            }
        }

        // Encode to PNG and let the native decoder upload it: simple and portable.
        using MemoryStream png = new();
        bitmap.Save(png);
        Texture texture = scene.LoadTexture(png.ToArray(), srgb: true);
        texture.SetSampler(WrapMode.ClampToEdge, WrapMode.ClampToEdge, linearFilter: true, mipmaps: true, anisotropy: 8);
        return texture;
    }
}
