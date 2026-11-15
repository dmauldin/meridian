namespace Meridian.ClientState;

/// <summary>
/// The player's lattice as the server reported it. Remaining times are as of receipt; count them
/// down locally for display and wait for the next update for the truth.
/// </summary>
public sealed record ClientProtocols(
    IReadOnlyList<int> Known,
    IReadOnlyList<(int ProtocolId, int RemainingMs)> Running,
    int LatticeUsed,
    int LatticeCapacity);
