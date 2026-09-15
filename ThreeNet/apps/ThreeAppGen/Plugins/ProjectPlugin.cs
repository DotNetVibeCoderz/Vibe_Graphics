using System.ComponentModel;
using System.Text;
using Microsoft.SemanticKernel;
using ThreeAppGen.Services;

namespace ThreeAppGen.Plugins;

/// <summary>
/// File and project operations Jack uses to actually write an application.
/// Every path is resolved inside the open project, so the assistant can never
/// touch files outside the workspace.
/// </summary>
public sealed class ProjectPlugin(ProjectService projects, LogService log)
{
    [KernelFunction, Description("Lists the files and folders of the open project, as a tree.")]
    public string ListFiles(
        [Description("Folder relative to the project root. Empty means the root.")] string folder = "",
        [Description("Maximum depth to walk, 1-8.")] int depth = 4)
    {
        if (projects.Current is not { } project)
        {
            return "No project is open. Use CreateProject first.";
        }

        string root = projects.ResolvePath(folder);
        StringBuilder builder = new();
        AppendTree(new DirectoryInfo(root), builder, string.Empty, Math.Clamp(depth, 1, 8));
        log.Info($"Jack listed files under '{Path.GetRelativePath(project.RootPath, root)}'");
        return builder.Length == 0 ? "(empty)" : builder.ToString();
    }

    [KernelFunction, Description("Reads a text file from the open project and returns its content.")]
    public async Task<string> ReadFile(
        [Description("Path relative to the project root, for example src/Program.cs")] string path)
    {
        string full = projects.ResolvePath(path);
        if (!File.Exists(full))
        {
            return $"File not found: {path}";
        }

        log.Info($"Jack read {path}");
        return await File.ReadAllTextAsync(full);
    }

    [KernelFunction, Description("Creates or overwrites a text file in the open project. Use it to write code.")]
    public async Task<string> WriteFile(
        [Description("Path relative to the project root, for example src/Program.cs")] string path,
        [Description("Full content of the file.")] string content)
    {
        string full = projects.ResolvePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);
        projects.NotifyFilesChanged();
        log.Success($"Jack wrote {path} ({content.Length} chars)");
        return $"Wrote {path} ({content.Length} characters).";
    }

    [KernelFunction, Description("Deletes a file from the open project.")]
    public string DeleteFile([Description("Path relative to the project root.")] string path)
    {
        string full = projects.ResolvePath(path);
        if (!File.Exists(full))
        {
            return $"File not found: {path}";
        }

        File.Delete(full);
        projects.NotifyFilesChanged();
        log.Warning($"Jack deleted {path}");
        return $"Deleted {path}.";
    }

    [KernelFunction, Description("Creates a folder in the open project.")]
    public string CreateDirectory([Description("Path relative to the project root.")] string path)
    {
        Directory.CreateDirectory(projects.ResolvePath(path));
        projects.NotifyFilesChanged();
        return $"Created folder {path}.";
    }

    [KernelFunction, Description("Searches the project for a piece of text and returns the matching lines.")]
    public string SearchInFiles(
        [Description("Text to look for (case insensitive).")] string query,
        [Description("File pattern, for example *.cs")] string pattern = "*.cs")
    {
        if (projects.Current is not { } project)
        {
            return "No project is open.";
        }

        StringBuilder builder = new();
        foreach (string file in Directory.EnumerateFiles(project.RootPath, pattern, SearchOption.AllDirectories))
        {
            if (IsIgnored(file))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    builder.AppendLine($"{Path.GetRelativePath(project.RootPath, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        return builder.Length == 0 ? $"No match for '{query}'." : builder.ToString();
    }

    [KernelFunction, Description("Creates a new Three.Net application from a template, in a new folder.")]
    public async Task<string> CreateProject(
        [Description("Project name, used as the folder and assembly name.")] string name,
        [Description("Template id: blank, spinning-cube, solar-system, terrain, game-runner, particle-lab, product-viewer, simulator")]
        string template = "blank",
        [Description("Parent folder. Empty means next to the currently open project, or the documents folder.")]
        string parentFolder = "")
    {
        ProjectTemplate? found = ProjectTemplates.Find(template);
        if (found is null)
        {
            return $"Unknown template '{template}'. Available: {string.Join(", ", ProjectTemplates.All.Select(t => t.Id))}";
        }

        string parent = string.IsNullOrWhiteSpace(parentFolder)
            ? projects.DefaultProjectsFolder
            : parentFolder;

        ProjectContext project = await projects.CreateProjectAsync(name, found, parent);
        log.Success($"Jack created the project {project.Name} from the '{found.Id}' template");
        return $"Created project '{project.Name}' at {project.RootPath} using the '{found.Id}' template.";
    }

    [KernelFunction, Description("Adds a NuGet package reference to the project.")]
    public async Task<string> AddNuGetPackage(
        [Description("Package id, for example Avalonia")] string package,
        [Description("Optional version; empty installs the latest.")] string version = "")
    {
        string arguments = string.IsNullOrWhiteSpace(version)
            ? $"add package {package}"
            : $"add package {package} --version {version}";

        (int exitCode, string output) = await projects.RunDotnetAsync(arguments);
        return exitCode == 0 ? $"Added {package}.\n{output}" : $"Failed to add {package}:\n{output}";
    }

    [KernelFunction, Description("Builds the open project and returns the compiler output, so errors can be fixed.")]
    public async Task<string> BuildProject()
    {
        (int exitCode, string output) = await projects.RunDotnetAsync("build");
        return exitCode == 0
            ? $"Build succeeded.\n{Tail(output, 40)}"
            : $"Build failed (exit {exitCode}).\n{Tail(output, 60)}";
    }

    private static string Tail(string text, int lines)
    {
        string[] all = text.Split('\n');
        return all.Length <= lines ? text : string.Join('\n', all[^lines..]);
    }

    private static bool IsIgnored(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static void AppendTree(DirectoryInfo directory, StringBuilder builder, string indent, int depth)
    {
        if (depth <= 0 || !directory.Exists)
        {
            return;
        }

        foreach (DirectoryInfo child in directory.GetDirectories().OrderBy(d => d.Name))
        {
            if (child.Name is "bin" or "obj" or ".git" or ".vs")
            {
                continue;
            }

            builder.AppendLine($"{indent}{child.Name}/");
            AppendTree(child, builder, indent + "  ", depth - 1);
        }

        foreach (FileInfo file in directory.GetFiles().OrderBy(f => f.Name))
        {
            builder.AppendLine($"{indent}{file.Name}  ({file.Length} bytes)");
        }
    }
}
