using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using DemoGraphics.Framework;

namespace DemoGraphics.Views;

/// <summary>
/// Builds the control column as a specification sheet: a label, a dotted leader
/// and the value, then the control that changes it. Leaders are borrowed from
/// printed spec sheets on purpose - they carry the eye across a wide panel to the
/// right number, which a wall of left aligned labels does not.
/// </summary>
public static class Sheet
{
    private const string Leader = "···························································································································";

    public static IBrush Brush(string key) =>
        Application.Current?.FindResource(key) as IBrush ?? Brushes.Gray;

    /// <summary>A section heading: engraved label over a hairline rule.</summary>
    public static Control Section(string title, string? trailing = null)
    {
        Grid header = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 18, 0, 6),
        };

        TextBlock label = new() { Text = title.ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center };
        label.Classes.Add("eyebrow");
        header.Children.Add(label);

        Border rule = new()
        {
            Height = 1,
            Background = Brush("Rule"),
            Margin = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(rule, 1);
        header.Children.Add(rule);

        if (trailing is not null)
        {
            TextBlock value = new() { Text = trailing, VerticalAlignment = VerticalAlignment.Center };
            value.Classes.Add("readout");
            value.Classes.Add("muted");
            Grid.SetColumn(value, 2);
            header.Children.Add(value);
        }

        return header;
    }

    /// <summary>A read-only fact: label, leader, value.</summary>
    public static Control Fact(string label, string value, IBrush? valueBrush = null) =>
        LeaderRow(label, value, valueBrush, out _);

    /// <summary>A fact whose value the caller keeps updating.</summary>
    public static Control LiveFact(string label, string value, out TextBlock valueBlock) =>
        LeaderRow(label, value, null, out valueBlock);

    private static Control LeaderRow(string label, string value, IBrush? valueBrush, out TextBlock valueBlock)
    {
        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 2, 0, 2),
        };

        TextBlock caption = new() { Text = label, VerticalAlignment = VerticalAlignment.Bottom };
        caption.Classes.Add("body");
        caption.Foreground = Brush("Muted");
        row.Children.Add(caption);

        TextBlock leader = new()
        {
            Text = Leader,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(5, 0, 5, 1),
            ClipToBounds = true,
            Foreground = Brush("Rule"),
            FontSize = 11,
        };
        Grid.SetColumn(leader, 1);
        row.Children.Add(leader);

        valueBlock = new TextBlock { Text = value, VerticalAlignment = VerticalAlignment.Bottom };
        valueBlock.Classes.Add("readout");
        if (valueBrush is not null)
        {
            valueBlock.Foreground = valueBrush;
        }

        Grid.SetColumn(valueBlock, 2);
        row.Children.Add(valueBlock);
        return row;
    }

    /// <summary>
    /// One scene parameter: the leader row plus the control under it. The value
    /// text updates as the control moves, so the sheet always reads true.
    /// </summary>
    public static Control Parameter(DemoParameter parameter, Action<DemoParameter> changed)
    {
        StackPanel block = new() { Margin = new Thickness(0, 4, 0, 8) };
        Control row = LeaderRow(parameter.Label, parameter.Display, null, out TextBlock valueBlock);
        block.Children.Add(row);

        switch (parameter.Kind)
        {
            case ParameterKind.Toggle:
            {
                ToggleSwitch toggle = new()
                {
                    IsChecked = parameter.BoolValue,
                    OnContent = null,
                    OffContent = null,
                    Margin = new Thickness(0, 2, 0, 0),
                    MinHeight = 22,
                };
                toggle.IsCheckedChanged += (_, _) =>
                {
                    parameter.Value = toggle.IsChecked == true ? 1f : 0f;
                    valueBlock.Text = parameter.Display;
                    changed(parameter);
                };
                block.Children.Add(toggle);
                break;
            }

            case ParameterKind.Choice:
            {
                ComboBox combo = new()
                {
                    ItemsSource = parameter.Options,
                    SelectedIndex = Math.Clamp(parameter.IntValue, 0, Math.Max(0, parameter.Options.Count - 1)),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 2, 0, 0),
                };
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedIndex < 0)
                    {
                        return;
                    }

                    parameter.Value = combo.SelectedIndex;
                    valueBlock.Text = parameter.Display;
                    changed(parameter);
                };
                block.Children.Add(combo);
                break;
            }

            default:
            {
                Slider slider = new()
                {
                    Minimum = parameter.Min,
                    Maximum = parameter.Max,
                    Value = parameter.Value,
                    TickFrequency = parameter.Step > 0f ? parameter.Step : 0d,
                    IsSnapToTickEnabled = parameter.Step > 0f,
                };
                slider.PropertyChanged += (_, e) =>
                {
                    if (e.Property != RangeBase.ValueProperty)
                    {
                        return;
                    }

                    parameter.Value = (float)slider.Value;
                    valueBlock.Text = parameter.Display;
                    changed(parameter);
                };
                block.Children.Add(slider);
                break;
            }
        }

        if (parameter.Note is { } note)
        {
            block.Children.Add(Note(note));
        }

        return block;
    }

    /// <summary>
    /// A fact whose value is too long for a leader row: the label sits above it
    /// and the value wraps underneath.
    /// </summary>
    public static Control Wide(string label, string value)
    {
        StackPanel block = new() { Spacing = 1, Margin = new Thickness(0, 4, 0, 4) };
        TextBlock caption = new() { Text = label.ToUpperInvariant() };
        caption.Classes.Add("eyebrow");
        block.Children.Add(caption);

        TextBlock body = new() { Text = value, TextWrapping = TextWrapping.Wrap };
        body.Classes.Add("readout");
        block.Children.Add(body);
        return block;
    }

    /// <summary>A caveat under a control, in the interface voice: short and plain.</summary>
    public static Control Note(string text)
    {
        TextBlock note = new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = Brush("Dim"),
            Margin = new Thickness(0, 3, 0, 0),
        };
        return note;
    }

    /// <summary>A wrapped row of small buttons.</summary>
    public static WrapPanel Buttons(params (string Label, Action Click)[] buttons)
    {
        WrapPanel panel = new() { Margin = new Thickness(0, 2, 0, 0) };
        foreach ((string label, Action click) in buttons)
        {
            Button button = new() { Content = label, Margin = new Thickness(0, 0, 5, 5) };
            button.Click += (_, _) => click();
            panel.Children.Add(button);
        }

        return panel;
    }

    /// <summary>A labelled paragraph, for scene descriptions and empty states.</summary>
    public static Control Paragraph(string text)
    {
        TextBlock block = new() { Text = text, TextWrapping = TextWrapping.Wrap };
        block.Classes.Add("body");
        return block;
    }
}
