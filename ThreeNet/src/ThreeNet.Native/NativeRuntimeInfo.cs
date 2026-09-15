namespace ThreeNet.Native;

/// <summary>
/// Describes where the Three.Net native core lives for the current platform.
/// The managed <c>ThreeNet</c> assembly uses these values to resolve the
/// shared library when it is not next to the running assembly.
/// </summary>
public static class NativeRuntimeInfo
{
    /// <summary>Name passed to <c>DllImport</c> / <c>LibraryImport</c>.</summary>
    public const string LibraryName = "threenet_core";

    /// <summary>Platform specific file name of the shared library.</summary>
    public static string FileName =>
        OperatingSystem.IsWindows() ? "threenet_core.dll"
        : OperatingSystem.IsMacOS() ? "libthreenet_core.dylib"
        : "libthreenet_core.so";

    /// <summary>Runtime identifier folder used inside the NuGet package.</summary>
    public static string RuntimeIdentifier =>
        OperatingSystem.IsWindows() ? "win-x64"
        : OperatingSystem.IsMacOS() ? "osx-arm64"
        : "linux-x64";
}
