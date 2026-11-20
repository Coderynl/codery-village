using Codery.Village.Core.Map;

namespace Codery.Village.Modules.Work;

/// <summary>
/// What the land has left to give, tile by tile: a yield from <see cref="Full"/> down to 0. A tile has a yield for
/// each layer: the ground's own (what gatherers, farmers and the like take, after the settings of the ground) and each
/// named one (such as game, after its own settings), which run out and grow back apart. Work takes from the tiles it
/// is done on, and they grow back once a day. A tile below <see cref="Low"/> is bare in that layer; for the work that
/// picks a tile it counts as bare only from the quarter after the one it ran low in, so a worker's tile holds all
/// through a quarter. A tile nobody worked, or of a ground or layer that does not run out, is full. It knows no
/// clock: it is told the day and the quarter.
/// </summary>
public sealed class Nature
{
    public const int Full = 100;
    public const int Low = 20;

    private readonly IReadOnlyDictionary<Ground, NatureSettings> _settings;
    private readonly IReadOnlyDictionary<string, NatureSettings> _layers;
    // the tiles that are not full, by cell and layer (null: the ground's own)
    private readonly Dictionary<(Cell Cell, string? Layer), NatureTile> _tiles = [];

    /// <param name="settings">By ground, how a tile of it runs out and grows back.</param>
    /// <param name="day">The last day it grew; it grows next for the day after.</param>
    /// <param name="tiles">
    /// The tiles that are not full, as <see cref="Tiles"/> gave them; one of a ground or a layer that no longer runs
    /// out is full. A tile without a layer, as saved before there were layers, is of its ground's own.
    /// </param>
    /// <param name="layers">By name, how a named layer runs out and grows back.</param>
    public Nature(IReadOnlyDictionary<Ground, NatureSettings> settings, DateOnly day, IEnumerable<NatureTile>? tiles = null,
        IReadOnlyDictionary<string, NatureSettings>? layers = null)
    {
        _settings = settings;
        _layers = layers ?? new Dictionary<string, NatureSettings>();
        Day = day;
        foreach (var tile in tiles ?? [])
            if (SettingsOf(tile) is not null && tile.Yield < Full) _tiles[(tile.Cell, tile.Layer)] = tile with { Yield = Math.Max(0, tile.Yield) };
    }

    /// <summary>The last day it grew.</summary>
    public DateOnly Day { get; private set; }

    /// <summary>The tiles that are not full, by row and column, the ground's own layer first.</summary>
    public IReadOnlyList<NatureTile> Tiles => [.. _tiles.Values.OrderBy(tile => tile.Cell.Row).ThenBy(tile => tile.Cell.Col)
        .ThenBy(tile => tile.Layer is not null).ThenBy(tile => tile.Layer, StringComparer.Ordinal)];

    /// <summary>The tiles whose ground has run low now; a named layer run low leaves its tile as it is.</summary>
    public IEnumerable<Cell> Bare => _tiles.Values.Where(tile => tile.Layer is null && tile.Yield < Low).Select(tile => tile.Cell);

    /// <summary>What the tile has left in the layer: its ground's own, or the named one.</summary>
    public int YieldOf(Cell cell, string? layer = null) => _tiles.TryGetValue((cell, layer), out var tile) ? tile.Yield : Full;

    /// <summary>Whether a tile still gives in the quarter, in the layer: not bare, or run low only in this quarter.</summary>
    public bool Yields(Cell cell, long quarter, string? layer = null) =>
        !_tiles.TryGetValue((cell, layer), out var tile) || tile.Yield >= Low || quarter < tile.BareFrom;

    /// <summary>
    /// Takes a batch from a tile that is worked as <paramref name="ground"/>, in the quarter <paramref name="quarter"/>:
    /// from what the ground has left, or with <paramref name="layer"/> from that layer.
    /// </summary>
    public void Take(Cell cell, Ground ground, long quarter, string? layer = null)
    {
        var settings = layer is null ? _settings.GetValueOrDefault(ground) : _layers.GetValueOrDefault(layer);
        if (settings is null || settings.PerBatch <= 0) return;
        var before = YieldOf(cell, layer);
        var after = Math.Max(0, before - settings.PerBatch);
        var bareFrom = before >= Low || !_tiles.TryGetValue((cell, layer), out var known) ? quarter + 1 : known.BareFrom;
        _tiles[(cell, layer)] = new NatureTile(cell, after, ground, bareFrom, layer);
    }

    /// <summary>
    /// Grows every tile that is not full for each day since the last, up to and including <paramref name="today"/>:
    /// from nothing to full in <c>regrowDays</c> days, on the days of the months its ground or layer grows in.
    /// </summary>
    public void Grow(DateOnly today)
    {
        var from = Day.AddDays(1);
        // after a year away everything that grows at all is full
        if (today.DayNumber - from.DayNumber > 366) from = today.AddDays(-366);
        if (today > Day) Day = today;
        for (var day = from; day <= today && _tiles.Count > 0; day = day.AddDays(1))
            foreach (var (key, tile) in _tiles.ToList())
            {
                var settings = SettingsOf(tile)!;
                if (settings.GrowsInMonths is { } months && !months.Contains(day.Month)) continue;
                var grown = tile.Yield + (Full + settings.RegrowDays - 1) / settings.RegrowDays;
                if (grown >= Full) _tiles.Remove(key);
                else _tiles[key] = tile with { Yield = grown };
            }
    }

    private NatureSettings? SettingsOf(NatureTile tile) =>
        tile.Layer is null ? _settings.GetValueOrDefault(tile.Ground) : _layers.GetValueOrDefault(tile.Layer);
}

/// <summary>
/// A tile that is not full in a layer: its yield, the ground it is worked as, and the quarter (since 1970, in quarters
/// of an hour) from which it counts as bare when its yield is below <see cref="Nature.Low"/>. <see cref="Layer"/> is
/// null for what the ground itself has left, which grows back as that ground; else it names the layer, such as game.
/// </summary>
public sealed record NatureTile(Cell Cell, int Yield, Ground Ground, long BareFrom, string? Layer = null);
