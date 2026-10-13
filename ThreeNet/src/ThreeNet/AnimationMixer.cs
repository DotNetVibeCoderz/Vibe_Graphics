namespace ThreeNet;

/// <summary>
/// Drives blend weights over time, so one clip can hand over to another instead
/// of snapping to it.
/// </summary>
/// <remarks>
/// <para>
/// The core already blends: every player carries a weight and the pose it
/// applies is mixed in by that much. What was missing is anything to move those
/// weights on a clock, which is all a cross fade is. The mixer owns that, so it
/// lives here rather than in the core - it needs no GPU and no interop beyond
/// setting a float.
/// </para>
/// <code>
/// AnimationMixer mixer = new();
/// AnimationPlayer idle = idleClip.Play();
/// AnimationPlayer run = runClip.Play(weight: 0f);
///
/// mixer.CrossFade(from: idle, to: run, seconds: 0.35f);
///
/// // every frame, before scene.UpdateAnimations
/// mixer.Update(deltaSeconds);
/// </code>
/// </remarks>
public sealed class AnimationMixer
{
    private readonly List<Fade> _fades = [];

    private sealed class Fade
    {
        public required AnimationPlayer Player;
        public required float From;
        public required float To;
        public required float Duration;
        public float Elapsed;
        public bool StopAtEnd;
    }

    /// <summary>How many weights are still moving.</summary>
    public int Active => _fades.Count;

    /// <summary>
    /// Takes <paramref name="player"/> to <paramref name="weight"/> over
    /// <paramref name="seconds"/>. A second call on the same player replaces the
    /// first, starting from wherever its weight has reached.
    /// </summary>
    public void FadeTo(AnimationPlayer player, float weight, float seconds)
    {
        _fades.RemoveAll(fade => fade.Player.Id == player.Id && ReferenceEquals(fade.Player.Scene, player.Scene));

        float target = Math.Clamp(weight, 0f, 1f);
        if (seconds <= 0f)
        {
            player.Weight = target;
            return;
        }

        _fades.Add(new Fade
        {
            Player = player,
            From = player.Weight,
            To = target,
            Duration = seconds,
        });
    }

    /// <summary>Fades a player up from silence.</summary>
    public void FadeIn(AnimationPlayer player, float seconds)
    {
        player.Weight = 0f;
        player.IsPlaying = true;
        FadeTo(player, 1f, seconds);
    }

    /// <summary>
    /// Fades a player out, and removes it once it is silent when
    /// <paramref name="stop"/> is set.
    /// </summary>
    public void FadeOut(AnimationPlayer player, float seconds, bool stop = false)
    {
        FadeTo(player, 0f, seconds);
        if (stop && _fades.Count > 0)
        {
            _fades[^1].StopAtEnd = true;
        }
    }

    /// <summary>
    /// Hands over from one clip to another: one fades out as the other fades in,
    /// over the same stretch of time.
    /// </summary>
    /// <param name="restart">Rewinds the incoming clip, so it starts from the top.</param>
    public void CrossFade(AnimationPlayer from, AnimationPlayer to, float seconds, bool restart = true)
    {
        if (restart)
        {
            to.Time = 0f;
        }

        to.IsPlaying = true;
        FadeTo(from, 0f, seconds);
        FadeTo(to, 1f, seconds);
    }

    /// <summary>Drops every fade in flight, leaving the weights where they are.</summary>
    public void Clear() => _fades.Clear();

    /// <summary>Advances the fades. Call once a frame, before updating animations.</summary>
    public void Update(float deltaSeconds)
    {
        for (int i = _fades.Count - 1; i >= 0; i--)
        {
            Fade fade = _fades[i];
            fade.Elapsed += deltaSeconds;
            float t = Math.Clamp(fade.Elapsed / fade.Duration, 0f, 1f);
            // Smoothstep: a linear fade starts and stops with a visible jerk.
            float eased = t * t * (3f - (2f * t));
            fade.Player.Weight = float.Lerp(fade.From, fade.To, eased);

            if (t >= 1f)
            {
                if (fade.StopAtEnd)
                {
                    fade.Player.IsPlaying = false;
                }

                _fades.RemoveAt(i);
            }
        }
    }
}
