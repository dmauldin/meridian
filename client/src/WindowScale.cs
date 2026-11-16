using System.Globalization;
using Godot;

namespace Meridian.Client;

/// <summary>
/// Makes the client the same apparent size on any display. The project uses canvas_items
/// stretch with a 1600x900 design size, so the UI scales with the window; this opens the window
/// at 1600x900 times the monitor's scale (capped to the screen), so a hi-DPI display gets a
/// proportionally bigger window instead of a tiny one.
///
/// <c>--ui-scale 1.25</c> (after Godot's <c>--</c>) or <c>MERIDIAN_UI_SCALE=1.25</c> multiplies the
/// UI on top of that. A window size given to Godot (<c>--resolution</c>) is respected as is.
/// </summary>
public static class WindowScale
{
    /// <summary>The project's viewport size (project.godot), which the UI is laid out for.</summary>
    public static readonly Vector2I DesignSize = new(1600, 900);

    public const float MinUiScale = 0.5f;
    public const float MaxUiScale = 3f;

    public static void Apply(Window root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (DisplayServer.GetName() == "headless")
        {
            return;
        }
        if (UserScale() is { } extra)
        {
            root.ContentScaleFactor = extra;
        }
        DisplayServer.WindowSetMinSize(new Vector2I(960, 540));
        // Godot keeps its own options (such as --resolution) out of GetCmdlineArgs, so tell an
        // explicit size by the window no longer being the project's default.
        if (DisplayServer.WindowGetSize() != DesignSize)
        {
            return; // an explicit size wins (screenshots, testing)
        }

        var screen = DisplayServer.WindowGetCurrentScreen();
        var scale = MonitorScale(screen);
        var usable = DisplayServer.ScreenGetUsableRect(screen);
        var design = (Vector2)DesignSize * scale;
        // Never larger than 90% of the usable area, keeping 16:9.
        var fit = Math.Min(1f, Math.Min(usable.Size.X * 0.9f / design.X, usable.Size.Y * 0.9f / design.Y));
        var size = new Vector2I((int)(design.X * fit), (int)(design.Y * fit));
        DisplayServer.WindowSetSize(size);
        DisplayServer.WindowSetPosition(usable.Position + ((usable.Size - size) / 2));
        GD.Print($"window: monitor scale {scale:0.##}, size {size.X}x{size.Y}, ui scale {root.ContentScaleFactor:0.##}");
    }

    /// <summary>
    /// The monitor's scale factor. macOS and Wayland report it directly; Windows and X11 report
    /// 1, so fall back to DPI / 96 (Windows' 100%).
    /// </summary>
    public static float MonitorScale(int screen)
    {
        var reported = DisplayServer.ScreenGetScale(screen);
        if (reported > 1.01f)
        {
            return reported;
        }
        var dpi = DisplayServer.ScreenGetDpi(screen);
        return dpi > 0 ? Math.Clamp(dpi / 96f, 1f, 4f) : 1f;
    }

    /// <summary>The extra UI scale from <c>--ui-scale</c> or <c>MERIDIAN_UI_SCALE</c>, if valid.</summary>
    public static float? UserScale()
    {
        var args = OS.GetCmdlineUserArgs();
        var i = Array.IndexOf(args, "--ui-scale");
        var text = i >= 0 && i + 1 < args.Length ? args[i + 1] : System.Environment.GetEnvironmentVariable("MERIDIAN_UI_SCALE");
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= MinUiScale and <= MaxUiScale
            ? value
            : null;
    }
}
