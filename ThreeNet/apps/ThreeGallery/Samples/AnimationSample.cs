using System.Numerics;
using ThreeNet;
using ThreeNet.Avalonia;

namespace ThreeGallery.Samples;

/// <summary>
/// Keyframe clips built in code drive a robot arm hierarchy, and a skinned
/// glTF (Khronos RiggedSimple) plays its own skeletal animation.
/// </summary>
public sealed class AnimationSample : GallerySample
{
    public override string Title => "Keyframe & skeletal animation";

    public override string Category => "Animation";

    public override string Summary =>
        "AnimationClip channels (step, linear, cubic spline) on a node hierarchy, plus a skinned glTF playing its imported clip.";

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with { Shadows = true };

    public override void Build(Scene scene)
    {
        Material floorMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0x8C939E), 0f, 0.85f));
        Node floor = scene.AddMesh(scene.CreatePlaneGeometry(30f, 30f), floorMaterial, name: "floor");
        floor.EulerAngles = new Vector3(-MathF.PI / 2f, 0f, 0f);

        BuildArm(scene);

        // Skinned model from the Khronos sample assets (CC-BY 4.0).
        string rigged = Path.Combine(AppContext.BaseDirectory, "Assets", "RiggedSimple.glb");
        if (File.Exists(rigged))
        {
            Node holder = scene.CreateNode(name: "rigged holder");
            holder.Position = new Vector3(2.5f, 0f, 0f);
            holder.Scale = new Vector3(0.5f);
            ImportResult import = scene.LoadGltf(rigged, holder);
            if (import.AnimationCount > 0)
            {
                scene.Animations[^1].Play();
            }
        }

        Node sun = scene.AddLight(Light.Directional(Vector3.One, 3f) with { CastShadow = true }, name: "sun");
        sun.Position = new Vector3(4f, 8f, 5f);
        sun.LookAt(Vector3.Zero);
        scene.Environment = scene.Environment with
        {
            Background = MathHelpers.FromHex(0x1E222A),
            AmbientIntensity = 0.3f,
        };
    }

    private static void BuildArm(Scene scene)
    {
        Material metal = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0xE8B04A), 0.8f, 0.3f));
        Material joint = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0x2F3542), 0.5f, 0.4f));

        Node basePlate = scene.AddMesh(scene.CreateCylinderGeometry(0.7f, 0.8f, 0.3f), joint, name: "base");
        basePlate.Position = new Vector3(-2f, 0.15f, 0f);

        Node shoulder = scene.CreateNode(basePlate, "shoulder");
        shoulder.Position = new Vector3(0f, 0.15f, 0f);
        Node upper = scene.AddMesh(scene.CreateBoxGeometry(0.3f, 1.6f, 0.3f), metal, shoulder, "upper arm");
        upper.Position = new Vector3(0f, 0.8f, 0f);

        Node elbow = scene.CreateNode(shoulder, "elbow");
        elbow.Position = new Vector3(0f, 1.6f, 0f);
        scene.AddMesh(scene.CreateSphereGeometry(0.22f), joint, elbow, "elbow ball");
        Node fore = scene.AddMesh(scene.CreateBoxGeometry(0.22f, 1.2f, 0.22f), metal, elbow, "forearm");
        fore.Position = new Vector3(0f, 0.6f, 0f);

        Node hand = scene.AddMesh(scene.CreateBoxGeometry(0.5f, 0.12f, 0.5f), joint, elbow, "hand");
        hand.Position = new Vector3(0f, 1.25f, 0f);

        static Quaternion Y(float angle) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle);
        static Quaternion Z(float angle) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle);

        AnimationClip clip = scene.CreateAnimation("robot arm")
            .AddRotation(basePlate, [0f, 2f, 4f, 6f], [Y(0f), Y(MathF.PI * 0.66f), Y(MathF.PI * 1.33f), Y(MathF.Tau)])
            .AddRotation(shoulder, [0f, 1.5f, 3f, 4.5f, 6f], [Z(0.2f), Z(-0.6f), Z(0.4f), Z(-0.3f), Z(0.2f)])
            .AddRotation(elbow, [0f, 1f, 2.5f, 4f, 6f], [Z(0.3f), Z(1.2f), Z(0.2f), Z(1.4f), Z(0.3f)])
            // Step interpolation makes the gripper snap open and closed.
            .AddChannel(hand, AnimationPath.Scale, Interpolation.Step,
                [0f, 1f, 2f, 3f, 4f, 5f],
                [1f, 1f, 1f, 0.5f, 1f, 0.5f, 1f, 1f, 1f, 0.5f, 1f, 0.5f, 1f, 1f, 1f, 0.5f, 1f, 0.5f]);
        clip.Play();
    }

    public override void Update(Scene scene, float deltaSeconds, double totalSeconds) =>
        scene.UpdateAnimations(deltaSeconds);

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 1.2f, 0f);
        orbit.Distance = 8f;
        orbit.Yaw = 0.3f;
        orbit.Pitch = 0.25f;
    }
}
