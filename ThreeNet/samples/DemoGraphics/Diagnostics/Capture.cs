using System.Globalization;
using System.Text.Json;
using Avalonia.Media.Imaging;

namespace DemoGraphics.Diagnostics;

/// <summary>
/// Saves a frame with everything needed to take the same shot again: the scene,
/// the preset, every parameter, the weather, the render settings and the GPU it
/// came from all go into a JSON file beside the image.
/// </summary>
public static class Capture
{
    /// <summary>
    /// Writes the PNG and its sidecar. Returns the image path.
    /// </summary>
    public static async Task<string> SaveAsync(
        WriteableBitmap bitmap,
        string sceneId,
        IReadOnlyDictionary<string, object?> metadata)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string baseName = $"{sceneId.ToLowerInvariant()}-{stamp}";
        string imagePath = Path.Combine(BenchmarkReport.OutputDirectory, baseName + ".png");
        string sidecarPath = Path.Combine(BenchmarkReport.OutputDirectory, baseName + ".json");

        await Task.Run(() => bitmap.Save(imagePath, new PngBitmapEncoderOptions()));
        await File.WriteAllTextAsync(
            sidecarPath,
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        return imagePath;
    }
}
