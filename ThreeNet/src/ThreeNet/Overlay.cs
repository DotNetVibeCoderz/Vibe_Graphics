using System.Numerics;
using ThreeNet.Interop;

namespace ThreeNet;

/// <summary>Point of the parent (or the viewport) an overlay element is attached to.</summary>
public enum OverlayAnchor : uint
{
    TopLeft = 0,
    Top = 1,
    TopRight = 2,
    Left = 3,
    Center = 4,
    Right = 5,
    BottomLeft = 6,
    Bottom = 7,
    BottomRight = 8,
}

public enum OverlayElementKind : uint
{
    Panel = 0,
    Image = 1,
    Text = 2,
}

public enum OverlayTextAlign : uint
{
    Start = 0,
    Center = 1,
    End = 2,
}

/// <summary>
/// Description of an overlay element. Sizes and offsets are pixels (multiplied
/// by <see cref="Overlay.Scale"/>); colours are sRGB with straight alpha.
/// </summary>
public record struct OverlayElementOptions
{
    public OverlayElementKind Kind;
    public OverlayElement? Parent;
    public OverlayAnchor Anchor;
    /// <summary>Pixels from the anchor point (x right, y down).</summary>
    public Vector2 Offset;
    /// <summary>Width and height; 0 on a text element means "fit the text".</summary>
    public Vector2 Size;
    /// <summary>Fill (panel), tint (image) or text colour.</summary>
    public Vector4 Color;
    public Vector4 BorderColor;
    public float BorderWidth;
    public float CornerRadius;
    /// <summary>Higher layers draw on top; children never draw below their parent.</summary>
    public int Layer;
    public bool Visible;
    /// <summary>Only interactive elements are found by hit tests (and clickable through <see cref="InteractionManager"/>).</summary>
    public bool Interactive;
    public Texture? Texture;
    /// <summary>Texture coordinates (u0, v0, u1, v1).</summary>
    public Vector4 Uv;
    public string? Text;
    /// <summary>Font index: 0 is the built-in Inter Regular, others come from <see cref="Overlay.LoadFont"/>.</summary>
    public int Font;
    public float FontSize;
    public OverlayTextAlign Align;
    public OverlayTextAlign VerticalAlign;
    public bool Wrap;

    public OverlayElementOptions()
    {
        Kind = OverlayElementKind.Panel;
        Color = Vector4.One;
        Visible = true;
        Uv = new Vector4(0f, 0f, 1f, 1f);
        FontSize = 16f;
    }

    internal readonly NativeOverlayElement ToNative() => new()
    {
        Kind = (uint)Kind,
        Parent = Parent?.Id ?? 0,
        Anchor = (uint)Anchor,
        Layer = Layer,
        Offset = Offset,
        Size = Size,
        Color = Color,
        BorderColor = BorderColor,
        BorderWidth = BorderWidth,
        CornerRadius = CornerRadius,
        Visible = Visible ? 1 : 0,
        Interactive = Interactive ? 1 : 0,
        Texture = Texture?.Id ?? 0,
        Uv = Uv,
        Font = (uint)Math.Max(0, Font),
        FontSize = FontSize,
        Align = (uint)Align,
        VerticalAlign = (uint)VerticalAlign,
        Wrap = Wrap ? 1 : 0,
    };
}

/// <summary>Handle to an element of a scene's <see cref="Overlay"/>.</summary>
public sealed class OverlayElement : IEquatable<OverlayElement>
{
    internal OverlayElement(Scene scene, uint id)
    {
        Scene = scene;
        Id = id;
    }

    public Scene Scene { get; }

    public uint Id { get; }

    /// <summary>The full description; setting it replaces the element in place.</summary>
    public unsafe OverlayElementOptions Options
    {
        get
        {
            NativeError.Check(NativeMethods.tn_overlay_get(Scene.Handle, Id, out NativeOverlayElement n));
            string text = NativeError.ReadString((buffer, capacity) =>
                NativeMethods.tn_overlay_get_text(Scene.Handle, Id, (byte*)buffer, capacity));
            return new OverlayElementOptions
            {
                Kind = (OverlayElementKind)n.Kind,
                Parent = n.Parent == 0 ? null : new OverlayElement(Scene, n.Parent),
                Anchor = (OverlayAnchor)n.Anchor,
                Offset = n.Offset,
                Size = n.Size,
                Color = n.Color,
                BorderColor = n.BorderColor,
                BorderWidth = n.BorderWidth,
                CornerRadius = n.CornerRadius,
                Layer = n.Layer,
                Visible = n.Visible != 0,
                Interactive = n.Interactive != 0,
                Texture = n.Texture == 0 ? null : new Texture(Scene, n.Texture),
                Uv = n.Uv,
                Text = n.Kind == (uint)OverlayElementKind.Text ? text : null,
                Font = (int)n.Font,
                FontSize = n.FontSize,
                Align = (OverlayTextAlign)n.Align,
                VerticalAlign = (OverlayTextAlign)n.VerticalAlign,
                Wrap = n.Wrap != 0,
            };
        }
        set
        {
            NativeOverlayElement native = value.ToNative();
            NativeError.Check(NativeMethods.tn_overlay_update(Scene.Handle, Id, in native, value.Text));
        }
    }

    /// <summary>Applies a partial change, e.g. <c>element.Update(o => o with { Color = red })</c>.</summary>
    public void Update(Func<OverlayElementOptions, OverlayElementOptions> change) => Options = change(Options);

    public string Text
    {
        get => Options.Text ?? string.Empty;
        set => Update(o => o with { Text = value });
    }

    public bool Visible
    {
        get => Options.Visible;
        set => Update(o => o with { Visible = value });
    }

    public Vector4 Color
    {
        get => Options.Color;
        set => Update(o => o with { Color = value });
    }

    public Vector2 Offset
    {
        get => Options.Offset;
        set => Update(o => o with { Offset = value });
    }

    public Vector2 Size
    {
        get => Options.Size;
        set => Update(o => o with { Size = value });
    }

    /// <summary>Screen rectangle (x, y, width, height) for a render target of the given size.</summary>
    public (float X, float Y, float Width, float Height) GetBounds(Vector2 targetSize)
    {
        NativeError.Check(NativeMethods.tn_overlay_get_rect(Scene.Handle, Id, targetSize.X, targetSize.Y, out Vector4 rect));
        return (rect.X, rect.Y, rect.Z, rect.W);
    }

    /// <summary>Removes the element and its children.</summary>
    public void Remove() => NativeMethods.tn_overlay_remove(Scene.Handle, Id);

    public bool Equals(OverlayElement? other) => other is not null && Id == other.Id && ReferenceEquals(Scene, other.Scene);

    public override bool Equals(object? obj) => Equals(obj as OverlayElement);

    public override int GetHashCode() => HashCode.Combine(Scene, Id);
}

/// <summary>
/// Screen space HUD drawn on top of the rendered frame: rounded panels with
/// borders, images and text (built-in Inter font), anchored to the viewport or
/// to a parent element. Access it through <see cref="Scene.Overlay"/>.
/// </summary>
public sealed class Overlay
{
    private readonly Scene _scene;
    private float _scale = 1f;
    private bool _enabled = true;

    internal Overlay(Scene scene) => _scene = scene;

    /// <summary>Multiplies sizes, offsets and font sizes (UI scale / DPI).</summary>
    public float Scale
    {
        get => _scale;
        set
        {
            _scale = value > 0f ? value : 1f;
            Apply();
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Apply();
        }
    }

    private void Apply() => NativeError.Check(NativeMethods.tn_overlay_configure(_scene.Handle, _scale, _enabled ? 1 : 0));

    public OverlayElement Add(in OverlayElementOptions options)
    {
        NativeOverlayElement native = options.ToNative();
        uint id = NativeMethods.tn_overlay_add(_scene.Handle, in native, options.Text);
        return new OverlayElement(_scene, NativeError.CheckHandle(id));
    }

    public OverlayElement AddPanel(Vector2 offset, Vector2 size, Vector4 color, OverlayAnchor anchor = OverlayAnchor.TopLeft,
        OverlayElement? parent = null, float cornerRadius = 0f, bool interactive = false) =>
        Add(new OverlayElementOptions
        {
            Kind = OverlayElementKind.Panel,
            Offset = offset,
            Size = size,
            Color = color,
            Anchor = anchor,
            Parent = parent,
            CornerRadius = cornerRadius,
            Interactive = interactive,
        });

    public OverlayElement AddText(string text, Vector2 offset, float fontSize, Vector4 color, OverlayAnchor anchor = OverlayAnchor.TopLeft,
        OverlayElement? parent = null, Vector2 size = default, OverlayTextAlign align = OverlayTextAlign.Start) =>
        Add(new OverlayElementOptions
        {
            Kind = OverlayElementKind.Text,
            Text = text,
            Offset = offset,
            FontSize = fontSize,
            Color = color,
            Anchor = anchor,
            Parent = parent,
            Size = size,
            Align = align,
            VerticalAlign = OverlayTextAlign.Center,
        });

    public OverlayElement AddImage(Texture texture, Vector2 offset, Vector2 size, OverlayAnchor anchor = OverlayAnchor.TopLeft,
        OverlayElement? parent = null) =>
        Add(new OverlayElementOptions
        {
            Kind = OverlayElementKind.Image,
            Texture = texture,
            Offset = offset,
            Size = size,
            Anchor = anchor,
            Parent = parent,
        });

    /// <summary>
    /// A rounded, interactive panel with a centred label: a simple button. Handle
    /// clicks with <see cref="InteractionManager.OnClick(OverlayElement, Action{OverlayElement})"/>.
    /// </summary>
    public OverlayElement AddButton(string label, Vector2 offset, Vector2 size, OverlayAnchor anchor = OverlayAnchor.TopLeft,
        OverlayElement? parent = null, Vector4? background = null, Vector4? textColor = null, float fontSize = 16f)
    {
        OverlayElement button = Add(new OverlayElementOptions
        {
            Kind = OverlayElementKind.Panel,
            Offset = offset,
            Size = size,
            Anchor = anchor,
            Parent = parent,
            Color = background ?? new Vector4(0.16f, 0.18f, 0.24f, 0.92f),
            BorderColor = new Vector4(1f, 1f, 1f, 0.25f),
            BorderWidth = 1f,
            CornerRadius = 8f,
            Interactive = true,
        });
        Add(new OverlayElementOptions
        {
            Kind = OverlayElementKind.Text,
            Text = label,
            Parent = button,
            Anchor = OverlayAnchor.Center,
            Size = size,
            FontSize = fontSize,
            Color = textColor ?? Vector4.One,
            Align = OverlayTextAlign.Center,
            VerticalAlign = OverlayTextAlign.Center,
        });
        return button;
    }

    /// <summary>Topmost interactive element at a pixel of a render target of the given size.</summary>
    public OverlayElement? HitTest(Vector2 point, Vector2 targetSize)
    {
        uint id = NativeMethods.tn_overlay_hit_test(_scene.Handle, point.X, point.Y, targetSize.X, targetSize.Y);
        return id == 0 ? null : new OverlayElement(_scene, id);
    }

    /// <summary>Size of text in unscaled pixels, wrapped at <paramref name="maxWidth"/> when positive.</summary>
    public Vector2 MeasureText(string text, float fontSize, int font = 0, float maxWidth = 0f)
    {
        NativeError.Check(NativeMethods.tn_overlay_measure_text(_scene.Handle, (uint)font, fontSize, text, maxWidth, out float width, out float height));
        return new Vector2(width, height);
    }

    /// <summary>Loads a TTF or OTF font; returns the index to use in <see cref="OverlayElementOptions.Font"/>.</summary>
    public unsafe int LoadFont(ReadOnlySpan<byte> fontFile)
    {
        fixed (byte* pointer = fontFile)
        {
            int index = NativeMethods.tn_overlay_load_font(_scene.Handle, pointer, (uint)fontFile.Length);
            NativeError.Check(index);
            return index;
        }
    }

    public int LoadFont(string path) => LoadFont(File.ReadAllBytes(path));

    public void Clear() => NativeError.Check(NativeMethods.tn_overlay_clear(_scene.Handle));
}
