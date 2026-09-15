namespace ThreeAppGen.Services.Conversion;

/// <summary>Where the converted application is meant to run.</summary>
public enum ConversionTarget
{
    /// <summary>Native window app for Windows, Linux and macOS.</summary>
    Desktop,

    /// <summary>Avalonia browser (WebAssembly) head plus a desktop preview head.</summary>
    Web,

    /// <summary>Avalonia Android head plus a desktop preview head.</summary>
    Mobile,
}

/// <summary>Stages reported to the progress UI, in execution order.</summary>
public enum ConversionStage
{
    Analyzing,
    Extracting,
    Scaffolding,
    CopyingAssets,
    Converting,
    Validating,
    Fixing,
    Reporting,
    Completed,
    Failed,
}

public enum ConversionLogLevel
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>A progress tick: stage, overall percentage and a short message.</summary>
public sealed record ConversionProgress(ConversionStage Stage, double Percent, string Message);

/// <summary>One line in the converter log.</summary>
public sealed record ConversionLogEntry(DateTime Timestamp, ConversionLogLevel Level, string Message)
{
    public string Time => Timestamp.ToString("HH:mm:ss");

    public string Badge => Level switch
    {
        ConversionLogLevel.Success => "OK",
        ConversionLogLevel.Warning => "WARN",
        ConversionLogLevel.Error => "FAIL",
        _ => "INFO",
    };
}

/// <summary>Everything the user chose on the conversion page.</summary>
public sealed record ConversionRequest
{
    public required string SourceFolder { get; init; }

    public required string ProjectName { get; init; }

    public required string DestinationFolder { get; init; }

    public required ConversionTarget Target { get; init; }

    /// <summary>Use the configured LLM to translate behaviour the static pass cannot.</summary>
    public bool UseLlm { get; init; } = true;

    /// <summary>How many build-and-fix rounds the LLM gets before falling back.</summary>
    public int MaxFixAttempts { get; init; } = 4;

    /// <summary>Allow writing into a destination that already has files.</summary>
    public bool OverwriteDestination { get; init; }
}

/// <summary>Outcome shown when the conversion finishes.</summary>
public sealed record ConversionResult
{
    public required bool Succeeded { get; init; }

    public required string ProjectRoot { get; init; }

    public required string SolutionPath { get; init; }

    /// <summary>The project a user would open first (the runnable head).</summary>
    public required string StartupProjectPath { get; init; }

    public required string ReportPath { get; init; }

    public bool UsedLlm { get; init; }

    public bool FellBackToBaseline { get; init; }

    public int FixAttempts { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? Error { get; init; }
}
