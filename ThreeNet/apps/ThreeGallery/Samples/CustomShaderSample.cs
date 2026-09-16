using System.Numerics;
using ThreeNet;
using ThreeNet.Avalonia;

namespace ThreeGallery.Samples;

/// <summary>
/// Custom shader hooks: a WGSL vertex hook bends a flag in the wind, a GLSL
/// surface hook draws animated scan lines. Both still get the built-in
/// lighting, shadows and fog because hooks only change the inputs.
/// </summary>
public sealed class CustomShaderSample : GallerySample
{
    private const string FlagWgsl = """
        fn user_vertex(context: VertexContext) -> vec3<f32> {
            // Pinned at the pole (uv.x = 0), free at the far edge.
            let amount = context.uv.x * context.custom0.x;
            let wave = sin(context.position.x * 2.6 - context.time * 3.2) * amount
                     + sin(context.position.y * 3.1 - context.time * 2.1) * amount * 0.35;
            return context.position + vec3<f32>(0.0, 0.0, wave);
        }

        fn user_surface(context: SurfaceContext, surface: Surface) -> Surface {
            var s = surface;
            // Two stripes in the material's custom colours.
            let stripe = step(0.5, fract(context.uv.y * 2.0));
            s.albedo = mix(context.custom1.rgb, vec3<f32>(0.95, 0.95, 0.95), stripe);
            return s;
        }
        """;

    private const string HologramGlsl = """
        Surface user_surface(SurfaceContext context, Surface surface) {
            float lines = step(0.55, fract(context.world_position.y * 12.0 - context.time * 1.5));
            float rim = pow(1.0 - clamp(dot(context.world_normal, context.view_direction), 0.0, 1.0), 2.5);
            surface.albedo = vec3(0.02);
            surface.emissive = context.custom0.rgb * (0.25 + lines * 0.6 + rim * 2.0);
            return surface;
        }
        """;

    private Node? _statue;

    public override string Title => "Custom shaders";

    public override string Category => "Materials";

    public override string Summary =>
        "WGSL vertex and surface hooks bend a flag; a GLSL hook (translated with naga) turns a torus into a hologram.";

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with
    {
        Bloom = true,
        BloomIntensity = 0.6f,
        Shadows = true,
    };

    public override void Build(Scene scene)
    {
        Shader flagShader = scene.CreateShader(FlagWgsl, ShaderLanguage.Wgsl, "flag");
        Shader hologram = scene.CreateShader(HologramGlsl, ShaderLanguage.Glsl, "hologram");

        Material flagMaterial = scene.CreateMaterial(MaterialOptions.Pbr(Colors.White, 0f, 0.7f) with
        {
            Shader = flagShader,
            CullMode = CullMode.None,
            Custom0 = new Vector4(0.35f, 0f, 0f, 0f),
            Custom1 = MathHelpers.FromHex(0xD7263D),
        });
        Node flag = scene.AddMesh(scene.CreatePlaneGeometry(3f, 2f, 48, 32), flagMaterial, name: "flag");
        flag.Position = new Vector3(-0.5f, 3f, 0f);

        Material poleMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0xB8BEC7), 0.9f, 0.3f));
        Node pole = scene.AddMesh(scene.CreateCylinderGeometry(0.06f, 0.06f, 4.2f), poleMaterial, name: "pole");
        pole.Position = new Vector3(-2f, 2.1f, 0f);

        Material hologramMaterial = scene.CreateMaterial(MaterialOptions.Pbr(Colors.White) with
        {
            Shader = hologram,
            Custom0 = new Vector4(0.2f, 0.9f, 1.4f, 0f),
        });
        _statue = scene.AddMesh(scene.CreateTorusGeometry(0.7f, 0.25f, 32, 96), hologramMaterial, name: "hologram");
        _statue.Position = new Vector3(2.4f, 1.2f, 0.5f);

        Material floorMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0x2B303A), 0f, 0.9f));
        Node floor = scene.AddMesh(scene.CreatePlaneGeometry(30f, 30f), floorMaterial, name: "floor");
        floor.EulerAngles = new Vector3(-MathF.PI / 2f, 0f, 0f);

        Node sun = scene.AddLight(Light.Directional(Vector3.One, 3f) with { CastShadow = true }, name: "sun");
        sun.Position = new Vector3(3f, 6f, 5f);
        sun.LookAt(Vector3.Zero);
        scene.Environment = scene.Environment with
        {
            Background = MathHelpers.FromHex(0x10131A),
            AmbientIntensity = 0.25f,
        };
    }

    public override void Update(Scene scene, float deltaSeconds, double totalSeconds)
    {
        _statue?.Rotation = Quaternion.CreateFromYawPitchRoll((float)totalSeconds * 0.6f, 0.3f, 0f);
    }

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0.3f, 2f, 0f);
        orbit.Distance = 8f;
        orbit.Yaw = 0.25f;
        orbit.Pitch = 0.15f;
    }
}
