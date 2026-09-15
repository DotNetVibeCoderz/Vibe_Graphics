using System.Reflection;
using System.Runtime.InteropServices;
using ThreeNet.Native;

namespace ThreeNet.Interop;

/// <summary>
/// Resolves <c>threenet_core</c> from the usual publish layouts. The default
/// probing already covers <c>runtimes/&lt;rid&gt;/native</c> for restored NuGet
/// packages, but self contained folders, single file bundles and side by side
/// build outputs need the extra hints below.
/// </summary>
internal static class NativeLibraryResolver
{
    private static int _registered;

    public static void Register()
    {
        // Interlocked keeps this idempotent when several static constructors race.
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, NativeRuntimeInfo.LibraryName, StringComparison.Ordinal))
        {
            return nint.Zero;
        }

        foreach (string candidate in CandidatePaths())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out nint handle))
            {
                return handle;
            }
        }

        // Fall back to the default probing logic (PATH, LD_LIBRARY_PATH, ...).
        return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out nint fallback)
            ? fallback
            : nint.Zero;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        string fileName = NativeRuntimeInfo.FileName;
        string rid = NativeRuntimeInfo.RuntimeIdentifier;

        // AppContext.BaseDirectory also works for single file bundles, where
        // Assembly.Location is empty.
        string baseDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDirectory))
        {
            yield return Path.Combine(baseDirectory, fileName);
            yield return Path.Combine(baseDirectory, "runtimes", rid, "native", fileName);
        }

        // Development layout: the Rust target folder of a source checkout.
        string? repository = FindRepositoryRoot(baseDirectory);
        if (repository is not null)
        {
            yield return Path.Combine(repository, "rust", "target", "release", fileName);
            yield return Path.Combine(repository, "rust", "target", "debug", fileName);
        }

        string? environmentOverride = Environment.GetEnvironmentVariable("THREENET_NATIVE_PATH");
        if (!string.IsNullOrEmpty(environmentOverride))
        {
            yield return Directory.Exists(environmentOverride)
                ? Path.Combine(environmentOverride, fileName)
                : environmentOverride;
        }
    }

    /// <summary>Walks up from <paramref name="start"/> looking for the repository marker.</summary>
    private static string? FindRepositoryRoot(string start)
    {
        DirectoryInfo? directory = new(start);
        for (int depth = 0; directory is not null && depth < 12; depth++)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "rust", "threenet-core")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
