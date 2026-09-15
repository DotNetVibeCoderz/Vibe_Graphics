using System.Text;

namespace ThreeAppGen.Services.Conversion;

/// <summary>A project of the generated solution and how strictly it must build.</summary>
public sealed record GeneratedProject(string Name, string ProjectPath, bool Required, string Role);

/// <summary>Paths of the solution the scaffolder wrote.</summary>
public sealed class ScaffoldResult
{
    public required string Root { get; init; }

    public required string SolutionPath { get; init; }

    public required string CoreFolder { get; init; }

    public required string AssetsFolder { get; init; }

    public required GeneratedProject Startup { get; init; }

    public List<GeneratedProject> Projects { get; } = [];
}

/// <summary>
/// Writes the solution around the converted scene. The layout is the same for
/// every target: a Core library holding the converted scene and assets, plus
/// thin host projects. Only Core is ever rewritten by the conversion; hosts are
/// fixed templates, which keeps the LLM's blast radius small.
/// </summary>
public static class ConvertedProjectScaffolder
{
    public const string AvaloniaVersion = "12.1.2";

    public static ScaffoldResult Scaffold(ConversionRequest request, string? threeNetRepository)
    {
        string name = request.ProjectName;
        string root = request.DestinationFolder;
        string src = Path.Combine(root, "src");
        Directory.CreateDirectory(src);

        string coreFolder = Path.Combine(src, $"{name}.Core");
        string assetsFolder = Path.Combine(coreFolder, "Assets");
        Directory.CreateDirectory(assetsFolder);

        WriteText(Path.Combine(root, "Directory.Build.props"), DirectoryBuildProps);
        WriteText(Path.Combine(root, ".gitignore"), "bin/\nobj/\npublish/\n*.user\n.vs/\n");

        string threeNetReference = threeNetRepository is null
            ? """    <PackageReference Include="ThreeNet" Version="0.1.0" />"""
            : $"""    <ProjectReference Include="{Path.Combine(threeNetRepository, "src", "ThreeNet", "ThreeNet.csproj")}" />""";
        string threeNetAvaloniaReference = threeNetRepository is null
            ? """    <PackageReference Include="ThreeNet.Avalonia" Version="0.1.0" />"""
            : $"""    <ProjectReference Include="{Path.Combine(threeNetRepository, "src", "ThreeNet.Avalonia", "ThreeNet.Avalonia.csproj")}" />""";

        // ---- Core -------------------------------------------------------------
        string corePath = Path.Combine(coreFolder, $"{name}.Core.csproj");
        WriteText(corePath, $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <RootNamespace>{name}.Core</RootNamespace>
                <Description>Scene converted from a Three.js project by Three.Net App Generator.</Description>
              </PropertyGroup>

              <ItemGroup>
            {threeNetReference}
              </ItemGroup>

              <ItemGroup>
                <!-- Copied next to every host, so the scene loads them from AppContext.BaseDirectory/Assets. -->
                <None Include="Assets\**\*" CopyToOutputDirectory="PreserveNewest" LinkBase="Assets" />
              </ItemGroup>

            </Project>
            """);

        ScaffoldResult result;
        List<GeneratedProject> projects = [new GeneratedProject($"{name}.Core", corePath, Required: true, "Converted scene and assets")];

        if (request.Target == ConversionTarget.Desktop)
        {
            string desktopFolder = Path.Combine(src, $"{name}.Desktop");
            string desktopPath = Path.Combine(desktopFolder, $"{name}.Desktop.csproj");
            WriteText(desktopPath, $"""
                <Project Sdk="Microsoft.NET.Sdk">

                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <RootNamespace>{name}.Desktop</RootNamespace>
                  </PropertyGroup>

                  <ItemGroup>
                    <ProjectReference Include="..\{name}.Core\{name}.Core.csproj" />
                  </ItemGroup>

                </Project>
                """);
            WriteText(Path.Combine(desktopFolder, "Program.cs"), DesktopProgram(name));
            GeneratedProject desktop = new($"{name}.Desktop", desktopPath, Required: true, "Native desktop window (Windows, Linux, macOS)");
            projects.Add(desktop);
            result = new ScaffoldResult
            {
                Root = root,
                SolutionPath = Path.Combine(root, $"{name}.slnx"),
                CoreFolder = coreFolder,
                AssetsFolder = assetsFolder,
                Startup = desktop,
            };
        }
        else
        {
            // Shared Avalonia UI used by every Avalonia head.
            string appFolder = Path.Combine(src, $"{name}.App");
            string appPath = Path.Combine(appFolder, $"{name}.App.csproj");
            WriteText(appPath, $"""
                <Project Sdk="Microsoft.NET.Sdk">

                  <PropertyGroup>
                    <RootNamespace>{name}.App</RootNamespace>
                  </PropertyGroup>

                  <ItemGroup>
                    <PackageReference Include="Avalonia" Version="{AvaloniaVersion}" />
                    <PackageReference Include="Avalonia.Themes.Fluent" Version="{AvaloniaVersion}" />
                    <PackageReference Include="Avalonia.Fonts.Inter" Version="{AvaloniaVersion}" />
                  </ItemGroup>

                  <ItemGroup>
                    <ProjectReference Include="..\{name}.Core\{name}.Core.csproj" />
                {threeNetAvaloniaReference}
                  </ItemGroup>

                </Project>
                """);
            WriteText(Path.Combine(appFolder, "App.cs"), AvaloniaApp(name));
            WriteText(Path.Combine(appFolder, "MainView.cs"), AvaloniaMainView(name));
            projects.Add(new GeneratedProject($"{name}.App", appPath, Required: true, "Shared Avalonia UI hosting the scene"));

            string previewFolder = Path.Combine(src, $"{name}.Preview");
            string previewPath = Path.Combine(previewFolder, $"{name}.Preview.csproj");
            WriteText(previewPath, $"""
                <Project Sdk="Microsoft.NET.Sdk">

                  <PropertyGroup>
                    <OutputType>WinExe</OutputType>
                    <RootNamespace>{name}.Preview</RootNamespace>
                  </PropertyGroup>

                  <ItemGroup>
                    <PackageReference Include="Avalonia.Desktop" Version="{AvaloniaVersion}" />
                  </ItemGroup>

                  <ItemGroup>
                    <ProjectReference Include="..\{name}.App\{name}.App.csproj" />
                  </ItemGroup>

                </Project>
                """);
            WriteText(Path.Combine(previewFolder, "Program.cs"), PreviewProgram(name));
            GeneratedProject preview = new($"{name}.Preview", previewPath, Required: true, "Desktop preview of the web / mobile UI");
            projects.Add(preview);

            if (request.Target == ConversionTarget.Web)
            {
                string webFolder = Path.Combine(src, $"{name}.Web");
                string webPath = Path.Combine(webFolder, $"{name}.Web.csproj");
                WriteText(webPath, $"""
                    <Project Sdk="Microsoft.NET.Sdk.WebAssembly">

                      <PropertyGroup>
                        <TargetFramework>net10.0-browser</TargetFramework>
                        <OutputType>Exe</OutputType>
                        <RootNamespace>{name}.Web</RootNamespace>
                      </PropertyGroup>

                      <ItemGroup>
                        <PackageReference Include="Avalonia.Browser" Version="{AvaloniaVersion}" />
                      </ItemGroup>

                      <ItemGroup>
                        <ProjectReference Include="..\{name}.App\{name}.App.csproj" />
                      </ItemGroup>

                    </Project>
                    """);
                WriteText(Path.Combine(webFolder, "Program.cs"), BrowserProgram(name));
                WriteText(Path.Combine(webFolder, "wwwroot", "index.html"), BrowserIndex(name));
                WriteText(Path.Combine(webFolder, "wwwroot", "main.js"), BrowserMainJs);
                projects.Add(new GeneratedProject($"{name}.Web", webPath, Required: false, "Browser (WebAssembly) head"));
            }
            else
            {
                string androidFolder = Path.Combine(src, $"{name}.Android");
                string androidPath = Path.Combine(androidFolder, $"{name}.Android.csproj");
                string applicationId = $"com.threenet.{name.ToLowerInvariant()}";
                WriteText(androidPath, $"""
                    <Project Sdk="Microsoft.NET.Sdk">

                      <PropertyGroup>
                        <TargetFramework>net10.0-android</TargetFramework>
                        <SupportedOSPlatformVersion>24</SupportedOSPlatformVersion>
                        <OutputType>Exe</OutputType>
                        <RootNamespace>{name}.Android</RootNamespace>
                        <ApplicationId>{applicationId}</ApplicationId>
                        <ApplicationVersion>1</ApplicationVersion>
                        <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
                        <AndroidPackageFormat>apk</AndroidPackageFormat>
                      </PropertyGroup>

                      <ItemGroup>
                        <PackageReference Include="Avalonia.Android" Version="{AvaloniaVersion}" />
                      </ItemGroup>

                      <ItemGroup>
                        <ProjectReference Include="..\{name}.App\{name}.App.csproj" />
                      </ItemGroup>

                    </Project>
                    """);
                WriteText(Path.Combine(androidFolder, "MainActivity.cs"), AndroidActivity(name));
                WriteText(Path.Combine(androidFolder, "AndroidManifest.xml"), AndroidManifest);
                projects.Add(new GeneratedProject($"{name}.Android", androidPath, Required: false, "Android head"));
            }

            result = new ScaffoldResult
            {
                Root = root,
                SolutionPath = Path.Combine(root, $"{name}.slnx"),
                CoreFolder = coreFolder,
                AssetsFolder = assetsFolder,
                Startup = preview,
            };
        }

        result.Projects.AddRange(projects);
        WriteText(result.SolutionPath, Solution(root, projects));
        return result;
    }

    /// <summary>Copies every asset, keeping the relative layout the source code refers to.</summary>
    public static int CopyAssets(ThreeJsProject project, string assetsFolder, Action<ConversionLogLevel, string> log, CancellationToken cancellationToken)
    {
        int copied = 0;
        foreach (AssetFile asset in project.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = Path.Combine(assetsFolder, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(asset.FullPath, destination, overwrite: true);
            copied++;
            log(ConversionLogLevel.Info, $"Asset {asset.Kind}: {asset.RelativePath} ({asset.Size / 1024.0:F1} KB)");
        }

        return copied;
    }

    private static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.ReplaceLineEndings(Environment.NewLine), new UTF8Encoding(false));
    }

    private static string Solution(string root, IEnumerable<GeneratedProject> projects)
    {
        StringBuilder builder = new();
        builder.AppendLine("<Solution>");
        builder.AppendLine("  <Folder Name=\"/src/\">");
        foreach (GeneratedProject project in projects)
        {
            builder.AppendLine($"    <Project Path=\"{Path.GetRelativePath(root, project.ProjectPath).Replace('\\', '/')}\" />");
        }

        builder.AppendLine("  </Folder>");
        builder.AppendLine("</Solution>");
        return builder.ToString();
    }

    private const string DirectoryBuildProps = """
        <Project>
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <LangVersion>preview</LangVersion>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
          </PropertyGroup>
        </Project>
        """;

    private static string DesktopProgram(string name) => $$"""
        // Desktop host for the converted Three.js scene.
        // Made with Three.Net by Gravicode Studios - led by Kang Fadhil.

        using ThreeNet;
        using {{name}}.Core;

        ThreeNetRuntime.Initialize();

        using ConvertedScene converted = new(Path.Combine(AppContext.BaseDirectory, "Assets"));
        OrbitRig? orbit = converted.UsesOrbitControls ? new OrbitRig(converted.Camera, converted.OrbitTarget) : null;

        AppWindow window = new(WindowOptions.Default with
        {
            Title = "{{name}}",
            Renderer = converted.RendererOptions,
        });

        double total = 0;
        window.Input += (renderer, input) =>
        {
            orbit?.Handle(input);
            converted.OnInput(input);
        };
        window.Render += (renderer, delta) =>
        {
            total += delta;
            converted.Update(delta, total);
            renderer.Render(converted.Scene, converted.Camera);
        };
        window.Run();
        """;

    private static string AvaloniaApp(string name) => $$"""
        using Avalonia;
        using Avalonia.Controls;
        using Avalonia.Controls.ApplicationLifetimes;
        using Avalonia.Themes.Fluent;

        namespace {{name}}.App;

        /// <summary>Code only Avalonia application shared by the preview, browser and mobile heads.</summary>
        public sealed class App : Application
        {
            public override void Initialize()
            {
                RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                Styles.Add(new FluentTheme());
            }

            public override void OnFrameworkInitializationCompleted()
            {
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.MainWindow = new Window
                    {
                        Title = "{{name}}",
                        Width = 1280,
                        Height = 760,
                        Content = new MainView(),
                    };
                }
                else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
                {
                    singleView.MainView = new MainView();
                }

                base.OnFrameworkInitializationCompleted();
            }
        }
        """;

    private static string AvaloniaMainView(string name) => $$"""
        using Avalonia.Controls;
        using ThreeNet;
        using ThreeNet.Avalonia;
        using {{name}}.Core;

        namespace {{name}}.App;

        /// <summary>Hosts the converted scene in a ThreeNetView with orbit controls.</summary>
        public sealed class MainView : UserControl
        {
            private readonly ConvertedScene _converted;
            private readonly OrbitController? _orbit;

            public MainView()
            {
                _converted = new ConvertedScene(Path.Combine(AppContext.BaseDirectory, "Assets"));

                ThreeNetView view = new()
                {
                    Scene = _converted.Scene,
                    Camera = _converted.Camera,
                    RendererOptions = _converted.RendererOptions with { BgraOutput = true },
                };
                view.Frame += (_, e) => _converted.Update(e.DeltaSeconds, e.TotalSeconds);

                if (_converted.UsesOrbitControls)
                {
                    System.Numerics.Vector3 offset = _converted.Camera.Position - _converted.OrbitTarget;
                    _orbit = new OrbitController(view, _converted.Camera)
                    {
                        Target = _converted.OrbitTarget,
                        Distance = offset.Length(),
                        Yaw = MathF.Atan2(offset.X, offset.Z),
                        Pitch = MathF.Asin(Math.Clamp(offset.Y / MathF.Max(offset.Length(), 0.001f), -1f, 1f)),
                    };
                    _orbit.Apply();
                }

                Content = view;
                DetachedFromVisualTree += (_, _) =>
                {
                    _orbit?.Dispose();
                    _converted.Dispose();
                };
            }
        }
        """;

    private static string PreviewProgram(string name) => $$"""
        using Avalonia;

        namespace {{name}}.Preview;

        internal static class Program
        {
            [STAThread]
            public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

            public static AppBuilder BuildAvaloniaApp() =>
                AppBuilder.Configure<{{name}}.App.App>()
                    .UsePlatformDetect()
                    .WithInterFont()
                    .LogToTrace();
        }
        """;

    private static string BrowserProgram(string name) => $$"""
        using System.Runtime.Versioning;
        using Avalonia;
        using Avalonia.Browser;

        [assembly: SupportedOSPlatform("browser")]

        namespace {{name}}.Web;

        internal static class Program
        {
            private static Task Main(string[] args) =>
                BuildAvaloniaApp()
                    .WithInterFont()
                    .StartBrowserAppAsync("out");

            public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<{{name}}.App.App>();
        }
        """;

    private static string BrowserIndex(string name) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1.0" />
          <title>{{name}}</title>
          <style>html, body, #out { margin: 0; height: 100%; background: #0b0d12; }</style>
          <script type="module" src="./main.js"></script>
        </head>
        <body>
          <div id="out"></div>
        </body>
        </html>
        """;

    private const string BrowserMainJs = """
        import { dotnet } from './_framework/dotnet.js';

        const runtime = await dotnet
          .withDiagnosticTracing(false)
          .withApplicationArgumentsFromQuery()
          .create();

        const config = runtime.getConfig();
        await runtime.runMain(config.mainAssemblyName, [globalThis.location.href]);
        """;

    private static string AndroidActivity(string name) => $$"""
        using Android.App;
        using Android.Content.PM;
        using Android.Runtime;
        using Avalonia;
        using Avalonia.Android;

        namespace {{name}}.Android;

        // Avalonia 12: the application object configures Avalonia, the activity only hosts it.
        [Application]
        public class AndroidApp(nint javaReference, JniHandleOwnership transfer)
            : AvaloniaAndroidApplication<{{name}}.App.App>(javaReference, transfer)
        {
            protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
                base.CustomizeAppBuilder(builder).WithInterFont();
        }

        [Activity(
            Label = "{{name}}",
            Theme = "@android:style/Theme.Material.NoActionBar",
            MainLauncher = true,
            ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
        public class MainActivity : AvaloniaMainActivity
        {
        }
        """;

    private const string AndroidManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <manifest xmlns:android="http://schemas.android.com/apk/res/android" android:installLocation="auto">
          <uses-permission android:name="android.permission.INTERNET" />
          <application android:label="Three.Net app" />
        </manifest>
        """;
}
