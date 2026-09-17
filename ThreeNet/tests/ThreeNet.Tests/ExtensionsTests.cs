using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

public class ExtensionsTests
{
    [Fact]
    public void PhysicsMovesNodesAndReportsContacts()
    {
        using Scene scene = new();
        Node ground = scene.CreateNode(name: "ground");
        ground.Position = new Vector3(0f, -0.5f, 0f);
        scene.Physics.AddCollider(ground, ColliderOptions.Box(new Vector3(20f, 0.5f, 20f)));

        Node ball = scene.AddMesh(scene.CreateSphereGeometry(0.5f), scene.CreateMaterial(Vector4.One), name: "ball");
        ball.Position = new Vector3(0f, 4f, 0f);
        scene.Physics.Add(ball, RigidBodyOptions.Dynamic, ColliderOptions.Sphere(0.5f) with { Restitution = 0f });
        Assert.True(scene.Physics.HasBody(ball));

        List<ContactEvent> contacts = [];
        scene.Physics.Contact += contacts.Add;
        for (int i = 0; i < 240; i++)
        {
            scene.Physics.Step(1f / 60f);
        }

        Assert.Equal(0.5f, ball.Position.Y, 1);
        Assert.Contains(contacts, c => c.Started && c.Involves(ball) && c.Other(ball).Equals(ground));

        PhysicsHit? hit = scene.Physics.Raycast(new Vector3(0f, 10f, 0f), -Vector3.UnitY);
        Assert.NotNull(hit);
        Assert.Equal(ball, hit.Value.Node);
        Assert.Equal(9f, hit.Value.Distance, 1);

        scene.Physics.SetVelocity(ball, new Vector3(0f, 5f, 0f));
        Assert.Equal(5f, scene.Physics.GetVelocity(ball).Linear.Y, 3);
        scene.Physics.Teleport(ball, new Vector3(3f, 2f, 0f));
        Assert.Equal(3f, ball.Position.X, 3);

        Assert.Throws<ThreeNetException>(() => scene.Physics.AddBody(ball, RigidBodyOptions.Dynamic));
        Assert.True(scene.Physics.Remove(ball));
        Assert.False(scene.Physics.HasBody(ball));
    }

    [Fact]
    public void CharactersAndJoints()
    {
        using Scene scene = new();
        Geometry plane = scene.CreatePlaneGeometry(40f, 40f, 2, 2);
        Node floor = scene.AddMesh(plane, scene.CreateMaterial(Vector4.One), name: "floor");
        floor.EulerAngles = new Vector3(-MathF.PI / 2f, 0f, 0f);
        scene.Physics.AddCollider(floor, ColliderOptions.TriangleMesh(plane));

        Node player = scene.CreateNode(name: "player");
        player.Position = new Vector3(0f, 0.9f, 0f);
        scene.Physics.Add(player, RigidBodyOptions.Kinematic, ColliderOptions.Capsule(0.5f, 0.4f));
        scene.Physics.ConfigureCharacters(maxSlopeDegrees: 40f, stepHeight: 0.25f);
        scene.Physics.Step(1f / 60f);

        bool grounded = false;
        for (int i = 0; i < 60; i++)
        {
            (_, grounded) = scene.Physics.MoveCharacter(player, new Vector3(0.05f, -0.05f, 0f), 1f / 60f);
            scene.Physics.Step(1f / 60f);
        }

        Assert.True(grounded);
        Assert.True(player.Position.X > 2.5f, $"x = {player.Position.X}");

        Node anchor = scene.CreateNode(name: "anchor");
        anchor.Position = new Vector3(0f, 5f, 0f);
        scene.Physics.AddBody(anchor, RigidBodyOptions.Fixed);
        Node bob = scene.CreateNode(name: "pendulum");
        bob.Position = new Vector3(1f, 5f, 0f);
        scene.Physics.Add(bob, RigidBodyOptions.Dynamic, ColliderOptions.Sphere(0.1f) with { Filter = 0 });
        PhysicsJoint rope = scene.Physics.AddJoint(JointType.Ball, anchor, bob, Vector3.Zero, new Vector3(-1f, 0f, 0f));
        for (int i = 0; i < 30; i++)
        {
            scene.Physics.Step(1f / 60f);
        }

        Assert.Equal(1f, Vector3.Distance(bob.Position, anchor.Position), 1);
        Assert.True(bob.Position.Y < 4.9f, "the pendulum swings down");
        Assert.True(rope.Remove());
    }

    [Fact]
    public void OfflineAudioMixesSpatialSounds()
    {
        using AudioEngine audio = AudioEngine.CreateOffline(48000);
        Assert.Equal(48000, audio.SampleRate);

        float[] samples = new float[4800];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = MathF.Sin(i / 48000f * 440f * MathF.Tau) * 0.5f;
        }

        AudioClip clip = audio.CreateClip(samples, 1, 48000);
        Assert.Equal(0.1, clip.Duration.TotalSeconds, 3);

        using Scene scene = new();
        Node camera = scene.AddCamera(Camera.Perspective(), Vector3.Zero);
        Node speaker = scene.CreateNode(name: "speaker");
        speaker.Position = new Vector3(-4f, 0f, 0f);
        SoundInstance sound = audio.Play(clip, SoundOptions.On(speaker));
        audio.Update(scene, camera, 1f / 60f);

        float[] buffer = new float[2 * 2400];
        audio.Render(buffer);
        double left = 0, right = 0;
        for (int i = 0; i < buffer.Length; i += 2)
        {
            left += buffer[i] * buffer[i];
            right += buffer[i + 1] * buffer[i + 1];
        }

        Assert.True(left > right * 10, $"left {left} right {right}");
        Assert.True(sound.IsPlaying);
        Assert.True(sound.Options.Spatial);

        sound.Update(o => o with { Loop = false, Gain = 0.5f });
        audio.Render(buffer);
        audio.Render(buffer);
        Assert.False(sound.IsPlaying, "a one-shot ends");

        Assert.Throws<ThreeNetException>(() => audio.LoadClip([1, 2, 3, 4]));
    }

    [Fact]
    public void AudioDeviceOpensWhenPresent()
    {
        AudioEngine audio;
        try
        {
            audio = AudioEngine.Open();
        }
        catch (ThreeNetException)
        {
            return; // no output device (CI)
        }

        using (audio)
        {
            Assert.True(audio.SampleRate > 0);
            float[] silence = new float[audio.SampleRate / 20];
            SoundInstance sound = audio.Play(audio.CreateClip(silence, 1, audio.SampleRate));
            Thread.Sleep(150);
            Assert.False(sound.IsPlaying, $"the device ({audio.DeviceName}) consumes samples");
        }
    }

    [Fact]
    public void XrProbeAndStereoRig()
    {
        XrRuntimeInfo info = XrRuntime.Probe();
        Assert.True(info.RuntimeFound || info.Message.Length > 0);

        using Scene scene = new();
        Node head = scene.CreateNode(name: "head");
        head.Position = new Vector3(0f, 1.7f, 0f);
        StereoRig rig = new(scene, head, interpupillaryDistance: 0.064f, focalDistance: 2f);
        rig.Apply(1f);

        Assert.Equal(-0.032f, rig.LeftEye.Position.X, 4);
        Camera left = rig.LeftEye.Camera!.Value;
        Camera right = rig.RightEye.Camera!.Value;
        Assert.True(left.IsOffAxis && right.IsOffAxis);
        // Each eye's frustum leans towards the head axis, mirror images of each other.
        Assert.True(left.AngleRight > -left.AngleLeft);
        Assert.Equal(left.AngleRight, -right.AngleLeft, 4);

        Renderer renderer;
        try
        {
            renderer = Renderer.CreateOffscreen(RendererOptions.Default with { Width = 48, Height = 48, MsaaSamples = 1 });
        }
        catch (ThreeNetException)
        {
            return;
        }

        using (renderer)
        {
            scene.AddMesh(scene.CreateBoxGeometry(0.2f, 0.2f, 0.2f), scene.CreateMaterial(MaterialOptions.Basic(Vector4.One)))
                .Position = new Vector3(0f, 1.7f, -0.5f);
            byte[] image = rig.RenderSideBySide(renderer);
            Assert.Equal(96 * 48 * 4, image.Length);

            // The near box appears right of centre for the left eye and left of centre for the right eye.
            int Brightest(int x0) => Enumerable.Range(0, 48).OrderByDescending(x => image[((24 * 96) + x0 + x) * 4]).First();
            Assert.True(Brightest(0) > Brightest(48), "stereo parallax");
        }
    }
}
