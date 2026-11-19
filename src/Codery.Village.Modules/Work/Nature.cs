using Codery.Village.Core.Map;

namespace Codery.Village.Modules.Work;

/// <summary>
/// What the ground has left to give, tile by tile: a yield from <see cref="Full"/> down to 0. Work takes from the
/// tiles it is done on, after the settings of their ground, and they grow back once a day. A tile below
/// <see cref="Low"/> is bare; for the work that picks a tile it counts as bare only from the quarter after the one it
/// ran low in, so a worker's tile holds all through a quarter. A tile nobody worked, or of a ground that does not run
/// out, is full. It knows no clock: it is told the day and the quarter.
/// </summary>
public sealed class Nature
{
    public const int Full = 100;
    public const int Low = 20;

    private readonly IReadOnlyDictionary<Ground, NatureSettings> _settings;
    // the tiles that are not full
    private readonly Dictionary<Cell, NatureTile> _tiles = [];

    /// <param name="settings">By ground, how a tile of it runs out and grows back.</param>
    /// <param name="day">The last day it grew; it grows next for the day after.</param>
    /// <param name="tiles">The tiles that are not full, as <see cref="Tiles"/> gave them; one of a ground that no longer runs out is full.</param>
    public Nature(IReadOnlyDictionary<Ground, NatureSettings> settings, DateOnly day, IEnumerable<NatureTile>? tiles = null)
    {
        _settings = settings;
        Day = day;
        foreach (var tile in tiles ?? [])
            if (_settings.ContainsKey(tile.Ground) && tile.Yield < Full) _tiles[tile.Cell] = tile with { Yield = Math.Max(0, tile.Yield) };
    }

    /// <summary>The last day it grew.</summary>
    public DateOnly Day { get; private set; }

    /// <summary>The tiles that are not full, by row and column.</summary>
    public IReadOnlyList<NatureTile> Tiles => [.. _tiles.Values.OrderBy(tile => tile.Cell.Row).ThenBy(tile => tile.Cell.Col)];

    /// <summary>The tiles that are bare now.</summary>
    public IEnumerable<Cell> Bare => _tiles.Values.Where(tile => tile.Yield < Low).Select(tile => tile.Cell);

    public int YieldOf(Cell cell) => _tiles.TryGetValue(cell, out var tile) ? tile.Yield : Full;

    /// <summary>Whether a tile still gives in the quarter: not bare, or run low only in this quarter.</summary>
    public bool Yields(Cell cell, long quarter) => !_tiles.TryGetValue(cell, out var tile) || tile.Yield >= Low || quarter < tile.BareFrom;

    /// <summary>Takes a batch from a tile that is worked as <paramref name="ground"/>, in the quarter <paramref name="quarter"/>.</summary>
    public void Take(Cell cell, Ground ground, long quarter)
    {
        if (!_settings.TryGetValue(ground, out var settings) || settings.PerBatch <= 0) return;
        var before = YieldOf(cell);
        var after = Math.Max(0, before - settings.PerBatch);
        var bareFrom = before >= Low || !_tiles.TryGetValue(cell, out var known) ? quarter + 1 : known.BareFrom;
        _tiles[cell] = new NatureTile(cell, after, ground, bareFrom);
    }

    /// <summary>
    /// Grows every tile that is not full for each day since the last, up to and including <paramref name="today"/>:
    /// from nothing to full in <c>regrowDays</c> days, on the days of the months its ground grows in.
    /// </summary>
    public void Grow(DateOnly today)
    {
        var from = Day.AddDays(1);
        // after a year away everything that grows at all is full
        if (today.DayNumber - from.DayNumber > 366) from = today.AddDays(-366);
        if (today > Day) Day = today;
        for (var day = from; day <= today && _tiles.Count > 0; day = day.AddDays(1))
            foreach (var tile in _tiles.Values.ToList())
            {
                var settings = _settings[tile.Ground];
                if (settings.GrowsInMonths is { } months && !months.Contains(day.Month)) continue;
                var grown = tile.Yield + (Full + settings.RegrowDays - 1) / settings.RegrowDays;
                if (grown >= Full) _tiles.Remove(tile.Cell);
                else _tiles[tile.Cell] = tile with { Yield = grown };
            }
    }
}

/// <summary>
/// A tile that is not full: its yield, the ground it grows back as, and the quarter (since 1970, in quarters of an hour)
/// from which it counts as bare when its yield is below <see cref="Nature.Low"/>.
/// </summary>
public sealed record NatureTile(Cell Cell, int Yield, Ground Ground, long BareFrom);
