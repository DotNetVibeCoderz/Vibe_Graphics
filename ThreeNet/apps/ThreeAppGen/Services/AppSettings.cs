using System.Configuration;
using System.Globalization;

namespace ThreeAppGen.Services;

/// <summary>LLM providers ThreeAppGen can talk to.</summary>
public enum LlmProvider
{
    OpenAI,
    Claude,
    Gemini,
    Ollama,
}

/// <summary>Per provider credentials and model.</summary>
public sealed class ProviderSettings
{
    public required LlmProvider Provider { get; init; }

    public string Model { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Optional base URL; empty means the provider default.</summary>
    public string Endpoint { get; set; } = string.Empty;

    public bool IsConfigured => Provider == LlmProvider.Ollama
        ? !string.IsNullOrWhiteSpace(Model)
        : !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Model);
}

/// <summary>
/// Reads and writes <c>app.config</c>. Everything the app can be configured
/// with lives here so the Settings dialog and the services share one source of
/// truth.
/// </summary>
public sealed class AppSettings
{
    private readonly Configuration _configuration;

    private AppSettings(Configuration configuration)
    {
        _configuration = configuration;
        Providers = Enum.GetValues<LlmProvider>().ToDictionary(
            provider => provider,
            provider => new ProviderSettings
            {
                Provider = provider,
                Model = Read($"{provider}.Model"),
                ApiKey = Read($"{provider}.ApiKey"),
                Endpoint = Read($"{provider}.Endpoint"),
            });

        ActiveProvider = Enum.TryParse(Read("Llm.Provider"), ignoreCase: true, out LlmProvider active)
            ? active
            : LlmProvider.OpenAI;
        Temperature = ReadDouble("Llm.Temperature", 0.2);
        MaxTokens = ReadInt("Llm.MaxTokens", 8192);
        SystemPrompt = Read("Llm.SystemPrompt");
        TavilyApiKey = Read("Tavily.ApiKey");

        ShowLineNumbers = ReadBool("Editor.ShowLineNumbers", true);
        WordWrap = ReadBool("Editor.WordWrap", false);
        EditorFontSize = ReadInt("Editor.FontSize", 13);
        TabSize = ReadInt("Editor.TabSize", 4);
        LastProject = Read("Workspace.LastProject");
        ChatPanelWidth = ReadDouble("Workspace.ChatPanelWidth", 420);
        ChatPanelVisible = ReadBool("Workspace.ChatPanelVisible", true);
    }

    /// <summary>Loads the configuration of the running executable.</summary>
    public static AppSettings Load() =>
        new(ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None));

    /// <summary>Loads (and later saves) a specific config file, used by tests and tooling.</summary>
    public static AppSettings LoadFrom(string configPath) =>
        new(ConfigurationManager.OpenMappedExeConfiguration(
            new ExeConfigurationFileMap { ExeConfigFilename = configPath },
            ConfigurationUserLevel.None));

    public IReadOnlyDictionary<LlmProvider, ProviderSettings> Providers { get; }

    public LlmProvider ActiveProvider { get; set; }

    public ProviderSettings Active => Providers[ActiveProvider];

    public double Temperature { get; set; }

    public int MaxTokens { get; set; }

    /// <summary>Extra instructions appended to the built-in assistant prompt.</summary>
    public string SystemPrompt { get; set; }

    public string TavilyApiKey { get; set; }

    public bool ShowLineNumbers { get; set; }

    public bool WordWrap { get; set; }

    public int EditorFontSize { get; set; }

    public int TabSize { get; set; }

    public string LastProject { get; set; }

    public double ChatPanelWidth { get; set; }

    public bool ChatPanelVisible { get; set; }

    /// <summary>Writes every value back to app.config.</summary>
    public void Save()
    {
        Write("Llm.Provider", ActiveProvider.ToString());
        Write("Llm.Temperature", Temperature.ToString(CultureInfo.InvariantCulture));
        Write("Llm.MaxTokens", MaxTokens.ToString(CultureInfo.InvariantCulture));
        Write("Llm.SystemPrompt", SystemPrompt);
        Write("Tavily.ApiKey", TavilyApiKey);

        foreach (ProviderSettings provider in Providers.Values)
        {
            Write($"{provider.Provider}.Model", provider.Model);
            Write($"{provider.Provider}.ApiKey", provider.ApiKey);
            Write($"{provider.Provider}.Endpoint", provider.Endpoint);
        }

        Write("Editor.ShowLineNumbers", ShowLineNumbers.ToString());
        Write("Editor.WordWrap", WordWrap.ToString());
        Write("Editor.FontSize", EditorFontSize.ToString(CultureInfo.InvariantCulture));
        Write("Editor.TabSize", TabSize.ToString(CultureInfo.InvariantCulture));
        Write("Workspace.LastProject", LastProject);
        Write("Workspace.ChatPanelWidth", ChatPanelWidth.ToString("F0", CultureInfo.InvariantCulture));
        Write("Workspace.ChatPanelVisible", ChatPanelVisible.ToString());

        _configuration.Save(ConfigurationSaveMode.Modified);
        ConfigurationManager.RefreshSection("appSettings");
    }

    private string Read(string key) => _configuration.AppSettings.Settings[key]?.Value ?? string.Empty;

    private bool ReadBool(string key, bool fallback) =>
        bool.TryParse(Read(key), out bool value) ? value : fallback;

    private int ReadInt(string key, int fallback) =>
        int.TryParse(Read(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    private double ReadDouble(string key, double fallback) =>
        double.TryParse(Read(key), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;

    private void Write(string key, string value)
    {
        if (_configuration.AppSettings.Settings[key] is null)
        {
            _configuration.AppSettings.Settings.Add(key, value);
        }
        else
        {
            _configuration.AppSettings.Settings[key].Value = value;
        }
    }
}
