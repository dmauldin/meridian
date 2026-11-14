namespace Meridian.ClientState;

/// <summary>Something the UI should show once: floating numbers, chat lines, banners.</summary>
public abstract record GameEvent;

/// <summary>Outcome values match <c>CombatEvent.outcome</c> on the wire.</summary>
public enum HitOutcome : byte
{
    Miss = 0,
    Hit = 1,
    Critical = 2,
    Evaded = 3,
}

public sealed record CombatText(uint AttackerId, uint TargetId, HitOutcome Outcome, int Damage) : GameEvent;

public sealed record EntityKilled(uint EntityId, uint KillerId) : GameEvent;

public sealed record ChatLine(uint FromEntityId, string From, string Text) : GameEvent;

/// <summary>A system message for the chat log or a banner (disconnects, respawns).</summary>
/// <summary>Known or running Protocols changed; read <see cref="ClientWorld.Protocols"/>.</summary>
public sealed record ProtocolsChanged : GameEvent;

/// <summary>The inventory or worn items changed; read <see cref="ClientWorld.Inventory"/>.</summary>
public sealed record InventoryChanged : GameEvent;

public sealed record SystemNotice(string Text) : GameEvent;

/// <summary>Experience earned (and any levels gained with it).</summary>
public sealed record ExperienceGained(int Amount, int Level, int LevelsGained) : GameEvent;
