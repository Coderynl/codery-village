namespace Codery.Village.Core.Map;

/// <summary>What a tile is made of. <see cref="Fishing"/> is water that gives fish; every other kind but <see cref="Water"/> is land.</summary>
public enum Ground { Water, Land, Forest, Fertile, Rock, Clay, Fishing, Lot }

/// <summary>A piece of land surrounded by water.</summary>
public sealed record Island(int Id, string Name, IReadOnlyList<Cell> Cells, bool Eddie);

/// <summary>The tiles to walk over, from the first step up to and including the goal.</summary>
public sealed record Route(IReadOnlyList<Cell> Cells);

/// <summary>The ground of the village: a hex grid of tiles, grouped into islands. Made by <see cref="MapText.Parse"/>.</summary>
public sealed class IslandMap
{
    private readonly Ground[,] _ground;
    private readonly int?[,] _island;
    private readonly int[,] _level;

    internal IslandMap(Ground[,] ground, int?[,] island, IReadOnlyList<Island> islands, int[,] level)
    {
        _ground = ground;
        _island = island;
        _level = level;
        Islands = islands;
    }

    public int Rows => _ground.GetLength(0);
    public int Cols => _ground.GetLength(1);
    public IReadOnlyList<Island> Islands { get; }

    /// <summary>The ground of a tile; outside the map is water.</summary>
    public Ground At(Cell cell) => Contains(cell) ? _ground[cell.Row, cell.Col] : Ground.Water;

    public bool IsWalkable(Cell cell) => IsLand(At(cell));

    /// <summary>How many terraces a tile stands above the sea; water and outside the map are 0.</summary>
    public int LevelOf(Cell cell) => Contains(cell) ? _level[cell.Row, cell.Col] : 0;

    /// <summary>The id of the island a tile belongs to, or null for water.</summary>
    public int? IslandOf(Cell cell) => Contains(cell) ? _island[cell.Row, cell.Col] : null;

    /// <summary>The shortest walk over land, or null when the goal cannot be reached.</summary>
    public Route? FindRoute(Cell from, Cell to)
    {
        if (!IsWalkable(from) || !IsWalkable(to)) return null;
        var cameFrom = new Dictionary<Cell, Cell> { [from] = from };
        var open = new Queue<Cell>([from]);
        while (open.TryDequeue(out var cell))
        {
            if (cell == to)
            {
                var cells = new List<Cell>();
                for (var at = cell; at != from; at = cameFrom[at]) cells.Add(at);
                cells.Reverse();
                return new Route(cells);
            }
            foreach (var next in cell.Neighbours())
            {
                if (!IsWalkable(next) || !cameFrom.TryAdd(next, cell)) continue;
                open.Enqueue(next);
            }
        }
        return null;
    }

    /// <summary>
    /// The walk over land that costs least, stepping onto a tile costing <paramref name="cost"/> of it, or null when
    /// the goal cannot be reached. Of walks that cost the same, it takes the one the plain search would: with the same
    /// cost everywhere it is the shortest walk of <see cref="FindRoute(Cell, Cell)"/>.
    /// </summary>
    public Route? FindRoute(Cell from, Cell to, Func<Cell, int> cost)
    {
        if (!IsWalkable(from) || !IsWalkable(to)) return null;
        var best = new Dictionary<Cell, int> { [from] = 0 };
        var cameFrom = new Dictionary<Cell, Cell>();
        // ties go in the order the tiles were reached, as in the plain search
        var open = new PriorityQueue<Cell, (int Cost, long Order)>();
        long order = 0;
        open.Enqueue(from, (0, order++));
        while (open.TryDequeue(out var cell, out var priority))
        {
            if (priority.Cost > best[cell]) continue;
            if (cell == to)
            {
                var cells = new List<Cell>();
                for (var at = cell; at != from; at = cameFrom[at]) cells.Add(at);
                cells.Reverse();
                return new Route(cells);
            }
            foreach (var next in cell.Neighbours())
            {
                if (!IsWalkable(next)) continue;
                var total = priority.Cost + Math.Max(1, cost(next));
                if (best.TryGetValue(next, out var known) && known <= total) continue;
                best[next] = total;
                cameFrom[next] = cell;
                open.Enqueue(next, (total, order++));
            }
        }
        return null;
    }

    internal static bool IsLand(Ground ground) => ground is not (Ground.Water or Ground.Fishing);

    private bool Contains(Cell cell) => cell.Row >= 0 && cell.Row < Rows && cell.Col >= 0 && cell.Col < Cols;
}
