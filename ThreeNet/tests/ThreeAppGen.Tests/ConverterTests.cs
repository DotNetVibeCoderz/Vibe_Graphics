using System.Numerics;
using ThreeAppGen.Services;
using ThreeAppGen.Services.Conversion;
using Xunit;
using Xunit.Abstractions;

namespace ThreeAppGen.Tests;

public sealed class ConverterTests(ITestOutputHelper output)
{
    private static string SampleFolder
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "samples", "threejs", "crystal-garden")))
            {
                directory = directory.Parent;
            }

            return directory is null
                ? throw new DirectoryNotFoundException("samples/threejs/crystal-garden not found")
                : Path.Combine(directory.FullName, "samples", "threejs", "crystal-garden");
        }
    }

    [Fact]
    public void AnalyzerFindsEntryScriptModulesAndAssets()
    {
        ThreeJsProject project = ThreeJsProjectAnalyzer.Analyze(SampleFolder);

        Assert.Equal("main.js", project.EntryScript?.RelativePath);
        Assert.Contains(project.Scripts, s => s.RelativePath == "src/crystals.js");
        Assert.Contains(project.Assets, a => a.RelativePath == "textures/checker.png" && a.Kind == "texture");
        Assert.Equal("0.170.0", project.ThreeVersion);
        Assert.Contains("OrbitControls", project.DetectedFeatures);
        Assert.Contains("Keyboard input", project.DetectedFeatures);
        Assert.Equal("CrystalGarden", project.SuggestedName);
    }

    [Fact]
    public void ExtractorBuildsAnAccurateInventory()
    {
        ThreeJsProject project = ThreeJsProjectAnalyzer.Analyze(SampleFolder);
        SceneInventory inventory = SceneInventoryExtractor.Extract(project);

        SceneObjectDef camera = Assert.IsType<SceneObjectDef>(inventory.Camera);
        Assert.Equal(60f, camera.FovDegrees);
        Assert.Equal(new Vector3(6f, 4.5f, 9f), camera.Position);
        Assert.Equal(200f, camera.Far);

        SceneObjectDef ground = inventory.Objects.Single(o => o.Var == "ground");
        Assert.Equal("groundGeometry", ground.Geometry);
        Assert.Equal(-MathF.PI / 2f, ground.Rotation!.Value.X, 4);

        SceneObjectDef moonA = inventory.Objects.Single(o => o.Var == "moonA");
        Assert.Equal("moons", moonA.Parent);

        SceneObjectDef pedestal = inventory.Objects.Single(o => o.Var == "pedestal");
        Assert.NotNull(pedestal.Geometry);
        MaterialDef glass = inventory.Materials.Single(m => m.Var == pedestal.Material);
        Assert.True(glass.Transparent);
        Assert.Equal(0.55f, glass.Color.W, 3);

        MaterialDef groundMaterial = inventory.Materials.Single(m => m.Var == "groundMaterial");
        Assert.Equal("checker", groundMaterial.Map);
        Assert.Equal("textures/checker.png", inventory.Textures["checker"]);

        Assert.Contains(inventory.Objects, o => o.Var == "sun" && o.LightType == "Directional" && o.Intensity == 2.5f);
        Assert.Contains(inventory.Objects, o => o.Var == "glow" && o.LightType == "Point" && o.Range == 20f);
        Assert.Equal("Aces", inventory.ToneMapping);
        Assert.Equal(1.1f, inventory.Exposure, 3);
        Assert.Equal(0.035f, inventory.FogDensity, 4);
        Assert.Equal(new Vector3(0f, 1f, 0f), inventory.OrbitTarget);

        // knot.rotation.y += 0.01 per frame -> 0.6 rad/s at 60 Hz.
        Assert.Contains(inventory.Animations, a => a.Target == "knot" && a.Axis == 'y' && MathF.Abs(a.PerSecond - 0.6f) < 1e-4f);
        Assert.Contains(inventory.Geometries, g => g.SourceType == "TorusKnotGeometry" && !g.Approximated);
        Assert.DoesNotContain(inventory.Objects, o => o.Var == "crystal");
        Assert.Equal(new System.Numerics.Vector2(8f, 8f), inventory.TextureRepeats["checker"]);
    }

    [Fact]
    public void ShadowFlagsSurviveTheConversion()
    {
        ThreeJsProject project = ThreeJsProjectAnalyzer.Analyze(SampleFolder);
        SceneInventory inventory = SceneInventoryExtractor.Extract(project);

        Assert.True(inventory.Shadows, "renderer.shadowMap.enabled should turn shadows on");
        Assert.True(inventory.Objects.Single(o => o.Var == "sun").CastShadow);
        SceneObjectDef ground = inventory.Objects.Single(o => o.Var == "ground");
        Assert.True(ground.ReceiveShadow);
        Assert.False(ground.CastShadow);
        Assert.DoesNotContain(inventory.Unsupported, note => note.StartsWith("Shadows:", StringComparison.Ordinal));

        string code = BaselineCodeGenerator.GenerateConvertedScene("Demo", inventory, project);
        Assert.Contains("Shadows = true", code, StringComparison.Ordinal);
        Assert.Contains("CastShadow = true", code, StringComparison.Ordinal);
        // The ground only receives; Three.js meshes never cast unless asked.
        Assert.Contains("_ground.CastShadow = false;", code, StringComparison.Ordinal);
        Assert.Contains("_ground.ReceiveShadow = true;", code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0xff0000", 1f, 0f, 0f)]
    [InlineData("'#00ff00'", 0f, 1f, 0f)]
    [InlineData("\"white\"", 1f, 1f, 1f)]
    public void ColoursAreLinearised(string literal, float r, float g, float b)
    {
        Vector3 color = Assert.IsType<Vector3>(SceneInventoryExtractor.ParseColor(literal));
        Assert.Equal(r, color.X, 3);
        Assert.Equal(g, color.Y, 3);
        Assert.Equal(b, color.Z, 3);
    }

    [Theory]
    [InlineData("Math.PI / 2", MathF.PI / 2f)]
    [InlineData("0.5 * 4", 2f)]
    [InlineData("THREE.MathUtils.degToRad(180)", MathF.PI)]
    public void NumericExpressionsAreEvaluated(string expression, float expected)
    {
        Assert.Equal(expected, SceneInventoryExtractor.EvaluateNumber(expression)!.Value, 4);
    }

    [Fact]
    public void ModelResponsesAreParsedAndProtectedFilesIgnored()
    {
        string response = """
            Here you go.
            <file path="src/Demo.Core/ConvertedScene.cs">
            ```csharp
            public sealed partial class ConvertedScene { }
            ```
            </file>
            <file path="ThreeJsCompat.cs">should be ignored</file>
            <file path="../evil.cs">nope</file>
            <notes>
            - approximated the torus knot
            </notes>
            """;

        LlmConversionOutput parsed = LlmCodeConverter.ParseResponse(response);

        GeneratedFile file = Assert.Single(parsed.Files);
        Assert.Equal("ConvertedScene.cs", file.RelativePath);
        Assert.StartsWith("public sealed partial class ConvertedScene", file.Content);
        Assert.Contains("torus knot", parsed.Notes);
    }

    [Fact]
    public void BuildDiagnosticsAreParsed()
    {
        const string text = """
            C:\p\Demo.Core\ConvertedScene.cs(12,9): error CS0103: The name 'foo' does not exist in the current context [C:\p\Demo.Core\Demo.Core.csproj]
            C:\p\Demo.Core\ConvertedScene.cs(3,1): warning CS8019: Unnecessary using directive. [C:\p\Demo.Core\Demo.Core.csproj]
            C:\Program Files\dotnet\sdk\Sdks\Microsoft.NET.Sdk\targets\Microsoft.NET.Sdk.ImportWorkloads.targets(38,5): error NETSDK1147: To build this project, the following workloads must be installed: wasm-tools [C:\p\Demo.Web\Demo.Web.csproj]
            """;

        List<BuildDiagnostic> diagnostics = BuildValidator.Parse(text).ToList();

        Assert.Contains(diagnostics, d => d is { Code: "CS0103", Line: 12, Column: 9, IsError: true });
        Assert.Contains(diagnostics, d => d is { Code: "CS8019", IsError: false });
        BuildOutcome outcome = new(false, 1, diagnostics.Where(d => d.IsError).ToList(), text);
        Assert.True(outcome.MissingWorkload);
    }

    /// <summary>Full pipeline without an LLM: the baseline must produce a compiling desktop solution.</summary>
    [Fact]
    [Trait("Category", "Slow")]
    public async Task BaselineDesktopConversionCompiles()
    {
        string destination = Path.Combine(Path.GetTempPath(), "threenet-conversion-tests", $"CrystalGarden-{Guid.NewGuid():N}");
        string config = CreateConfig(provider: null);
        try
        {
            ThreeJsConverter converter = new(AppSettings.LoadFrom(config));
            converter.Logged += entry => output.WriteLine($"{entry.Badge,-4} {entry.Message}");

            ConversionResult result = await converter.ConvertAsync(new ConversionRequest
            {
                SourceFolder = SampleFolder,
                ProjectName = "CrystalGarden",
                DestinationFolder = destination,
                Target = ConversionTarget.Desktop,
                UseLlm = false,
            }, CancellationToken.None);

            Assert.True(result.Succeeded, result.Error);
            Assert.False(result.UsedLlm);
            Assert.True(File.Exists(result.SolutionPath));
            Assert.True(File.Exists(Path.Combine(destination, "src", "CrystalGarden.Core", "Assets", "textures", "checker.png")));
            string report = await File.ReadAllTextAsync(result.ReportPath);
            Assert.Contains("succeeded", report);
        }
        finally
        {
            File.Delete(config);
            TryDelete(destination);
        }
    }

    /// <summary>Web and mobile scaffolds: Core, shared Avalonia UI and the desktop preview must compile.</summary>
    [Theory]
    [Trait("Category", "Slow")]
    [InlineData(ConversionTarget.Web)]
    [InlineData(ConversionTarget.Mobile)]
    public async Task BaselineAvaloniaTargetsCompile(ConversionTarget target)
    {
        string destination = Path.Combine(Path.GetTempPath(), "threenet-conversion-tests", $"CrystalGarden{target}-{Guid.NewGuid():N}");
        string config = CreateConfig(provider: null);
        try
        {
            ThreeJsConverter converter = new(AppSettings.LoadFrom(config));
            converter.Logged += entry => output.WriteLine($"{entry.Badge,-4} {entry.Message}");

            ConversionResult result = await converter.ConvertAsync(new ConversionRequest
            {
                SourceFolder = SampleFolder,
                ProjectName = "CrystalGarden",
                DestinationFolder = destination,
                Target = target,
                UseLlm = false,
            }, CancellationToken.None);

            Assert.True(result.Succeeded, result.Error);
            Assert.EndsWith("CrystalGarden.Preview.csproj", result.StartupProjectPath);
            string head = target == ConversionTarget.Web ? "CrystalGarden.Web" : "CrystalGarden.Android";
            Assert.True(Directory.Exists(Path.Combine(destination, "src", head)));
        }
        finally
        {
            File.Delete(config);
            TryDelete(destination);
        }
    }

    /// <summary>
    /// Real LLM conversion with build validation and auto-fix. Opt in by pointing
    /// THREENET_LLM_KEYFILE at a key file (never committed). Skipped otherwise.
    /// </summary>
    [Fact]
    [Trait("Category", "LLM")]
    public async Task LlmDesktopConversionCompiles()
    {
        string? keyFile = Environment.GetEnvironmentVariable("THREENET_LLM_KEYFILE");
        if (string.IsNullOrWhiteSpace(keyFile) || !File.Exists(keyFile))
        {
            output.WriteLine("THREENET_LLM_KEYFILE not set; skipping the LLM conversion test.");
            return;
        }

        string destination = Environment.GetEnvironmentVariable("THREENET_LLM_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "threenet-conversion-tests", $"CrystalGardenLlm-{Guid.NewGuid():N}");
        string config = CreateConfig(ReadAzureKeys(keyFile));
        try
        {
            AppSettings settings = AppSettings.LoadFrom(config);
            ThreeJsConverter converter = new(settings);
            converter.Logged += entry => output.WriteLine($"{entry.Time} {entry.Badge,-4} {entry.Message}");

            ConversionResult result = await converter.ConvertAsync(new ConversionRequest
            {
                SourceFolder = SampleFolder,
                ProjectName = "CrystalGarden",
                DestinationFolder = destination,
                Target = ConversionTarget.Desktop,
                UseLlm = true,
                MaxFixAttempts = 4,
                OverwriteDestination = true,
            }, CancellationToken.None);

            output.WriteLine($"succeeded={result.Succeeded} llm={result.UsedLlm} fallback={result.FellBackToBaseline} fixes={result.FixAttempts}");
            Assert.True(result.Succeeded, result.Error);
            Assert.True(result.UsedLlm);
        }
        finally
        {
            File.Delete(config);
        }
    }

    private static (string Endpoint, string Key, string Model)? ReadAzureKeys(string keyFile)
    {
        string[] lines = File.ReadAllLines(keyFile);
        string? Value(string prefix) => lines
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..].Trim();

        string? key = Value("apikey:");
        string? endpoint = Value("endpoint:");
        string? model = Value("model:");
        return key is null || endpoint is null || model is null ? null : (endpoint, key, model);
    }

    private static string CreateConfig((string Endpoint, string Key, string Model)? provider)
    {
        string path = Path.Combine(Path.GetTempPath(), $"threeappgen-test-{Guid.NewGuid():N}.config");
        string openAi = provider is { } p
            ? $"""
                <add key="OpenAI.Model" value="{p.Model}" />
                <add key="OpenAI.ApiKey" value="{p.Key}" />
                <add key="OpenAI.Endpoint" value="{p.Endpoint}" />
              """
            : string.Empty;

        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <appSettings>
                <add key="Llm.Provider" value="OpenAI" />
                <add key="Llm.Temperature" value="0.1" />
                <add key="Llm.MaxTokens" value="32000" />
            {openAi}
              </appSettings>
            </configuration>
            """);
        return path;
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // Build servers can hold files briefly; temp cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
