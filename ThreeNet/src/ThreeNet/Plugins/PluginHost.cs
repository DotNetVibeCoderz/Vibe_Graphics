using System.Reflection;
using System.Runtime.Loader;
using ThreeNet.Scenes;

namespace ThreeNet.Plugins;

/// <summary>
/// Implemented by a plugin assembly. Plugins are discovered in a folder and
/// loaded into their own <see cref="AssemblyLoadContext"/>, so they can be
/// unloaded again.
/// </summary>
public interface IThreeNetPlugin
{
    string Name { get; }

    string Description => string.Empty;

    string Version => "1.0";

    /// <summary>Registers commands, generators and importers with the host.</summary>
    void Initialize(IPluginHost host);

    /// <summary>Called before the plugin is unloaded.</summary>
    void Shutdown()
    {
    }
}

/// <summary>What a plugin does when the user runs it.</summary>
/// <param name="Name">Shown in menus.</param>
/// <param name="Category">Groups commands ("Generate", "Tools", ...).</param>
/// <param name="Execute">Receives the current context; edits the document.</param>
public sealed record PluginCommand(string Name, string Category, Action<PluginContext> Execute)
{
    public string Description { get; init; } = string.Empty;
}

/// <summary>Imports a file format the core does not know about, into a document.</summary>
/// <param name="Name">Shown in file dialogs.</param>
/// <param name="Extensions">Lower case, with the dot (".csv").</param>
/// <param name="Import">Adds whatever the file describes to the document.</param>
public sealed record PluginImporter(string Name, IReadOnlyList<string> Extensions, Action<PluginContext, string> Import);

/// <summary>Handed to a plugin command: the document and (when running in a host with a viewport) the live scene.</summary>
public sealed class PluginContext(SceneDocument document)
{
    public SceneDocument Document { get; } = document;

    /// <summary>The node selected in the host, if any.</summary>
    public NodeDefinition? Selection { get; init; }

    /// <summary>The live scene, when the host has one (null in headless use).</summary>
    public Scene? Scene { get; init; }

    /// <summary>Host services: logging and a rebuild request.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Ask the host to rebuild the viewport from the document.</summary>
    public Action? RequestRebuild { get; init; }

    public void Report(string message) => Log?.Invoke(message);
}

/// <summary>The API a plugin uses to register itself.</summary>
public interface IPluginHost
{
    /// <summary>Name of the application hosting the plugin ("ThreeEditor").</summary>
    string ApplicationName { get; }

    void AddCommand(PluginCommand command);

    void AddImporter(PluginImporter importer);

    void Log(string message);
}

/// <summary>A plugin that has been loaded, with what it registered.</summary>
public sealed class LoadedPlugin
{
    internal LoadedPlugin(IThreeNetPlugin instance, string path, PluginLoadContext context)
    {
        Instance = instance;
        Path = path;
        Context = context;
    }

    public IThreeNetPlugin Instance { get; }

    /// <summary>Assembly the plugin came from.</summary>
    public string Path { get; }

    internal PluginLoadContext Context { get; }

    public List<PluginCommand> Commands { get; } = [];

    public List<PluginImporter> Importers { get; } = [];

    public bool Enabled { get; set; } = true;
}

/// <summary>Load context that resolves a plugin's own dependencies but shares ThreeNet with the host.</summary>
internal sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Shared contracts must come from the host, or the interfaces would not match.
        if (assemblyName.Name is "ThreeNet" or "ThreeNet.Native")
        {
            return null;
        }

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}

/// <summary>
/// Discovers and loads Three.Net plugins from a folder: every
/// <c>*.dll</c> is probed for public <see cref="IThreeNetPlugin"/> types.
/// Each plugin gets its own collectible load context, so
/// <see cref="Unload"/> really releases it.
/// </summary>
public sealed class PluginManager(string applicationName) : IPluginHost, IDisposable
{
    private readonly List<LoadedPlugin> _plugins = [];
    private LoadedPlugin? _loading;

    public string ApplicationName { get; } = applicationName;

    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    /// <summary>Commands of every enabled plugin.</summary>
    public IEnumerable<PluginCommand> Commands => _plugins.Where(p => p.Enabled).SelectMany(p => p.Commands);

    public IEnumerable<PluginImporter> Importers => _plugins.Where(p => p.Enabled).SelectMany(p => p.Importers);

    /// <summary>Messages from plugins and the loader.</summary>
    public event Action<string>? Logged;

    /// <summary>Default plugin folder: <c>plugins</c> next to the application.</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "plugins");

    /// <summary>Loads every plugin assembly in a folder (and its immediate subfolders). Returns what loaded.</summary>
    public IReadOnlyList<LoadedPlugin> LoadDirectory(string directory)
    {
        List<LoadedPlugin> loaded = [];
        if (!Directory.Exists(directory))
        {
            return loaded;
        }

        IEnumerable<string> candidates = Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories)
            .Where(file => !Path.GetFileName(file).StartsWith("ThreeNet", StringComparison.OrdinalIgnoreCase));
        foreach (string file in candidates)
        {
            loaded.AddRange(Load(file));
        }

        return loaded;
    }

    /// <summary>Loads the plugins in one assembly.</summary>
    public IReadOnlyList<LoadedPlugin> Load(string assemblyPath)
    {
        List<LoadedPlugin> loaded = [];
        PluginLoadContext context = new(assemblyPath);
        Assembly assembly;
        try
        {
            assembly = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        }
        catch (Exception exception) when (exception is BadImageFormatException or FileLoadException or FileNotFoundException)
        {
            context.Unload();
            return loaded;
        }

        foreach (Type type in SafeTypes(assembly))
        {
            if (!typeof(IThreeNetPlugin).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface || type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }

            try
            {
                IThreeNetPlugin instance = (IThreeNetPlugin)Activator.CreateInstance(type)!;
                LoadedPlugin plugin = new(instance, assemblyPath, context);
                _loading = plugin;
                instance.Initialize(this);
                _loading = null;
                _plugins.Add(plugin);
                loaded.Add(plugin);
                Log($"loaded plugin '{instance.Name}' {instance.Version} ({plugin.Commands.Count} commands)");
            }
            catch (Exception exception)
            {
                _loading = null;
                Log($"plugin '{type.FullName}' failed to load: {exception.Message}");
            }
        }

        if (loaded.Count == 0)
        {
            context.Unload();
        }

        return loaded;
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    public void AddCommand(PluginCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        (_loading ?? throw new InvalidOperationException("commands can only be registered from Initialize")).Commands.Add(command);
    }

    public void AddImporter(PluginImporter importer)
    {
        ArgumentNullException.ThrowIfNull(importer);
        (_loading ?? throw new InvalidOperationException("importers can only be registered from Initialize")).Importers.Add(importer);
    }

    public void Log(string message) => Logged?.Invoke(message);

    /// <summary>Runs a command, reporting failures instead of throwing.</summary>
    public bool Run(PluginCommand command, PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            command.Execute(context);
            return true;
        }
        catch (Exception exception)
        {
            Log($"command '{command.Name}' failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>Imports a file with the first importer that handles its extension.</summary>
    public bool Import(string path, PluginContext context)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        PluginImporter? importer = Importers.FirstOrDefault(i => i.Extensions.Contains(extension));
        if (importer is null)
        {
            return false;
        }

        try
        {
            importer.Import(context, path);
            return true;
        }
        catch (Exception exception)
        {
            Log($"importer '{importer.Name}' failed: {exception.Message}");
            return false;
        }
    }

    public void Unload(LoadedPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (!_plugins.Remove(plugin))
        {
            return;
        }

        try
        {
            plugin.Instance.Shutdown();
        }
        catch (Exception exception)
        {
            Log($"plugin '{plugin.Instance.Name}' failed to shut down: {exception.Message}");
        }

        plugin.Context.Unload();
    }

    public void Dispose()
    {
        foreach (LoadedPlugin plugin in _plugins.ToArray())
        {
            Unload(plugin);
        }
    }
}
