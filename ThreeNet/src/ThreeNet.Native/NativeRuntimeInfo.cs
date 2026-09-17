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

    /// <summary>True where the core is linked statically into the app (iOS, browser).</summary>
    public static bool IsStaticallyLinked => OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsBrowser();

    /// <summary>Platform specific file name of the native library.</summary>
    public static string FileName =>
        OperatingSystem.IsWindows() ? "threenet_core.dll"
        : OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() ? "libthreenet_core.dylib"
        : IsStaticallyLinked ? "libthreenet_core.a"
        : "libthreenet_core.so";

    /// <summary>Runtime identifier folder used inside the NuGet package.</summary>
    public static string RuntimeIdentifier
    {
        get
        {
            string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                System.Runtime.InteropServices.Architecture.X86 => "x86",
                System.Runtime.InteropServices.Architecture.Wasm => "wasm",
                _ => "x64",
            };
            return OperatingSystem.IsWindows() ? $"win-{arch}"
                : OperatingSystem.IsMacOS() ? $"osx-{arch}"
                : OperatingSystem.IsAndroid() ? $"android-{arch}"
                : OperatingSystem.IsIOS() ? "ios-arm64"
                : OperatingSystem.IsBrowser() ? "browser-wasm"
                : $"linux-{arch}";
        }
    }
}
