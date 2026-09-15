using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ThreeAppGen.Plugins;

namespace ThreeAppGen.Services;

/// <summary>An image pasted or attached to a chat message.</summary>
public sealed record ChatAttachment(string FileName, string MimeType, byte[] Data);

/// <summary>Who wrote a message in the transcript.</summary>
public enum ChatRole
{
    User,
    Assistant,
    System,
}

/// <summary>
/// One bubble in the chat panel. <see cref="Text"/> raises change
/// notifications so the UI can grow the bubble while the reply streams in.
/// </summary>
public sealed class ChatMessage : System.ComponentModel.INotifyPropertyChanged
{
    private string _text = string.Empty;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public required ChatRole Role { get; init; }

    public required string Text
    {
        get => _text;
        set
        {
            if (_text == value)
            {
                return;
            }

            _text = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Text)));
        }
    }

    public DateTime Timestamp { get; } = DateTime.Now;

    public List<ChatAttachment> Attachments { get; init; } = [];

    public bool IsUser => Role == ChatRole.User;

    public string Author => Role switch
    {
        ChatRole.User => "You",
        ChatRole.Assistant => "Jack",
        _ => "System",
    };
}

/// <summary>
/// Drives the conversation with Jack: owns the kernel, the history and the
/// streaming call, and rebuilds the kernel whenever the provider or model
/// changes.
/// </summary>
public sealed class ChatService(AppSettings settings, ProjectService projects, LogService log)
{
    private readonly WebPlugin _webPlugin = new(settings, log);
    private Kernel? _kernel;
    private ChatHistory _history = [];
    private string _kernelSignature = string.Empty;

    /// <summary>The visible transcript, for the UI to bind to.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ChatMessage> Messages { get; } = [];

    /// <summary>Base instructions; the user prompt from Settings is appended.</summary>
    public string BuildSystemPrompt()
    {
        string project = projects.Current is { } current
            ? $"The open project is '{current.Name}' at {current.RootPath}."
            : "No project is open yet; create one with CreateProject before writing files.";

        string extra = string.IsNullOrWhiteSpace(settings.SystemPrompt)
            ? string.Empty
            : $"\n\nAdditional instructions from the user:\n{settings.SystemPrompt}";

        return $"""
            You are Jack - The Code Bender, the coding assistant inside Three.Net App Generator,
            a desktop tool by Gravicode Studios (led by Kang Fadhil).

            You build complete .NET 10 applications on top of Three.Net, a native multiplatform 3D
            library whose rendering core is written in Rust (wgpu) and whose API is C#.

            How you work:
            - Call GetThreeNetApi before writing rendering code, so the API you use is real.
              Never invent Three.Net members; if you are unsure, look the topic up first.
            - Write real files with WriteFile instead of only printing code in the chat.
              Split larger apps into several files rather than one giant Program.cs.
            - After writing code, call BuildProject and fix any compiler error you caused.
            - Use ListFiles and ReadFile to understand a project before changing it.
            - Use SearchInternet and ScrapeWebPage for facts you do not know, MathCalculation for
              arithmetic and GetCurrentDateTime for anything time related.
            - Prefer C# 13/.NET 10 idioms, file scoped namespaces, expressive names and short
              comments that explain why, not what.
            - Keep answers concise. Say what you changed and what the user should try next.

            {project}{extra}

            Answer in the language the user writes in (Indonesian or English).
            """;
    }

    /// <summary>Creates or reuses the kernel for the current provider and model.</summary>
    private Kernel GetKernel()
    {
        string signature = $"{settings.ActiveProvider}|{settings.Active.Model}|{settings.Active.Endpoint}|{settings.Active.ApiKey.Length}";
        if (_kernel is not null && signature == _kernelSignature)
        {
            return _kernel;
        }

        object[] plugins =
        [
            new ProjectPlugin(projects, log),
            _webPlugin,
            new UtilityPlugin(),
            new ThreeNetPlugin(),
        ];

        _kernel = KernelFactory.Create(settings, plugins);
        _kernelSignature = signature;
        log.Info($"Kernel ready: {settings.ActiveProvider} / {settings.Active.Model}");
        return _kernel;
    }

    /// <summary>Forgets the transcript and the kernel history.</summary>
    public void ClearThread()
    {
        Messages.Clear();
        _history = [];
        log.Info("Chat thread cleared");
    }

    /// <summary>Drops the cached kernel so the next message rebuilds it.</summary>
    public void InvalidateKernel() => _kernel = null;

    /// <summary>
    /// Sends a message and streams the reply back token by token. Tool calls
    /// happen automatically and are reported through <see cref="LogService"/>.
    /// </summary>
    public async IAsyncEnumerable<string> SendAsync(
        string message,
        IReadOnlyList<ChatAttachment> attachments,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Kernel kernel = GetKernel();
        IChatCompletionService chat = kernel.GetRequiredService<IChatCompletionService>();

        if (_history.Count == 0)
        {
            _history.AddSystemMessage(BuildSystemPrompt());
        }

        if (attachments.Count == 0)
        {
            _history.AddUserMessage(message);
        }
        else
        {
            // Mixed content: the text plus every attached image.
            ChatMessageContentItemCollection items = [new TextContent(message)];
            foreach (ChatAttachment attachment in attachments)
            {
                items.Add(new ImageContent(attachment.Data, attachment.MimeType));
            }

            _history.AddUserMessage(items);
        }

        PromptExecutionSettings executionSettings = KernelFactory.CreateExecutionSettings(
            settings, settings.Temperature, settings.MaxTokens, autoInvokeFunctions: true);

        System.Text.StringBuilder reply = new();
        await foreach (StreamingChatMessageContent chunk in chat.GetStreamingChatMessageContentsAsync(
            _history, executionSettings, kernel, cancellationToken))
        {
            if (string.IsNullOrEmpty(chunk.Content))
            {
                continue;
            }

            reply.Append(chunk.Content);
            yield return chunk.Content;
        }

        _history.AddAssistantMessage(reply.ToString());
    }
}
