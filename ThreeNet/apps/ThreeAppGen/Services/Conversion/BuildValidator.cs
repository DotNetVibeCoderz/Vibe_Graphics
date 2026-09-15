using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ThreeAppGen.Services.Conversion;

/// <summary>A compiler or MSBuild diagnostic parsed from the build output.</summary>
public sealed record BuildDiagnostic(string File, int Line, int Column, string Code, string Message, bool IsError)
{
    public override string ToString() => $"{Path.GetFileName(File)}({Line},{Column}): {(IsError ? "error" : "warning")} {Code}: {Message}";
}

public sealed record BuildOutcome(bool Succeeded, int ExitCode, IReadOnlyList<BuildDiagnostic> Errors, string Output)
{
    /// <summary>The build failed because a workload (android, wasm-tools) is missing, not because of code.</summary>
    public bool MissingWorkload => !Succeeded && Errors.Any(e => e.Code is "NETSDK1147" or "NETSDK1178" or "NETSDK1135" or "NETSDK1202");
}

/// <summary>Runs `dotnet build` and turns its output into structured diagnostics.</summary>
public static partial class BuildValidator
{
    public static async Task<BuildOutcome> BuildAsync(
        string projectPath,
        bool skipRustBuild,
        Action<ConversionLogLevel, string> log,
        CancellationToken cancellationToken)
    {
        // --disable-build-servers: reused MSBuild nodes and the compiler server inherit
        // the redirected stdout pipe and keep it open, so the build would never look finished.
        string arguments = $"build \"{projectPath}\" -nologo -v q -clp:NoSummary --disable-build-servers";
        if (skipRustBuild)
        {
            arguments += " -p:SkipRustBuild=true";
        }

        ProcessStartInfo startInfo = new("dotnet", arguments)
        {
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using Process process = new() { StartInfo = startInfo };
        StringBuilder output = new();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw;
        }

        string text = output.ToString();
        List<BuildDiagnostic> errors = Parse(text).Where(d => d.IsError).DistinctBy(d => (d.File, d.Line, d.Code, d.Message)).ToList();

        foreach (BuildDiagnostic error in errors.Take(12))
        {
            log(ConversionLogLevel.Error, error.ToString());
        }

        if (errors.Count > 12)
        {
            log(ConversionLogLevel.Error, $"... and {errors.Count - 12} more errors");
        }

        return new BuildOutcome(process.ExitCode == 0, process.ExitCode, errors, text);
    }

    public static IEnumerable<BuildDiagnostic> Parse(string output)
    {
        foreach (Match match in Diagnostic().Matches(output))
        {
            yield return new BuildDiagnostic(
                match.Groups["file"].Value.Trim(),
                int.TryParse(match.Groups["line"].Value, out int line) ? line : 0,
                int.TryParse(match.Groups["column"].Value, out int column) ? column : 0,
                match.Groups["code"].Value,
                match.Groups["message"].Value.Trim(),
                match.Groups["kind"].Value == "error");
        }

        // MSBuild level errors ("error NETSDK1147: ...") carry no file position.
        foreach (Match match in ToolDiagnostic().Matches(output))
        {
            yield return new BuildDiagnostic(match.Groups["file"].Value.Trim(), 0, 0, match.Groups["code"].Value, match.Groups["message"].Value.Trim(), IsError: true);
        }
    }

    [GeneratedRegex(@"^(?<file>[^\r\n()]+?)\((?<line>\d+),(?<column>\d+)(?:,\d+,\d+)?\):\s*(?:Avalonia\s+)?(?<kind>error|warning)\s+(?<code>[A-Z]+\d+):\s*(?<message>.*?)(?:\s+\[[^\]]+\])?\r?$", RegexOptions.Multiline)]
    private static partial Regex Diagnostic();

    [GeneratedRegex(@"^(?<file>[^\r\n:]+?)\s*:\s*error\s+(?<code>(?:NETSDK|MSB|NU)\d+):\s*(?<message>.*?)(?:\s+\[[^\]]+\])?\r?$", RegexOptions.Multiline)]
    private static partial Regex ToolDiagnostic();
}
