using Godot;

namespace Meridian.Client;

/// <summary>
/// Input actions and their default keys. Registered at startup so bindings live in code review,
/// not in serialized project settings. Remapping UI comes later.
/// </summary>
public static class Controls
{
    public const string MoveForward = "move_forward";
    public const string MoveBack = "move_back";
    public const string MoveLeft = "move_left";
    public const string MoveRight = "move_right";
    public const string TargetNext = "target_next";
    public const string ClearTarget = "clear_target";
    public const string ToggleAttack = "toggle_attack";
    public const string OpenChat = "open_chat";

    /// <summary>Shown in the HUD's help panel.</summary>
    public const string HelpText =
        "WASD move   Right-drag look   Wheel zoom\n" +
        "Tab / Left-click target   Q auto-attack   Esc clear\n" +
        "Enter chat";

    public static void Register()
    {
        Bind(MoveForward, Key.W, Key.Up);
        Bind(MoveBack, Key.S, Key.Down);
        Bind(MoveLeft, Key.A, Key.Left);
        Bind(MoveRight, Key.D, Key.Right);
        Bind(TargetNext, Key.Tab);
        Bind(ClearTarget, Key.Escape);
        Bind(ToggleAttack, Key.Q);
        Bind(OpenChat, Key.Enter, Key.KpEnter);
    }

    private static void Bind(string action, params Key[] keys)
    {
        if (InputMap.HasAction(action))
        {
            InputMap.EraseAction(action);
        }
        InputMap.AddAction(action);
        foreach (var key in keys)
        {
            InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = key });
        }
    }
}
