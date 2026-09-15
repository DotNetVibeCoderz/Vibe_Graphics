using Avalonia;

namespace ThreeGallery;

internal static class Program
{
    // Avalonia needs an STA main thread on Windows, which is also what the
    // Three.Net window loop expects.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
