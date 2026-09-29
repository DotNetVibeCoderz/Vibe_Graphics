using System.Diagnostics;
using System.Numerics;
using DemoGraphics.Framework;
using DemoGraphics.Scenes;
using ThreeNet;

namespace DemoGraphics;

/// <summary>
/// <c>DemoGraphics --check</c>: builds every scene offscreen and renders a few
/// frames of each without opening a window. It compiles every custom shader,
/// touches every render path and prints what each scene costs, which makes it a
/// usable smoke test from a terminal or from CI.
/// </summary>
public static class SelfCheck
{
    public static int Run()
    {
        Console.WriteLine($"Three.Net core {ThreeNetRuntime.NativeVersion}, ABI {ThreeNetRuntime.NativeAbiVersion}");

        Renderer renderer;
        try
        {
            renderer = Renderer.CreateOffscreen(RendererOptions.Default with
            {
                Width = 480,
                Height = 270,
                MsaaSamples = 1,
                VSync = false,
            });
        }
        catch (ThreeNetException exception)
        {
            Console.WriteLine($"no GPU adapter, skipping: {exception.Message}");
            return 0;
        }

        using (renderer)
        {
            GpuCapabilities capabilities = renderer.Capabilities;
            Console.WriteLine($"adapter  {renderer.AdapterName}");
            Console.WriteLine($"driver   {renderer.AdapterDriver}");
            Console.WriteLine(
                $"features timestamps={capabilities.TimestampQueries} wireframe={capabilities.WireframeRendering} " +
                $"bc={capabilities.TextureCompressionBc} maxTexture={capabilities.MaxTextureSize} maxMsaa={capabilities.MaxMsaaSamples}x");
            Console.WriteLine();

            List<DemoScene> scenes = [.. SceneCatalog.All, new SandboxScene()];
            int failures = 0;
            foreach (DemoScene demo in scenes)
            {
                failures += CheckScene(renderer, demo) ? 0 : 1;
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? $"all {scenes.Count} scenes built and rendered"
                : $"{failures} of {scenes.Count} scenes failed");
            return failures == 0 ? 0 : 1;
        }
    }

    private static bool CheckScene(Renderer renderer, DemoScene demo)
    {
        EnvironmentState world = new();
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            using Scene scene = new();
            Node camera = scene.AddCamera(Camera.Perspective(52f.ToRadians(), 0.08f, 900f), new Vector3(0f, 3f, 14f));
            demo.Load(scene, world, camera);
            double buildMs = clock.Elapsed.TotalMilliseconds;

            RendererOptions options = demo.ConfigureRenderer(renderer.Options with { Width = 480, Height = 270, MsaaSamples = 1 });
            renderer.Options = options;

            // Render both paths and one debug view: the shaders are validated on
            // the CPU, but only a real frame proves the pipelines link.
            // The first frames compile pipelines, so they are not timed.
            for (int warmup = 0; warmup < 3; warmup++)
            {
                demo.Update(1f / 60f, warmup / 60.0);
                renderer.Render(scene, camera);
            }

            clock.Restart();
            const int timed = 5;
            for (int frame = 0; frame < timed; frame++)
            {
                demo.Update(1f / 60f, (3 + frame) / 60.0);
                scene.UpdateAnimations(1f / 60f);
                renderer.Render(scene, camera);
            }

            double frameMs = clock.Elapsed.TotalMilliseconds / timed;
            FrameStats stats = renderer.Stats;

            renderer.Options = options with { RenderPath = RenderPath.Deferred };
            renderer.Render(scene, camera);
            renderer.Options = options with { DebugView = DebugView.WorldNormal };
            renderer.Render(scene, camera);
            renderer.Options = options with { DebugView = DebugView.Off };

            foreach (DemoPreset preset in demo.Presets)
            {
                demo.ApplyPreset(preset);
                renderer.Render(scene, camera);
            }

            demo.Reset();
            demo.Unload();

            Console.WriteLine(
                $"{demo.Id}  {demo.Title,-22} nodes {scene.NodeCount,6}  draws {stats.DrawCalls,5}  " +
                $"tris {stats.Triangles,9:N0}  lights {stats.Lights,3}  shadow layers {stats.ShadowLayers,2}  " +
                $"build {buildMs,7:F1} ms  frame {frameMs,6:F2} ms  presets {demo.Presets.Count}");
            return true;
        }
        catch (Exception exception) when (exception is ThreeNetException or InvalidOperationException or ArgumentException)
        {
            Console.WriteLine($"{demo.Id}  {demo.Title,-22} FAILED: {exception.Message}");
            return false;
        }
    }
}
