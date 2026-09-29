using System.Globalization;

namespace DemoGraphics.Framework;

/// <summary>What kind of control a parameter needs.</summary>
public enum ParameterKind
{
    Slider,
    Toggle,
    Choice,
}

/// <summary>
/// One knob a scene exposes. Scenes declare parameters as data, so the control
/// sheet, the presets and the capture sidecar all work from the same list and no
/// scene has to build UI.
/// </summary>
public sealed class DemoParameter
{
    private DemoParameter(string id, string label, ParameterKind kind, float value)
    {
        Id = id;
        Label = label;
        Kind = kind;
        Value = value;
        Default = value;
    }

    /// <summary>Stable key used by presets, captures and benchmark reports.</summary>
    public string Id { get; }

    public string Label { get; }

    public ParameterKind Kind { get; }

    /// <summary>Unit shown after the value, for example <c>m</c> or <c>deg</c>.</summary>
    public string Unit { get; private init; } = string.Empty;

    /// <summary>One short line under the row, for a caveat worth stating.</summary>
    public string? Note { get; private init; }

    public float Min { get; private init; }

    public float Max { get; private init; } = 1f;

    /// <summary>Slider step; 0 leaves the slider continuous.</summary>
    public float Step { get; private init; }

    public float Default { get; }

    public float Value { get; set; }

    public IReadOnlyList<string> Options { get; private init; } = [];

    /// <summary>Set when a change only takes effect after the scene reloads.</summary>
    public bool RequiresRebuild { get; private init; }

    public bool BoolValue => Value >= 0.5f;

    public int IntValue => (int)MathF.Round(Value);

    public static DemoParameter Slider(
        string id,
        string label,
        float value,
        float min,
        float max,
        string unit = "",
        float step = 0f,
        string? note = null,
        bool requiresRebuild = false) =>
        new(id, label, ParameterKind.Slider, value)
        {
            Min = min,
            Max = max,
            Unit = unit,
            Step = step,
            Note = note,
            RequiresRebuild = requiresRebuild,
        };

    public static DemoParameter Toggle(string id, string label, bool value, string? note = null, bool requiresRebuild = false) =>
        new(id, label, ParameterKind.Toggle, value ? 1f : 0f)
        {
            Min = 0f,
            Max = 1f,
            Step = 1f,
            Note = note,
            RequiresRebuild = requiresRebuild,
        };

    public static DemoParameter Choice(
        string id,
        string label,
        int index,
        string[] options,
        string? note = null,
        bool requiresRebuild = false) =>
        new(id, label, ParameterKind.Choice, index)
        {
            Min = 0f,
            Max = Math.Max(0, options.Length - 1),
            Step = 1f,
            Options = options,
            Note = note,
            RequiresRebuild = requiresRebuild,
        };

    /// <summary>The value as the control sheet prints it, units included.</summary>
    public string Display => Kind switch
    {
        ParameterKind.Toggle => BoolValue ? "on" : "off",
        ParameterKind.Choice => Options.Count == 0 ? "-" : Options[Math.Clamp(IntValue, 0, Options.Count - 1)],
        _ => FormatNumber(),
    };

    public void Reset() => Value = Default;

    private string FormatNumber()
    {
        float range = MathF.Abs(Max - Min);
        int decimals = range >= 200f ? 0 : range >= 20f ? 1 : range >= 2f ? 2 : 3;
        string number = Value.ToString("F" + decimals, CultureInfo.InvariantCulture);
        return Unit.Length == 0 ? number : $"{number} {Unit}";
    }
}
