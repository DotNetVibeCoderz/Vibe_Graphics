using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

public class ShaderTests
{
    private const string GreenWgsl = """
        fn user_surface(context: SurfaceContext, surface: Surface) -> Surface {
            var s = surface;
            s.albedo = vec3<f32>(0.0, 0.0, 0.0);
            s.emissive = context.custom0.rgb;
            return s;
        }
        """;

    private const string BlueGlsl = """
        Surface user_surface(SurfaceContext context, Surface surface) {
            surface.albedo = vec3(0.0);
            surface.emissive = context.custom1.rgb;
            return surface;
        }
        """;

    private static Renderer? TryCreateRenderer(RenderPath path = RenderPath.Forward)
    {
        try
        {
            return Renderer.CreateOffscreen(RendererOptions.Default with { Width = 96, Height = 96, RenderPath = path, ToneMapping = ToneMapping.None });
        }
        catch (ThreeNetException)
        {
            return null;
        }
    }

    [Fact]
    public void InvalidShadersReportTheCompilerMessage()
    {
        using Scene scene = new();
        ThreeNetException error = Assert.Throws<ThreeNetException>(() =>
            scene.CreateShader("fn user_surface(context: SurfaceContext, surface: Surface) -> Surface { return 1.0; }"));
        Assert.Contains("error", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GlslHooksAreTranslatedToWgsl()
    {
        using Scene scene = new();
        Shader shader = scene.CreateShader(BlueGlsl, ShaderLanguage.Glsl, "blue");
        Assert.Contains("fn user_surface", shader.CompiledWgsl);
    }

    [Theory]
    [InlineData(RenderPath.Forward)]
    [InlineData(RenderPath.Deferred)]
    public void CustomShadersDriveTheSurface(RenderPath path)
    {
        using Renderer? renderer = TryCreateRenderer(path);
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new() { Environment = SceneEnvironment.Default with { Background = new Vector4(0f, 0f, 0f, 1f) } };
        Shader green = scene.CreateShader(GreenWgsl, name: "green");
        Material material = scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One) with
        {
            Shader = green,
            Custom0 = new Vector4(0f, 1f, 0f, 0f),
            Custom1 = new Vector4(0f, 0f, 1f, 0f),
        });
        scene.AddMesh(scene.CreateSphereGeometry(1f), material);
        Node camera = scene.AddCamera(Camera.Perspective(0.8f), new Vector3(0f, 0f, 4f));

        renderer.Render(scene, camera);
        byte[] pixels = renderer.ReadPixels();
        int centre = ((48 * 96) + 48) * 4;
        Assert.True(pixels[centre + 1] > 150 && pixels[centre] < 40, $"expected green, got {pixels[centre]},{pixels[centre + 1]},{pixels[centre + 2]}");

        // Swapping the shader source (GLSL this time) takes effect on the next frame.
        green.Update(BlueGlsl, ShaderLanguage.Glsl);
        renderer.Render(scene, camera);
        pixels = renderer.ReadPixels();
        Assert.True(pixels[centre + 2] > 150 && pixels[centre + 1] < 40, $"expected blue, got {pixels[centre]},{pixels[centre + 1]},{pixels[centre + 2]}");
    }
}
