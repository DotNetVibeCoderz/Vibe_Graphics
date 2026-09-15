using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ThreeAppGen.Services;

namespace ThreeAppGen.Views;

/// <summary>
/// Edits everything stored in app.config: the four LLM providers, generation
/// parameters, the Tavily key and the editor preferences.
/// </summary>
public sealed class SettingsWindow : Window
{
    public SettingsWindow()
        : this(AppSettings.Load())
    {
    }

    public SettingsWindow(AppSettings settings)
    {
        DialogKit.Configure(this, "Settings", 640, 760);
        CanResize = true;

        ComboBox provider = new()
        {
            ItemsSource = Enum.GetValues<LlmProvider>(),
            SelectedItem = settings.ActiveProvider,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        // One editable copy per provider, so switching the combo does not lose edits.
        Dictionary<LlmProvider, (string Model, string Key, string Endpoint)> drafts = settings.Providers.ToDictionary(
            pair => pair.Key,
            pair => (pair.Value.Model, pair.Value.ApiKey, pair.Value.Endpoint));

        TextBox model = new();
        TextBox apiKey = new() { PasswordChar = '•', RevealPassword = false };
        TextBox endpoint = new() { PlaceholderText = "Default endpoint" };
        CheckBox reveal = new() { Content = "Show key" };
        reveal.IsCheckedChanged += (_, _) => apiKey.RevealPassword = reveal.IsChecked == true;

        LlmProvider current = settings.ActiveProvider;
        void LoadDraft(LlmProvider p)
        {
            (string m, string k, string e) = drafts[p];
            model.Text = m;
            apiKey.Text = k;
            endpoint.Text = e;
        }

        void StoreDraft() => drafts[current] = (model.Text ?? string.Empty, apiKey.Text ?? string.Empty, endpoint.Text ?? string.Empty);

        LoadDraft(current);
        provider.SelectionChanged += (_, _) =>
        {
            if (provider.SelectedItem is LlmProvider next)
            {
                StoreDraft();
                current = next;
                LoadDraft(next);
            }
        };

        NumericUpDown temperature = new()
        {
            Minimum = 0,
            Maximum = 2,
            Increment = 0.1m,
            FormatString = "0.0",
            Value = (decimal)settings.Temperature,
        };
        NumericUpDown maxTokens = new() { Minimum = 256, Maximum = 200_000, Increment = 256, Value = settings.MaxTokens };
        TextBox systemPrompt = new()
        {
            Text = settings.SystemPrompt,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Height = 110,
            PlaceholderText = "Extra instructions for Jack (optional)",
        };
        TextBox tavily = new() { Text = settings.TavilyApiKey, PasswordChar = '•' };

        NumericUpDown fontSize = new() { Minimum = 9, Maximum = 28, Value = settings.EditorFontSize };
        NumericUpDown tabSize = new() { Minimum = 2, Maximum = 8, Value = settings.TabSize };
        CheckBox lineNumbers = new() { Content = "Show line numbers", IsChecked = settings.ShowLineNumbers };
        CheckBox wordWrap = new() { Content = "Word wrap", IsChecked = settings.WordWrap };

        Button save = DialogKit.Button("Save", accent: true);
        Button cancel = DialogKit.Button("Cancel");
        cancel.Click += (_, _) => Close(false);
        save.Click += (_, _) =>
        {
            StoreDraft();
            foreach ((LlmProvider p, (string m, string k, string e)) in drafts)
            {
                settings.Providers[p].Model = m.Trim();
                settings.Providers[p].ApiKey = k.Trim();
                settings.Providers[p].Endpoint = e.Trim();
            }

            settings.ActiveProvider = current;
            settings.Temperature = (double)(temperature.Value ?? 0.2m);
            settings.MaxTokens = (int)(maxTokens.Value ?? 8192);
            settings.SystemPrompt = systemPrompt.Text ?? string.Empty;
            settings.TavilyApiKey = tavily.Text?.Trim() ?? string.Empty;
            settings.EditorFontSize = (int)(fontSize.Value ?? 13);
            settings.TabSize = (int)(tabSize.Value ?? 4);
            settings.ShowLineNumbers = lineNumbers.IsChecked == true;
            settings.WordWrap = wordWrap.IsChecked == true;
            Close(true);
        };

        Grid TwoColumns(Control left, Control right)
        {
            Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
            grid.Children.Add(left);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            return grid;
        }

        StackPanel Field(string label, Control control) => new() { Children = { DialogKit.Label(label), control } };

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Spacing = 2,
                Children =
                {
                    DialogKit.Heading("Settings"),
                    DialogKit.Body("Stored in app.config next to ThreeAppGen. API keys never leave this machine except to call the provider."),

                    DialogKit.Label("Language model"),
                    TwoColumns(Field("Provider", provider), Field("Model", model)),
                    Field("API key", apiKey),
                    reveal,
                    Field("Endpoint", endpoint),
                    TwoColumns(Field("Temperature", temperature), Field("Max tokens", maxTokens)),
                    Field("System prompt", systemPrompt),

                    DialogKit.Label("Tools"),
                    Field("Tavily API key (internet search)", tavily),

                    DialogKit.Label("Editor"),
                    TwoColumns(Field("Font size", fontSize), Field("Tab size", tabSize)),
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Children = { lineNumbers, wordWrap } },

                    DialogKit.Buttons(cancel, save),
                },
            },
        };

    }
}
