using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ThreeAppGen.Views;

namespace ThreeAppGen;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindow window = new();
            desktop.MainWindow = window;

            // `ThreeAppGen --convert [folder]` opens the Three.js conversion page on start.
            string[] args = desktop.Args ?? [];
            int convert = Array.FindIndex(args, a => a.Equals("--convert", StringComparison.OrdinalIgnoreCase));
            if (convert >= 0)
            {
                string? folder = convert + 1 < args.Length ? args[convert + 1] : null;
                window.Opened += (_, _) => window.OpenConverter(folder);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
