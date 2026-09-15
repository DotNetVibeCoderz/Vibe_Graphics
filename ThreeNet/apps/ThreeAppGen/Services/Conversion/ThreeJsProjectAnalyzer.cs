using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThreeAppGen.Services.Conversion;

/// <summary>A source file of the Three.js project.</summary>
public sealed record SourceFile(string RelativePath, string FullPath, long Size, string Content)
{
    public string Extension => Path.GetExtension(RelativePath).ToLowerInvariant();
}

/// <summary>A non-code file that has to travel with the app (model, texture, audio...).</summary>
public sealed record AssetFile(string RelativePath, string FullPath, long Size, string Kind);

/// <summary>What the analyzer found in the source folder.</summary>
public sealed class ThreeJsProject
{
    public required string RootPath { get; init; }

    public required string SuggestedName { get; init; }

    public List<SourceFile> Scripts { get; } = [];

    public List<SourceFile> Pages { get; } = [];

    public List<AssetFile> Assets { get; } = [];

    /// <summary>Script referenced by the HTML entry page, if one was found.</summary>
    public SourceFile? EntryScript { get; set; }

    /// <summary>Three.js version from package.json or an import map URL, when available.</summary>
    public string? ThreeVersion { get; set; }

    /// <summary>Notable APIs detected across all scripts (loaders, controls, post-processing...).</summary>
    public SortedSet<string> DetectedFeatures { get; } = new(StringComparer.Ordinal);

    public bool UsesThree => Scripts.Any(s => s.Content.Contains("three", StringComparison.OrdinalIgnoreCase));

    public long TotalScriptCharacters => Scripts.Sum(s => (long)s.Content.Length);

    /// <summary>Scripts ordered for the LLM: entry first, then by size.</summary>
    public IEnumerable<SourceFile> ScriptsByPriority =>
        Scripts.OrderByDescending(s => s == EntryScript).ThenByDescending(s => s.Content.Contains("THREE.") || s.Content.Contains("from 'three'") || s.Content.Contains("from \"three\"")).ThenBy(s => s.Size);
}

/// <summary>
/// Walks a Three.js web project, separating application scripts from vendored
/// libraries and collecting the assets the scene loads.
/// </summary>
public static partial class ThreeJsProjectAnalyzer
{
    private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".github", ".vscode", ".idea", "dist", "build", ".cache", ".next", ".parcel-cache", "coverage",
    };

    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".mjs", ".ts", ".jsx", ".tsx",
    };

    private static readonly Dictionary<string, string> AssetKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".gltf"] = "model", [".glb"] = "model", [".obj"] = "model", [".fbx"] = "model", [".mtl"] = "model", [".bin"] = "model",
        [".png"] = "texture", [".jpg"] = "texture", [".jpeg"] = "texture", [".webp"] = "texture", [".bmp"] = "texture",
        [".tga"] = "texture", [".hdr"] = "environment", [".exr"] = "environment", [".ktx2"] = "texture",
        [".mp3"] = "audio", [".ogg"] = "audio", [".wav"] = "audio",
        [".json"] = "data", [".csv"] = "data", [".ttf"] = "font", [".woff"] = "font", [".woff2"] = "font",
    };

    /// <summary>Vendored copies of these libraries are not application code.</summary>
    private static readonly string[] LibraryFileMarkers =
    [
        "three.module", "three.min", "three.js", "three.core", "orbitcontrols", "gltfloader", "objloader", "fbxloader",
        "dracoloader", "effectcomposer", "renderpass", "unrealbloompass", "lil-gui", "dat.gui", "stats.module", "stats.min",
        "tween", "cannon", "ammo", "rapier", ".min.js",
    ];

    /// <summary>Guards against sending megabytes of bundled code to the model.</summary>
    private const long MaxScriptBytes = 400_000;

    public static ThreeJsProject Analyze(string rootPath, Action<ConversionLogLevel, string>? log = null)
    {
        DirectoryInfo root = new(rootPath);
        if (!root.Exists)
        {
            throw new DirectoryNotFoundException($"source folder not found: {rootPath}");
        }

        ThreeJsProject project = new()
        {
            RootPath = root.FullName,
            SuggestedName = SuggestName(root),
        };

        foreach (FileInfo file in EnumerateFiles(root))
        {
            string relative = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');
            string extension = file.Extension.ToLowerInvariant();

            if (ScriptExtensions.Contains(extension))
            {
                if (IsLibraryFile(relative) || file.Length > MaxScriptBytes)
                {
                    log?.Invoke(ConversionLogLevel.Info, $"Skipping library or bundle: {relative}");
                    continue;
                }

                project.Scripts.Add(new SourceFile(relative, file.FullName, file.Length, File.ReadAllText(file.FullName)));
            }
            else if (extension is ".html" or ".htm")
            {
                project.Pages.Add(new SourceFile(relative, file.FullName, file.Length, File.ReadAllText(file.FullName)));
            }
            else if (AssetKinds.TryGetValue(extension, out string? kind))
            {
                if (extension == ".json" && file.Name is "package.json" or "package-lock.json" or "tsconfig.json" or "jsconfig.json")
                {
                    continue;
                }

                project.Assets.Add(new AssetFile(relative, file.FullName, file.Length, kind));
            }
        }

        project.EntryScript = FindEntryScript(project);
        project.ThreeVersion = DetectVersion(root, project);
        DetectFeatures(project);

        log?.Invoke(ConversionLogLevel.Info,
            $"Found {project.Scripts.Count} scripts, {project.Pages.Count} pages and {project.Assets.Count} assets");
        if (project.EntryScript is not null)
        {
            log?.Invoke(ConversionLogLevel.Info, $"Entry script: {project.EntryScript.RelativePath}");
        }

        return project;
    }

    private static IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo directory)
    {
        foreach (FileInfo file in directory.EnumerateFiles())
        {
            yield return file;
        }

        foreach (DirectoryInfo child in directory.EnumerateDirectories())
        {
            if (IgnoredFolders.Contains(child.Name) || child.Name.StartsWith('.'))
            {
                continue;
            }

            foreach (FileInfo file in EnumerateFiles(child))
            {
                yield return file;
            }
        }
    }

    private static bool IsLibraryFile(string relativePath)
    {
        string lower = relativePath.ToLowerInvariant();
        string name = Path.GetFileName(lower);
        return lower.Contains("/vendor/") || lower.Contains("/libs/") || lower.Contains("/lib/three") ||
               lower.StartsWith("libs/", StringComparison.Ordinal) || lower.StartsWith("vendor/", StringComparison.Ordinal) ||
               LibraryFileMarkers.Any(marker => name.Contains(marker, StringComparison.Ordinal));
    }

    private static string SuggestName(DirectoryInfo root)
    {
        string packageJson = Path.Combine(root.FullName, "package.json");
        if (File.Exists(packageJson))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(packageJson));
                if (document.RootElement.TryGetProperty("name", out JsonElement name) && name.GetString() is { Length: > 0 } value)
                {
                    return ToPascalIdentifier(value);
                }
            }
            catch (JsonException)
            {
                // A malformed package.json just means we fall back to the folder name.
            }
        }

        return ToPascalIdentifier(root.Name);
    }

    /// <summary>"my-three_demo" becomes "MyThreeDemo", which is a valid C# namespace.</summary>
    public static string ToPascalIdentifier(string value)
    {
        string[] parts = NonIdentifier().Split(value).Where(p => p.Length > 0).ToArray();
        string joined = string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        if (joined.Length == 0)
        {
            return "ConvertedThreeApp";
        }

        return char.IsDigit(joined[0]) ? "App" + joined : joined;
    }

    private static SourceFile? FindEntryScript(ThreeJsProject project)
    {
        SourceFile? page = project.Pages.FirstOrDefault(p => Path.GetFileName(p.RelativePath).Equals("index.html", StringComparison.OrdinalIgnoreCase))
                           ?? project.Pages.FirstOrDefault();
        if (page is not null)
        {
            foreach (Match match in ScriptTag().Matches(page.Content))
            {
                string src = match.Groups["src"].Value.TrimStart('.', '/');
                string pageFolder = Path.GetDirectoryName(page.RelativePath)?.Replace('\\', '/') ?? string.Empty;
                string candidate = string.IsNullOrEmpty(pageFolder) ? src : $"{pageFolder}/{src}";
                SourceFile? script = project.Scripts.FirstOrDefault(s =>
                    s.RelativePath.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                    s.RelativePath.Equals(src, StringComparison.OrdinalIgnoreCase));
                if (script is not null)
                {
                    return script;
                }
            }

            // Inline module scripts are converted as if they were a file.
            Match inline = InlineModule().Match(page.Content);
            if (inline.Success && inline.Groups["body"].Value.Contains("three", StringComparison.OrdinalIgnoreCase))
            {
                SourceFile inlineScript = new($"{page.RelativePath}#inline.js", page.FullPath, inline.Groups["body"].Value.Length, inline.Groups["body"].Value);
                project.Scripts.Add(inlineScript);
                return inlineScript;
            }
        }

        string[] conventional = ["main.js", "src/main.js", "index.js", "src/index.js", "app.js", "src/app.js", "main.ts", "src/main.ts"];
        return conventional
            .Select(name => project.Scripts.FirstOrDefault(s => s.RelativePath.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(s => s is not null)
            ?? project.Scripts.OrderByDescending(s => ThreeReference().Matches(s.Content).Count).FirstOrDefault();
    }

    private static string? DetectVersion(DirectoryInfo root, ThreeJsProject project)
    {
        string packageJson = Path.Combine(root.FullName, "package.json");
        if (File.Exists(packageJson))
        {
            Match match = PackageThreeVersion().Match(File.ReadAllText(packageJson));
            if (match.Success)
            {
                return match.Groups["version"].Value;
            }
        }

        foreach (SourceFile page in project.Pages)
        {
            Match match = CdnThreeVersion().Match(page.Content);
            if (match.Success)
            {
                return match.Groups["version"].Value;
            }
        }

        return null;
    }

    private static void DetectFeatures(ThreeJsProject project)
    {
        (string Pattern, string Feature)[] probes =
        [
            ("OrbitControls", "OrbitControls"), ("TrackballControls", "TrackballControls"), ("PointerLockControls", "PointerLockControls"),
            ("GLTFLoader", "GLTFLoader"), ("OBJLoader", "OBJLoader"), ("FBXLoader", "FBXLoader"), ("DRACOLoader", "DRACOLoader"),
            ("TextureLoader", "TextureLoader"), ("RGBELoader", "RGBELoader"), ("CubeTextureLoader", "CubeTextureLoader"),
            ("EffectComposer", "EffectComposer"), ("UnrealBloomPass", "UnrealBloomPass"), ("ShaderMaterial", "ShaderMaterial"),
            ("RawShaderMaterial", "RawShaderMaterial"), ("InstancedMesh", "InstancedMesh"), ("AnimationMixer", "AnimationMixer"),
            ("Raycaster", "Raycaster"), ("THREE.Points", "Points"), ("new Points(", "Points"), ("LineSegments", "Lines"),
            ("new THREE.Line(", "Lines"), ("Sprite", "Sprites"), ("CSS2DRenderer", "CSS2DRenderer"), ("CSS3DRenderer", "CSS3DRenderer"),
            ("PositionalAudio", "PositionalAudio"), ("AudioListener", "Audio"), ("VRButton", "WebXR"), ("ARButton", "WebXR"),
            ("castShadow", "Shadows"), ("cannon", "Physics (cannon)"), ("ammo", "Physics (ammo)"), ("rapier", "Physics (rapier)"),
            ("lil-gui", "GUI panel"), ("dat.gui", "GUI panel"), ("addEventListener('keydown'", "Keyboard input"),
            ("addEventListener(\"keydown\"", "Keyboard input"), ("pointerdown", "Pointer input"), ("mousedown", "Pointer input"),
            ("setAnimationLoop", "Animation loop"), ("requestAnimationFrame", "Animation loop"), ("Clock", "THREE.Clock"),
            ("Fog", "Fog"), ("ACESFilmicToneMapping", "ACES tone mapping"),
        ];

        foreach (SourceFile script in project.Scripts)
        {
            foreach ((string pattern, string feature) in probes)
            {
                if (script.Content.Contains(pattern, StringComparison.Ordinal))
                {
                    project.DetectedFeatures.Add(feature);
                }
            }
        }
    }

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex NonIdentifier();

    [GeneratedRegex(@"<script[^>]*\bsrc\s*=\s*[""'](?<src>[^""']+\.(?:m?js|ts|jsx|tsx))[""'][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTag();

    [GeneratedRegex(@"<script[^>]*type\s*=\s*[""']module[""'][^>]*>(?<body>.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InlineModule();

    [GeneratedRegex(@"THREE\.|from\s+['""]three")]
    private static partial Regex ThreeReference();

    [GeneratedRegex(@"""three""\s*:\s*""[\^~]?(?<version>[0-9.]+)""")]
    private static partial Regex PackageThreeVersion();

    [GeneratedRegex(@"three@(?<version>[0-9.]+)")]
    private static partial Regex CdnThreeVersion();
}
