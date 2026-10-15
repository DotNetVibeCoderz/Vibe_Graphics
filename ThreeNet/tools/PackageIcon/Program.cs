using System.Numerics;
using ThreeNet;

// The package icon, rendered with the library it advertises.
//
// A 3D library whose icon was drawn in a paint program would be saying
// something unfortunate about itself, so this renders one: three interlocking
// torus knots around a core, lit the way the gallery scenes are, and written
// out as the PNG the NuGet packages carry.
//
// It is built for 32 pixels first. That is the size nuget.org puts in a search
// result, and almost everything legible at 128 turns to mush there - so the
// shapes are few, large and separated by background rather than by detail.

const int Size = 256;
string output = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "icon.png");

using Renderer renderer = Renderer.CreateOffscreen(RendererOptions.Default with
{
    Width = Size,
    Height = Size,
    MsaaSamples = 4,
    ToneMapping = ToneMapping.Aces,
    Exposure = 1.15f,
    Bloom = true,
    BloomIntensity = 0.22f,
    BloomThreshold = 1.1f,
});

using Scene scene = new()
{
    Environment = SceneEnvironment.Default with
    {
        // Transparent so the icon sits on any gallery background. NuGet shows
        // packages on white and on dark, and a baked-in backdrop looks wrong on
        // one of them.
        Background = new Vector4(0f, 0f, 0f, 0f),
        AmbientColor = new Vector3(0.55f, 0.62f, 0.78f),
        AmbientIntensity = 0.3f,
        Sky = SkyMode.Color,
    },
};

// The .NET purple and a warm counter-light, so the thing reads as a .NET
// library rather than as a generic 3D widget.
Vector4 primary = new(0.37f, 0.16f, 0.78f, 1f);
Vector4 accent = new(0.96f, 0.45f, 0.15f, 1f);
Vector4 cool = new(0.18f, 0.62f, 0.92f, 1f);

// One knot, not three. Three interlocking knots is detail, and detail is
// exactly what disappears at 32 pixels - what survives is a silhouette with
// clear holes in it, so the shape has to be legible as a shape.
Node knot = scene.AddMesh(
    scene.CreateTorusKnotGeometry(1.0f, 0.3f, 220, 28, 2, 3),
    scene.CreateMaterial(MaterialOptions.Pbr(primary, 0.9f, 0.22f) with
    {
        // A clear coat puts a second, tighter peak on the highlight, which is
        // what keeps the tube reading as round when it is a few pixels across.
        Clearcoat = 1f,
        ClearcoatRoughness = 0.06f,
    }));
knot.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.42f);

// One ring around it, tilted, to say "3D" without adding another silhouette
// to decode.
Node ring = scene.AddMesh(
    scene.CreateTorusGeometry(1.46f, 0.085f, 128, 20),
    scene.CreateMaterial(MaterialOptions.Pbr(cool, 1f, 0.15f) with
    {
        Clearcoat = 1f,
        ClearcoatRoughness = 0.05f,
    }));
ring.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.12f)
    * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f);

// A small warm core. Bright enough to hold the centre, dim enough that bloom
// does not eat the knot around it.
Node core = scene.AddMesh(
    scene.CreateSphereGeometry(0.26f, 48, 32),
    scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 0f, 0.1f) with
    {
        Emissive = new Vector3(accent.X, accent.Y, accent.Z),
        EmissiveIntensity = 3.2f,
    }));
core.Position = Vector3.Zero;

Node key = scene.AddLight(Light.Directional(new Vector3(1f, 0.97f, 0.9f), 5.5f));
key.LookAt(new Vector3(-0.75f, -0.9f, -0.7f));

Node fill = scene.AddLight(Light.Directional(new Vector3(0.35f, 0.5f, 1f), 2.4f));
fill.LookAt(new Vector3(0.95f, 0.1f, 0.5f));

// A rim from behind: it draws a bright line along the far side of every tube,
// which is what separates the knot from itself where it crosses over.
Node rim = scene.AddLight(Light.Directional(new Vector3(1f, 0.55f, 0.25f), 4.5f));
rim.LookAt(new Vector3(-0.2f, -0.35f, 1f));

// Far enough back that the whole silhouette fits with margin. A cropped icon
// reads as a mistake at any size.
Node camera = scene.AddCamera(Camera.Perspective(30f.ToRadians()), new Vector3(3.4f, 2.6f, 6.2f));
camera.LookAt(Vector3.Zero);

renderer.Render(scene, camera);
byte[] pixels = renderer.ReadPixels();

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
WritePng(output, pixels, Size, Size);
Console.WriteLine($"wrote {output} ({Size}x{Size})");

/// <summary>
/// Writes RGBA pixels as a PNG.
/// </summary>
/// <remarks>
/// Written by hand rather than pulled from a package: this tool exists to make
/// one file at build time and an image dependency would outlive its usefulness.
/// Stored (uncompressed) deflate blocks keep it short and the size difference
/// does not matter for one icon.
/// </remarks>
static void WritePng(string path, byte[] rgba, int width, int height)
{
    using MemoryStream raw = new();
    for (int y = 0; y < height; y++)
    {
        // Filter byte 0: this row is stored as-is.
        raw.WriteByte(0);
        raw.Write(rgba, y * width * 4, width * 4);
    }

    byte[] data = raw.ToArray();
    using MemoryStream stream = new();
    // The signature byte by byte: a u8 literal would UTF-8 encode 0x89 into
    // two bytes, and the file would not be a PNG at all.
    stream.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

    using (MemoryStream header = new())
    {
        WriteBigEndian(header, width);
        WriteBigEndian(header, height);
        header.WriteByte(8); // bit depth
        header.WriteByte(6); // colour type: RGBA
        header.WriteByte(0); // deflate
        header.WriteByte(0); // adaptive filtering
        header.WriteByte(0); // no interlace
        WriteChunk(stream, "IHDR"u8, header.ToArray());
    }

    WriteChunk(stream, "IDAT"u8, Deflate(data));
    WriteChunk(stream, "IEND"u8, []);
    File.WriteAllBytes(path, stream.ToArray());
}

static void WriteBigEndian(Stream stream, int value)
{
    stream.WriteByte((byte)(value >> 24));
    stream.WriteByte((byte)(value >> 16));
    stream.WriteByte((byte)(value >> 8));
    stream.WriteByte((byte)value);
}

static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, byte[] body)
{
    WriteBigEndian(stream, body.Length);
    byte[] typed = [.. type, .. body];
    stream.Write(typed);
    WriteBigEndian(stream, (int)Crc32(typed));
}

/// <summary>Compresses to a zlib stream, which is what a PNG IDAT holds.</summary>
/// <remarks>
/// This started out writing stored (uncompressed) blocks by hand, which made a
/// 512 square icon 1,049,236 bytes - four thousand over NuGet's one megabyte
/// limit, which is a tedious way to find out that deflate was not optional.
/// <c>ZLibStream</c> is in the framework and does it properly.
/// </remarks>
static byte[] Deflate(byte[] data)
{
    using MemoryStream stream = new();
    using (System.IO.Compression.ZLibStream zlib = new(
        stream, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
    {
        zlib.Write(data);
    }

    return stream.ToArray();
}

static uint Crc32(ReadOnlySpan<byte> data)
{
    uint crc = 0xFFFFFFFF;
    foreach (byte value in data)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
    }

    return crc ^ 0xFFFFFFFF;
}
