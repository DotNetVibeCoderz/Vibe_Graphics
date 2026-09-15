using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ThreeAppGen.Services;
using ThreeAppGen.Services.Conversion;

namespace ThreeAppGen.Views;

/// <summary>
/// The "Convert Three.js project" page: pick the source folder, the target and
/// the destination, watch the progress and logs, then open the result.
/// </summary>
public sealed class ConverterWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ObservableCollection<ConversionLogEntry> _logs = [];

    private readonly TextBox _source = new() { PlaceholderText = "Folder containing index.html, scripts and assets" };
    private readonly TextBox _name = new() { PlaceholderText = "ProjectName" };
    private readonly TextBox _destination = new();
    private readonly TextBlock _analysis = new() { Classes = { "body" }, TextWrapping = TextWrapping.Wrap };
    private readonly RadioButton _desktop = new() { Content = "Desktop", GroupName = "target", IsChecked = true };
    private readonly RadioButton _web = new() { Content = "Web", GroupName = "target" };
    private readonly RadioButton _mobile = new() { Content = "Mobile", GroupName = "target" };
    private readonly TextBlock _targetHint = new() { Classes = { "body" }, TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
    private readonly CheckBox _useLlm = new() { Content = "Use the LLM to translate behaviour (recommended)", IsChecked = true };
    private readonly CheckBox _overwrite = new() { Content = "Allow a non-empty destination" };
    private readonly NumericUpDown _fixAttempts = new() { Minimum = 0, Maximum = 10, Value = 4, Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 6 };
    private readonly TextBlock _stage = new() { Classes = { "mono" }, Text = "Idle" };
    private readonly Button _convert = DialogKit.Button("Convert", accent: true);
    private readonly Button _cancel = DialogKit.Button("Cancel");
    private readonly Button _openInAppGen = DialogKit.Button("Open in ThreeAppGen", accent: true);
    private readonly Button _openInExplorer = DialogKit.Button("Open folder");
    private readonly Button _openReport = DialogKit.Button("View report");
    private readonly StackPanel _completion = new() { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false };
    private readonly TextBlock _resultText = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly ScrollViewer _logScroller = new();

    private CancellationTokenSource? _cancellation;
    private ConversionResult? _result;
    private bool _nameEdited;
    private bool _destinationEdited;

    public ConverterWindow()
        : this(AppSettings.Load())
    {
    }

    public ConverterWindow(AppSettings settings)
    {
        _settings = settings;
        DialogKit.Configure(this, "Convert a Three.js project to Three.Net", 1180, 720);
        CanResize = true;
        MinWidth = 900;
        MinHeight = 640;

        Button browseSource = DialogKit.Button("Browse...");
        Button browseDestination = DialogKit.Button("Browse...");
        browseSource.Click += async (_, _) => await PickSourceAsync();
        browseDestination.Click += async (_, _) => await PickDestinationAsync();

        _name.TextChanged += (_, _) =>
        {
            if (_name.IsFocused)
            {
                _nameEdited = true;
            }

            if (!_destinationEdited && !string.IsNullOrWhiteSpace(_name.Text))
            {
                _destination.Text = ThreeJsConverter.DefaultDestination(_name.Text.Trim());
            }
        };
        _destination.TextChanged += (_, _) =>
        {
            if (_destination.IsFocused)
            {
                _destinationEdited = true;
            }
        };

        foreach (RadioButton radio in new[] { _desktop, _web, _mobile })
        {
            radio.IsCheckedChanged += (_, _) => UpdateTargetHint();
        }

        UpdateTargetHint();

        _convert.Click += async (_, _) => await ConvertAsync();
        _cancel.Click += (_, _) =>
        {
            if (_cancellation is not null)
            {
                _cancellation.Cancel();
            }
            else
            {
                Close(null);
            }
        };
        _openInAppGen.Click += (_, _) => Close(_result);
        _openInExplorer.Click += (_, _) => OpenInFileManager(_result?.ProjectRoot);
        _openReport.Click += (_, _) => OpenWithShell(_result?.ReportPath);
        _completion.Children.AddRange([_openInAppGen, _openInExplorer, _openReport]);

        string provider = settings.Active.IsConfigured
            ? $"LLM: {settings.ActiveProvider} / {settings.Active.Model}"
            : $"LLM: {settings.ActiveProvider} is not configured - the static conversion will be used";

        // ---------------------------------------------------------------- left
        StackPanel form = new()
        {
            Spacing = 2,
            Children =
            {
                DialogKit.Heading("Three.js → Three.Net"),
                DialogKit.Body("Converts a Three.js web project (scripts and assets) into a .NET 10 solution. " +
                               "A static pass maps the scene reliably, the LLM translates the behaviour, and the result " +
                               "is compiled and auto-fixed before you open it."),
                DialogKit.Label("1. Three.js project folder"),
                Row(_source, browseSource),
                new Border
                {
                    Background = DialogKit.Brush("ElevatedBrush"),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10),
                    Margin = new Thickness(0, 8, 0, 0),
                    Child = _analysis,
                },
                DialogKit.Label("2. Target"),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Children = { _desktop, _web, _mobile } },
                _targetHint,
                DialogKit.Label("3. Project name and destination"),
                _name,
                new Border { Height = 6 },
                Row(_destination, browseDestination),
                _overwrite,
                DialogKit.Label("4. Conversion"),
                _useLlm,
                new TextBlock { Text = provider, Classes = { "mono" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = "Auto-fix rounds", VerticalAlignment = VerticalAlignment.Center, Classes = { "body" } },
                        _fixAttempts,
                    },
                },
                DialogKit.Buttons(_cancel, _convert),
            },
        };
        _analysis.Text = "Pick a folder to see what will be converted.";

        // --------------------------------------------------------------- right
        ItemsControl logList = new()
        {
            ItemsSource = _logs,
            ItemTemplate = new FuncDataTemplate<ConversionLogEntry>((entry, _) =>
            {
                Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("62,44,*"), Margin = new Thickness(0, 1) };
                grid.Children.Add(new TextBlock { Text = entry.Time, Classes = { "mono" }, Foreground = DialogKit.Brush("TextMutedBrush") });
                TextBlock badge = new()
                {
                    Text = entry.Badge,
                    Classes = { "mono" },
                    Foreground = entry.Level switch
                    {
                        ConversionLogLevel.Success => DialogKit.Brush("SuccessBrush"),
                        ConversionLogLevel.Warning => DialogKit.Brush("WarningBrush"),
                        ConversionLogLevel.Error => DialogKit.Brush("DangerBrush"),
                        _ => DialogKit.Brush("TextMutedBrush"),
                    },
                };
                Grid.SetColumn(badge, 1);
                grid.Children.Add(badge);
                TextBlock message = new() { Text = entry.Message, Classes = { "mono" }, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(message, 2);
                grid.Children.Add(message);
                return grid;
            }),
        };
        _logScroller.Content = logList;
        _logScroller.Padding = new Thickness(12, 8);

        // Result and actions sit above the log so they stay visible on small screens.
        Grid right = new() { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*") };
        right.Children.Add(DialogKit.Label("Progress"));
        StackPanel stage = new() { Spacing = 6, Margin = new Thickness(0, 0, 0, 6), Children = { _stage, _progress } };
        Grid.SetRow(stage, 1);
        right.Children.Add(stage);
        StackPanel done = new() { Spacing = 8, Margin = new Thickness(0, 0, 0, 6), Children = { _resultText, _completion } };
        Grid.SetRow(done, 2);
        right.Children.Add(done);
        TextBlock logsLabel = DialogKit.Label("Conversion log");
        Grid.SetRow(logsLabel, 3);
        right.Children.Add(logsLabel);
        Border logBorder = new()
        {
            Background = DialogKit.Brush("PanelBrush"),
            BorderBrush = DialogKit.Brush("HairlineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = _logScroller,
        };
        Grid.SetRow(logBorder, 4);
        right.Children.Add(logBorder);

        Grid root = new() { ColumnDefinitions = new ColumnDefinitions("420,24,*"), Margin = new Thickness(22) };
        root.Children.Add(new ScrollViewer { Content = form });
        Grid.SetColumn(right, 2);
        root.Children.Add(right);
        Content = root;

        Closing += (_, e) =>
        {
            if (_cancellation is not null)
            {
                // Keep the window while a conversion runs; cancel it instead.
                e.Cancel = true;
                _cancellation.Cancel();
            }
        };
    }

    /// <summary>Preselects a source folder, for example the open project.</summary>
    public void SetSource(string folder)
    {
        _source.Text = folder;
        Analyze();
    }

    private static Grid Row(Control main, Control side)
    {
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(main);
        Grid.SetColumn(side, 1);
        grid.Children.Add(side);
        return grid;
    }

    private void UpdateTargetHint() => _targetHint.Text =
        _web.IsChecked == true
            ? "Avalonia UI + browser (WebAssembly) head, plus a desktop preview. The browser build of the native core is on the roadmap (phase 5)."
            : _mobile.IsChecked == true
                ? "Avalonia UI + Android head, plus a desktop preview. The Android build of the native core is on the roadmap (phase 5)."
                : "Native window app for Windows, Linux and macOS. Runs immediately.";

    private ConversionTarget SelectedTarget =>
        _web.IsChecked == true ? ConversionTarget.Web : _mobile.IsChecked == true ? ConversionTarget.Mobile : ConversionTarget.Desktop;

    private async Task PickSourceAsync()
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the Three.js project folder",
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            SetSource(path);
        }
    }

    private async Task PickDestinationAsync()
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the destination parent folder",
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            _destinationEdited = true;
            _destination.Text = Path.Combine(path, string.IsNullOrWhiteSpace(_name.Text) ? "ConvertedThreeApp" : _name.Text.Trim());
        }
    }

    private void Analyze()
    {
        string? folder = _source.Text;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            _analysis.Text = "The folder does not exist.";
            return;
        }

        try
        {
            ThreeJsProject project = ThreeJsProjectAnalyzer.Analyze(folder);
            if (!_nameEdited)
            {
                _name.Text = project.SuggestedName;
            }

            long kilobytes = project.Assets.Sum(a => a.Size) / 1024;
            _analysis.Text =
                $"{project.Scripts.Count} script(s), {project.Pages.Count} page(s), {project.Assets.Count} asset(s) ({kilobytes:N0} KB)\n" +
                $"Entry: {project.EntryScript?.RelativePath ?? "not found"}   Three.js: {project.ThreeVersion ?? "unknown version"}\n" +
                (project.DetectedFeatures.Count > 0 ? $"Uses: {string.Join(", ", project.DetectedFeatures)}" : "No Three.js features detected") +
                (project.UsesThree ? string.Empty : "\nWarning: no reference to three.js was found.");
        }
        catch (Exception exception)
        {
            _analysis.Text = $"Could not analyse the folder: {exception.Message}";
        }
    }

    private async Task ConvertAsync()
    {
        string source = _source.Text?.Trim() ?? string.Empty;
        string name = ThreeJsProjectAnalyzer.ToPascalIdentifier(_name.Text?.Trim() ?? string.Empty);
        string destination = _destination.Text?.Trim() is { Length: > 0 } d ? d : ThreeJsConverter.DefaultDestination(name);

        if (!Directory.Exists(source))
        {
            AddLog(new ConversionLogEntry(DateTime.Now, ConversionLogLevel.Error, "Select an existing Three.js project folder first."));
            return;
        }

        _name.Text = name;
        _logs.Clear();
        _completion.IsVisible = false;
        _resultText.Text = string.Empty;
        _convert.IsEnabled = false;
        _cancel.Content = "Stop";
        _cancellation = new CancellationTokenSource();

        ConversionRequest request = new()
        {
            SourceFolder = source,
            ProjectName = name,
            DestinationFolder = destination,
            Target = SelectedTarget,
            UseLlm = _useLlm.IsChecked == true,
            MaxFixAttempts = (int)(_fixAttempts.Value ?? 4),
            OverwriteDestination = _overwrite.IsChecked == true,
        };

        ThreeJsConverter converter = new(_settings);
        converter.Logged += entry => Dispatcher.UIThread.Post(() => AddLog(entry));
        converter.ProgressChanged += progress => Dispatcher.UIThread.Post(() =>
        {
            _progress.Value = progress.Percent;
            _stage.Text = $"{progress.Stage} - {progress.Message}";
        });

        try
        {
            // The pipeline does file IO and waits on dotnet build: keep it off the UI thread.
            _result = await Task.Run(() => converter.ConvertAsync(request, _cancellation.Token));
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            _convert.IsEnabled = true;
            _cancel.Content = "Close";
        }

        if (_result.Succeeded)
        {
            _resultText.Foreground = DialogKit.Brush("SuccessBrush");
            _resultText.Text = _result.FellBackToBaseline
                ? $"Converted with the static baseline (the LLM output did not compile). Project: {_result.ProjectRoot}"
                : $"Converted and compiled{(_result.FixAttempts > 0 ? $" after {_result.FixAttempts} fix round(s)" : string.Empty)}. Project: {_result.ProjectRoot}";
        }
        else
        {
            _resultText.Foreground = DialogKit.Brush("DangerBrush");
            _resultText.Text = _result.Error ?? "The conversion failed.";
        }

        bool hasProject = Directory.Exists(_result.ProjectRoot) && File.Exists(_result.SolutionPath);
        _openInAppGen.IsEnabled = hasProject;
        _openInExplorer.IsEnabled = Directory.Exists(_result.ProjectRoot);
        _openReport.IsEnabled = File.Exists(_result.ReportPath);
        _completion.IsVisible = true;
    }

    private void AddLog(ConversionLogEntry entry)
    {
        _logs.Add(entry);
        if (_logs.Count > 3000)
        {
            _logs.RemoveAt(0);
        }

        _logScroller.ScrollToEnd();
    }

    private static void OpenInFileManager(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start("open", folder);
        }
        else
        {
            Process.Start("xdg-open", folder);
        }
    }

    private static void OpenWithShell(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }
}
