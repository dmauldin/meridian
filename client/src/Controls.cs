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
    public const string ToggleCharacter = "toggle_character";
    public const string ToggleInventory = "toggle_inventory";

    /// <summary>Protocol bar hotkeys: "protocol_1" to "protocol_9".</summary>
    public static string Protocol(int number) => $"protocol_{number}";

    /// <summary>Shown in the HUD's help panel.</summary>
    public const string HelpText =
        "WASD move   Right-drag look   Wheel zoom\n" +
        "Tab / Left-click target   Q auto-attack   Esc clear\n" +
        "Enter chat   C character   I inventory   1-9 Protocols";

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
        Bind(ToggleCharacter, Key.C);
        Bind(ToggleInventory, Key.I);
        for (var i = 1; i <= 9; i++)
        {
            Bind(Protocol(i), Key.Key0 + i);
        }
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
