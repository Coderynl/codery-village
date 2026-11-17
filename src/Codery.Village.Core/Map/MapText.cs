using System.Globalization;

namespace Codery.Village.Core.Map;

/// <summary>
/// Reads a map from text. First the tiles, one character per tile and one line per row:
/// <c>~</c> water, <c>v</c> fishing ground, <c>.</c> land, <c>T</c> forest, <c>F</c> fertile ground,
/// <c>R</c> rock, <c>C</c> clay, <c>h</c> lot. Then one line per island, <c>island &lt;row&gt; &lt;col&gt; &lt;name&gt; [eddie]</c>,
/// naming the island that holds that tile. Lines starting with <c>#</c> explain and blank lines are skipped.
/// </summary>
public static class MapText
{
    public static IslandMap Parse(string text)
    {
        var rows = new List<(int Line, string Tiles)>();
        var names = new List<(int Line, string[] Words)>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("island ", StringComparison.Ordinal))
                names.Add((i + 1, line.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
            else if (names.Count > 0)
                throw Mistake(i + 1, "tiles come before the island lines");
            else
                rows.Add((i + 1, line));
        }
        if (rows.Count == 0) throw Mistake(1, "the map has no tiles");

        var ground = ReadTiles(rows);
        var (island, firsts) = FloodFill(ground);
        var named = new (string Name, bool Eddie)?[firsts.Count];
        foreach (var (line, words) in names) Name(line, words, ground, island, named);

        var islands = new List<Island>();
        for (var id = 0; id < firsts.Count; id++)
        {
            var (first, cells) = firsts[id];
            if (named[id] is not { } name) throw Mistake(rows[first.Row].Line, $"the island at {first} has no name");
            islands.Add(new Island(id, name.Name, cells, name.Eddie));
        }
        return new IslandMap(ground, island, islands);
    }

    private static Ground[,] ReadTiles(List<(int Line, string Tiles)> rows)
    {
        var cols = rows[0].Tiles.Length;
        var ground = new Ground[rows.Count, cols];
        for (var row = 0; row < rows.Count; row++)
        {
            var (line, tiles) = rows[row];
            if (tiles.Length != cols) throw Mistake(line, $"{tiles.Length} tiles where the first row has {cols}");
            for (var col = 0; col < cols; col++)
                ground[row, col] = tiles[col] switch
                {
                    '~' => Ground.Water,
                    'v' => Ground.Fishing,
                    '.' => Ground.Land,
                    'T' => Ground.Forest,
                    'F' => Ground.Fertile,
                    'R' => Ground.Rock,
                    'C' => Ground.Clay,
                    'h' => Ground.Lot,
                    var other => throw Mistake(line, $"unknown tile '{other}'"),
                };
        }
        return ground;
    }

    /// <summary>Gives every piece of connected land an id, in the order their first tile appears.</summary>
    private static (int?[,] Island, List<(Cell First, List<Cell> Cells)> Islands) FloodFill(Ground[,] ground)
    {
        var (rows, cols) = (ground.GetLength(0), ground.GetLength(1));
        var island = new int?[rows, cols];
        var islands = new List<(Cell, List<Cell>)>();
        for (var row = 0; row < rows; row++)
        for (var col = 0; col < cols; col++)
        {
            if (!IslandMap.IsLand(ground[row, col]) || island[row, col] is not null) continue;
            var id = islands.Count;
            var first = new Cell(row, col);
            var cells = new List<Cell>();
            var open = new Queue<Cell>([first]);
            island[row, col] = id;
            while (open.TryDequeue(out var cell))
            {
                cells.Add(cell);
                foreach (var next in cell.Neighbours())
                {
                    if (next.Row < 0 || next.Row >= rows || next.Col < 0 || next.Col >= cols) continue;
                    if (!IslandMap.IsLand(ground[next.Row, next.Col]) || island[next.Row, next.Col] is not null) continue;
                    island[next.Row, next.Col] = id;
                    open.Enqueue(next);
                }
            }
            islands.Add((first, cells));
        }
        return (island, islands);
    }

    private static void Name(int line, string[] words, Ground[,] ground, int?[,] island, (string, bool)?[] named)
    {
        if (words.Length is < 4 or > 5) throw Mistake(line, "expected 'island <row> <col> <name> [eddie]'");
        if (words.Length == 5 && words[4] != "eddie") throw Mistake(line, $"unknown mark '{words[4]}'");
        if (!int.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out var row)
            || !int.TryParse(words[2], NumberStyles.None, CultureInfo.InvariantCulture, out var col)
            || row >= ground.GetLength(0) || col >= ground.GetLength(1))
            throw Mistake(line, $"no tile at {words[1]} {words[2]}");
        if (island[row, col] is not { } id) throw Mistake(line, $"the tile at ({row},{col}) is water");
        if (named[id] is not null) throw Mistake(line, $"the island at ({row},{col}) already has a name");
        named[id] = (words[3], words.Length == 5);
    }

    private static FormatException Mistake(int line, string what) => new($"Map, line {line}: {what}.");
}
