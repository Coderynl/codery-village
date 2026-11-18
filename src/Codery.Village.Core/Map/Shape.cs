namespace Codery.Village.Core.Map;

/// <summary>
/// The tiles a building covers, relative to its anchor. Offsets are axial coordinates (Row is r, Col is q),
/// not odd-r offsets, so a shape keeps its form on even and odd rows and turns in steps of 60 degrees.
/// </summary>
public readonly record struct Shape(IReadOnlyList<Cell> Offsets)
{
    /// <summary>One tile: the anchor itself.</summary>
    public static Shape Single { get; } = new([new Cell(0, 0)]);

    /// <summary>The tiles covered with the anchor at <paramref name="anchor"/>, turned <paramref name="rotation"/> sixths clockwise.</summary>
    public IEnumerable<Cell> Cover(Cell anchor, int rotation)
    {
        var turns = (rotation % 6 + 6) % 6;
        var q0 = anchor.Col - (anchor.Row - (anchor.Row & 1)) / 2;
        var r0 = anchor.Row;
        foreach (var offset in Offsets)
        {
            var (q, r) = (offset.Col, offset.Row);
            for (var i = 0; i < turns; i++) (q, r) = (-r, q + r);
            var row = r0 + r;
            yield return new Cell(row, q0 + q + (row - (row & 1)) / 2);
        }
    }
}
