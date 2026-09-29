using System.Numerics;
using ThreeNet;

namespace DemoGraphics.Framework;

/// <summary>
/// The sky: an unlit sphere seen from the inside that follows the camera, so it
/// is always the same distance away.
/// </summary>
/// <remarks>
/// Following the camera is what keeps the sky out of the fog - a dome parked at
/// the far plane would be dissolved into the fog colour, taking the sun, the
/// clouds and the stars with it. It writes no depth and is drawn before every
/// other opaque object, so the rest of the scene simply paints over it.
/// One trade-off: the depth prepass that feeds SSAO and depth of field does see
/// the dome, so geometry further away than <see cref="Radius"/> is treated as if
/// it were at the dome. Outdoor scenes here keep their subject well inside it.
/// </remarks>
public sealed class SkyDome
{
    private SkyDome(Node node, Material material)
    {
        Node = node;
        Material = material;
    }

    public const float Radius = 60f;

    public Node Node { get; }

    public Material Material { get; }

    public static SkyDome Add(Scene scene)
    {
        Shader shader = scene.CreateShader(ShaderHooks.Sky, ShaderLanguage.Wgsl, "sky");
        Material material = scene.CreateMaterial(MaterialOptions.Basic(Colors.Black) with
        {
            Shader = shader,
            // Seen from the inside, and it must never occlude or be occluded.
            CullMode = CullMode.Front,
            DepthWrite = false,
            DepthTest = false,
            RenderOrder = -1000,
        });

        Node node = scene.AddMesh(scene.CreateSphereGeometry(Radius, 48, 24), material, name: "sky");
        node.CastShadow = false;
        node.ReceiveShadow = false;
        return new SkyDome(node, material);
    }

    /// <summary>Keeps the dome centred on the camera.</summary>
    public void Follow(Node camera) => Node.Position = camera.WorldPosition;

    /// <summary>Pushes the clock and the weather into the sky shader.</summary>
    public void Apply(EnvironmentState world, float starBrightness = 1f)
    {
        Vector3 sun = world.SunPosition;
        Material.Update(options => options with
        {
            Custom0 = new Vector4(sun.X, sun.Y, sun.Z, world.CloudCover),
            Custom1 = new Vector4(world.NightFactor, world.FogDensity * 12f, starBrightness, world.WindVector.X * 0.1f),
        });
    }
}
