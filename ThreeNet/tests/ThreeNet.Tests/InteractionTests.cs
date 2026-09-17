using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

public class InteractionTests
{
    private static readonly Vector2 Viewport = new(200f, 200f);
    private static readonly Vector2 Centre = new(100f, 100f);
    private static readonly Vector2 Corner = new(5f, 5f);

    private static (Scene Scene, Node Group, Node Box, InteractionManager Interaction) Build()
    {
        Scene scene = new();
        Node group = scene.CreateNode(name: "group");
        Node box = scene.AddMesh(scene.CreateBoxGeometry(), scene.CreateMaterial(Vector4.One), group, "box");
        Node camera = scene.AddCamera(Camera.Perspective(0.8f), new Vector3(0f, 0f, 5f));
        return (scene, group, box, new InteractionManager(scene, camera));
    }

    [Fact]
    public void ClicksBubbleToAncestorsUntilHandled()
    {
        (Scene scene, Node group, Node box, InteractionManager interaction) = Build();
        using (scene)
        {
            List<string> log = [];
            interaction.OnClick(box, e => log.Add($"box:{e.HitNode!.Name}"));
            interaction.OnClick(group, e => log.Add($"group:{e.Target.Name}"));

            interaction.PointerDown(Centre, Viewport);
            interaction.PointerUp(Centre, Viewport);
            Assert.Equal(["box:box", "group:group"], log);

            // Pressing on the box but releasing on empty space is not a click.
            log.Clear();
            interaction.PointerDown(Centre, Viewport);
            interaction.PointerUp(Corner, Viewport);
            Assert.Empty(log);

            // A handled event stops bubbling.
            interaction.OnClick(box, e => e.Handled = true);
            interaction.PointerDown(Centre, Viewport);
            interaction.PointerUp(Centre, Viewport);
            Assert.Equal(["box:box"], log);
        }
    }

    [Fact]
    public void HoverRaisesEnterAndLeaveOnce()
    {
        (Scene scene, Node group, _, InteractionManager interaction) = Build();
        using (scene)
        {
            int enter = 0, leave = 0;
            interaction.OnPointerEnter(group, _ => enter++).OnPointerLeave(group, _ => leave++);

            interaction.PointerMove(Corner, Viewport);
            interaction.PointerMove(Centre, Viewport);
            interaction.PointerMove(Centre + new Vector2(3f, 0f), Viewport);
            Assert.Equal(1, enter);
            Assert.Equal("box", interaction.HoveredNode!.Name);

            interaction.PointerMove(Corner, Viewport);
            Assert.Equal(1, leave);
            Assert.Null(interaction.HoveredNode);
        }
    }

    [Fact]
    public void DoubleClickFiresOnSecondClick()
    {
        (Scene scene, _, Node box, InteractionManager interaction) = Build();
        using (scene)
        {
            int doubles = 0;
            interaction.OnDoubleClick(box, _ => doubles++);
            for (int i = 0; i < 2; i++)
            {
                interaction.PointerDown(Centre, Viewport);
                interaction.PointerUp(Centre, Viewport);
            }

            Assert.Equal(1, doubles);
        }
    }

    [Fact]
    public void DraggingMovesTheNodeInTheCameraPlane()
    {
        (Scene scene, Node group, _, InteractionManager interaction) = Build();
        using (scene)
        {
            List<NodeEventKind> kinds = [];
            interaction.MakeDraggable(group, DragMode.CameraPlane);
            interaction.On(group, NodeEventKind.DragStart, e => kinds.Add(e.Kind))
                .On(group, NodeEventKind.Drag, e => kinds.Add(e.Kind))
                .On(group, NodeEventKind.DragEnd, e => kinds.Add(e.Kind));
            int clicks = 0;
            interaction.OnClick(group, _ => clicks++);

            Assert.True(interaction.PointerDown(Centre, Viewport));
            Assert.False(interaction.IsDragging);
            Assert.True(interaction.PointerMove(Centre + new Vector2(40f, 0f), Viewport));
            Assert.True(interaction.IsDragging);
            Assert.True(interaction.PointerUp(Centre + new Vector2(40f, 0f), Viewport));

            Assert.Equal([NodeEventKind.DragStart, NodeEventKind.Drag, NodeEventKind.DragEnd], kinds);
            Assert.Equal(0, clicks);
            // Moved right, stayed at the same depth and height.
            Vector3 position = group.Position;
            Assert.True(position.X > 0.5f, $"x = {position.X}");
            Assert.Equal(0f, position.Y, 2);
            Assert.Equal(0f, position.Z, 2);
        }
    }

    [Fact]
    public void GroundPlaneDragKeepsHeight()
    {
        (Scene scene, Node group, _, InteractionManager interaction) = Build();
        using (scene)
        {
            scene.ActiveCamera!.Position = new Vector3(0f, 5f, 5f);
            scene.ActiveCamera.LookAt(Vector3.Zero);
            interaction.MakeDraggable(group, DragMode.GroundPlane);
            interaction.PointerDown(Centre, Viewport);
            interaction.PointerMove(Centre + new Vector2(0f, -30f), Viewport);
            interaction.PointerUp(Centre + new Vector2(0f, -30f), Viewport);
            Assert.Equal(0f, group.Position.Y, 3);
            Assert.True(group.Position.Z < -0.3f, $"z = {group.Position.Z}");
        }
    }
}
