using System.Diagnostics;
using System.Text;

namespace ThreeAppGen.Services;

/// <summary>The project currently open in the editor.</summary>
public sealed record ProjectContext(string Name, string RootPath, string ProjectFilePath);

/// <summary>
/// Owns the open project: creation from templates, path resolution for the
/// assistant, and the dotnet CLI commands behind Build, Run and Deploy.
/// </summary>
public sealed class ProjectService(LogService log)
{
    /// <summary>Raised when files are added, removed or written.</summary>
    public event Action? FilesChanged;

    public ProjectContext? Current { get; private set; }

    /// <summary>Where new projects go when the caller does not say.</summary>
    public string DefaultProjectsFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "ThreeNetProjects");

    public void Open(string path)
    {
        string root = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        string? projectFile = Directory
            .EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();

        Current = new ProjectContext(new DirectoryInfo(root).Name, root, projectFile ?? string.Empty);
        log.Success($"Opened project {Current.Name} ({root})");
        FilesChanged?.Invoke();
    }

    public void Close()
    {
        if (Current is not null)
        {
            log.Info($"Closed project {Current.Name}");
        }

        Current = null;
        FilesChanged?.Invoke();
    }

    public void NotifyFilesChanged() => FilesChanged?.Invoke();

    /// <summary>
    /// Turns a project relative path into an absolute one, refusing anything
    /// that would escape the project folder.
    /// </summary>
    public string ResolvePath(string relativePath)
    {
        if (Current is not { } project)
        {
            throw new InvalidOperationException("no project is open");
        }

        string combined = Path.GetFullPath(Path.Combine(project.RootPath, relativePath ?? string.Empty));
        string root = Path.GetFullPath(project.RootPath);
        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"'{relativePath}' is outside the project folder");
        }

        return combined;
    }

    /// <summary>Creates a project folder, csproj and Program.cs from a template.</summary>
    public async Task<ProjectContext> CreateProjectAsync(string name, ProjectTemplate template, string parentFolder)
    {
        string safeName = SanitizeName(name);
        string root = Path.Combine(string.IsNullOrWhiteSpace(parentFolder) ? DefaultProjectsFolder : parentFolder, safeName);
        Directory.CreateDirectory(root);

        string projectFile = Path.Combine(root, $"{safeName}.csproj");
        await File.WriteAllTextAsync(projectFile, BuildProjectFile(template));
        await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), template.ProgramCode);
        await File.WriteAllTextAsync(Path.Combine(root, "README.md"), BuildReadme(safeName, template));

        Current = new ProjectContext(safeName, root, projectFile);
        log.Success($"Created project {safeName} from template '{template.Id}' at {root}");
        FilesChanged?.Invoke();
        return Current;
    }

    /// <summary>Runs `dotnet &lt;arguments&gt;` inside the project folder.</summary>
    public async Task<(int ExitCode, string Output)> RunDotnetAsync(string arguments, CancellationToken cancellationToken = default)
    {
        if (Current is not { } project)
        {
            return (-1, "No project is open.");
        }

        log.Info($"> dotnet {arguments}");
        ProcessStartInfo startInfo = new("dotnet", arguments)
        {
            WorkingDirectory = project.RootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Reused MSBuild nodes and the MSBuild/compiler servers inherit the redirected
        // output pipe and would keep this call waiting long after the build finished.
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        startInfo.Environment["UseSharedCompilation"] = "false";

        using Process process = new() { StartInfo = startInfo };
        StringBuilder output = new();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output.AppendLine(e.Data);
                log.Info(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output.AppendLine(e.Data);
                log.Error(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode == 0)
        {
            log.Success($"dotnet {arguments.Split(' ')[0]} finished");
        }
        else
        {
            log.Error($"dotnet {arguments.Split(' ')[0]} failed with exit code {process.ExitCode}");
        }

        return (process.ExitCode, output.ToString());
    }

    /// <summary>Starts the project in a separate process so the editor stays responsive.</summary>
    public Process? StartProject()
    {
        if (Current is not { } project)
        {
            return null;
        }

        log.Info("> dotnet run");
        ProcessStartInfo startInfo = new("dotnet", "run")
        {
            WorkingDirectory = project.RootPath,
            UseShellExecute = false,
        };

        Process? process = Process.Start(startInfo);
        if (process is null)
        {
            log.Error("Could not start the project");
        }

        return process;
    }

    /// <summary>Publishes a self contained build into <c>publish/</c>.</summary>
    public async Task<(int ExitCode, string Output)> DeployAsync(string runtimeIdentifier = "win-x64")
    {
        (int exitCode, string output) = await RunDotnetAsync(
            $"publish -c Release -r {runtimeIdentifier} --self-contained true -o publish");

        if (exitCode == 0 && Current is { } project)
        {
            log.Success($"Published to {Path.Combine(project.RootPath, "publish")}");
        }

        return (exitCode, output);
    }

    /// <summary>
    /// Generated projects reference the Three.Net sources of this checkout, so
    /// they build without a published NuGet package.
    /// </summary>
    private static string BuildProjectFile(ProjectTemplate template)
    {
        string? repository = FindRepositoryRoot();
        StringBuilder references = new();

        if (repository is not null)
        {
            references.AppendLine($"""    <ProjectReference Include="{Path.Combine(repository, "src", "ThreeNet", "ThreeNet.csproj")}" />""");
            if (template.UsesAvalonia)
            {
                references.AppendLine($"""    <ProjectReference Include="{Path.Combine(repository, "src", "ThreeNet.Avalonia", "ThreeNet.Avalonia.csproj")}" />""");
            }
        }
        else
        {
            references.AppendLine("""    <PackageReference Include="ThreeNet" Version="0.1.0" />""");
            if (template.UsesAvalonia)
            {
                references.AppendLine("""    <PackageReference Include="ThreeNet.Avalonia" Version="0.1.0" />""");
            }
        }

        string avaloniaPackages = template.UsesAvalonia
            ? """
                  <PackageReference Include="Avalonia" Version="12.1.2" />
                  <PackageReference Include="Avalonia.Desktop" Version="12.1.2" />
                  <PackageReference Include="Avalonia.Themes.Fluent" Version="12.1.2" />
              """
            : string.Empty;

        return $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <OutputType>{(template.UsesAvalonia ? "WinExe" : "Exe")}</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
              </PropertyGroup>

              <ItemGroup>
            {references}{avaloniaPackages}  </ItemGroup>

            </Project>

            """;
    }

    private static string BuildReadme(string name, ProjectTemplate template) => $"""
        # {name}

        {template.Description}

        Dibuat dengan **Three.Net App Generator** (Jack - The Code Bender).
        Generated with **Three.Net App Generator** (Jack - The Code Bender).

        ## Menjalankan / Running

        ```bash
        dotnet run
        ```

        Template: `{template.Id}` ({template.Category})

        ---
        Three.Net - dibuat oleh Gravicode Studios, dipimpin Kang Fadhil.
        """;

    /// <summary>Walks up looking for the Three.Net checkout that hosts this tool.</summary>
    private static string? FindRepositoryRoot()
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

    private static string SanitizeName(string name)
    {
        string trimmed = string.Concat(name.Trim().Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        trimmed = trimmed.Replace(' ', '.');
        return string.IsNullOrWhiteSpace(trimmed) ? "ThreeNetApp" : trimmed;
    }
}
