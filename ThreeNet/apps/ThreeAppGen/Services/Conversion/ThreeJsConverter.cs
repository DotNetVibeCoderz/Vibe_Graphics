using System.Text;

namespace ThreeAppGen.Services.Conversion;

/// <summary>
/// Orchestrates a Three.js to Three.Net conversion:
/// analyse, extract the scene inventory, scaffold the solution, copy assets,
/// convert (baseline + LLM), validate with a real build, auto-fix with the LLM,
/// and fall back to the always-compiling baseline when the fixes run out.
/// </summary>
public sealed class ThreeJsConverter(AppSettings settings)
{
    private const long LargeProjectCharacters = 60_000;
    private const int MaxSummarizedModules = 12;

    public event Action<ConversionLogEntry>? Logged;

    public event Action<ConversionProgress>? ProgressChanged;

    /// <summary>Default destination: Documents/ThreeNet/[ProjectName].</summary>
    public static string DefaultDestination(string projectName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "ThreeNet",
        projectName);

    public async Task<ConversionResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken)
    {
        List<string> warnings = [];
        string reportPath = Path.Combine(request.DestinationFolder, "CONVERSION_REPORT.md");
        StringBuilder llmNotes = new();
        bool usedLlm = false;
        bool fellBack = false;
        int fixAttempts = 0;
        ScaffoldResult? scaffold = null;

        void Log(ConversionLogLevel level, string message)
        {
            if (level == ConversionLogLevel.Warning)
            {
                warnings.Add(message);
            }

            Logged?.Invoke(new ConversionLogEntry(DateTime.Now, level, message));
        }

        void Progress(ConversionStage stage, double percent, string message)
        {
            ProgressChanged?.Invoke(new ConversionProgress(stage, percent, message));
            Log(ConversionLogLevel.Info, $"[{stage}] {message}");
        }

        try
        {
            // ---------------------------------------------------------- analyse
            Progress(ConversionStage.Analyzing, 3, $"Scanning {request.SourceFolder}");
            ValidateRequest(request);
            ThreeJsProject project = await Task.Run(() => ThreeJsProjectAnalyzer.Analyze(request.SourceFolder, Log), cancellationToken);
            if (project.Scripts.Count == 0)
            {
                throw new InvalidOperationException("No JavaScript or TypeScript sources were found in the selected folder.");
            }

            if (!project.UsesThree)
            {
                Log(ConversionLogLevel.Warning, "The sources never mention 'three'; the conversion will likely be empty.");
            }

            // ---------------------------------------------------------- extract
            Progress(ConversionStage.Extracting, 10, "Building the scene inventory from the sources");
            SceneInventory inventory = await Task.Run(() => SceneInventoryExtractor.Extract(project, Log), cancellationToken);

            // --------------------------------------------------------- scaffold
            Progress(ConversionStage.Scaffolding, 18, $"Creating the {request.Target} solution in {request.DestinationFolder}");
            string? repository = FindThreeNetRepository();
            if (repository is null)
            {
                Log(ConversionLogLevel.Warning, "Three.Net sources were not found next to ThreeAppGen; the project references the ThreeNet NuGet packages instead.");
            }

            scaffold = ConvertedProjectScaffolder.Scaffold(request, repository);
            foreach (GeneratedProject generated in scaffold.Projects)
            {
                Log(ConversionLogLevel.Success, $"Project {generated.Name}: {generated.Role}");
            }

            string compatPath = Path.Combine(scaffold.CoreFolder, "ThreeJsCompat.cs");
            await File.WriteAllTextAsync(compatPath, BaselineCodeGenerator.GenerateCompat(request.ProjectName), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(scaffold.CoreFolder, "OrbitRig.cs"), BaselineCodeGenerator.GenerateOrbitRig(request.ProjectName), cancellationToken);

            // ------------------------------------------------------------ assets
            Progress(ConversionStage.CopyingAssets, 24, $"Copying {project.Assets.Count} assets");
            int copied = await Task.Run(() => ConvertedProjectScaffolder.CopyAssets(project, scaffold.AssetsFolder, Log, cancellationToken), cancellationToken);
            Log(ConversionLogLevel.Success, $"Copied {copied} assets");

            // ---------------------------------------------------------- baseline
            Progress(ConversionStage.Converting, 30, "Generating the deterministic baseline");
            string baseline = BaselineCodeGenerator.GenerateConvertedScene(request.ProjectName, inventory, project);
            string scenePath = Path.Combine(scaffold.CoreFolder, "ConvertedScene.cs");
            await File.WriteAllTextAsync(scenePath, baseline, cancellationToken);

            // Build the baseline first: it proves the scaffold and the native core are sound
            // before any model output is involved, and primes the incremental build.
            Progress(ConversionStage.Validating, 36, "Compiling the baseline");
            BuildOutcome baselineBuild = await BuildRequiredAsync(scaffold, skipRustBuild: false, Log, cancellationToken);
            if (!baselineBuild.Succeeded)
            {
                Log(ConversionLogLevel.Error, "The baseline does not compile; this is a converter bug, the details are in the report.");
            }
            else
            {
                Log(ConversionLogLevel.Success, "Baseline compiles");
            }

            // --------------------------------------------------------------- LLM
            LlmCodeConverter llm = new(settings);
            List<GeneratedFile> llmFiles = [];
            bool llmBuildSucceeded = false;

            if (request.UseLlm && !llm.IsAvailable)
            {
                Log(ConversionLogLevel.Warning, $"The {settings.ActiveProvider} provider is not configured; using the baseline only. Configure it in Settings for a full conversion.");
            }
            else if (request.UseLlm)
            {
                usedLlm = true;
                Dictionary<string, string> summaries = [];
                if (project.TotalScriptCharacters > LargeProjectCharacters)
                {
                    List<SourceFile> toSummarize = project.ScriptsByPriority.Skip(1).Take(MaxSummarizedModules).ToList();
                    for (int i = 0; i < toSummarize.Count; i++)
                    {
                        Progress(ConversionStage.Converting, 40 + (10.0 * i / toSummarize.Count), $"Summarising {toSummarize[i].RelativePath} ({llm.ModelDescription})");
                        summaries[toSummarize[i].RelativePath] = await llm.SummarizeModuleAsync(toSummarize[i], cancellationToken);
                    }
                }

                Progress(ConversionStage.Converting, 52, $"Translating the scene with {llm.ModelDescription}");
                LlmConversionOutput output = await llm.ConvertAsync(request.ProjectName, project, inventory, baseline, summaries, cancellationToken);
                if (output.Notes.Length > 0)
                {
                    llmNotes.AppendLine(output.Notes);
                }

                if (!output.Files.Any(f => f.Content.Contains("class ConvertedScene", StringComparison.Ordinal)))
                {
                    Log(ConversionLogLevel.Warning, "The model reply did not contain a usable ConvertedScene.cs; keeping the baseline.");
                }
                else
                {
                    llmFiles.AddRange(output.Files);
                    await WriteFilesAsync(scaffold.CoreFolder, output.Files, Log, cancellationToken);

                    // ------------------------------------------ validate and fix
                    for (int attempt = 0; attempt <= request.MaxFixAttempts; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        double percent = 62 + (28.0 * attempt / Math.Max(request.MaxFixAttempts, 1));
                        Progress(attempt == 0 ? ConversionStage.Validating : ConversionStage.Fixing, percent,
                            attempt == 0 ? "Compiling the converted project" : $"Rebuilding after fix {attempt}");

                        BuildOutcome build = await BuildRequiredAsync(scaffold, skipRustBuild: true, Log, cancellationToken);
                        if (build.Succeeded)
                        {
                            llmBuildSucceeded = true;
                            Log(ConversionLogLevel.Success, attempt == 0 ? "The converted project compiles on the first try" : $"Compiles after {attempt} fix round(s)");
                            break;
                        }

                        List<BuildDiagnostic> coreErrors = build.Errors
                            .Where(e => IsInside(e.File, scaffold.CoreFolder))
                            .ToList();

                        if (coreErrors.Count == 0)
                        {
                            Log(ConversionLogLevel.Error, "The remaining errors are outside the converted code; stopping the fix loop.");
                            break;
                        }

                        if (attempt == request.MaxFixAttempts)
                        {
                            Log(ConversionLogLevel.Warning, $"Still {coreErrors.Count} error(s) after {attempt} fix round(s).");
                            break;
                        }

                        fixAttempts = attempt + 1;
                        Progress(ConversionStage.Fixing, percent + 2, $"Asking {llm.ModelDescription} to fix {coreErrors.Count} error(s) (round {fixAttempts}/{request.MaxFixAttempts})");
                        IReadOnlyList<GeneratedFile> current = await ReadCoreFilesAsync(scaffold.CoreFolder, cancellationToken);
                        LlmConversionOutput fix = await llm.FixAsync(request.ProjectName, coreErrors, current, fixAttempts, cancellationToken);
                        if (fix.Files.Count == 0)
                        {
                            Log(ConversionLogLevel.Warning, "The fix reply contained no files.");
                            continue;
                        }

                        if (fix.Notes.Length > 0)
                        {
                            llmNotes.AppendLine(fix.Notes);
                        }

                        await WriteFilesAsync(scaffold.CoreFolder, fix.Files, Log, cancellationToken);
                    }

                    if (!llmBuildSucceeded)
                    {
                        // Safety net: remove what the model wrote and restore the baseline.
                        fellBack = true;
                        Log(ConversionLogLevel.Warning, "Falling back to the baseline conversion so the project compiles.");
                        foreach (GeneratedFile file in llmFiles.Concat(await ReadCoreFilesAsync(scaffold.CoreFolder, cancellationToken)).DistinctBy(f => f.RelativePath))
                        {
                            string path = Path.Combine(scaffold.CoreFolder, file.RelativePath);
                            if (!BaselineCodeGenerator.ProtectedFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) && File.Exists(path))
                            {
                                string backup = Path.Combine(scaffold.Root, "llm-attempt", file.RelativePath);
                                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                                File.Move(path, backup, overwrite: true);
                            }
                        }

                        await File.WriteAllTextAsync(scenePath, baseline, cancellationToken);
                        Log(ConversionLogLevel.Info, "The last LLM attempt was kept in llm-attempt/ for reference.");
                    }
                }
            }

            // ------------------------------------------------------- final build
            Progress(ConversionStage.Validating, 92, "Final validation build");
            BuildOutcome final = await BuildRequiredAsync(scaffold, skipRustBuild: true, Log, cancellationToken);
            await BuildOptionalHeadsAsync(scaffold, Log, cancellationToken);

            // ------------------------------------------------------------ report
            Progress(ConversionStage.Reporting, 97, "Writing CONVERSION_REPORT.md and README.md");
            await File.WriteAllTextAsync(reportPath, ConversionReport.Build(request, project, inventory, scaffold, final, usedLlm, fellBack, fixAttempts, llmNotes.ToString(), warnings, settings), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(scaffold.Root, "README.md"), ConversionReport.BuildReadme(request, scaffold), cancellationToken);

            ConversionResult result = new()
            {
                Succeeded = final.Succeeded,
                ProjectRoot = scaffold.Root,
                SolutionPath = scaffold.SolutionPath,
                StartupProjectPath = scaffold.Startup.ProjectPath,
                ReportPath = reportPath,
                UsedLlm = usedLlm,
                FellBackToBaseline = fellBack,
                FixAttempts = fixAttempts,
                Warnings = warnings,
                Error = final.Succeeded ? null : "The generated project does not compile; see the logs and CONVERSION_REPORT.md.",
            };

            Progress(final.Succeeded ? ConversionStage.Completed : ConversionStage.Failed, 100,
                final.Succeeded ? $"Done: {scaffold.Root}" : "Finished with build errors");
            return result;
        }
        catch (OperationCanceledException)
        {
            Log(ConversionLogLevel.Warning, "Conversion cancelled.");
            ProgressChanged?.Invoke(new ConversionProgress(ConversionStage.Failed, 100, "Cancelled"));
            return Failure(request, scaffold, reportPath, "Cancelled by the user.", warnings);
        }
        catch (Exception exception)
        {
            Log(ConversionLogLevel.Error, exception.Message);
            ProgressChanged?.Invoke(new ConversionProgress(ConversionStage.Failed, 100, exception.Message));
            return Failure(request, scaffold, reportPath, exception.Message, warnings);
        }
    }

    private static ConversionResult Failure(ConversionRequest request, ScaffoldResult? scaffold, string reportPath, string error, List<string> warnings) => new()
    {
        Succeeded = false,
        ProjectRoot = scaffold?.Root ?? request.DestinationFolder,
        SolutionPath = scaffold?.SolutionPath ?? string.Empty,
        StartupProjectPath = scaffold?.Startup.ProjectPath ?? string.Empty,
        ReportPath = reportPath,
        Warnings = warnings,
        Error = error,
    };

    private static void ValidateRequest(ConversionRequest request)
    {
        if (!Directory.Exists(request.SourceFolder))
        {
            throw new DirectoryNotFoundException($"The source folder does not exist: {request.SourceFolder}");
        }

        string source = Path.GetFullPath(request.SourceFolder).TrimEnd(Path.DirectorySeparatorChar);
        string destination = Path.GetFullPath(request.DestinationFolder).TrimEnd(Path.DirectorySeparatorChar);
        if (destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || destination.Equals(source, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The destination must not be inside the source folder.");
        }

        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any() && !request.OverwriteDestination)
        {
            throw new InvalidOperationException($"The destination is not empty: {destination}. Pick another folder or allow overwriting.");
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(request.ProjectName, "^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            throw new InvalidOperationException("The project name must be a valid C# identifier (letters, digits, underscore).");
        }
    }

    private static async Task<BuildOutcome> BuildRequiredAsync(
        ScaffoldResult scaffold,
        bool skipRustBuild,
        Action<ConversionLogLevel, string> log,
        CancellationToken cancellationToken)
    {
        // Building the startup head builds Core (and App) through project references.
        return await BuildValidator.BuildAsync(scaffold.Startup.ProjectPath, skipRustBuild, log, cancellationToken);
    }

    private static async Task BuildOptionalHeadsAsync(ScaffoldResult scaffold, Action<ConversionLogLevel, string> log, CancellationToken cancellationToken)
    {
        foreach (GeneratedProject head in scaffold.Projects.Where(p => !p.Required))
        {
            log(ConversionLogLevel.Info, $"Checking the {head.Name} head (best effort)");
            BuildOutcome outcome = await BuildValidator.BuildAsync(head.ProjectPath, skipRustBuild: true, (level, message) =>
                log(level == ConversionLogLevel.Error ? ConversionLogLevel.Warning : level, message), cancellationToken);

            if (outcome.Succeeded)
            {
                log(ConversionLogLevel.Success, $"{head.Name} compiles");
            }
            else if (outcome.MissingWorkload)
            {
                log(ConversionLogLevel.Warning, $"{head.Name} needs a .NET workload that is not installed (for example `dotnet workload install wasm-tools` or `android`).");
            }
            else
            {
                log(ConversionLogLevel.Warning, $"{head.Name} did not compile; the preview head is unaffected. See the logs above.");
            }
        }
    }

    private static async Task WriteFilesAsync(string coreFolder, IEnumerable<GeneratedFile> files, Action<ConversionLogLevel, string> log, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(coreFolder);
        foreach (GeneratedFile file in files)
        {
            string path = Path.GetFullPath(Path.Combine(root, file.RelativePath));
            if (!IsInside(path, root))
            {
                log(ConversionLogLevel.Warning, $"Ignored a file outside the Core project: {file.RelativePath}");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, file.Content, cancellationToken);
            log(ConversionLogLevel.Success, $"Wrote {file.RelativePath} ({file.Content.Length:N0} chars)");
        }
    }

    private static async Task<IReadOnlyList<GeneratedFile>> ReadCoreFilesAsync(string coreFolder, CancellationToken cancellationToken)
    {
        List<GeneratedFile> files = [];
        foreach (string path in Directory.EnumerateFiles(coreFolder, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(coreFolder, path).Replace('\\', '/');
            if (relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            files.Add(new GeneratedFile(relative, await File.ReadAllTextAsync(path, cancellationToken)));
        }

        return files;
    }

    private static bool IsInside(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string full = Path.GetFullPath(path);
        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindThreeNetRepository()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int depth = 0; directory is not null && depth < 12; depth++)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "ThreeNet")) &&
                Directory.Exists(Path.Combine(directory.FullName, "rust", "threenet-core")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
