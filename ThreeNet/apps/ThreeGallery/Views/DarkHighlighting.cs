using Avalonia.Media;
using AvaloniaEdit.Highlighting;

namespace ThreeGallery.Views;

/// <summary>
/// AvaloniaEdit ships highlighting colours tuned for light backgrounds. This
/// remaps them by role to a dark palette (close to VS Code Dark+), so code
/// stays readable on the editor's dark surface.
/// </summary>
internal static class DarkHighlighting
{
    private static readonly HashSet<IHighlightingDefinition> Applied = [];

    public static IHighlightingDefinition? Get(string name)
    {
        IHighlightingDefinition? definition = HighlightingManager.Instance.GetDefinition(name);
        if (definition is not null && Applied.Add(definition))
        {
            foreach (HighlightingColor color in definition.NamedHighlightingColors)
            {
                color.Foreground = new SimpleHighlightingBrush(ColorFor(color.Name));
                color.Background = null;
            }
        }

        return definition;
    }

    private static Color ColorFor(string role)
    {
        string name = role.ToLowerInvariant();
        return name switch
        {
            _ when name.Contains("doc") || name.Contains("xmltag") => Color.Parse("#608B4E"),
            _ when name.Contains("comment") => Color.Parse("#6A9955"),
            _ when name.Contains("string") || name.Contains("char") || name.Contains("attributevalue") => Color.Parse("#CE9178"),
            _ when name.Contains("number") || name.Contains("digit") => Color.Parse("#B5CEA8"),
            _ when name.Contains("method") || name.Contains("function") => Color.Parse("#DCDCAA"),
            _ when name.Contains("preprocessor") => Color.Parse("#9B9B9B"),
            _ when name.Contains("goto") || name.Contains("exception") || name.Contains("return") => Color.Parse("#C586C0"),
            _ when name.Contains("valuetype") || name.Contains("referencetype") || name.Contains("type") => Color.Parse("#4EC9B0"),
            _ when name.Contains("punctuation") => Color.Parse("#D4D4D4"),
            _ when name.Contains("attributename") || name.Contains("property") => Color.Parse("#9CDCFE"),
            _ => Color.Parse("#569CD6"),
        };
    }
}
