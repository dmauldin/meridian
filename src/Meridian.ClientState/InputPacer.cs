using Meridian.Wire;

namespace Meridian.ClientState;

/// <summary>
/// Decides when to send <see cref="MoveIntent"/>: immediately when the direction changes, then
/// every <see cref="ResendMs"/> while moving (the server stops a player whose input goes quiet,
/// and intents travel unreliably). Standing still sends one stop and then nothing.
/// </summary>
public sealed class InputPacer
{
    public const double ResendMs = 100;

    private short _lastX, _lastZ;
    private double _lastSentMs = double.NegativeInfinity;
    private uint _sequence;
    private bool _stopSent = true;

    public uint Sequence => _sequence;

    public MoveIntent? Update(double nowMs, short dirX, short dirZ)
    {
        var moving = dirX != 0 || dirZ != 0;
        var changed = dirX != _lastX || dirZ != _lastZ;
        var due = moving && nowMs - _lastSentMs >= ResendMs;
        if (!changed && !due && (moving || _stopSent))
        {
            return null;
        }
        _lastX = dirX;
        _lastZ = dirZ;
        _lastSentMs = nowMs;
        _stopSent = !moving;
        return new MoveIntent { Sequence = ++_sequence, DirX = dirX, DirZ = dirZ };
    }
}
