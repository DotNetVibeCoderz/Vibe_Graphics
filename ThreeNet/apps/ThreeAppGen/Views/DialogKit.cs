using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ThreeAppGen.Views;

/// <summary>
/// Small helpers shared by the dialogs, which are built in code: they are
/// simple forms and do not benefit from a separate XAML file.
/// </summary>
internal static class DialogKit
{
    public static IBrush Brush(string key) =>
        Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush brush
            ? brush
            : Brushes.Gray;

    public static Window Configure(Window window, string title, double width, double height)
    {
        window.Title = title;
        window.Width = width;
        window.Height = height;
        window.CanResize = false;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.Background = Brush("CanvasBrush");
        return window;
    }

    public static TextBlock Label(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        Classes = { "label" },
        Margin = new Thickness(0, 6, 0, 4),
    };

    public static TextBlock Heading(string title) => new()
    {
        Text = title,
        FontSize = 18,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 0, 0, 4),
    };

    public static TextBlock Body(string text) => new()
    {
        Text = text,
        Classes = { "body" },
        TextWrapping = TextWrapping.Wrap,
    };

    public static StackPanel Buttons(params Control[] buttons)
    {
        StackPanel panel = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0),
        };
        foreach (Control button in buttons)
        {
            panel.Children.Add(button);
        }

        return panel;
    }

    public static Button Button(string text, bool accent = false)
    {
        Button button = new() { Content = text, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (accent)
        {
            button.Classes.Add("accent");
        }

        return button;
    }
}

/// <summary>Single line text prompt, used by Go to line.</summary>
public sealed class PromptWindow : Window
{
    public PromptWindow()
        : this("Input", "Value", string.Empty)
    {
    }

    public PromptWindow(string title, string label, string initialValue)
    {
        DialogKit.Configure(this, title, 380, 190);
        TextBox input = new() { Text = initialValue };
        Button ok = DialogKit.Button("OK", accent: true);
        Button cancel = DialogKit.Button("Cancel");

        ok.Click += (_, _) => Close(input.Text);
        cancel.Click += (_, _) => Close(null);
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                Close(input.Text);
            }
            else if (e.Key == Avalonia.Input.Key.Escape)
            {
                Close(null);
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children = { DialogKit.Label(label), input, DialogKit.Buttons(cancel, ok) },
        };

        Opened += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
    }
}
