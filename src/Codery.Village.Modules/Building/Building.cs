using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Modules.Contracts;
using Codery.Village.Modules.People;
using Activity = Codery.Village.Modules.Day.Activity;
using DayModule = Codery.Village.Modules.Day.Day;
using PeopleModule = Codery.Village.Modules.People.People;

namespace Codery.Village.Modules.Building;

/// <summary>
/// What stands and what goes up. A building is placed on free tiles of one island that its blueprint may stand
/// on, and goes up in the phases of its blueprint: a phase starts when the island's stock holds its cost, which
/// is taken in one go, and goes on while builders are at the site, each builder a crew-th of a tick per tick.
/// A site that stands still says why, once per reason. Each ordinary island starts with its fireplace and a hut
/// per household, Eddie with his house and workshop; all finished. Building gives the homes, as People moves households in and
/// out of them, the places of the day, and the buildings Work staffs. It also keeps the loose things a visitor
/// can move: a few at the start, and one by every house that is finished.
/// </summary>
public sealed class Building : IModule, IHousing, IPlaces, IStructures
{
    public const string Materials = "materials";
    public const string Builders = "builders";

    private const string Section = "building";
    private const string Built = "built";
    private static readonly string[] LogKinds = [Built, Waiting(Materials), Waiting(Builders)];

    private readonly List<Structure> _structures = [];
    // the structure on each tile it covers
    private readonly Dictionary<Cell, int> _covered = [];
    private readonly List<LooseThing> _things = [];
    private int _nextStructure;
    private int _nextThing;
    private ModuleContext? _village;
    private BuildingSettings _settings = null!;
    private CrewSettings _crew = null!;
    private Dictionary<string, Blueprint> _blueprints = [];
    private List<string> _kinds = [];
    // fishing ground, by the island of its shore
    private Dictionary<Cell, int> _shore = [];
    private Dictionary<int, Cell> _centres = [];
    // per island, the tile where it gathers while it has no place for it; nothing is built there
    private Dictionary<int, Cell> _squares = [];

    public string Name => Section;

    public int Version => 1;

    /// <summary>Everything that stands or goes up, by id.</summary>
    public IReadOnlyList<Structure> All => _structures.AsReadOnly();

    public IReadOnlyList<LooseThing> Things => _things.AsReadOnly();

    // ── placing ──

    /// <summary>
    /// Whether the blueprint fits there: every tile of its shape, turned <paramref name="rotation"/> sixths, lies
    /// on the island of the anchor, is a ground of its <c>on</c>, and is free of buildings, loose things and the
    /// island's gathering tile.
    /// </summary>
    public bool CanPlace(string blueprint, Cell anchor, int rotation)
    {
        if (_village is null || !_blueprints.TryGetValue(blueprint, out var plan) || IslandAt(anchor) is not { } island) return false;
        foreach (var cell in plan.Footprint.Cover(anchor, rotation))
            if (IslandAt(cell) != island || !plan.On.Contains(_village.Map.At(cell)) || Taken(cell)) return false;
        return true;
    }

    /// <summary>Starts a site there and gives its id; it begins on the next tick. Throws when it does not fit.</summary>
    public int Place(string blueprint, Cell anchor, int rotation)
    {
        if (!CanPlace(blueprint, anchor, rotation))
            throw new InvalidOperationException($"A {blueprint} does not fit at {anchor} turned {rotation}.");
        return Add(blueprint, anchor, rotation).Id;
    }

    /// <summary>
    /// The place nearest the island's <see cref="Heart"/> where the blueprint fits: the anchor and the turn, or
    /// null when it fits nowhere on the island.
    /// </summary>
    public (Cell Anchor, int Rotation)? FindPlace(string blueprint, int island)
    {
        if (_village is null || !_blueprints.TryGetValue(blueprint, out var plan)) return null;
        var heart = Heart(island);
        var turns = plan.Footprint.Offsets.Count > 1 ? 6 : 1;
        foreach (var anchor in Grounds(island).OrderBy(cell => cell.DistanceTo(heart)).ThenBy(cell => cell.Row).ThenBy(cell => cell.Col))
            for (var rotation = 0; rotation < turns; rotation++)
                if (CanPlace(blueprint, anchor, rotation)) return (anchor, rotation);
        return null;
    }

    /// <summary>The heart of the island: the land tile nearest the middle of its homes, or of its land while it has none.</summary>
    public Cell Heart(int island)
    {
        var homes = _structures.Where(structure => structure.Island == island && structure.Done && HomesIn(structure) > 0).ToList();
        if (homes.Count == 0) return _centres[island];
        return Nearest(Land(island), homes.Average(home => home.Anchor.Row), homes.Average(home => home.Anchor.Col));
    }

    /// <summary>The island a tile belongs to: its own, or for fishing ground the island of the shore; null for open water.</summary>
    public int? IslandAt(Cell cell) =>
        _village?.Map.IslandOf(cell) ?? (_shore.TryGetValue(cell, out var island) ? island : null);

    // ── loose things ──

    /// <summary>Puts a loose thing on another free land tile of its island; null when it is moved, else why not.</summary>
    public Refusal? Move(int thing, Cell to)
    {
        var index = _things.FindIndex(item => item.Id == thing);
        if (index < 0 || _village is null) return Refusal.NotFound;
        var item = _things[index];
        var map = _village.Map;
        if (!map.IsWalkable(to)) return Refusal.Water;
        if (map.IslandOf(to) != map.IslandOf(item.At)) return Refusal.OtherIsland;
        if (to == item.At) return null;
        if (_covered.ContainsKey(to) || _things.Any(other => other.At == to)) return Refusal.Taken;
        _things[index] = item with { At = to };
        return null;
    }

    // ── housing ──

    public int? FreeHome(int island) =>
        _structures.FirstOrDefault(structure => structure.Island == island && structure.Done && structure.Living.Count < HomesIn(structure))?.Id;

    public void MoveIn(int household, int home)
    {
        var dwelling = _structures.Find(structure => structure.Id == home)
            ?? throw new ArgumentException($"There is no building {home}.", nameof(home));
        if (dwelling.Living.Contains(household)) return;
        if (!dwelling.Done || dwelling.Living.Count >= HomesIn(dwelling))
            throw new InvalidOperationException($"Building {home} has no free home.");
        foreach (var structure in _structures) structure.Living.Remove(household);
        dwelling.Living.Add(household);
    }

    public bool HasHome(int household) => _structures.Any(structure => structure.Living.Contains(household));

    // ── places ──

    public Cell? HomeOf(int resident)
    {
        if (_village?.Find<PeopleModule>()?.All.FirstOrDefault(who => who.Id == resident) is not { Household: { } household }) return null;
        return _structures.Find(structure => structure.Living.Contains(household))?.Anchor;
    }

    public Cell? SchoolOn(int island) => Serving(island, _settings?.School);

    /// <summary>What serves the gathering label, else the island's fireplace, else a fixed tile of the island.</summary>
    public Cell? GatheringOn(int island) =>
        Serving(island, _settings?.Gathering) ?? Fireplace(island) ?? (_squares.TryGetValue(island, out var square) ? square : null);

    private Cell? Fireplace(int island) =>
        _settings?.Fireplace is { } fireplace
            ? _structures.Find(structure => structure.Island == island && structure.Done && structure.Blueprint == fireplace)?.Anchor
            : null;

    private Cell? Serving(int island, string? label) =>
        label is null ? null : _structures.Find(structure => structure.Island == island && structure.Done
            && _blueprints.TryGetValue(structure.Blueprint, out var plan) && plan.Serves == label)?.Anchor;

    // ── structures, as Work sees them ──

    public IReadOnlyList<StructureView> Standing() => [.. _structures.Where(structure => structure.Done).Select(View)];

    public IReadOnlyList<StructureView> Sites() => [.. _structures.Where(structure => !structure.Done).Select(View)];

    private static StructureView View(Structure structure) => new(structure.Id, structure.Blueprint, structure.Island, structure.Anchor);

    // ── the village ──

    public void Start(ModuleContext village)
    {
        Read(village);
        Clear();
        var people = village.Find<PeopleModule>();
        var stock = village.Find<IStock>();
        foreach (var island in village.Map.Islands)
        {
            if (island.Eddie)
            {
                foreach (var blueprint in _settings.Eddie)
                    if (FindPlace(blueprint, island.Id) is { } place) Finish(Add(blueprint, place.Anchor, place.Rotation));
                continue;
            }
            if (_settings.Fireplace is { } fireplace && FindPlace(fireplace, island.Id) is { } hearth)
                Finish(Add(fireplace, hearth.Anchor, hearth.Rotation));
            var households = people?.On(island.Id).Where(resident => resident.Household is not null).Select(resident => resident.Household).Distinct().Count() ?? 0;
            if (StartDwelling(island.Id, stock) is { } dwelling)
                for (var hut = 0; hut < Math.Min(households, _settings.MaxStartHuts); hut++)
                {
                    if (FindPlace(dwelling, island.Id) is not { } place) break;
                    Finish(Add(dwelling, place.Anchor, place.Rotation));
                }
            Scatter(village, island.Id, _squares[island.Id], _settings.ThingsAtStart);
        }
    }

    public void Step(ModuleContext village)
    {
        _village = village;
        Vacate(village);
        var sites = _structures.Where(structure => !structure.Done).ToList();
        if (sites.Count == 0) return;
        var present = Present(village, sites);
        var stock = village.Find<IStock>();
        foreach (var site in sites) Advance(village, site, present.GetValueOrDefault(site.Id), stock);
    }

    // a home whose last resident has left is free again
    private void Vacate(ModuleContext village)
    {
        var people = village.Find<PeopleModule>();
        foreach (var left in village.Received.OfType<ResidentLeft>())
        {
            if (left.Household is not { } household || people?.All.Any(resident => resident.Household == household) == true) continue;
            foreach (var structure in _structures) structure.Living.Remove(household);
        }
    }

    // per site, the builders of its island who are at work on its tile, and not on their way
    private Dictionary<int, int> Present(ModuleContext village, List<Structure> sites)
    {
        var present = new Dictionary<int, int>();
        if (village.Find<DayModule>() is not { } day || village.Find<IJobs>() is not { } jobs || village.Find<PeopleModule>() is not { } people) return present;
        var spots = new Dictionary<Cell, List<Structure>>();
        foreach (var site in sites)
        {
            var spot = Spot(site);
            if (!spots.TryGetValue(spot, out var here)) spots[spot] = here = [];
            here.Add(site);
        }
        foreach (var resident in people.All)
        {
            if (Doing(day, resident.Id) is not { Activity: Activity.Work, Walk: null } doing || !spots.TryGetValue(doing.Place, out var here)) continue;
            if (jobs.JobOf(resident.Id) is not { } job || job.Role != _crew.BuilderRole) continue;
            var site = here.Find(structure => structure.Id == job.Building) ?? here[0];
            if (site.Island == resident.Island) present[site.Id] = present.GetValueOrDefault(site.Id) + 1;
        }
        return present;
    }

    private static Day.Doing? Doing(DayModule day, int resident)
    {
        try
        {
            return day.Of(resident);
        }
        catch (KeyNotFoundException)
        {
            // he came this tick and has no plan yet
            return null;
        }
    }

    private void Advance(ModuleContext village, Structure site, int builders, IStock? stock)
    {
        if (!_blueprints.TryGetValue(site.Blueprint, out var plan)) return;
        var tick = village.Clocks.Tick;
        var phase = plan.Phases[site.Phase];
        if (!site.Paid)
        {
            if (stock is null ? phase.Cost.Count > 0 : !stock.TryTake(site.Island, phase.Cost))
            {
                Wait(village, site, Materials);
                return;
            }
            site.Paid = true;
            village.Publish(new PhaseStarted(tick, site.Id, phase.Id));
        }
        var working = Math.Min(builders, _crew.Crew);
        if (working == 0)
        {
            Wait(village, site, Builders);
            return;
        }
        site.Waiting = null;
        site.Units += working;
        site.Progress = site.Units / _crew.Crew;
        if (site.Units < phase.Ticks * _crew.Crew) return;

        site.Phase++;
        site.Units = 0;
        site.Progress = 0;
        site.Paid = false;
        if (!site.Done) return;
        village.Publish(new StructureDone(tick, site.Id));
        village.Log(new LogEntry(tick, Built, site.Blueprint, Id(site)));
        if (HomesIn(site) > 0) Scatter(village, site.Island, site.Anchor, _settings.ThingsPerHouse);
    }

    private static void Wait(ModuleContext village, Structure site, string reason)
    {
        site.Waiting = reason;
        if (!site.Told.Add(reason)) return;
        village.Publish(new ConstructionWaiting(village.Clocks.Tick, site.Id, reason));
        village.Log(new LogEntry(village.Clocks.Tick, Waiting(reason), site.Blueprint, Id(site)));
    }

    private static string Waiting(string reason) => "waiting-" + reason;

    private static string Id(Structure structure) => structure.Id.ToString(CultureInfo.InvariantCulture);

    // ── helpers ──

    private Structure Add(string blueprint, Cell anchor, int rotation, int? id = null)
    {
        var plan = _blueprints[blueprint];
        var structure = new Structure(id ?? _nextStructure++, blueprint, IslandAt(anchor)!.Value, anchor, rotation,
            [.. plan.Footprint.Cover(anchor, rotation)], plan.Phases.Count);
        _structures.Add(structure);
        foreach (var cell in structure.Cells) _covered[cell] = structure.Id;
        return structure;
    }

    private static void Finish(Structure structure) => structure.Phase = structure.Phases;

    /// <summary>Total cost of a blueprint's phases from <paramref name="from"/> on, by material.</summary>
    internal static Dictionary<string, int> CostFrom(Blueprint blueprint, int from)
    {
        var cost = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var phase = from; phase < blueprint.Phases.Count; phase++)
            foreach (var (material, amount) in blueprint.Phases[phase].Cost)
                cost[material] = cost.GetValueOrDefault(material) + amount;
        return cost;
    }

    // the first dwelling the island can pay for in full, else the first that fits
    private string? StartDwelling(int island, IStock? stock)
    {
        var dwellings = _blueprints.Values.Where(plan => plan.Homes > 0 && !_settings.Eddie.Contains(plan.Id) && FindPlace(plan.Id, island) is not null).ToList();
        return dwellings.FirstOrDefault(plan => stock is not null && CostFrom(plan, 0).All(item => stock.Count(island, item.Key) >= item.Value))?.Id
            ?? dwellings.FirstOrDefault()?.Id;
    }

    // a few loose things on the free land nearest the tile
    private void Scatter(ModuleContext village, int island, Cell near, int count)
    {
        if (_kinds.Count == 0 || count <= 0) return;
        var map = village.Map;
        var free = Land(island).Where(cell => map.At(cell) != Ground.Lot && !Taken(cell))
            .OrderBy(cell => cell.DistanceTo(near)).ThenBy(cell => cell.Row).ThenBy(cell => cell.Col).Take(count);
        foreach (var cell in free.ToList())
            _things.Add(new LooseThing(_nextThing++, _kinds[village.Random.Next(_kinds.Count)], cell, cell));
    }

    private bool Taken(Cell cell) =>
        _covered.ContainsKey(cell) || _things.Any(thing => thing.At == cell) || _squares.ContainsValue(cell);

    private int HomesIn(Structure structure) =>
        _blueprints.TryGetValue(structure.Blueprint, out var plan) ? plan.Homes ?? 0 : 0;

    // where the builders work: on the site, or for one on the water on the nearest land of its island
    private Cell Spot(Structure site)
    {
        var map = _village!.Map;
        if (map.IsWalkable(site.Anchor) && map.IslandOf(site.Anchor) == site.Island) return site.Anchor;
        return Land(site.Island).OrderBy(cell => cell.DistanceTo(site.Anchor)).ThenBy(cell => cell.Row).ThenBy(cell => cell.Col).First();
    }

    private IEnumerable<Cell> Land(int island) =>
        _village!.Map.Islands.Single(item => item.Id == island).Cells.Where(_village.Map.IsWalkable);

    // the land of the island and the fishing ground off its shore
    private IEnumerable<Cell> Grounds(int island) =>
        _village!.Map.Islands.Single(item => item.Id == island).Cells.Concat(_shore.Where(item => item.Value == island).Select(item => item.Key));

    private static Cell Nearest(IEnumerable<Cell> cells, double row, double col) =>
        cells.MinBy(cell => (cell.Row - row) * (cell.Row - row) + (cell.Col - col) * (cell.Col - col));

    private void Clear()
    {
        _structures.Clear();
        _covered.Clear();
        _things.Clear();
        _nextStructure = 0;
        _nextThing = 0;
    }

    private void Read(ModuleContext village)
    {
        _village = village;
        _settings = village.Data.Section<BuildingSettings>(Section);
        _crew = village.Data.Section<CrewSettings>("economy");
        var catalog = village.Data.Section<Catalog>("catalog");
        _blueprints = catalog.Blueprints.ToDictionary(blueprint => blueprint.Id);
        _kinds = [.. catalog.Things.Select(thing => thing.Id)];

        var community = _settings.Community;
        if (_crew.Crew <= 0 || _settings.MaxStartHuts < 0 || _settings.ThingsAtStart < 0 || _settings.ThingsPerHouse < 0
            || community.MaxSites < 0 || community.LowStockDays < 0 || community.DecideTicks <= 0)
            throw new InvalidDataException($"The data section '{Section}' or the crew in 'economy' has a number out of range.");
        foreach (var blueprint in _settings.Eddie.Where(blueprint => !_blueprints.ContainsKey(blueprint)))
            throw new InvalidDataException($"The data section '{Section}' gives Eddie the unknown blueprint '{blueprint}'.");
        if (_settings.Fireplace is { } fireplace && !_blueprints.ContainsKey(fireplace))
            throw new InvalidDataException($"The data section '{Section}' has the unknown fireplace '{fireplace}'.");
        var missing = LogKinds.Where(kind => !_settings.Log.ContainsKey(kind)).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"The data section '{Section}' has no log sentence for {string.Join(", ", missing)}.");

        var map = village.Map;
        _shore = [];
        for (var row = 0; row < map.Rows; row++)
            for (var col = 0; col < map.Cols; col++)
            {
                var cell = new Cell(row, col);
                if (map.At(cell) != Ground.Fishing) continue;
                foreach (var next in cell.Neighbours())
                    if (map.IslandOf(next) is { } island)
                    {
                        _shore[cell] = island;
                        break;
                    }
            }
        _centres = [];
        _squares = [];
        foreach (var island in map.Islands)
        {
            var land = Land(island.Id).ToList();
            var (row, col) = (land.Average(cell => cell.Row), land.Average(cell => cell.Col));
            _centres[island.Id] = Nearest(land, row, col);
            var open = land.Where(cell => map.At(cell) != Ground.Lot).ToList();
            _squares[island.Id] = Nearest(open.Count > 0 ? open : land, row, col);
        }
    }

    // ── saving ──

    public JsonNode Save() =>
        JsonSerializer.SerializeToNode(new SavedBuilding(
            _nextStructure,
            _nextThing,
            [.. _structures.Select(structure => new SavedStructure(structure.Id, structure.Blueprint, structure.Anchor, structure.Rotation,
                structure.Phase, structure.Units, structure.Paid, [.. structure.Living], [.. structure.Told], structure.Waiting))],
            [.. _things]), SaveJson.Options)!;

    public void Load(ModuleContext village, JsonNode data, int version)
    {
        Read(village);
        var saved = data.Deserialize<SavedBuilding>(SaveJson.Options) ?? throw new InvalidDataException("The building part of the save is empty.");
        Clear();
        foreach (var part in saved.Structures)
        {
            if (!_blueprints.ContainsKey(part.Blueprint) || IslandAt(part.Anchor) is null)
                throw new InvalidDataException($"The save has a {part.Blueprint} at {part.Anchor} that the data or the map does not know.");
            var structure = Add(part.Blueprint, part.Anchor, part.Rotation, part.Id);
            structure.Phase = part.Phase;
            structure.Units = part.Units;
            structure.Progress = part.Units / _crew.Crew;
            structure.Paid = part.Paid;
            structure.Waiting = part.Waiting;
            structure.Living.AddRange(part.Households);
            foreach (var reason in part.Told) structure.Told.Add(reason);
        }
        _things.AddRange(saved.Things);
        _nextStructure = saved.NextStructure;
        _nextThing = saved.NextThing;
    }

    private sealed record SavedBuilding(int NextStructure, int NextThing, IReadOnlyList<SavedStructure> Structures, IReadOnlyList<LooseThing> Things);

    private sealed record SavedStructure(int Id, string Blueprint, Cell Anchor, int Rotation, int Phase, int Units, bool Paid,
        IReadOnlyList<int> Households, IReadOnlyList<string> Told, string? Waiting = null);
}
