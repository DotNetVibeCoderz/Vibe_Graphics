using System.Numerics;

namespace ThreeNet.Effects;

/// <summary>
/// A height field of water, stepped with the 2D wave equation and published as
/// an <see cref="TextureFormat.Rgba32Float"/> texture that a shader samples.
/// </summary>
/// <remarks>
/// <para>
/// The model is the one Evan Wallace's WebGL Water uses, and that Yong Su's
/// Three.js port keeps: two numbers per cell, height and vertical velocity,
/// advanced by
/// </para>
/// <code>
/// velocity += c² * ∇²h * dt;  velocity *= damping;  height += velocity * dt
/// </code>
/// <para>
/// The discrete Laplacian is the four neighbour stencil. Ripples therefore
/// reflect off the walls, interfere, and die away on their own - none of which a
/// sum of sine waves can do.
/// </para>
/// <para>
/// It runs on the CPU because a grid this size is cheap there (a 128x128 field
/// is ~16k cells, well under a millisecond) and because it keeps the height
/// readable from game code: <see cref="SampleHeight"/> is what makes a boat
/// bob and a buoy follow the swell. The texture layout matches the reference
/// exactly - R = height, G = velocity, B and A = the x and z of the normal - so
/// the shader side reads the same way.
/// </para>
/// </remarks>
public sealed class WaterSimulation
{
    // Height, velocity, normal.x, normal.z - the reference layout.
    private const int Channels = 4;

    private readonly float[] _cells;
    private readonly float[] _scratch;
    private readonly Random _random;
    private float _accumulator;
    private float _rainCarry;

    /// <param name="resolution">Cells along each axis. 128 is plenty for a pool.</param>
    /// <param name="size">World size of the field in metres, along x and z.</param>
    public WaterSimulation(int resolution = 128, float size = 10f, int seed = 1771)
    {
        Resolution = Math.Clamp(resolution, 16, 1024);
        Size = MathF.Max(size, 0.01f);
        _cells = new float[Resolution * Resolution * Channels];
        _scratch = new float[_cells.Length];
        _random = new Random(seed);
    }

    /// <summary>Cells along each axis.</summary>
    public int Resolution { get; }

    /// <summary>World size of the field in metres.</summary>
    public float Size { get; }

    /// <summary>Centre of the field in world space; the field spans <see cref="Size"/> around it.</summary>
    public Vector3 Center { get; set; }

    /// <summary>
    /// Wave speed. Above about 0.5 the explicit integration goes unstable, which
    /// is why the reference bakes in a similar constant.
    /// </summary>
    public float Speed { get; set; } = 0.42f;

    /// <summary>Velocity kept each step. Lower settles the water sooner.</summary>
    public float Damping { get; set; } = 0.994f;

    /// <summary>Steps per second. Fixed, so the water behaves the same at any frame rate.</summary>
    public float StepRate { get; set; } = 60f;

    /// <summary>Drops per second falling at random, for rain.</summary>
    public float RainRate { get; set; }

    /// <summary>Radius of a rain drop, in metres.</summary>
    public float RainRadius { get; set; } = 0.12f;

    /// <summary>Depth of a rain drop, in metres.</summary>
    public float RainDepth { get; set; } = 0.012f;

    /// <summary>Steps taken since the field was created.</summary>
    public long Steps { get; private set; }

    /// <summary>Flattens the water and stops it.</summary>
    public void Reset()
    {
        Array.Clear(_cells);
        _accumulator = 0f;
        _rainCarry = 0f;
    }

    /// <summary>
    /// Pushes the surface at a world position. A positive <paramref name="strength"/>
    /// lifts the water, a negative one dents it, which is what a falling drop does.
    /// </summary>
    public void AddDrop(Vector3 worldPosition, float radius, float strength)
    {
        float u = ((worldPosition.X - Center.X) / Size) + 0.5f;
        float v = ((worldPosition.Z - Center.Z) / Size) + 0.5f;
        AddDropUv(u, v, radius, strength);
    }

    /// <summary>
    /// Pushes the surface at a normalised position, where (0,0) is one corner of
    /// the field and (1,1) the other.
    /// </summary>
    public void AddDropUv(float u, float v, float radius, float strength)
    {
        float cellSize = Size / Resolution;
        float radiusCells = MathF.Max(radius / cellSize, 1f);
        float cx = u * Resolution;
        float cy = v * Resolution;

        int x0 = Math.Max(0, (int)MathF.Floor(cx - radiusCells));
        int x1 = Math.Min(Resolution - 1, (int)MathF.Ceiling(cx + radiusCells));
        int y0 = Math.Max(0, (int)MathF.Floor(cy - radiusCells));
        int y1 = Math.Min(Resolution - 1, (int)MathF.Ceiling(cy + radiusCells));

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float dx = x + 0.5f - cx;
                float dy = y + 0.5f - cy;
                float distance = MathF.Sqrt((dx * dx) + (dy * dy)) / radiusCells;
                if (distance >= 1f)
                {
                    continue;
                }

                // A raised cosine, not a cone: its slope is zero at both the rim
                // and the centre, so the ripple leaves without a ringing edge.
                float profile = 0.5f - (MathF.Cos((1f - distance) * MathF.PI) * 0.5f);
                _cells[Index(x, y)] += profile * strength;
            }
        }
    }

    /// <summary>Height of the surface above rest at a world position, interpolated.</summary>
    public float SampleHeight(float worldX, float worldZ)
    {
        float u = ((worldX - Center.X) / Size) + 0.5f;
        float v = ((worldZ - Center.Z) / Size) + 0.5f;
        return Bilinear(u, v, 0);
    }

    /// <summary>Surface normal at a world position, pointing up out of the water.</summary>
    public Vector3 SampleNormal(float worldX, float worldZ)
    {
        float u = ((worldX - Center.X) / Size) + 0.5f;
        float v = ((worldZ - Center.Z) / Size) + 0.5f;
        return Vector3.Normalize(new Vector3(Bilinear(u, v, 2), 1f, Bilinear(u, v, 3)));
    }

    /// <summary>
    /// Advances the water by real time, in fixed steps. Returns the number of
    /// steps taken, which is zero when not enough time has passed.
    /// </summary>
    public int Update(float deltaSeconds)
    {
        if (StepRate <= 0f)
        {
            return 0;
        }

        // A long stall must not turn into a hundred catch-up steps.
        _accumulator = MathF.Min(_accumulator + deltaSeconds, 0.25f);
        float period = 1f / StepRate;
        int taken = 0;
        while (_accumulator >= period)
        {
            _accumulator -= period;
            Rain(period);
            Step();
            taken++;
        }

        if (taken > 0)
        {
            ComputeNormals();
        }

        return taken;
    }

    /// <summary>Takes one step without waiting for the clock.</summary>
    public void Step()
    {
        int n = Resolution;
        Array.Copy(_cells, _scratch, _cells.Length);
        float c2 = Speed * Speed;

        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int index = Index(x, y);
                float here = _scratch[index];
                // Clamped edges: a wave bounces off the wall of the pool rather
                // than wrapping round to the far side.
                float left = _scratch[Index(x > 0 ? x - 1 : 0, y)];
                float right = _scratch[Index(x < n - 1 ? x + 1 : n - 1, y)];
                float down = _scratch[Index(x, y > 0 ? y - 1 : 0)];
                float up = _scratch[Index(x, y < n - 1 ? y + 1 : n - 1)];

                float laplacian = left + right + down + up - (4f * here);
                float velocity = (_scratch[index + 1] + (c2 * laplacian)) * Damping;
                _cells[index + 1] = velocity;
                _cells[index] = here + velocity;
            }
        }

        Steps++;
    }

    /// <summary>
    /// Rebuilds the normals from the heights, as the cross product of the two
    /// surface tangents. Called for you by <see cref="Update"/>.
    /// </summary>
    public void ComputeNormals()
    {
        int n = Resolution;
        float spacing = Size / n;
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int index = Index(x, y);
                float here = _cells[index];
                float dhdx = _cells[Index(x < n - 1 ? x + 1 : n - 1, y)] - here;
                float dhdz = _cells[Index(x, y < n - 1 ? y + 1 : n - 1)] - here;
                Vector3 normal = Vector3.Normalize(new Vector3(-dhdx, spacing, -dhdz));
                _cells[index + 2] = normal.X;
                _cells[index + 3] = normal.Z;
            }
        }
    }

    /// <summary>Creates the texture a water shader samples. Format is RGBA32F.</summary>
    public Texture CreateTexture(Scene scene)
    {
        Texture texture = scene.CreateTexture(
            Resolution,
            Resolution,
            System.Runtime.InteropServices.MemoryMarshal.AsBytes<float>(_cells),
            TextureFormat.Rgba32Float);
        // Clamped and unfiltered-safe: wrapping would make a ripple reappear on
        // the opposite shore, and there is no mip chain to build.
        texture.SetSampler(WrapMode.ClampToEdge, WrapMode.ClampToEdge, linearFilter: true, mipmaps: false, anisotropy: 1);
        return texture;
    }

    /// <summary>Uploads the current state to a texture made by <see cref="CreateTexture"/>.</summary>
    public void Upload(Texture texture) => texture.Update(_cells.AsSpan());

    /// <summary>The raw field, four floats per cell: height, velocity, normal x, normal z.</summary>
    public ReadOnlySpan<float> Cells => _cells;

    private void Rain(float deltaSeconds)
    {
        if (RainRate <= 0f)
        {
            return;
        }

        _rainCarry += RainRate * deltaSeconds;
        int drops = (int)_rainCarry;
        _rainCarry -= drops;
        for (int i = 0; i < drops; i++)
        {
            AddDropUv(
                (float)_random.NextDouble(),
                (float)_random.NextDouble(),
                RainRadius * (0.6f + ((float)_random.NextDouble() * 0.8f)),
                -RainDepth);
        }
    }

    private int Index(int x, int y) => ((y * Resolution) + x) * Channels;

    private float Bilinear(float u, float v, int channel)
    {
        float fx = Math.Clamp(u, 0f, 1f) * (Resolution - 1);
        float fy = Math.Clamp(v, 0f, 1f) * (Resolution - 1);
        int x0 = (int)fx;
        int y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, Resolution - 1);
        int y1 = Math.Min(y0 + 1, Resolution - 1);
        float tx = fx - x0;
        float ty = fy - y0;

        float a = float.Lerp(_cells[Index(x0, y0) + channel], _cells[Index(x1, y0) + channel], tx);
        float b = float.Lerp(_cells[Index(x0, y1) + channel], _cells[Index(x1, y1) + channel], tx);
        return float.Lerp(a, b, ty);
    }
}
