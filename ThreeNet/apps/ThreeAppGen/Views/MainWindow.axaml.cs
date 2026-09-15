using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit.Highlighting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using ThreeAppGen.Models;
using ThreeAppGen.Services;

namespace ThreeAppGen.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly LogService _log = new();
    private readonly ProjectService _projects;
    private readonly ChatService _chat;
    private readonly ObservableCollection<FileNode> _tree = [];
    private readonly ObservableCollection<ChatAttachment> _attachments = [];

    private string? _openFilePath;
    private bool _isDirty;
    private CancellationTokenSource? _chatCancellation;

    public MainWindow()
    {
        InitializeComponent();

        _projects = new ProjectService(_log);
        _chat = new ChatService(_settings, _projects, _log);

        _log.Logged += entry => Dispatcher.UIThread.Post(() =>
        {
            _log.Append(entry);
            LogScroller.ScrollToEnd();
        });

        LogList.ItemsSource = _log.Entries;
        FileTree.ItemsSource = _tree;
        ChatList.ItemsSource = _chat.Messages;
        AttachmentList.ItemsSource = _attachments;

        Editor.SyntaxHighlighting = DarkHighlighting.Get("C#");
        Editor.ShowLineNumbers = _settings.ShowLineNumbers;
        Editor.WordWrap = _settings.WordWrap;
        Editor.FontSize = _settings.EditorFontSize;
        Editor.Options.IndentationSize = _settings.TabSize;
        Editor.TextChanged += (_, _) => MarkDirty(true);
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
            CaretText.Text = $"Ln {Editor.TextArea.Caret.Line}, Col {Editor.TextArea.Caret.Column}";

        LineNumbersMenuItem.Header = _settings.ShowLineNumbers ? "Hide line numbers" : "Show line numbers";
        WordWrapMenuItem.Header = _settings.WordWrap ? "Disable word wrap" : "Enable word wrap";

        ProviderCombo.ItemsSource = Enum.GetValues<LlmProvider>();
        ProviderCombo.SelectedItem = _settings.ActiveProvider;
        RefreshModelList();

        ChatPanel.Width = _settings.ChatPanelWidth;
        ChatPanel.IsVisible = _settings.ChatPanelVisible;
        ChatToggle.IsChecked = _settings.ChatPanelVisible;

        _projects.FilesChanged += () => Dispatcher.UIThread.Post(RefreshExplorer);

        _log.Info("Three.Net App Generator ready. Jack is standing by.");
        UpdateStatus();

        if (!string.IsNullOrWhiteSpace(_settings.LastProject) && Directory.Exists(_settings.LastProject))
        {
            _projects.Open(_settings.LastProject);
            RefreshExplorer();
            Opened += async (_, _) =>
            {
                if (_openFilePath is null && FindMainFile(_settings.LastProject) is { } mainFile)
                {
                    await OpenFileAsync(mainFile);
                }
            };
        }
    }

    /// <summary>The file worth showing first: a converted scene, then Program.cs.</summary>
    private static string? FindMainFile(string root)
    {
        foreach (string name in new[] { "ConvertedScene.cs", "Program.cs" })
        {
            string? match = Directory
                .EnumerateFiles(root, name, SearchOption.AllDirectories)
                .FirstOrDefault(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                        !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    // ------------------------------------------------------------- explorer

    private void RefreshExplorer()
    {
        _tree.Clear();
        if (_projects.Current is { } project && Directory.Exists(project.RootPath))
        {
            _tree.Add(FileNode.Load(project.RootPath));
            ProjectNameText.Text = project.Name;
        }
        else
        {
            ProjectNameText.Text = "no project";
        }

        UpdateStatus();
    }

    private void OnRefreshExplorer(object? sender, RoutedEventArgs e) => RefreshExplorer();

    private async void OnFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (FileTree.SelectedItem is not FileNode node || node.IsDirectory)
        {
            return;
        }

        if (!node.IsTextFile)
        {
            _log.Warning($"{node.Name} is not a text file, so it cannot be opened in the editor");
            return;
        }

        await OpenFileAsync(node.Path);
    }

    private async Task OpenFileAsync(string path)
    {
        if (_isDirty && _openFilePath is not null)
        {
            await SaveFileAsync();
        }

        try
        {
            Editor.Text = await File.ReadAllTextAsync(path);
            Editor.SyntaxHighlighting = HighlightingForExtension(Path.GetExtension(path));
            _openFilePath = path;
            OpenFileText.Text = _projects.Current is { } project
                ? Path.GetRelativePath(project.RootPath, path)
                : path;
            MarkDirty(false);
            _log.Info($"Opened {OpenFileText.Text}");
        }
        catch (Exception exception)
        {
            _log.Error($"Could not open {path}: {exception.Message}");
        }
    }

    private static IHighlightingDefinition? HighlightingForExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".cs" => DarkHighlighting.Get("C#"),
        ".xml" or ".csproj" or ".axaml" or ".xaml" or ".config" or ".props" or ".targets" =>
            DarkHighlighting.Get("XML"),
        ".json" => DarkHighlighting.Get("Json"),
        ".md" => DarkHighlighting.Get("MarkDown"),
        _ => null,
    };

    private void MarkDirty(bool dirty)
    {
        _isDirty = dirty && _openFilePath is not null;
        DirtyText.Text = _isDirty ? "modified" : string.Empty;
    }

    // ------------------------------------------------------------ file menu

    private async void OnNewProject(object? sender, RoutedEventArgs e)
    {
        NewProjectWindow dialog = new(_projects.DefaultProjectsFolder);
        NewProjectResult? result = await dialog.ShowDialog<NewProjectResult?>(this);
        if (result is null)
        {
            return;
        }

        try
        {
            ProjectContext project = await _projects.CreateProjectAsync(result.Name, result.Template, result.Location);
            RefreshExplorer();
            _settings.LastProject = project.RootPath;
            _settings.Save();
            await OpenFileAsync(Path.Combine(project.RootPath, "Program.cs"));
        }
        catch (Exception exception)
        {
            _log.Error($"Could not create the project: {exception.Message}");
        }
    }

    private async void OnOpenProject(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open a project folder",
            AllowMultiple = false,
        });

        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        _projects.Open(path);
        RefreshExplorer();
        _settings.LastProject = path;
        _settings.Save();

        string program = Path.Combine(path, "Program.cs");
        if (File.Exists(program))
        {
            await OpenFileAsync(program);
        }
    }

    private async void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a file",
            AllowMultiple = false,
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            await OpenFileAsync(path);
        }
    }

    private async void OnSaveFile(object? sender, RoutedEventArgs e) => await SaveFileAsync();

    private async Task SaveFileAsync()
    {
        if (_openFilePath is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(_openFilePath, Editor.Text);
            MarkDirty(false);
            _log.Success($"Saved {Path.GetFileName(_openFilePath)}");
        }
        catch (Exception exception)
        {
            _log.Error($"Could not save {_openFilePath}: {exception.Message}");
        }
    }

    private void OnCloseProject(object? sender, RoutedEventArgs e)
    {
        _projects.Close();
        _openFilePath = null;
        Editor.Text = string.Empty;
        OpenFileText.Text = "No file open";
        MarkDirty(false);
        RefreshExplorer();
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------ edit menu

    private async void OnGoToLine(object? sender, RoutedEventArgs e)
    {
        PromptWindow dialog = new("Go to line", $"Line number (1 - {Editor.Document.LineCount})", "1");
        string? answer = await dialog.ShowDialog<string?>(this);
        if (!int.TryParse(answer, out int line))
        {
            return;
        }

        line = Math.Clamp(line, 1, Editor.Document.LineCount);
        Editor.TextArea.Caret.Line = line;
        Editor.TextArea.Caret.Column = 1;
        Editor.ScrollToLine(line);
        Editor.Focus();
    }

    private void OnFormatCode(object? sender, RoutedEventArgs e)
    {
        if (_openFilePath is null || !_openFilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            _log.Warning("Format code works on C# files");
            return;
        }

        try
        {
            // Roslyn does the real work: parse, then format the syntax tree.
            int caretOffset = Editor.CaretOffset;
            SyntaxNode root = CSharpSyntaxTree.ParseText(Editor.Text).GetRoot();
            using AdhocWorkspace workspace = new();
            string formatted = Formatter.Format(root, workspace).ToFullString();
            if (formatted != Editor.Text)
            {
                Editor.Text = formatted;
                Editor.CaretOffset = Math.Min(caretOffset, formatted.Length);
                _log.Success("Formatted the document");
            }
            else
            {
                _log.Info("The document was already formatted");
            }
        }
        catch (Exception exception)
        {
            _log.Error($"Could not format: {exception.Message}");
        }
    }

    // --------------------------------------------------------- project menu

    private async void OnBuild(object? sender, RoutedEventArgs e)
    {
        if (!EnsureProject())
        {
            return;
        }

        await SaveFileAsync();
        StatusText.Text = "Building...";
        (int exitCode, _) = await _projects.RunDotnetAsync("build");
        StatusText.Text = exitCode == 0 ? "Build succeeded" : "Build failed";
    }

    private async void OnRun(object? sender, RoutedEventArgs e)
    {
        if (!EnsureProject())
        {
            return;
        }

        await SaveFileAsync();
        StatusText.Text = "Building before run...";
        (int exitCode, _) = await _projects.RunDotnetAsync("build");
        if (exitCode != 0)
        {
            StatusText.Text = "Build failed, not running";
            return;
        }

        Process? process = _projects.StartProject();
        StatusText.Text = process is null ? "Could not start the app" : "App started";
    }

    private async void OnDeploy(object? sender, RoutedEventArgs e)
    {
        if (!EnsureProject())
        {
            return;
        }

        await SaveFileAsync();
        StatusText.Text = "Publishing...";
        (int exitCode, _) = await _projects.DeployAsync();
        StatusText.Text = exitCode == 0 ? "Published to publish/" : "Publish failed";
    }

    private bool EnsureProject()
    {
        if (_projects.Current is not null)
        {
            return true;
        }

        _log.Warning("Open or create a project first");
        return false;
    }

    // ------------------------------------------------------------ view menu

    private void OnToggleLineNumbers(object? sender, RoutedEventArgs e)
    {
        Editor.ShowLineNumbers = !Editor.ShowLineNumbers;
        _settings.ShowLineNumbers = Editor.ShowLineNumbers;
        LineNumbersMenuItem.Header = Editor.ShowLineNumbers ? "Hide line numbers" : "Show line numbers";
        _settings.Save();
    }

    private void OnToggleWordWrap(object? sender, RoutedEventArgs e)
    {
        Editor.WordWrap = !Editor.WordWrap;
        _settings.WordWrap = Editor.WordWrap;
        WordWrapMenuItem.Header = Editor.WordWrap ? "Disable word wrap" : "Enable word wrap";
        _settings.Save();
    }

    private void OnToggleChat(object? sender, RoutedEventArgs e)
    {
        ChatPanel.IsVisible = !ChatPanel.IsVisible;
        ChatToggle.IsChecked = ChatPanel.IsVisible;
        _settings.ChatPanelVisible = ChatPanel.IsVisible;
        _settings.Save();
    }

    private void OnToggleLogs(object? sender, RoutedEventArgs e)
    {
        LogsPanel.IsVisible = !LogsPanel.IsVisible;
        LogsToggle.IsChecked = LogsPanel.IsVisible;
    }

    private void OnClearLogs(object? sender, RoutedEventArgs e) => _log.Clear();

    // ----------------------------------------------------------- tools menu

    private async void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        SettingsWindow dialog = new(_settings);
        bool saved = await dialog.ShowDialog<bool>(this);
        if (!saved)
        {
            return;
        }

        _settings.Save();
        _chat.InvalidateKernel();
        ProviderCombo.SelectedItem = _settings.ActiveProvider;
        RefreshModelList();
        Editor.FontSize = _settings.EditorFontSize;
        Editor.Options.IndentationSize = _settings.TabSize;
        UpdateStatus();
        _log.Success("Settings saved");
    }

    private void OnConvertThreeJs(object? sender, RoutedEventArgs e) => OpenConverter(null);

    /// <summary>Opens the Three.js conversion page, optionally with a source folder preselected.</summary>
    public async void OpenConverter(string? sourceFolder)
    {
        ConverterWindow converter = new(_settings);
        if (!string.IsNullOrWhiteSpace(sourceFolder) && Directory.Exists(sourceFolder))
        {
            converter.SetSource(sourceFolder);
        }

        ThreeAppGen.Services.Conversion.ConversionResult? result =
            await converter.ShowDialog<ThreeAppGen.Services.Conversion.ConversionResult?>(this);

        // The page returns a result only when the user chose "Open in ThreeAppGen".
        if (result is null || !Directory.Exists(result.ProjectRoot))
        {
            return;
        }

        _projects.Open(result.ProjectRoot);
        RefreshExplorer();
        _settings.LastProject = result.ProjectRoot;
        _settings.Save();

        string? scene = Directory.EnumerateFiles(result.ProjectRoot, "ConvertedScene.cs", SearchOption.AllDirectories).FirstOrDefault();
        if (scene is not null)
        {
            await OpenFileAsync(scene);
        }

        _log.Success($"Opened the converted project {Path.GetFileName(result.ProjectRoot)}");
    }

    private void OnAbout(object? sender, RoutedEventArgs e) =>
        _log.Info("Three.Net App Generator - Jack the Code Bender. Built by Gravicode Studios, led by Kang Fadhil.");

    // ----------------------------------------------------------------- chat

    private void RefreshModelList()
    {
        IReadOnlyList<string> models = KernelFactory.SuggestedModels(_settings.ActiveProvider);
        ModelCombo.ItemsSource = models;

        string current = _settings.Active.Model;
        if (!string.IsNullOrWhiteSpace(current) && !models.Contains(current))
        {
            // Keep a custom model the user typed in Settings.
            ModelCombo.ItemsSource = models.Append(current).ToList();
        }

        ModelCombo.SelectedItem = string.IsNullOrWhiteSpace(current) ? models.FirstOrDefault() : current;
        UpdateStatus();
    }

    private void OnProviderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ProviderCombo.SelectedItem is not LlmProvider provider || provider == _settings.ActiveProvider)
        {
            return;
        }

        _settings.ActiveProvider = provider;
        _settings.Save();
        _chat.InvalidateKernel();
        RefreshModelList();
    }

    private void OnModelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ModelCombo.SelectedItem is not string model || model == _settings.Active.Model)
        {
            return;
        }

        _settings.Active.Model = model;
        _settings.Save();
        _chat.InvalidateKernel();
        UpdateStatus();
    }

    private void OnChatInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            SendChat();
        }
    }

    private void OnSendChat(object? sender, RoutedEventArgs e) => SendChat();

    private async void SendChat()
    {
        if (_chatCancellation is not null)
        {
            // A reply is streaming: the button doubles as stop.
            await _chatCancellation.CancelAsync();
            return;
        }

        string prompt = ChatInput.Text?.Trim() ?? string.Empty;
        if (prompt.Length == 0 && _attachments.Count == 0)
        {
            return;
        }

        ChatMessage question = new()
        {
            Role = ChatRole.User,
            Text = prompt,
            Attachments = [.. _attachments],
        };
        _chat.Messages.Add(question);

        ChatAttachment[] attachments = [.. _attachments];
        _attachments.Clear();
        ChatInput.Text = string.Empty;

        ChatMessage answer = new() { Role = ChatRole.Assistant, Text = string.Empty };
        _chat.Messages.Add(answer);
        ChatScroller.ScrollToEnd();

        _chatCancellation = new CancellationTokenSource();
        SendButton.Content = "Stop";
        StatusText.Text = $"Jack is thinking ({_settings.ActiveProvider}/{_settings.Active.Model})...";

        try
        {
            StringBuilder buffer = new();
            await foreach (string chunk in _chat.SendAsync(prompt, attachments, _chatCancellation.Token))
            {
                buffer.Append(chunk);
                answer.Text = buffer.ToString();
                ChatScroller.ScrollToEnd();
            }

            if (buffer.Length == 0)
            {
                answer.Text = "(no reply)";
            }

            StatusText.Text = "Ready";
        }
        catch (OperationCanceledException)
        {
            answer.Text += "\n\n(stopped)";
            StatusText.Text = "Stopped";
        }
        catch (Exception exception)
        {
            answer.Text = $"Error: {exception.Message}";
            _log.Error($"Chat failed: {exception.Message}");
            StatusText.Text = "Chat failed";
        }
        finally
        {
            _chatCancellation?.Dispose();
            _chatCancellation = null;
            SendButton.Content = "Send";
        }
    }

    private async void OnAttachImage(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach images",
            AllowMultiple = true,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });

        foreach (IStorageFile file in files)
        {
            if (file.TryGetLocalPath() is not { } path)
            {
                continue;
            }

            byte[] data = await File.ReadAllBytesAsync(path);
            _attachments.Add(new ChatAttachment(Path.GetFileName(path), MimeTypeFor(path), data));
        }
    }

    private static string MimeTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "image/jpeg",
    };

    private void OnClearChat(object? sender, RoutedEventArgs e)
    {
        _chat.ClearThread();
        _attachments.Clear();
    }

    private void UpdateStatus()
    {
        ModelStatusText.Text = $"{_settings.ActiveProvider} / {_settings.Active.Model}";
        StatusText.Text = _projects.Current is { } project
            ? $"Project: {project.Name}"
            : "No project open";
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _settings.ChatPanelWidth = ChatPanel.Width;
        _settings.ChatPanelVisible = ChatPanel.IsVisible;
        _settings.Save();
        base.OnClosing(e);
    }
}
