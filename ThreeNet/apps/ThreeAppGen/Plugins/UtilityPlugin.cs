using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.SemanticKernel;

namespace ThreeAppGen.Plugins;

/// <summary>
/// Small deterministic helpers. Language models are unreliable at arithmetic
/// and have no clock, so both are provided as tools.
/// </summary>
public sealed class UtilityPlugin
{
    [KernelFunction, Description("Evaluates a mathematical expression and returns the result.")]
    public string MathCalculation(
        [Description("Expression, for example (2+3)*sqrt(16)/pi")] string expression)
    {
        try
        {
            double value = ExpressionEvaluator.Evaluate(expression);
            return value.ToString("G15", CultureInfo.InvariantCulture);
        }
        catch (Exception exception)
        {
            return $"Could not evaluate '{expression}': {exception.Message}";
        }
    }

    [KernelFunction, Description("Returns the current local date and time, plus the UTC offset and time zone.")]
    public string GetCurrentDateTime(
        [Description("Optional .NET format string, for example yyyy-MM-dd HH:mm")] string format = "")
    {
        DateTimeOffset now = DateTimeOffset.Now;
        string formatted = string.IsNullOrWhiteSpace(format)
            ? now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
            : now.ToString(format, CultureInfo.InvariantCulture);
        return $"{formatted} ({TimeZoneInfo.Local.StandardName}, day {now.DayOfWeek})";
    }

    [KernelFunction, Description("Returns the number of days between two dates.")]
    public string DaysBetween(
        [Description("First date, ISO format.")] string from,
        [Description("Second date, ISO format.")] string to)
    {
        if (!DateTime.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime start) ||
            !DateTime.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime end))
        {
            return "Both dates must be parseable, for example 2026-01-31.";
        }

        return ((int)(end.Date - start.Date).TotalDays).ToString(CultureInfo.InvariantCulture);
    }

    [KernelFunction, Description("Returns the runtime and machine information of the developer box.")]
    public string GetSystemInfo() =>
        $"OS: {Environment.OSVersion}\n" +
        $"Runtime: .NET {Environment.Version}\n" +
        $"Processors: {Environment.ProcessorCount}\n" +
        $"64 bit: {Environment.Is64BitOperatingSystem}\n" +
        $"Machine: {Environment.MachineName}";

    [KernelFunction, Description("Generates a new GUID, useful for project and assembly identifiers.")]
    public string GenerateGuid() => Guid.NewGuid().ToString();
}

/// <summary>
/// A tiny recursive descent evaluator: numbers, the four operators, unary
/// minus, parentheses, power, a few constants and common functions.
/// </summary>
internal static class ExpressionEvaluator
{
    public static double Evaluate(string expression)
    {
        int position = 0;
        double result = ParseExpression(expression, ref position);
        SkipWhitespace(expression, ref position);
        if (position < expression.Length)
        {
            throw new FormatException($"unexpected '{expression[position]}' at {position}");
        }

        return result;
    }

    private static double ParseExpression(string text, ref int position)
    {
        double value = ParseTerm(text, ref position);
        while (true)
        {
            SkipWhitespace(text, ref position);
            if (position < text.Length && (text[position] == '+' || text[position] == '-'))
            {
                char op = text[position++];
                double right = ParseTerm(text, ref position);
                value = op == '+' ? value + right : value - right;
            }
            else
            {
                return value;
            }
        }
    }

    private static double ParseTerm(string text, ref int position)
    {
        double value = ParsePower(text, ref position);
        while (true)
        {
            SkipWhitespace(text, ref position);
            if (position < text.Length && (text[position] == '*' || text[position] == '/' || text[position] == '%'))
            {
                char op = text[position++];
                double right = ParsePower(text, ref position);
                value = op switch
                {
                    '*' => value * right,
                    '/' => value / right,
                    _ => value % right,
                };
            }
            else
            {
                return value;
            }
        }
    }

    private static double ParsePower(string text, ref int position)
    {
        double value = ParseUnary(text, ref position);
        SkipWhitespace(text, ref position);
        if (position < text.Length && text[position] == '^')
        {
            position++;
            // Right associative: 2^3^2 is 2^(3^2).
            double exponent = ParsePower(text, ref position);
            return Math.Pow(value, exponent);
        }

        return value;
    }

    private static double ParseUnary(string text, ref int position)
    {
        SkipWhitespace(text, ref position);
        if (position < text.Length && text[position] == '-')
        {
            position++;
            return -ParseUnary(text, ref position);
        }

        if (position < text.Length && text[position] == '+')
        {
            position++;
        }

        return ParseAtom(text, ref position);
    }

    private static double ParseAtom(string text, ref int position)
    {
        SkipWhitespace(text, ref position);
        if (position >= text.Length)
        {
            throw new FormatException("unexpected end of expression");
        }

        if (text[position] == '(')
        {
            position++;
            double value = ParseExpression(text, ref position);
            SkipWhitespace(text, ref position);
            if (position >= text.Length || text[position] != ')')
            {
                throw new FormatException("missing closing parenthesis");
            }

            position++;
            return value;
        }

        if (char.IsLetter(text[position]))
        {
            int start = position;
            while (position < text.Length && char.IsLetterOrDigit(text[position]))
            {
                position++;
            }

            string name = text[start..position].ToLowerInvariant();
            SkipWhitespace(text, ref position);

            if (position < text.Length && text[position] == '(')
            {
                position++;
                double argument = ParseExpression(text, ref position);
                SkipWhitespace(text, ref position);
                if (position >= text.Length || text[position] != ')')
                {
                    throw new FormatException($"missing closing parenthesis after {name}");
                }

                position++;
                return name switch
                {
                    "sin" => Math.Sin(argument),
                    "cos" => Math.Cos(argument),
                    "tan" => Math.Tan(argument),
                    "asin" => Math.Asin(argument),
                    "acos" => Math.Acos(argument),
                    "atan" => Math.Atan(argument),
                    "sqrt" => Math.Sqrt(argument),
                    "abs" => Math.Abs(argument),
                    "log" => Math.Log(argument),
                    "log10" => Math.Log10(argument),
                    "exp" => Math.Exp(argument),
                    "floor" => Math.Floor(argument),
                    "ceil" => Math.Ceiling(argument),
                    "round" => Math.Round(argument),
                    "rad" => argument * Math.PI / 180.0,
                    "deg" => argument * 180.0 / Math.PI,
                    _ => throw new FormatException($"unknown function '{name}'"),
                };
            }

            return name switch
            {
                "pi" => Math.PI,
                "e" => Math.E,
                "tau" => Math.Tau,
                _ => throw new FormatException($"unknown constant '{name}'"),
            };
        }

        int numberStart = position;
        while (position < text.Length && (char.IsDigit(text[position]) || text[position] == '.'))
        {
            position++;
        }

        if (numberStart == position)
        {
            throw new FormatException($"unexpected '{text[position]}' at {position}");
        }

        return double.Parse(text[numberStart..position], CultureInfo.InvariantCulture);
    }

    private static void SkipWhitespace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }
    }
}
