using Meridian.Core.World;

namespace Meridian.Server.Zone;

/// <summary>
/// Uniform spatial hash for interest queries. Rebuilt from scratch each tick: at M0 scale
/// (hundreds of entities) a rebuild is cheaper and simpler than incremental updates.
/// </summary>
internal sealed class SpatialGrid(int cellSizeMm)
{
    private readonly Dictionary<long, List<int>> _cells = [];
    private readonly Stack<List<int>> _spareLists = new();

    public int CellSizeMm { get; } = cellSizeMm;

    public void Clear()
    {
        foreach (var list in _cells.Values)
        {
            list.Clear();
            _spareLists.Push(list);
        }
        _cells.Clear();
    }

    public void Insert(int index, Position p)
    {
        var key = Key(Cell(p.X), Cell(p.Z));
        if (!_cells.TryGetValue(key, out var list))
        {
            list = _spareLists.Count > 0 ? _spareLists.Pop() : [];
            _cells.Add(key, list);
        }
        list.Add(index);
    }

    /// <summary>Adds every index in cells overlapping the square around <paramref name="center"/> to <paramref name="results"/>.</summary>
    public void Query(Position center, int radiusMm, List<int> results)
    {
        var minX = Cell(center.X - radiusMm);
        var maxX = Cell(center.X + radiusMm);
        var minZ = Cell(center.Z - radiusMm);
        var maxZ = Cell(center.Z + radiusMm);
        for (var cx = minX; cx <= maxX; cx++)
        {
            for (var cz = minZ; cz <= maxZ; cz++)
            {
                if (_cells.TryGetValue(Key(cx, cz), out var list))
                {
                    results.AddRange(list);
                }
            }
        }
    }

    private int Cell(int mm) => (int)Math.Floor((double)mm / CellSizeMm);

    private static long Key(int cx, int cz) => ((long)cx << 32) | (uint)cz;
}
