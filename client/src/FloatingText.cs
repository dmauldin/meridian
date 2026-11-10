using Godot;

namespace Meridian.Client;

/// <summary>A number or word that rises above an entity and fades out.</summary>
public static class FloatingText
{
    public static void Spawn(Node3D parent, Vector3 at, string text, Color color, bool big = false)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var label = new Label3D
        {
            Text = text,
            Modulate = color,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            FontSize = big ? 96 : 64,
            OutlineSize = 12,
            PixelSize = 0.006f,
            Position = at + new Vector3((float)GD.RandRange(-0.4, 0.4), 2.6f, 0),
        };
        parent.AddChild(label);
        var tween = label.CreateTween();
        tween.TweenProperty(label, "position", label.Position + new Vector3(0, 1.6f, 0), 1.1);
        tween.Parallel().TweenProperty(label, "modulate:a", 0f, 1.1).SetDelay(0.3);
        tween.TweenCallback(Callable.From(label.QueueFree));
    }
}
