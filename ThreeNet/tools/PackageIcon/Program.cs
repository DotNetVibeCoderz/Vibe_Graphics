using SkiaSharp;

// Prepares the NuGet package icon from the artwork in docs/images.
//
// The artwork itself is not drawn here - it is generated art, kept in the
// repository as package-icon-source.png at full size. This only does the part
// that has rules attached to it:
//
//   - NuGet's gallery lists packages at 32 pixels and shows the detail page at
//     128, so the icon ships at 256: sharp on a high density display and still
//     well inside the limit.
//   - The file must be under one megabyte. The source is a megabyte on its own,
//     which is how that limit was discovered.
//   - Transparency has to survive, because nuget.org renders packages on both
//     a light and a dark background.
//
// Run it with the source and the destination:
//
//   dotnet run (project tools/PackageIcon) source.png icon.png

const int Size = 256;

string source = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "package-icon-source.png");
string output = args.Length > 1
    ? args[1]
    : Path.Combine(AppContext.BaseDirectory, "icon.png");

if (!File.Exists(source))
{
    Console.Error.WriteLine($"no artwork at {source}");
    return 1;
}

using SKBitmap original = SKBitmap.Decode(source);
if (original is null)
{
    Console.Error.WriteLine($"{source} is not an image this can read");
    return 1;
}

using SKSurface surface = SKSurface.Create(
    new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
SKCanvas canvas = surface.Canvas;
canvas.Clear(SKColors.Transparent);

// Mitchell resampling rather than the default. Downscaling by four with a
// cheaper filter aliases the thin cyan rim light along the brow into a dashed
// line, which is exactly the detail the icon is carried by.
using (SKImage source_image = SKImage.FromBitmap(original))
{
    canvas.DrawImage(
        source_image,
        new SKRect(0, 0, original.Width, original.Height),
        new SKRect(0, 0, Size, Size),
        new SKSamplingOptions(SKCubicResampler.Mitchell));
}

using SKImage image = surface.Snapshot();
using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
using (FileStream file = File.Create(output))
{
    encoded.SaveTo(file);
}

long bytes = new FileInfo(output).Length;
Console.WriteLine($"wrote {output} ({Size}x{Size}, {bytes} bytes)");

// Worth failing the build over rather than finding out when a push is rejected.
const long Limit = 1024 * 1024;
if (bytes >= Limit)
{
    Console.Error.WriteLine($"that is {bytes - Limit} bytes over NuGet's one megabyte limit");
    return 1;
}

return 0;
