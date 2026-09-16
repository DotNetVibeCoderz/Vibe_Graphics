using System.Numerics;

namespace MotoCross.Game;

/// <summary>
/// Lap timing: checkpoints spread around the circuit have to be taken in order,
/// which keeps a rider from shortcutting across the infield.
/// </summary>
public sealed class RaceState(Track track, int laps = 3, int checkpoints = 10)
{
    private readonly Track _track = track;
    private readonly float[] _checkpointDistances = Enumerable
        .Range(0, checkpoints)
        .Select(i => track.Length * i / checkpoints)
        .ToArray();

    public int TotalLaps { get; } = laps;

    public int Lap { get; private set; } = 1;

    public int NextCheckpoint { get; private set; } = 1;

    public int CheckpointCount => _checkpointDistances.Length;

    public double LapTime { get; private set; }

    public double TotalTime { get; private set; }

    public double BestLap { get; private set; } = double.PositiveInfinity;

    public double LastLap { get; private set; }

    public bool Finished { get; private set; }

    /// <summary>Progress around the current lap, 0..1, for the HUD bar.</summary>
    public float LapProgress { get; private set; }

    /// <summary>Raised when a checkpoint is taken; the argument is the index.</summary>
    public event Action<int>? CheckpointPassed;

    public event Action<double>? LapCompleted;

    public event Action<double>? RaceFinished;

    public Vector3 CheckpointPosition(int index) => _track.PositionAt(_checkpointDistances[index % _checkpointDistances.Length]);

    public void Reset()
    {
        Lap = 1;
        NextCheckpoint = 1;
        LapTime = 0;
        TotalTime = 0;
        LastLap = 0;
        BestLap = double.PositiveInfinity;
        Finished = false;
        LapProgress = 0f;
    }

    /// <summary>Advances the timers and checks the next gate.</summary>
    public void Update(float dt, float lapDistance, float lateralDistance)
    {
        if (Finished)
        {
            return;
        }

        LapTime += dt;
        TotalTime += dt;

        float target = _checkpointDistances[NextCheckpoint % _checkpointDistances.Length];
        LapProgress = Math.Clamp(NextCheckpoint / (float)_checkpointDistances.Length, 0f, 1f);

        // A gate counts when the rider is close to it and still on the circuit.
        float delta = MathF.Abs(WrapDelta(lapDistance - target));
        if (delta > 9f || lateralDistance > Track.HalfWidth + Track.ShoulderWidth)
        {
            return;
        }

        int passed = NextCheckpoint;
        NextCheckpoint++;
        CheckpointPassed?.Invoke(passed % _checkpointDistances.Length);

        if (NextCheckpoint <= _checkpointDistances.Length)
        {
            return;
        }

        // Back over the start / finish line: the lap is done.
        NextCheckpoint = 1;
        LastLap = LapTime;
        BestLap = Math.Min(BestLap, LapTime);
        LapCompleted?.Invoke(LapTime);
        LapTime = 0;
        Lap++;
        if (Lap > TotalLaps)
        {
            Lap = TotalLaps;
            Finished = true;
            RaceFinished?.Invoke(TotalTime);
        }
    }

    private float WrapDelta(float delta)
    {
        float half = _track.Length * 0.5f;
        while (delta > half)
        {
            delta -= _track.Length;
        }

        while (delta < -half)
        {
            delta += _track.Length;
        }

        return delta;
    }

    public static string FormatTime(double seconds)
    {
        if (double.IsInfinity(seconds) || seconds <= 0)
        {
            return "--:--.---";
        }

        TimeSpan span = TimeSpan.FromSeconds(seconds);
        return $"{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds:000}";
    }
}
