using System.Runtime.Versioning;

namespace MotoCross.Game.Audio;

/// <summary>
/// The whole soundtrack, synthesised in real time: a four stroke single whose
/// pitch follows the revs, intake and exhaust noise, wind that rises with
/// speed, tyre scrub while sliding, landing thumps and lap chimes.
/// On platforms without <c>winmm</c> it silently does nothing.
/// </summary>
public sealed class GameAudio : IDisposable
{
    private readonly object _gate = new();
    private readonly WaveOutPlayer? _player;
    private readonly Random _random = new(7);
    private readonly List<OneShot> _oneShots = [];

    private float _revs;
    private float _throttle;
    private float _speed;
    private float _slide;
    private bool _grounded = true;
    private float _masterVolume = 0.7f;

    private double _enginePhase;
    private double _harmonicPhase;
    private double _fireGate;
    private float _noiseState;
    private float _windState;

    public GameAudio()
    {
        if (!OperatingSystem.IsWindows())
        {
            Unavailable = "audio needs Windows (winmm)";
            return;
        }

        try
        {
            _player = CreatePlayer();
        }
        catch (Exception error)
        {
            Unavailable = error.Message;
        }
    }

    [SupportedOSPlatform("windows")]
    private WaveOutPlayer CreatePlayer() => new(Fill);

    /// <summary>Set when no audio device could be opened; shown in the HUD.</summary>
    public string? Unavailable { get; }

    public bool Muted { get; set; }

    public float Volume
    {
        get => _masterVolume;
        set => _masterVolume = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Feeds the synth with the current state of the bike.</summary>
    public void Update(BikePhysics bike)
    {
        lock (_gate)
        {
            _revs = bike.Revs;
            _throttle = Math.Clamp(bike.Revs + 0.1f, 0f, 1f);
            _speed = Math.Clamp(MathF.Abs(bike.Speed) / 32f, 0f, 1f);
            _slide = bike.Slide;
            _grounded = bike.Grounded;
            if (bike.LandingImpact > 0.05f)
            {
                _oneShots.Add(new OneShot(OneShotKind.Landing, 0f, 0.45f, bike.LandingImpact));
            }
        }
    }

    public void PlayCheckpoint() => Trigger(OneShotKind.Checkpoint, 0.35f, 0.5f);

    public void PlayLap() => Trigger(OneShotKind.Lap, 0.9f, 0.6f);

    public void PlayFinish() => Trigger(OneShotKind.Finish, 1.6f, 0.7f);

    private void Trigger(OneShotKind kind, float duration, float gain)
    {
        lock (_gate)
        {
            _oneShots.Add(new OneShot(kind, 0f, duration, gain));
        }
    }

    private void Fill(float[] buffer, int frames)
    {
        float revs;
        float throttle;
        float speed;
        float slide;
        bool grounded;
        OneShot[] shots;
        lock (_gate)
        {
            revs = _revs;
            throttle = _throttle;
            speed = _speed;
            slide = _slide;
            grounded = _grounded;
            shots = _oneShots.ToArray();
            _oneShots.Clear();
        }

        float master = Muted ? 0f : _masterVolume;
        if (master <= 0f)
        {
            return;
        }

        const double rate = WaveOutPlayer.SampleRate;
        // A 450 four stroke idles near 1500 rpm and screams past 11000.
        double frequency = 25.0 + (revs * 145.0);
        double step = frequency / rate;

        // Keep long running one shots alive between buffers.
        List<OneShot> alive = [];
        foreach (OneShot shot in shots)
        {
            alive.Add(shot);
        }

        for (int i = 0; i < frames; i++)
        {
            _enginePhase += step;
            if (_enginePhase >= 1.0)
            {
                _enginePhase -= 1.0;
            }

            _harmonicPhase += step * 2.0;
            if (_harmonicPhase >= 1.0)
            {
                _harmonicPhase -= 1.0;
            }

            // Sawtooth body plus a second harmonic, shaped by the firing pulse.
            float saw = (float)((_enginePhase * 2.0) - 1.0);
            float harmonic = (float)Math.Sin(_harmonicPhase * Math.Tau) * 0.4f;
            _fireGate += step;
            if (_fireGate >= 1.0)
            {
                _fireGate -= 1.0;
            }

            float pulse = (float)Math.Pow(Math.Max(0.0, Math.Sin(_fireGate * Math.PI)), 3.0);
            float rasp = Noise1() * (0.12f + (revs * 0.28f));
            float engine = ((saw * 0.55f) + harmonic + rasp) * (0.25f + (pulse * 0.75f));
            engine *= 0.18f + (throttle * 0.42f);
            if (!grounded)
            {
                // Free revving is brighter and a little quieter.
                engine *= 0.85f;
            }

            // Wind grows with speed, tyre scrub with the slide angle.
            _windState = (_windState * 0.98f) + (Noise1() * 0.02f);
            float wind = _windState * speed * speed * 0.55f;
            float scrub = Noise1() * slide * (grounded ? 0.28f : 0f);

            float sample = engine + wind + scrub;

            // One shots: landing thump, checkpoint beep, lap and finish chimes.
            for (int s = 0; s < alive.Count; s++)
            {
                OneShot shot = alive[s];
                if (shot.Time >= shot.Duration)
                {
                    continue;
                }

                float t = shot.Time / shot.Duration;
                float envelope = MathF.Exp(-4f * t) * shot.Gain;
                sample += shot.Kind switch
                {
                    OneShotKind.Landing => (MathF.Sin((float)(shot.Time * 90.0 * Math.Tau)) * 0.7f + (Noise1() * 0.6f)) * envelope,
                    OneShotKind.Checkpoint => MathF.Sin((float)(shot.Time * 880.0 * Math.Tau)) * envelope * 0.5f,
                    OneShotKind.Lap => (MathF.Sin((float)(shot.Time * 660.0 * Math.Tau)) + MathF.Sin((float)(shot.Time * 990.0 * Math.Tau))) * envelope * 0.35f,
                    _ => (MathF.Sin((float)(shot.Time * 523.25 * Math.Tau)) + MathF.Sin((float)(shot.Time * 783.99 * Math.Tau))
                        + MathF.Sin((float)(shot.Time * 1046.5 * Math.Tau))) * envelope * 0.3f,
                };

                alive[s] = shot with { Time = shot.Time + (float)(1.0 / rate) };
            }

            sample = MathF.Tanh(sample * 1.2f) * master;
            buffer[(i * 2)] = sample;
            buffer[(i * 2) + 1] = sample;
        }

        lock (_gate)
        {
            foreach (OneShot shot in alive)
            {
                if (shot.Time < shot.Duration)
                {
                    _oneShots.Add(shot);
                }
            }
        }
    }

    private float Noise1()
    {
        // Cheap pink-ish noise: a one pole filter over white noise.
        float white = ((float)_random.NextDouble() * 2f) - 1f;
        _noiseState = (_noiseState * 0.86f) + (white * 0.14f);
        return _noiseState * 2.2f;
    }

    public void Dispose() => _player?.Dispose();

    private enum OneShotKind
    {
        Landing,
        Checkpoint,
        Lap,
        Finish,
    }

    private readonly record struct OneShot(OneShotKind Kind, float Time, float Duration, float Gain);
}
