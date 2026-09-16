using System.Numerics;
using ThreeNet.Interop;

namespace ThreeNet;

/// <summary>Node property an animation channel drives.</summary>
public enum AnimationPath : uint
{
    Translation = 0,
    /// <summary>Quaternion keys (x, y, z, w).</summary>
    Rotation = 1,
    Scale = 2,
}

/// <summary>How values between keyframes are computed.</summary>
public enum Interpolation : uint
{
    Step = 0,
    Linear = 1,
    /// <summary>Hermite spline; each key stores (in tangent, value, out tangent).</summary>
    CubicSpline = 2,
}

/// <summary>
/// A keyframe animation clip, imported from glTF / FBX or built with
/// <see cref="Scene.CreateAnimation"/>. Play it with <see cref="Play"/> and advance
/// time with <see cref="Scene.UpdateAnimations"/>. Skinned meshes are deformed
/// automatically.
/// </summary>
public sealed class AnimationClip : IEquatable<AnimationClip>
{
    internal AnimationClip(Scene scene, uint id)
    {
        Scene = scene;
        Id = id;
    }

    public Scene Scene { get; }

    public uint Id { get; }

    public unsafe string Name => NativeError.ReadString((buffer, capacity) =>
        NativeMethods.tn_animation_get_name(Scene.Handle, Id, (byte*)buffer, capacity));

    /// <summary>Length in seconds (the last key time of any channel).</summary>
    public float Duration
    {
        get
        {
            NativeError.Check(NativeMethods.tn_animation_get_duration(Scene.Handle, Id, out float duration));
            return duration;
        }
    }

    /// <summary>Adds translation keys for <paramref name="node"/>.</summary>
    public AnimationClip AddTranslation(Node node, ReadOnlySpan<float> times, ReadOnlySpan<Vector3> values, Interpolation interpolation = Interpolation.Linear) =>
        AddChannel(node, AnimationPath.Translation, interpolation, times, System.Runtime.InteropServices.MemoryMarshal.Cast<Vector3, float>(values));

    /// <summary>Adds scale keys for <paramref name="node"/>.</summary>
    public AnimationClip AddScale(Node node, ReadOnlySpan<float> times, ReadOnlySpan<Vector3> values, Interpolation interpolation = Interpolation.Linear) =>
        AddChannel(node, AnimationPath.Scale, interpolation, times, System.Runtime.InteropServices.MemoryMarshal.Cast<Vector3, float>(values));

    /// <summary>Adds rotation keys for <paramref name="node"/> (spherically interpolated).</summary>
    public AnimationClip AddRotation(Node node, ReadOnlySpan<float> times, ReadOnlySpan<Quaternion> values, Interpolation interpolation = Interpolation.Linear) =>
        AddChannel(node, AnimationPath.Rotation, interpolation, times, System.Runtime.InteropServices.MemoryMarshal.Cast<Quaternion, float>(values));

    /// <summary>Adds a raw channel; <paramref name="values"/> holds 3 or 4 floats per key (times 3 for cubic splines).</summary>
    public unsafe AnimationClip AddChannel(Node node, AnimationPath path, Interpolation interpolation, ReadOnlySpan<float> times, ReadOnlySpan<float> values)
    {
        ArgumentNullException.ThrowIfNull(node);
        fixed (float* timePointer = times)
        fixed (float* valuePointer = values)
        {
            NativeError.Check(NativeMethods.tn_animation_add_channel(
                Scene.Handle, Id, node.Id, (uint)path, (uint)interpolation,
                timePointer, (uint)times.Length, valuePointer, (uint)values.Length));
        }

        return this;
    }

    /// <summary>Starts playing the clip and returns its player.</summary>
    public AnimationPlayer Play(bool loop = true, float speed = 1f, float weight = 1f)
    {
        NativePlayerDesc desc = new()
        {
            Time = 0f,
            Speed = speed,
            Weight = weight,
            Looping = loop ? 1 : 0,
            Playing = 1,
        };
        uint id = NativeMethods.tn_animation_play(Scene.Handle, Id, in desc);
        return new AnimationPlayer(Scene, NativeError.CheckHandle(id));
    }

    public void Destroy() => NativeMethods.tn_animation_destroy(Scene.Handle, Id);

    public bool Equals(AnimationClip? other) => other is not null && Id == other.Id && ReferenceEquals(Scene, other.Scene);

    public override bool Equals(object? obj) => Equals(obj as AnimationClip);

    public override int GetHashCode() => HashCode.Combine(Scene, Id);

    public override string ToString() => $"AnimationClip(#{Id} {Name}, {Duration:0.##} s)";
}

/// <summary>A playing instance of a clip: time, speed, blend weight and looping.</summary>
public sealed class AnimationPlayer
{
    internal AnimationPlayer(Scene scene, uint id)
    {
        Scene = scene;
        Id = id;
    }

    public Scene Scene { get; }

    public uint Id { get; }

    private NativePlayerDesc Read()
    {
        NativeError.Check(NativeMethods.tn_player_get(Scene.Handle, Id, out NativePlayerDesc desc));
        return desc;
    }

    private void Write(NativePlayerDesc desc) => NativeError.Check(NativeMethods.tn_player_set(Scene.Handle, Id, in desc));

    /// <summary>Current position in seconds.</summary>
    public float Time
    {
        get => Read().Time;
        set => Write(Read() with { Time = value });
    }

    /// <summary>Playback rate; negative plays backwards.</summary>
    public float Speed
    {
        get => Read().Speed;
        set => Write(Read() with { Speed = value });
    }

    /// <summary>0..1 blend towards the clip's pose, for cross fades.</summary>
    public float Weight
    {
        get => Read().Weight;
        set => Write(Read() with { Weight = value });
    }

    public bool Loop
    {
        get => Read().Looping != 0;
        set => Write(Read() with { Looping = value ? 1 : 0 });
    }

    /// <summary>False once a non looping clip reached its end, or while paused.</summary>
    public bool IsPlaying
    {
        get => Read().Playing != 0;
        set => Write(Read() with { Playing = value ? 1 : 0 });
    }

    /// <summary>Removes the player; the pose it applied stays.</summary>
    public void Stop() => NativeMethods.tn_player_stop(Scene.Handle, Id);
}
