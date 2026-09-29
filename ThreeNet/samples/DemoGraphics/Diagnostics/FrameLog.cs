namespace DemoGraphics.Diagnostics;

/// <summary>
/// A rolling record of frame times. Everything the app reports about performance
/// - the read-out, the ribbon and the benchmark - is computed from this one
/// buffer, so the numbers on screen and the numbers in an exported run agree.
/// </summary>
public sealed class FrameLog
{
    private readonly float[] _samples;
    private int _next;
    private int _count;
    private float _windowMs;
    private int _windowFrames;
    private float _fps;

    public FrameLog(int capacity = 2048)
    {
        _samples = new float[Math.Max(16, capacity)];
    }

    /// <summary>Frames currently held.</summary>
    public int Count => _count;

    public int Capacity => _samples.Length;

    /// <summary>Most recent frame time in milliseconds.</summary>
    public float LatestMs { get; private set; }

    /// <summary>Frames per second over the last half second, not since startup.</summary>
    public float Fps => _fps;

    public void Add(float milliseconds)
    {
        if (!float.IsFinite(milliseconds) || milliseconds <= 0f)
        {
            return;
        }

        LatestMs = milliseconds;
        _samples[_next] = milliseconds;
        _next = (_next + 1) % _samples.Length;
        _count = Math.Min(_count + 1, _samples.Length);

        // A short window keeps the rate honest while the scene changes.
        _windowMs += milliseconds;
        _windowFrames++;
        if (_windowMs >= 500f)
        {
            _fps = _windowFrames / (_windowMs / 1000f);
            _windowMs = 0f;
            _windowFrames = 0;
        }
    }

    public void Clear()
    {
        Array.Clear(_samples);
        _next = 0;
        _count = 0;
        _windowMs = 0f;
        _windowFrames = 0;
        _fps = 0f;
        LatestMs = 0f;
    }

    /// <summary>Oldest to newest, at most <paramref name="destination"/> long.</summary>
    public int Recent(Span<float> destination)
    {
        int take = Math.Min(destination.Length, _count);
        for (int i = 0; i < take; i++)
        {
            // _next points at the slot the next sample goes into, so walking
            // back from it gives newest first; fill the span in reverse.
            int index = ((_next - 1 - i) % _samples.Length + _samples.Length) % _samples.Length;
            destination[take - 1 - i] = _samples[index];
        }

        return take;
    }

    /// <summary>Every sample held, oldest first.</summary>
    public float[] Snapshot()
    {
        float[] copy = new float[_count];
        Recent(copy);
        return copy;
    }

    public float AverageMs
    {
        get
        {
            if (_count == 0)
            {
                return 0f;
            }

            float sum = 0f;
            foreach (float sample in Snapshot())
            {
                sum += sample;
            }

            return sum / _count;
        }
    }

    public float MinMs => _count == 0 ? 0f : Snapshot().Min();

    public float MaxMs => _count == 0 ? 0f : Snapshot().Max();

    /// <summary>Frame time at a percentile, 0-100 (95 = the slow tail).</summary>
    public float PercentileMs(float percentile)
    {
        if (_count == 0)
        {
            return 0f;
        }

        float[] sorted = Snapshot();
        Array.Sort(sorted);
        int index = (int)MathF.Round(Math.Clamp(percentile, 0f, 100f) / 100f * (sorted.Length - 1));
        return sorted[index];
    }

    /// <summary>
    /// The 1% low frame rate: the average of the slowest one percent of frames,
    /// which is what a stutter actually feels like.
    /// </summary>
    public float OnePercentLowFps
    {
        get
        {
            if (_count < 10)
            {
                return 0f;
            }

            float[] sorted = Snapshot();
            Array.Sort(sorted);
            int take = Math.Max(1, sorted.Length / 100);
            float sum = 0f;
            for (int i = 0; i < take; i++)
            {
                sum += sorted[^(i + 1)];
            }

            float average = sum / take;
            return average <= 0f ? 0f : 1000f / average;
        }
    }

    /// <summary>
    /// Frames that took more than <paramref name="factor"/> times the median.
    /// Counting against the median rather than a fixed budget keeps the number
    /// meaningful on slow and fast machines alike.
    /// </summary>
    public int Stutters(float factor = 2f)
    {
        if (_count < 10)
        {
            return 0;
        }

        float median = PercentileMs(50f);
        if (median <= 0f)
        {
            return 0;
        }

        float threshold = median * factor;
        int count = 0;
        foreach (float sample in Snapshot())
        {
            if (sample > threshold)
            {
                count++;
            }
        }

        return count;
    }
}
