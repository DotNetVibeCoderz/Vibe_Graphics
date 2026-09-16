using System.Numerics;

namespace MotoCross.Game;

/// <summary>A jump, roller section or berm placed at a position along the lap.</summary>
public readonly record struct TrackFeature(string Kind, float Start, float Length, float Height);

/// <summary>
/// The circuit: a closed Catmull-Rom loop through hand placed control points,
/// resampled to a uniform polyline so everything else (terrain stamping,
/// checkpoints, the mini map, the AI-free lap logic) can work in metres.
/// </summary>
public sealed class Track
{
    /// <summary>Rideable width in metres; the graded shoulder adds a little more.</summary>
    public const float HalfWidth = 4.2f;

    public const float ShoulderWidth = 3.0f;

    private readonly Vector3[] _controls;
    private readonly Vector3[] _samples;
    private readonly Vector3[] _tangents;
    private readonly float[] _cumulative;

    public Track(int sampleCount = 1600)
    {
        // A figure of eight-ish motocross loop: two long straights, a hairpin,
        // a fast sweeper and a rhythm section, all inside a 200 m square.
        _controls =
        [
            new(0f, 0f, 70f),
            new(38f, 0f, 64f),
            new(62f, 0f, 38f),
            new(70f, 0f, 4f),
            new(58f, 0f, -30f),
            new(28f, 0f, -48f),
            new(-6f, 0f, -52f),
            new(-34f, 0f, -38f),
            new(-44f, 0f, -8f),
            new(-30f, 0f, 20f),
            new(-52f, 0f, 40f),
            new(-56f, 0f, 66f),
            new(-30f, 0f, 78f),
        ];

        Features =
        [
            // Kind, start (metres along the lap), length, height.
            new("kicker", 30f, 16f, 2.6f),
            new("table", 95f, 26f, 2.2f),
            new("whoops", 150f, 34f, 0.85f),
            new("double", 230f, 22f, 3.1f),
            new("roller", 300f, 24f, 1.1f),
            new("kicker", 380f, 18f, 2.8f),
            new("whoops", 440f, 28f, 0.7f),
            new("table", 500f, 24f, 1.9f),
        ];

        // Resample the spline at a constant arc length so `t` is metres.
        List<Vector3> raw = [];
        int segments = _controls.Length;
        const int stepsPerSegment = 64;
        for (int segment = 0; segment < segments; segment++)
        {
            Vector3 p0 = _controls[(segment - 1 + segments) % segments];
            Vector3 p1 = _controls[segment];
            Vector3 p2 = _controls[(segment + 1) % segments];
            Vector3 p3 = _controls[(segment + 2) % segments];
            for (int step = 0; step < stepsPerSegment; step++)
            {
                raw.Add(Noise.CatmullRom(p0, p1, p2, p3, step / (float)stepsPerSegment));
            }
        }

        float rawLength = 0f;
        for (int i = 0; i < raw.Count; i++)
        {
            rawLength += Vector3.Distance(raw[i], raw[(i + 1) % raw.Count]);
        }

        Length = rawLength;
        _samples = new Vector3[sampleCount];
        _tangents = new Vector3[sampleCount];
        _cumulative = new float[sampleCount];

        float spacing = rawLength / sampleCount;
        int source = 0;
        float carried = 0f;
        Vector3 cursor = raw[0];
        for (int i = 0; i < sampleCount; i++)
        {
            _samples[i] = cursor;
            _cumulative[i] = i * spacing;

            float remaining = spacing;
            while (remaining > 0f)
            {
                Vector3 next = raw[(source + 1) % raw.Count];
                float available = Vector3.Distance(cursor, next) - carried;
                if (available > remaining)
                {
                    Vector3 direction = Vector3.Normalize(next - cursor);
                    cursor += direction * remaining;
                    carried = 0f;
                    remaining = 0f;
                }
                else
                {
                    cursor = next;
                    source = (source + 1) % raw.Count;
                    carried = 0f;
                    remaining -= MathF.Max(available, 0f);
                }
            }
        }

        for (int i = 0; i < sampleCount; i++)
        {
            Vector3 next = _samples[(i + 1) % sampleCount];
            Vector3 previous = _samples[(i - 1 + sampleCount) % sampleCount];
            _tangents[i] = Vector3.Normalize(next - previous);
        }

        // Elevation follows the terrain hills, then the features are added on top.
        for (int i = 0; i < sampleCount; i++)
        {
            Vector3 p = _samples[i];
            float rolling = ((Noise.Fbm(p.X * 0.006f, p.Z * 0.006f, 3, 1f, 11) - 0.5f) * 9f) + 1.2f;
            _samples[i].Y = rolling + FeatureHeight(_cumulative[i]);
        }

        // Smooth the profile so the bike never hits a step between samples.
        for (int pass = 0; pass < 2; pass++)
        {
            float[] smoothed = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                smoothed[i] = ((_samples[(i - 1 + sampleCount) % sampleCount].Y * 0.25f)
                    + (_samples[i].Y * 0.5f)
                    + (_samples[(i + 1) % sampleCount].Y * 0.25f));
            }

            for (int i = 0; i < sampleCount; i++)
            {
                _samples[i].Y = smoothed[i];
            }
        }

        // The grid sits 14 m before the start / finish gate, so the rider drives
        // under the banner right after the lights go out.
        int gridIndex = sampleCount - (int)(14f / (rawLength / sampleCount));
        StartPosition = _samples[gridIndex % sampleCount];
        StartHeading = MathF.Atan2(_tangents[gridIndex % sampleCount].X, _tangents[gridIndex % sampleCount].Z);
    }

    /// <summary>Total lap length in metres.</summary>
    public float Length { get; }

    public IReadOnlyList<Vector3> Samples => _samples;

    public TrackFeature[] Features { get; }

    public Vector3 StartPosition { get; }

    /// <summary>Yaw (radians) the bike starts with, looking down the first straight.</summary>
    public float StartHeading { get; }

    /// <summary>Curvature radius driven berm angle, used when shaping the terrain.</summary>
    public float BankAt(float distance)
    {
        int index = IndexAt(distance);
        int count = _samples.Length;
        Vector3 a = _tangents[(index - 6 + count) % count];
        Vector3 b = _tangents[(index + 6) % count];
        float turn = (a.X * b.Z) - (a.Z * b.X);
        return Math.Clamp(turn * 9f, -0.55f, 0.55f);
    }

    public Vector3 PositionAt(float distance) => _samples[IndexAt(distance)];

    public Vector3 TangentAt(float distance) => _tangents[IndexAt(distance)];

    public int IndexAt(float distance)
    {
        float wrapped = distance % Length;
        if (wrapped < 0f)
        {
            wrapped += Length;
        }

        int index = (int)(wrapped / Length * _samples.Length);
        return Math.Clamp(index, 0, _samples.Length - 1);
    }

    /// <summary>Height added by jumps, tables, rollers and whoops at a lap position.</summary>
    public float FeatureHeight(float distance)
    {
        float height = 0f;
        foreach (TrackFeature feature in Features)
        {
            float local = distance - feature.Start;
            if (local < -Length * 0.5f)
            {
                local += Length;
            }

            if (local < 0f || local > feature.Length)
            {
                continue;
            }

            float u = local / feature.Length;
            height += feature.Kind switch
            {
                // A kicker ramps up and drops off a lip.
                "kicker" => feature.Height * MathF.Pow(u, 1.6f) * (u > 0.92f ? 0.4f : 1f),
                // A table has a flat top between two faces.
                "table" => feature.Height * MathF.Min(1f, MathF.Min(u * 4f, (1f - u) * 4f)),
                // A double is two lips with a gap in between.
                "double" => feature.Height * (MathF.Max(0f, 1f - MathF.Abs((u * 4f) - 0.7f)) + MathF.Max(0f, 1f - MathF.Abs((u * 4f) - 3.3f))),
                // Rollers and whoops are repeating bumps.
                "roller" => feature.Height * MathF.Sin(u * MathF.PI) * MathF.Sin(u * MathF.PI * 3f),
                _ => feature.Height * MathF.Sin(u * MathF.PI) * MathF.Sin(u * MathF.PI * 7f),
            };
        }

        return height;
    }
}
