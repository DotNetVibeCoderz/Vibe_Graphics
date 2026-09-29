using Avalonia;

namespace DemoGraphics;

internal static class Program
{
    // Avalonia needs an STA main thread on Windows, which is also what the
    // Three.Net window loop expects.
    [STAThread]
    public static int Main(string[] args)
    {
        // --check builds and renders every scene offscreen, without a window.
        if (args.Contains("--check", StringComparer.OrdinalIgnoreCase))
        {
            return SelfCheck.Run();
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
