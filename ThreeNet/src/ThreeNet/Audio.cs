using System.Numerics;
using ThreeNet.Interop;

namespace ThreeNet;

/// <summary>Decoded sound data owned by an <see cref="AudioEngine"/>.</summary>
public sealed class AudioClip
{
    internal AudioClip(AudioEngine engine, uint id)
    {
        Engine = engine;
        Id = id;
    }

    public AudioEngine Engine { get; }

    public uint Id { get; }

    public TimeSpan Duration
    {
        get
        {
            float seconds = NativeMethods.tn_audio_clip_duration(Engine.Handle, Id);
            return seconds < 0f ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
        }
    }

    public void Remove() => NativeMethods.tn_audio_remove_clip(Engine.Handle, Id);
}

/// <summary>How a sound plays. Spatial sounds are positioned relative to the listener.</summary>
public record struct SoundOptions
{
    public float Gain;
    /// <summary>Playback rate (1 = original pitch).</summary>
    public float Pitch;
    public bool Loop;
    public bool Spatial;
    public Vector3 Position;
    public Vector3 Velocity;
    /// <summary>Full volume inside this distance.</summary>
    public float MinDistance;
    /// <summary>Attenuation stops changing beyond this distance.</summary>
    public float MaxDistance;
    /// <summary>Inverse distance rolloff (1 = physically based).</summary>
    public float Rolloff;
    /// <summary>Follows this node's world position (velocity derived from its motion).</summary>
    public Node? FollowNode;
    public bool Paused;

    public SoundOptions()
    {
        Gain = 1f;
        Pitch = 1f;
        MinDistance = 1f;
        MaxDistance = 100f;
        Rolloff = 1f;
    }

    /// <summary>A non-spatial sound (music, UI).</summary>
    public static SoundOptions Flat(float gain = 1f, bool loop = false) => new() { Gain = gain, Loop = loop };

    /// <summary>A 3D sound at a position.</summary>
    public static SoundOptions At(Vector3 position, float gain = 1f, bool loop = false) => new() { Gain = gain, Loop = loop, Spatial = true, Position = position };

    /// <summary>A 3D sound attached to a node.</summary>
    public static SoundOptions On(Node node, float gain = 1f, bool loop = true) => new() { Gain = gain, Loop = loop, Spatial = true, FollowNode = node };

    internal readonly NativeSoundDesc ToNative(uint clip) => new()
    {
        Clip = clip,
        Gain = Gain,
        Pitch = Pitch,
        Looping = Loop ? 1 : 0,
        Spatial = Spatial ? 1 : 0,
        Position = Position,
        Velocity = Velocity,
        MinDistance = MinDistance,
        MaxDistance = MaxDistance,
        Rolloff = Rolloff,
        Node = FollowNode?.Id ?? 0,
        Paused = Paused ? 1 : 0,
    };
}

/// <summary>A playing sound.</summary>
public sealed class SoundInstance
{
    private readonly AudioClip _clip;

    internal SoundInstance(AudioClip clip, uint id)
    {
        _clip = clip;
        Id = id;
    }

    public uint Id { get; }

    /// <summary>False once a one-shot sound ended or <see cref="Stop"/> was called.</summary>
    public bool IsPlaying => NativeMethods.tn_audio_is_playing(_clip.Engine.Handle, Id) == 1;

    /// <summary>Playback position; setting it seeks.</summary>
    public TimeSpan Time
    {
        get
        {
            float seconds = NativeMethods.tn_audio_get_time(_clip.Engine.Handle, Id);
            return seconds < 0f ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
        }
        set => NativeMethods.tn_audio_seek(_clip.Engine.Handle, Id, (float)value.TotalSeconds);
    }

    /// <summary>Current settings; setting them changes the sound live (gain, pitch, position, ...).</summary>
    public SoundOptions Options
    {
        get
        {
            NativeError.Check(NativeMethods.tn_audio_get_source(_clip.Engine.Handle, Id, out NativeSoundDesc n));
            return new SoundOptions
            {
                Gain = n.Gain,
                Pitch = n.Pitch,
                Loop = n.Looping != 0,
                Spatial = n.Spatial != 0,
                Position = n.Position,
                Velocity = n.Velocity,
                MinDistance = n.MinDistance,
                MaxDistance = n.MaxDistance,
                Rolloff = n.Rolloff,
                FollowNode = n.Node == 0 || _clip.Engine.BoundScene is null ? null : new Node(_clip.Engine.BoundScene, n.Node),
                Paused = n.Paused != 0,
            };
        }
        set
        {
            NativeSoundDesc native = value.ToNative(_clip.Id);
            NativeError.Check(NativeMethods.tn_audio_update_source(_clip.Engine.Handle, Id, in native));
        }
    }

    public void Update(Func<SoundOptions, SoundOptions> change) => Options = change(Options);

    public void Stop() => NativeMethods.tn_audio_stop(_clip.Engine.Handle, Id);
}

/// <summary>
/// Audio output with a software mixer: 3D panning, distance attenuation, Doppler
/// and air absorption. Clips decode from WAV, OGG Vorbis, MP3 and FLAC, or come
/// from raw samples. Call <see cref="Update"/> each frame to follow the camera
/// and node attached sounds.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private nint _handle;
    private float _masterGain = 1f;
    private float _dopplerFactor = 1f;
    private float _speedOfSound = 343f;

    private AudioEngine(nint handle) => _handle = handle;

    /// <summary>Opens the default output device. Throws when there is none.</summary>
    public static AudioEngine Open()
    {
        nint handle = NativeMethods.tn_audio_create(0, 0);
        return handle == nint.Zero
            ? throw new ThreeNetException($"cannot open audio output: {NativeError.GetLastMessage()}")
            : new AudioEngine(handle);
    }

    /// <summary>An engine without a device, rendered with <see cref="Render"/> (tests, bouncing to files).</summary>
    public static AudioEngine CreateOffline(int sampleRate = 48000) => new(NativeMethods.tn_audio_create(1, (uint)sampleRate));

    internal nint Handle => _handle != nint.Zero ? _handle : throw new ObjectDisposedException(nameof(AudioEngine));

    internal Scene? BoundScene { get; private set; }

    public unsafe string DeviceName => NativeError.ReadString((buffer, capacity) =>
        NativeMethods.tn_audio_get_info(Handle, out _, (byte*)buffer, capacity));

    public unsafe int SampleRate
    {
        get
        {
            NativeMethods.tn_audio_get_info(Handle, out uint rate, null, 0);
            return (int)rate;
        }
    }

    public float MasterGain
    {
        get => _masterGain;
        set
        {
            _masterGain = value;
            Configure();
        }
    }

    /// <summary>0 disables the Doppler effect; 1 is physically based.</summary>
    public float DopplerFactor
    {
        get => _dopplerFactor;
        set
        {
            _dopplerFactor = value;
            Configure();
        }
    }

    public float SpeedOfSound
    {
        get => _speedOfSound;
        set
        {
            _speedOfSound = value;
            Configure();
        }
    }

    private void Configure() => NativeError.Check(NativeMethods.tn_audio_configure(Handle, _masterGain, _dopplerFactor, _speedOfSound));

    public unsafe AudioClip LoadClip(ReadOnlySpan<byte> encoded)
    {
        fixed (byte* pointer = encoded)
        {
            uint id = NativeMethods.tn_audio_load_clip(Handle, pointer, (uint)encoded.Length);
            return new AudioClip(this, NativeError.CheckHandle(id));
        }
    }

    public AudioClip LoadClip(string path) => LoadClip(File.ReadAllBytes(path));

    /// <summary>A clip from interleaved samples in [-1, 1] (1 or 2 channels).</summary>
    public unsafe AudioClip CreateClip(ReadOnlySpan<float> samples, int channels, int sampleRate)
    {
        fixed (float* pointer = samples)
        {
            uint id = NativeMethods.tn_audio_create_clip(Handle, pointer, (uint)samples.Length, (uint)channels, (uint)sampleRate);
            return new AudioClip(this, NativeError.CheckHandle(id));
        }
    }

    public SoundInstance Play(AudioClip clip, in SoundOptions options)
    {
        NativeSoundDesc native = options.ToNative(clip.Id);
        uint id = NativeMethods.tn_audio_play(Handle, in native);
        return new SoundInstance(clip, NativeError.CheckHandle(id));
    }

    public SoundInstance Play(AudioClip clip) => Play(clip, SoundOptions.Flat());

    public void SetListener(Vector3 position, Vector3 forward, Vector3 up, Vector3 velocity = default) =>
        NativeError.Check(NativeMethods.tn_audio_set_listener(Handle, position, forward, up, velocity));

    /// <summary>Puts the listener on <paramref name="listener"/> (typically the camera) and moves node attached sounds.</summary>
    public void Update(Scene scene, Node? listener, float deltaSeconds)
    {
        BoundScene = scene;
        NativeError.Check(NativeMethods.tn_audio_sync_scene(Handle, scene.Handle, listener?.Id ?? 0, deltaSeconds));
    }

    /// <summary>Renders interleaved stereo into <paramref name="stereo"/> (offline engines only).</summary>
    public unsafe int Render(Span<float> stereo)
    {
        fixed (float* pointer = stereo)
        {
            int frames = NativeMethods.tn_audio_render(Handle, pointer, (uint)(stereo.Length / 2));
            NativeError.Check(frames);
            return frames;
        }
    }

    public void Dispose()
    {
        if (_handle != nint.Zero)
        {
            NativeMethods.tn_audio_destroy(_handle);
            _handle = nint.Zero;
        }
    }
}
