using System.Collections.ObjectModel;

namespace ThreeAppGen.Models;

/// <summary>One entry of the project explorer tree.</summary>
public sealed class FileNode
{
    private static readonly HashSet<string> IgnoredFolders =
        new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", "node_modules", "publish" };

    public required string Name { get; init; }

    public required string Path { get; init; }

    public required bool IsDirectory { get; init; }

    public ObservableCollection<FileNode> Children { get; } = [];

    /// <summary>Glyph shown before the name; keeps the tree readable without an icon set.</summary>
    public string Glyph => IsDirectory
        ? "▸"
        : System.IO.Path.GetExtension(Path).ToLowerInvariant() switch
        {
            ".cs" => "#",
            ".csproj" or ".slnx" or ".sln" => "⚙",
            ".json" or ".config" or ".xml" => "{}",
            ".md" => "¶",
            ".axaml" or ".xaml" => "<>",
            ".png" or ".jpg" or ".jpeg" or ".hdr" => "▣",
            _ => "·",
        };

    /// <summary>Builds the tree for a folder, skipping build output and VCS folders.</summary>
    public static FileNode Load(string path)
    {
        DirectoryInfo directory = new(path);
        FileNode node = new()
        {
            Name = directory.Name,
            Path = directory.FullName,
            IsDirectory = true,
        };

        if (!directory.Exists)
        {
            return node;
        }

        foreach (DirectoryInfo child in directory.GetDirectories().OrderBy(d => d.Name))
        {
            if (IgnoredFolders.Contains(child.Name) || child.Attributes.HasFlag(FileAttributes.Hidden))
            {
                continue;
            }

            node.Children.Add(Load(child.FullName));
        }

        foreach (FileInfo file in directory.GetFiles().OrderBy(f => f.Name))
        {
            if (file.Attributes.HasFlag(FileAttributes.Hidden))
            {
                continue;
            }

            node.Children.Add(new FileNode
            {
                Name = file.Name,
                Path = file.FullName,
                IsDirectory = false,
            });
        }

        return node;
    }

    /// <summary>True for files the editor can open as text.</summary>
    public bool IsTextFile => !IsDirectory && System.IO.Path.GetExtension(Path).ToLowerInvariant() is
        ".cs" or ".csproj" or ".slnx" or ".sln" or ".json" or ".config" or ".xml" or ".md" or ".txt" or
        ".axaml" or ".xaml" or ".wgsl" or ".glsl" or ".hlsl" or ".yml" or ".yaml" or ".toml" or ".rs" or ".props" or ".targets";
}
