using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// Cross fading, additive animation and the audio analysis tap. None of these
/// need a GPU; the audio ones render the mixer offline.
/// </summary>
public class MixerAndAnalysisTests
{
    private static (Scene Scene, Node Node, AnimationClip Clip) SlideClip(float distance)
    {
        Scene scene = new();
        Node node = scene.AddMesh(scene.CreateBoxGeometry(), scene.CreateMaterial(Vector4.One));
        AnimationClip clip = scene.CreateAnimation("slide")
            .AddTranslation(node, [0f, 1f], [Vector3.Zero, new Vector3(distance, 0f, 0f)]);
        return (scene, node, clip);
    }

    [Fact]
    public void ACrossFadeHandsOverBetweenTwoClips()
    {
        (Scene scene, Node node, AnimationClip first) = SlideClip(10f);
        using Scene owned = scene;
        AnimationClip second = scene.CreateAnimation("lift")
            .AddTranslation(node, [0f, 1f], [Vector3.Zero, new Vector3(0f, 10f, 0f)]);

        AnimationPlayer from = first.Play(loop: true);
        AnimationPlayer to = second.Play(loop: true, weight: 0f);

        AnimationMixer mixer = new();
        mixer.CrossFade(from, to, seconds: 1f);
        Assert.Equal(2, mixer.Active);

        // Halfway through, both clips should be pulling.
        for (int i = 0; i < 30; i++)
        {
            mixer.Update(1f / 60f);
        }

        Assert.InRange(from.Weight, 0.3f, 0.7f);
        Assert.InRange(to.Weight, 0.3f, 0.7f);

        // And at the end the handover is complete and the fades are gone.
        for (int i = 0; i < 40; i++)
        {
            mixer.Update(1f / 60f);
        }

        Assert.Equal(0f, from.Weight, 3);
        Assert.Equal(1f, to.Weight, 3);
        Assert.Equal(0, mixer.Active);
    }

    [Fact]
    public void AZeroLengthFadeLandsImmediately()
    {
        (Scene scene, _, AnimationClip clip) = SlideClip(1f);
        using Scene owned = scene;
        AnimationPlayer player = clip.Play(weight: 0f);

        AnimationMixer mixer = new();
        mixer.FadeTo(player, 1f, 0f);

        Assert.Equal(1f, player.Weight, 3);
        Assert.Equal(0, mixer.Active);
    }

    [Fact]
    public void AnAdditiveClipAddsToThePoseInsteadOfReplacingIt()
    {
        (Scene scene, Node node, AnimationClip walk) = SlideClip(10f);
        using Scene owned = scene;

        // A second clip that lifts, played additively against its own pose at
        // time zero - which is the origin, so what it adds is its own travel.
        AnimationClip lean = scene.CreateAnimation("lean")
            .AddTranslation(node, [0f, 1f], [Vector3.Zero, new Vector3(0f, 2f, 0f)]);

        walk.Play(loop: false);
        lean.Play(loop: false, additive: true);

        scene.UpdateAnimations(0.5f);

        // The walk put it at 5 along x; the additive clip added 1 up.
        Assert.Equal(5f, node.Position.X, 2);
        Assert.Equal(1f, node.Position.Y, 2);
    }

    [Fact]
    public void AnAdditiveClipMeasuresAgainstItsReferenceTime()
    {
        (Scene scene, Node node, AnimationClip walk) = SlideClip(10f);
        using Scene owned = scene;

        AnimationClip lean = scene.CreateAnimation("lean")
            .AddTranslation(node, [0f, 1f], [new Vector3(0f, 4f, 0f), new Vector3(0f, 6f, 0f)]);

        walk.Play(loop: false);
        // Neutral is the end of the clip, so halfway through it is 1 below it.
        lean.Play(loop: false, additive: true, referenceTime: 1f);

        scene.UpdateAnimations(0.5f);

        Assert.Equal(5f, node.Position.X, 2);
        Assert.Equal(-1f, node.Position.Y, 2);
    }

    [Fact]
    public void AnAdditiveClipAtZeroWeightChangesNothing()
    {
        (Scene scene, Node node, AnimationClip walk) = SlideClip(10f);
        using Scene owned = scene;
        AnimationClip lean = scene.CreateAnimation("lean")
            .AddTranslation(node, [0f, 1f], [Vector3.Zero, new Vector3(0f, 8f, 0f)]);

        walk.Play(loop: false);
        lean.Play(loop: false, weight: 0f, additive: true);

        scene.UpdateAnimations(0.5f);
        Assert.Equal(0f, node.Position.Y, 3);
    }

    [Fact]
    public void TheAnalyserReportsTheLevelOfWhatItRendered()
    {
        using AudioEngine engine = AudioEngine.CreateOffline(48000);
        engine.EnableAnalysis(1024);
        Assert.Equal(1024, engine.AnalysisSize);

        // A loud tone, rendered offline so no device is needed.
        float[] samples = new float[48000];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = MathF.Sin(MathF.Tau * 440f * i / 48000f) * 0.8f;
        }

        AudioClip clip = engine.CreateClip(samples, channels: 1, sampleRate: 48000);
        engine.Play(clip, SoundOptions.Flat(gain: 1f));

        float[] block = new float[4096];
        engine.Render(block);

        Assert.True(engine.Level > 0.1f, $"a loud tone should register, got {engine.Level}");

        float[] waveform = new float[1024];
        Assert.Equal(1024, engine.GetWaveform(waveform));
        Assert.Contains(waveform, sample => MathF.Abs(sample) > 0.1f);
    }

    [Fact]
    public void TheSpectrumFindsTheToneItWasGiven()
    {
        using AudioEngine engine = AudioEngine.CreateOffline(48000);
        const int Size = 1024;
        engine.EnableAnalysis(Size);

        const float Frequency = 1500f;
        float[] samples = new float[48000];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = MathF.Sin(MathF.Tau * Frequency * i / 48000f) * 0.9f;
        }

        AudioClip clip = engine.CreateClip(samples, channels: 1, sampleRate: 48000);
        engine.Play(clip, SoundOptions.Flat(gain: 1f));

        // Render more than the window, so the ring holds nothing but the tone.
        float[] block = new float[8192];
        engine.Render(block);

        float[] spectrum = new float[Size / 2];
        Assert.Equal(Size / 2, engine.GetSpectrum(spectrum));

        int loudest = 0;
        for (int i = 1; i < spectrum.Length; i++)
        {
            if (spectrum[i] > spectrum[loudest])
            {
                loudest = i;
            }
        }

        // Bin i is centred on i * rate / size hertz, so the peak should sit on
        // the bin nearest the tone, give or take the window's spread.
        float peak = loudest * 48000f / Size;
        Assert.InRange(peak, Frequency - 100f, Frequency + 100f);
    }

    [Fact]
    public void AnalysisIsOffUntilItIsAskedFor()
    {
        using AudioEngine engine = AudioEngine.CreateOffline(48000);
        Assert.Equal(0, engine.AnalysisSize);
        Assert.Equal(0f, engine.Level);

        engine.EnableAnalysis(256);
        Assert.Equal(256, engine.AnalysisSize);

        engine.EnableAnalysis(0);
        Assert.Equal(0, engine.AnalysisSize);
    }
}
