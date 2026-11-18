namespace Codery.Village.Core.Map;

/// <summary>
/// One tile of the hex grid. Pointy-top hexes in rows; every odd row is shifted half a tile to the right,
/// the same layout the level in the browser uses.
/// </summary>
public readonly record struct Cell(int Row, int Col)
{
    // east, south-east, south-west, west, north-west, north-east
    private static readonly (int Row, int Col)[] EvenRow = [(0, 1), (1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0)];
    private static readonly (int Row, int Col)[] OddRow = [(0, 1), (1, 1), (1, 0), (0, -1), (-1, 0), (-1, 1)];

    public IEnumerable<Cell> Neighbours()
    {
        foreach (var (row, col) in (Row & 1) == 0 ? EvenRow : OddRow)
            yield return new Cell(Row + row, Col + col);
    }

    /// <summary>Steps between two cells if nothing were in the way.</summary>
    public int DistanceTo(Cell other)
    {
        var dq = Col - (Row - (Row & 1)) / 2 - (other.Col - (other.Row - (other.Row & 1)) / 2);
        var dr = Row - other.Row;
        return (Math.Abs(dq) + Math.Abs(dr) + Math.Abs(dq + dr)) / 2;
    }

    public override string ToString() => $"({Row},{Col})";
}
