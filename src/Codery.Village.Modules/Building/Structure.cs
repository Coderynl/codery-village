using Codery.Village.Core.Map;

namespace Codery.Village.Modules.Building;

/// <summary>
/// Something built, or going up: a site until its last phase is done. A dwelling holds up to its blueprint's
/// <c>homes</c> households.
/// </summary>
public sealed class Structure
{
    private readonly List<int> _households = [];

    internal Structure(int id, string blueprint, int island, Cell anchor, int rotation, IReadOnlyList<Cell> cells, int phases)
    {
        Id = id;
        Blueprint = blueprint;
        Island = island;
        Anchor = anchor;
        Rotation = rotation;
        Cells = cells;
        Phases = phases;
    }

    public int Id { get; }

    public string Blueprint { get; }

    /// <summary>The island it belongs to; for one on fishing ground, the island of the shore.</summary>
    public int Island { get; }

    public Cell Anchor { get; }

    /// <summary>Sixths of a turn, clockwise.</summary>
    public int Rotation { get; }

    /// <summary>The tiles it covers, the anchor first.</summary>
    public IReadOnlyList<Cell> Cells { get; }

    /// <summary>The index of the phase going on; equal to <see cref="Phases"/> when it is done.</summary>
    public int Phase { get; internal set; }

    /// <summary>How many phases its blueprint has.</summary>
    public int Phases { get; }

    /// <summary>Ticks of a full crew done in the phase going on.</summary>
    public int Progress { get; internal set; }

    public bool Done => Phase >= Phases;

    /// <summary>Why the site stands still now, <c>materials</c> or <c>builders</c>; null while it goes on or when it is done.</summary>
    public string? Waiting { get; internal set; }

    /// <summary>The household that lives here, the first of them in a dwelling of more homes; null when nobody does.</summary>
    public int? Household => _households.Count > 0 ? _households[0] : null;

    /// <summary>The households that live here, in the order they came.</summary>
    public IReadOnlyList<int> Households => _households.AsReadOnly();

    // the work done in the phase, in builder ticks: a full crew adds the crew every tick
    internal int Units { get; set; }

    // the cost of the phase going on has been taken from the stock
    internal bool Paid { get; set; }

    // the reasons it has been told to wait for
    internal SortedSet<string> Told { get; } = new(StringComparer.Ordinal);

    internal List<int> Living => _households;
}

/// <summary>Something loose a visitor can pick up and put down: a bench, a crate, a sapling. <paramref name="Home"/> is where it belongs.</summary>
public sealed record LooseThing(int Id, string Kind, Cell At, Cell Home);

/// <summary>Why a loose thing was not moved.</summary>
public enum Refusal { NotFound, NotMovable, Water, Taken, OtherIsland }
