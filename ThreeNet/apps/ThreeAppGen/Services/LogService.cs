using System.Collections.ObjectModel;

namespace ThreeAppGen.Services;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>One line in the logs panel.</summary>
public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Message)
{
    public string Time => Timestamp.ToString("HH:mm:ss");

    public string Badge => Level switch
    {
        LogLevel.Success => "OK",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "FAIL",
        _ => "INFO",
    };
}

/// <summary>
/// Collects everything worth showing in the logs panel: tool calls, build
/// output, file writes. The collection is observable so the UI just binds to it.
/// </summary>
public sealed class LogService
{
    private const int MaxEntries = 2000;

    /// <summary>Raised on the thread that logged; the UI marshals it.</summary>
    public event Action<LogEntry>? Logged;

    public ObservableCollection<LogEntry> Entries { get; } = [];

    public void Info(string message) => Add(LogLevel.Info, message);

    public void Success(string message) => Add(LogLevel.Success, message);

    public void Warning(string message) => Add(LogLevel.Warning, message);

    public void Error(string message) => Add(LogLevel.Error, message);

    /// <summary>Logs a block of process output, one entry per line.</summary>
    public void Output(string text, LogLevel level = LogLevel.Info)
    {
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.TrimEnd();
            if (trimmed.Length > 0)
            {
                Add(level, trimmed);
            }
        }
    }

    private void Add(LogLevel level, string message)
    {
        LogEntry entry = new(DateTime.Now, level, message);
        Logged?.Invoke(entry);
    }

    /// <summary>Appends to the bound collection; must be called on the UI thread.</summary>
    public void Append(LogEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(0);
        }
    }

    public void Clear() => Entries.Clear();
}
