using System.Numerics;

namespace MotoCross.Game;

/// <summary>Where a point sits relative to the circuit.</summary>
public readonly record struct GroundSample(float Height, float DirtWeight, float Distance, float LapDistance);

/// <summary>
/// The ground the bike rides on. Heights are analytic rather than a baked grid:
/// rolling hills from noise, blended into the graded track corridor (with its
/// jumps, whoops and banked berms) through the shoulder band. The renderer gets
/// a coarse mesh for the landscape plus a fine ribbon for the track itself, but
/// physics always asks this function, so the bike never falls through a seam.
/// </summary>
public sealed class TerrainField(Track track)
{
    public const float WorldSize = 280f;
    private const float BucketSize = 8f;
    private const int Buckets = (int)(WorldSize / BucketSize) + 1;

    private readonly Track _track = track;
    private readonly List<int>[] _buckets = BuildBuckets(track);

    public Track Track => _track;

    private static List<int>[] BuildBuckets(Track track)
    {
        List<int>[] buckets = new List<int>[Buckets * Buckets];
        for (int i = 0; i < buckets.Length; i++)
        {
            buckets[i] = [];
        }

        for (int i = 0; i < track.Samples.Count; i++)
        {
            Vector3 p = track.Samples[i];
            int cx = BucketIndex(p.X);
            int cz = BucketIndex(p.Z);
            // Register in the neighbourhood too, so a lookup never has to scan
            // the surrounding cells.
            for (int dz = -2; dz <= 2; dz++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    int x = cx + dx;
                    int z = cz + dz;
                    if (x >= 0 && x < Buckets && z >= 0 && z < Buckets)
                    {
                        buckets[(z * Buckets) + x].Add(i);
                    }
                }
            }
        }

        return buckets;
    }

    private static int BucketIndex(float value) =>
        Math.Clamp((int)((value + (WorldSize * 0.5f)) / BucketSize), 0, Buckets - 1);

    /// <summary>Rolling landscape without the circuit graded into it.</summary>
    public static float HillHeight(float x, float z)
    {
        float broad = (Noise.Fbm(x * 0.006f, z * 0.006f, 3, 1f, 11) - 0.5f) * 9f;
        float medium = (Noise.Fbm(x * 0.02f, z * 0.02f, 3, 1f, 23) - 0.5f) * 2.6f;
        float detail = (Noise.Fbm(x * 0.09f, z * 0.09f, 2, 1f, 37) - 0.5f) * 0.5f;
        // The bowl keeps the circuit inside a natural basin.
        float bowl = ((x * x) + (z * z)) * 0.00035f;
        return broad + medium + detail + bowl + 1.2f;
    }

    /// <summary>Nearest point on the centreline: lateral offset and lap distance.</summary>
    public (float Lateral, float LapDistance, Vector3 Tangent) NearestTrackPoint(float x, float z)
    {
        List<int> candidates = _buckets[(BucketIndex(z) * Buckets) + BucketIndex(x)];
        if (candidates.Count == 0)
        {
            return (float.MaxValue, 0f, Vector3.UnitZ);
        }

        float best = float.MaxValue;
        int bestIndex = 0;
        foreach (int index in candidates)
        {
            Vector3 p = _track.Samples[index];
            float dx = p.X - x;
            float dz = p.Z - z;
            float distance = (dx * dx) + (dz * dz);
            if (distance < best)
            {
                best = distance;
                bestIndex = index;
            }
        }

        Vector3 centre = _track.Samples[bestIndex];
        Vector3 tangent = _track.TangentAt(bestIndex / (float)_track.Samples.Count * _track.Length);
        // Signed lateral offset: positive to the right of the travel direction.
        float lateral = ((x - centre.X) * tangent.Z) - ((z - centre.Z) * tangent.X);
        return (lateral, bestIndex / (float)_track.Samples.Count * _track.Length, tangent);
    }

    /// <summary>Ground height, dirt weight and lap position at a world position.</summary>
    public GroundSample Sample(float x, float z)
    {
        float hill = HillHeight(x, z);
        (float lateral, float lapDistance, _) = NearestTrackPoint(x, z);
        float absolute = MathF.Abs(lateral);
        if (absolute > Track.HalfWidth + Track.ShoulderWidth + 6f)
        {
            return new GroundSample(hill, 0f, absolute, lapDistance);
        }

        Vector3 centre = _track.PositionAt(lapDistance);
        float bank = _track.BankAt(lapDistance);
        // Banked berm on the outside of corners plus a slight crown for drainage.
        float crown = -0.05f * (absolute / Track.HalfWidth) * (absolute / Track.HalfWidth);
        float berm = bank * lateral * 0.9f;
        float rut = MathF.Sin(lapDistance * 0.8f) * 0.04f * MathF.Cos(lateral * 1.7f);
        float surface = centre.Y + crown + berm + rut;

        float blend = 1f - Smoothstep(Track.HalfWidth, Track.HalfWidth + Track.ShoulderWidth, absolute);
        float height = float.Lerp(hill, surface, blend);
        float dirt = 1f - Smoothstep(Track.HalfWidth * 0.9f, Track.HalfWidth + (Track.ShoulderWidth * 0.6f), absolute);
        return new GroundSample(height, dirt, absolute, lapDistance);
    }

    public float HeightAt(float x, float z) => Sample(x, z).Height;

    /// <summary>Surface normal from central differences.</summary>
    public Vector3 NormalAt(float x, float z)
    {
        const float e = 0.6f;
        float left = HeightAt(x - e, z);
        float right = HeightAt(x + e, z);
        float back = HeightAt(x, z - e);
        float front = HeightAt(x, z + e);
        return Vector3.Normalize(new Vector3(left - right, 2f * e, back - front));
    }

    private static float Smoothstep(float edge0, float edge1, float value)
    {
        float t = Math.Clamp((value - edge0) / MathF.Max(edge1 - edge0, 1e-4f), 0f, 1f);
        return t * t * (3f - (2f * t));
    }
}
