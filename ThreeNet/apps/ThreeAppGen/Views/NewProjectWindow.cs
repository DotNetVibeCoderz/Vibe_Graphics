using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ThreeAppGen.Services;

namespace ThreeAppGen.Views;

/// <summary>What the New project dialog returns.</summary>
public sealed record NewProjectResult(string Name, string Location, ProjectTemplate Template);

/// <summary>New project: Blank or From template, with name and location.</summary>
public sealed class NewProjectWindow : Window
{
    public NewProjectWindow()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
    {
    }

    public NewProjectWindow(string defaultLocation)
    {
        DialogKit.Configure(this, "New project", 720, 600);

        TextBox name = new() { Text = "MyThreeNetApp" };
        TextBox location = new() { Text = defaultLocation };
        Button browse = DialogKit.Button("Browse...");

        RadioButton blank = new() { Content = "Blank project", GroupName = "kind", IsChecked = true };
        RadioButton fromTemplate = new() { Content = "From template", GroupName = "kind" };

        ListBox templates = new()
        {
            ItemsSource = ProjectTemplates.All.Where(t => t.Id != "blank").ToList(),
            SelectedIndex = 0,
            IsEnabled = false,
            Height = 290,
            ItemTemplate = new FuncDataTemplate<ProjectTemplate>((template, _) => new StackPanel
            {
                Spacing = 2,
                Margin = new Thickness(2, 3),
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock { Text = template.Title, Classes = { "title" } },
                            new Border
                            {
                                Background = DialogKit.Brush("AccentSoftBrush"),
                                CornerRadius = new CornerRadius(4),
                                Padding = new Thickness(6, 1),
                                Child = new TextBlock
                                {
                                    Text = template.Category,
                                    FontSize = 10.5,
                                    Foreground = DialogKit.Brush("AccentBrush"),
                                },
                            },
                        },
                    },
                    new TextBlock { Text = template.Description, Classes = { "body" }, TextWrapping = TextWrapping.Wrap },
                },
            }),
        };

        fromTemplate.IsCheckedChanged += (_, _) => templates.IsEnabled = fromTemplate.IsChecked == true;

        browse.Click += async (_, _) =>
        {
            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "Project location" });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            {
                location.Text = path;
            }
        };

        TextBlock error = new() { Foreground = DialogKit.Brush("DangerBrush"), FontSize = 12 };
        Button create = DialogKit.Button("Create", accent: true);
        Button cancel = DialogKit.Button("Cancel");
        cancel.Click += (_, _) => Close(null);
        create.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text))
            {
                error.Text = "Enter a project name.";
                return;
            }

            ProjectTemplate template = fromTemplate.IsChecked == true && templates.SelectedItem is ProjectTemplate selected
                ? selected
                : ProjectTemplates.Find("blank")!;
            Close(new NewProjectResult(name.Text.Trim(), location.Text ?? defaultLocation, template));
        };

        Grid locationRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        locationRow.Children.Add(location);
        Grid.SetColumn(browse, 1);
        locationRow.Children.Add(browse);

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Children =
                {
                    DialogKit.Heading("Create a Three.Net project"),
                    DialogKit.Body("Start empty, or pick a template for 3D graphics, animation, games or simulations."),
                    DialogKit.Label("Name"),
                    name,
                    DialogKit.Label("Location"),
                    locationRow,
                    DialogKit.Label("Start from"),
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Children = { blank, fromTemplate } },
                    new Border { Height = 8 },
                    templates,
                    error,
                    DialogKit.Buttons(cancel, create),
                },
            },
        };
    }
}
