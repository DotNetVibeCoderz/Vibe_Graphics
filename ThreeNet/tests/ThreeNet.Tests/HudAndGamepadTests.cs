using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

public class HudAndGamepadTests
{
    [Fact]
    public void OverlayElementsRoundTripAndLayout()
    {
        using Scene scene = new();
        OverlayElement bar = scene.Overlay.AddPanel(new Vector2(0f, -10f), new Vector2(200f, 40f), new Vector4(0f, 0f, 0f, 0.6f), OverlayAnchor.Bottom);
        OverlayElement label = scene.Overlay.AddText("Lap 1/3", Vector2.Zero, 18f, Vector4.One, OverlayAnchor.Center, bar);

        Assert.Equal("Lap 1/3", label.Text);
        label.Text = "Lap 2/3";
        Assert.Equal("Lap 2/3", label.Options.Text);
        Assert.Equal(bar, label.Options.Parent);

        var bounds = bar.GetBounds(new Vector2(800f, 600f));
        Assert.Equal((300f, 550f, 200f, 40f), bounds);

        Vector2 size = scene.Overlay.MeasureText("Lap 2/3", 18f);
        var labelBounds = label.GetBounds(new Vector2(800f, 600f));
        Assert.Equal(MathF.Ceiling(size.X), labelBounds.Width, 1);

        bar.Visible = false;
        Assert.Equal(0f, label.GetBounds(new Vector2(800f, 600f)).Width);

        bar.Remove();
        Assert.Throws<ThreeNetException>(() => label.Options);
        Assert.Throws<ThreeNetException>(() => scene.Overlay.LoadFont([1, 2, 3]));
    }

    [Fact]
    public void OverlayButtonsBlockPickingAndRaiseClicks()
    {
        using Scene scene = new();
        Node box = scene.AddMesh(scene.CreateBoxGeometry(4f, 4f, 1f), scene.CreateMaterial(Vector4.One));
        Node camera = scene.AddCamera(Camera.Perspective(0.8f), new Vector3(0f, 0f, 5f));
        OverlayElement button = scene.Overlay.AddButton("Pause", Vector2.Zero, new Vector2(60f, 30f), OverlayAnchor.Center);

        InteractionManager interaction = new(scene, camera) { OverlayTargetSize = new Vector2(400f, 400f) };
        int buttonClicks = 0, boxClicks = 0, enters = 0;
        interaction.OnClick(button, _ => buttonClicks++).OnPointerEnter(button, _ => enters++);
        interaction.OnClick(box, _ => boxClicks++);

        // The viewport is half the target size: (100, 100) maps to the centre pixel (200, 200).
        Vector2 viewport = new(200f, 200f);
        interaction.PointerMove(new Vector2(100f, 100f), viewport);
        Assert.Equal(1, enters);
        Assert.Null(interaction.HoveredNode);
        Assert.True(interaction.PointerDown(new Vector2(100f, 100f), viewport));
        Assert.True(interaction.HasPointerCapture);
        Assert.True(interaction.PointerUp(new Vector2(100f, 100f), viewport));
        Assert.Equal(1, buttonClicks);
        Assert.Equal(0, boxClicks);

        // Off the button, the box underneath is clickable.
        interaction.PointerDown(new Vector2(70f, 70f), viewport);
        interaction.PointerUp(new Vector2(70f, 70f), viewport);
        Assert.Equal(1, boxClicks);
    }

    [Fact]
    public void VirtualGamepadsReportPressesAndReleases()
    {
        using Gamepads pads = new();
        pads.Update();
        int slot = pads.SlotCount;
        List<string> events = [];
        pads.ControllerConnected += s => events.Add($"+{s.Name}");
        pads.ControllerDisconnected += s => events.Add($"-{s.Slot}");

        pads.SetVirtual(slot, GamepadButton.A, new Vector2(0.5f, -1f), rightTrigger: 0.8f, name: "Test pad");
        GamepadState state = pads[slot];
        Assert.True(state.IsConnected && state.IsVirtual);
        Assert.True(state.WasPressed(GamepadButton.A));
        Assert.Equal(new Vector2(0.5f, -1f), state.LeftStick);
        Assert.Equal(0.8f, state.RightTrigger);

        pads.Update();
        Assert.Contains("+Test pad", events);
        Assert.True(pads[slot].IsDown(GamepadButton.South));
        Assert.False(pads[slot].WasPressed(GamepadButton.South));

        pads.SetVirtual(slot, GamepadButton.None);
        Assert.True(pads[slot].WasReleased(GamepadButton.A));
        Assert.Contains(pads.Connected, s => s.Slot == slot);

        pads.SetVirtual(slot, GamepadButton.None, connected: false);
        pads.Update();
        Assert.Contains($"-{slot}", events);
        Assert.Throws<ThreeNetException>(() => pads.SetVirtual(slot + 3, GamepadButton.A));
        Assert.False(pads.Rumble(slot, 1f, 1f, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void HudRendersOnTopOfTheScene()
    {
        Renderer renderer;
        try
        {
            renderer = Renderer.CreateOffscreen(RendererOptions.Default with { Width = 128, Height = 128, ToneMapping = ToneMapping.None, MsaaSamples = 1 });
        }
        catch (ThreeNetException)
        {
            return;
        }

        using (renderer)
        {
            using Scene scene = new() { Environment = SceneEnvironment.Default with { Background = new Vector4(0f, 0f, 0f, 1f) } };
            Node camera = scene.AddCamera(Camera.Perspective(0.8f), new Vector3(0f, 0f, 5f));
            scene.Overlay.AddPanel(new Vector2(8f, 8f), new Vector2(40f, 40f), new Vector4(1f, 0f, 0f, 1f));
            renderer.Render(scene, camera);
            byte[] pixels = renderer.ReadPixels();
            int inside = ((28 * 128) + 28) * 4;
            Assert.True(pixels[inside] > 240 && pixels[inside + 1] < 20);
            int outside = ((100 * 128) + 100) * 4;
            Assert.True(pixels[outside] < 10);

            scene.Overlay.Enabled = false;
            renderer.Render(scene, camera);
            Assert.True(renderer.ReadPixels()[inside] < 10);
        }
    }
}
