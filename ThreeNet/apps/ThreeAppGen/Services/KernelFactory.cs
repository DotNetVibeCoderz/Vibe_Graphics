using Anthropic.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ThreeAppGen.Services;

/// <summary>
/// Builds the Semantic Kernel for the provider selected in the settings and
/// registers the plugins Jack can call.
/// </summary>
public static class KernelFactory
{
    /// <summary>
    /// Creates a kernel. <paramref name="plugins"/> are registered as native
    /// plugins, so every public method marked with <c>[KernelFunction]</c>
    /// becomes callable by the model.
    /// </summary>
    public static Kernel Create(AppSettings settings, IEnumerable<object> plugins)
    {
        IKernelBuilder builder = Kernel.CreateBuilder();
        ProviderSettings provider = settings.Active;

        if (!provider.IsConfigured)
        {
            throw new InvalidOperationException(
                $"the {provider.Provider} provider is not configured yet - set the model and API key in Settings");
        }

        switch (settings.ActiveProvider)
        {
            case LlmProvider.OpenAI:
                if (string.IsNullOrWhiteSpace(provider.Endpoint))
                {
                    builder.AddOpenAIChatCompletion(provider.Model, provider.ApiKey);
                }
                else if (IsAzureEndpoint(provider.Endpoint))
                {
                    // Azure OpenAI: the model field holds the deployment name.
                    builder.AddAzureOpenAIChatCompletion(provider.Model, provider.Endpoint, provider.ApiKey);
                }
                else
                {
                    // Any OpenAI compatible server (DeepSeek, LM Studio, vLLM, gateways).
                    builder.AddOpenAIChatCompletion(provider.Model, new Uri(provider.Endpoint), provider.ApiKey);
                }

                break;

            case LlmProvider.Gemini:
                builder.AddGoogleAIGeminiChatCompletion(provider.Model, provider.ApiKey);
                break;

            case LlmProvider.Ollama:
                Uri ollama = new(string.IsNullOrWhiteSpace(provider.Endpoint)
                    ? "http://localhost:11434"
                    : provider.Endpoint);
                builder.AddOllamaChatCompletion(provider.Model, ollama);
                break;

            case LlmProvider.Claude:
                // Semantic Kernel has no first party Anthropic connector, so the
                // Anthropic SDK chat client is adapted to the kernel interface.
                builder.Services.AddSingleton<IChatCompletionService>(_ => CreateClaudeService(provider));
                break;

            default:
                throw new NotSupportedException($"unsupported provider {settings.ActiveProvider}");
        }

        foreach (object plugin in plugins)
        {
            builder.Plugins.AddFromObject(plugin);
        }

        return builder.Build();
    }

    private static IChatCompletionService CreateClaudeService(ProviderSettings provider)
    {
        AnthropicClient client = new(new APIAuthentication(provider.ApiKey));
        if (!string.IsNullOrWhiteSpace(provider.Endpoint))
        {
            client.ApiUrlFormat = provider.Endpoint.TrimEnd('/') + "/{0}/{1}";
        }

        IChatClient chatClient = new ChatClientBuilder(client.Messages)
            .UseFunctionInvocation()
            .Build();

        return chatClient.AsChatCompletionService();
    }

    public static bool IsAzureEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) &&
        (uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reasoning model families (gpt-5, o-series) reject a custom temperature and
    /// the legacy max_tokens parameter.
    /// </summary>
    public static bool IsReasoningModel(string model)
    {
        string name = model.Trim().ToLowerInvariant();
        return name.StartsWith("gpt-5", StringComparison.Ordinal) ||
               name.StartsWith("o1", StringComparison.Ordinal) ||
               name.StartsWith("o3", StringComparison.Ordinal) ||
               name.StartsWith("o4", StringComparison.Ordinal);
    }

    /// <summary>
    /// Execution settings that each connector accepts: typed settings for the
    /// OpenAI family (so reasoning models get max_completion_tokens and no
    /// temperature), generic extension data for the others.
    /// </summary>
    public static PromptExecutionSettings CreateExecutionSettings(
        AppSettings settings,
        double temperature,
        int maxTokens,
        bool autoInvokeFunctions)
    {
        FunctionChoiceBehavior? functions = autoInvokeFunctions ? FunctionChoiceBehavior.Auto() : null;
        ProviderSettings provider = settings.Active;
        bool reasoning = IsReasoningModel(provider.Model);

        if (settings.ActiveProvider == LlmProvider.OpenAI)
        {
            if (!string.IsNullOrWhiteSpace(provider.Endpoint) && IsAzureEndpoint(provider.Endpoint))
            {
                Microsoft.SemanticKernel.Connectors.AzureOpenAI.AzureOpenAIPromptExecutionSettings azure = new()
                {
                    FunctionChoiceBehavior = functions,
                    MaxTokens = maxTokens,
                    SetNewMaxCompletionTokensEnabled = reasoning,
                };
                if (!reasoning)
                {
                    azure.Temperature = temperature;
                }

                return azure;
            }

            Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIPromptExecutionSettings openAi = new()
            {
                FunctionChoiceBehavior = functions,
            };
            if (!reasoning)
            {
                // Reasoning models only accept the service defaults for both.
                openAi.Temperature = temperature;
                openAi.MaxTokens = maxTokens;
            }

            return openAi;
        }

        return new PromptExecutionSettings
        {
            FunctionChoiceBehavior = functions,
            ExtensionData = new Dictionary<string, object>
            {
                ["temperature"] = temperature,
                ["max_tokens"] = maxTokens,
            },
        };
    }

    /// <summary>Models offered in the chat panel picker, per provider.</summary>
    public static IReadOnlyList<string> SuggestedModels(LlmProvider provider) => provider switch
    {
        LlmProvider.OpenAI => ["gpt-5", "gpt-5-mini", "gpt-4.1", "o4-mini"],
        LlmProvider.Claude => ["claude-opus-5", "claude-sonnet-5", "claude-fable-5-1", "claude-haiku-4-5-20251001"],
        LlmProvider.Gemini => ["gemini-2.5-pro", "gemini-2.5-flash", "gemini-2.0-flash"],
        LlmProvider.Ollama => ["llama3.2", "qwen2.5-coder", "deepseek-coder-v2", "phi4"],
        _ => [],
    };
}
