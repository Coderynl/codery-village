using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;
using Codery.Village.Modules.Contracts;
using Codery.Village.Modules.People;
using Codery.Village.Modules.Day;
using Activity = Codery.Village.Modules.Day.Activity;
using DayModule = Codery.Village.Modules.Day.Day;
using PeopleModule = Codery.Village.Modules.People.People;

namespace Codery.Village.Modules.Building;

/// <summary>
/// What stands and what goes up. A building is placed on free tiles of one island that its blueprint may stand
/// on, and goes up in the phases of its blueprint: a phase starts when the island's stock holds its cost, which
/// is taken in one go. The builders at the site then fetch it from the store where the island gathers, and once
/// one is back with it the phase goes on while builders are at the site, each builder a crew-th of a tick per tick
/// and each helper (a resident who spends his free time there) a share of a builder, up to a full crew.
/// A site that stands still says why, once per reason. Each ordinary island starts with its fireplace, its start
/// workplaces and a hut per household, Eddie with his house and workshop; all finished. Building gives the homes,
/// as People moves households in and out of them, the places of the day, and the buildings Work staffs. It also keeps the loose things a visitor
/// can move: a few at the start, and one by every house that is finished.
/// </summary>
public sealed class Building : IModule, IHousing, IPlaces, IStructures
{
    public const string Materials = "materials";
    public const string Builders = "builders";

    private const string Section = "building";
    private const string Built = "built";
    private const string PutBackKind = "put-back";
    // the work of one builder in one tick, in units: a helper does a share of it
    private const int Full = 100;
    private static readonly string[] LogKinds = [Built, Waiting(Materials), Waiting(Builders), PutBackKind];

    private readonly List<Structure> _structures = [];
    // the structure on each tile it covers
    private readonly Dictionary<Cell, int> _covered = [];
    private readonly List<LooseThing> _things = [];
    // per moved thing that stands in someone's way, the tick that was first seen
    private readonly SortedDictionary<int, long> _inTheWay = [];
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

    // 2: the work of a site in hundredths of a builder
    public int Version => 2;

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

    /// <summary>
    /// Puts a loose thing on another free land tile of its island; null when it is moved, else why not. A lot and
    /// the tile where the island gathers count as taken: a visitor does not block a home or the square. The tile it
    /// stands on counts as taken too: putting it down where it is moves nothing. A moved thing that stands in
    /// someone's way goes back within <c>putBackMinutes</c>.
    /// </summary>
    public Refusal? Move(int thing, Cell to)
    {
        var index = _things.FindIndex(item => item.Id == thing);
        if (index < 0 || _village is null) return Refusal.NotFound;
        var item = _things[index];
        var map = _village.Map;
        if (!map.IsWalkable(to)) return Refusal.Water;
        if (map.IslandOf(to) != map.IslandOf(item.At)) return Refusal.OtherIsland;
        if (to == item.At) return Refusal.Taken;
        if (map.At(to) == Ground.Lot || _squares.ContainsValue(to)) return Refusal.Taken;
        if (_covered.ContainsKey(to) || _things.Any(other => other.At == to)) return Refusal.Taken;
        _things[index] = item with { At = to };
        return null;
    }

    /// <summary>The resident on the tile's island who is awake and stands nearest to it, or null when everyone there sleeps.</summary>
    public int? NearestAwake(Cell near)
    {
        if (_village is not { } village || village.Map.IslandOf(near) is not { } island
            || village.Find<DayModule>() is not { } day || village.Find<PeopleModule>() is not { } people) return null;
        var tick = village.Clocks.Tick;
        int? nearest = null;
        var best = int.MaxValue;
        foreach (var resident in people.All)
        {
            if (resident.Island != island || Doing(day, resident.Id) is not { } doing || doing.Activity == Activity.Sleep) continue;
            var distance = Walker.Position(doing, tick).DistanceTo(near);
            if (distance >= best) continue;
            best = distance;
            nearest = resident.Id;
        }
        return nearest;
    }

    // a moved thing in someone's way: on a tile he walks over or goes to. After putBackMinutes the nearest one
    // awake puts it back where it belongs, or on the nearest free tile when that is taken now
    private void PutBack(ModuleContext village)
    {
        foreach (var id in _inTheWay.Keys.Where(id => _things.Find(thing => thing.Id == id) is not { } thing || thing.At == thing.Home).ToList())
            _inTheWay.Remove(id);
        if (_things.All(thing => thing.At == thing.Home) || village.Find<DayModule>() is not { } day || village.Find<PeopleModule>() is not { } people) return;

        var tick = village.Clocks.Tick;
        var ways = new HashSet<Cell>();
        foreach (var resident in people.All)
        {
            if (Doing(day, resident.Id) is not { } doing) continue;
            ways.Add(doing.Place);
            if (doing.Walk is { } walk) ways.UnionWith(walk.Cells);
        }
        var after = (long)_settings.PutBackMinutes * 60 * Clocks.TicksPerSecond;
        for (var i = 0; i < _things.Count; i++)
        {
            var thing = _things[i];
            if (thing.At == thing.Home) continue;
            if (!_inTheWay.TryGetValue(thing.Id, out var since))
            {
                if (ways.Contains(thing.At) || village.Map.At(thing.At) == Ground.Lot) _inTheWay[thing.Id] = tick;
                continue;
            }
            if (tick - since < after || NearestAwake(thing.At) is not { } who) continue;
            var to = Free(thing.Home, thing.Id) ? thing.Home : Aside(village, thing, ways);
            _things[i] = thing with { At = to };
            _inTheWay.Remove(thing.Id);
            village.Log(new LogEntry(tick, PutBackKind, people.Get(who).Name, thing.Kind));
        }
    }

    // the free tile nearest the thing's own place that is in nobody's way
    private Cell Aside(ModuleContext village, LooseThing thing, HashSet<Cell> ways) =>
        Land(IslandAt(thing.Home)!.Value)
            .Where(cell => village.Map.At(cell) != Ground.Lot && !ways.Contains(cell) && Free(cell, thing.Id))
            .OrderBy(cell => cell.DistanceTo(thing.Home)).ThenBy(cell => cell.Row).ThenBy(cell => cell.Col)
            .Select(cell => (Cell?)cell).FirstOrDefault() ?? thing.Home;

    // nothing stands on the tile but the thing itself
    private bool Free(Cell cell, int thing) =>
        !_covered.ContainsKey(cell) && !_squares.ContainsValue(cell) && _things.All(other => other.Id == thing || other.At != cell);

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

    /// <summary>Where a helper works: the first site of the island that does not wait for materials, or null.</summary>
    public Cell? HelpOn(int island) =>
        _village is null ? null : _structures.Find(structure => structure.Island == island && !structure.Done && structure.Waiting != Materials) is { } site ? Spot(site) : null;

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
            if (_settings.StartWorkplaces?.GetValueOrDefault(island.Name) is { } workplaces)
                foreach (var workplace in workplaces)
                    if (FindPlace(workplace, island.Id) is { } place) Finish(Add(workplace, place.Anchor, place.Rotation));
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
        PutBack(village);
        var sites = _structures.Where(structure => !structure.Done).ToList();
        if (sites.Count == 0) return;
        Delivered(village, sites);
        var present = Present(village, sites);
        var stock = village.Find<IStock>();
        foreach (var site in sites) Advance(village, site, present.GetValueOrDefault(site.Id) ?? [], stock);
    }

    // a builder back from the store with what the phase costs: the site has it
    private void Delivered(ModuleContext village, List<Structure> sites)
    {
        foreach (var back in village.Received.OfType<Returned>())
        {
            if (back.Errand != Activity.Fetch) continue;
            foreach (var site in sites)
                if (site.Paid && Spot(site) == back.At && site.Island == IslandAt(back.At)) site.Delivered = true;
        }
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

    // per site, who is at work on its tile and not on his way, with the share of a builder he does: the builders of
    // its island, and the helpers
    private Dictionary<int, List<(int Resident, int Share)>?> Present(ModuleContext village, List<Structure> sites)
    {
        var present = new Dictionary<int, List<(int Resident, int Share)>?>();
        if (village.Find<DayModule>() is not { } day || village.Find<IJobs>() is not { } jobs || village.Find<PeopleModule>() is not { } people) return present;
        var spots = new Dictionary<Cell, List<Structure>>();
        foreach (var site in sites)
        {
            var spot = Spot(site);
            if (!spots.TryGetValue(spot, out var here)) spots[spot] = here = [];
            here.Add(site);
        }
        var helping = (int)Math.Round(Full * _settings.HelpShare, MidpointRounding.AwayFromZero);
        foreach (var resident in people.All)
        {
            if (Doing(day, resident.Id) is not { Activity: Activity.Work or Activity.Help, Walk: null } doing || !spots.TryGetValue(doing.Place, out var here)) continue;
            Structure site;
            int share;
            if (doing.Activity == Activity.Help) (site, share) = (here[0], helping);
            else if (jobs.JobOf(resident.Id) is { } job && job.Role == _crew.BuilderRole) (site, share) = (here.Find(structure => structure.Id == job.Building) ?? here[0], Full);
            else continue;
            if (site.Island != resident.Island) continue;
            if (!present.TryGetValue(site.Id, out var crew) || crew is null) present[site.Id] = crew = [];
            crew.Add((resident.Id, share));
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

    private void Advance(ModuleContext village, Structure site, List<(int Resident, int Share)> crew, IStock? stock)
    {
        if (!_blueprints.TryGetValue(site.Blueprint, out var plan)) return;
        var tick = village.Clocks.Tick;
        var phase = plan.Phases[site.Phase];
        if (!site.Paid)
        {
            // where the most of what it costs lies, before it is taken from the stores nearest the site
            var spot = Spot(site);
            var source = phase.Cost.Count > 0 && stock is not null ? stock.SourceOf(site.Island, Most(phase.Cost), spot) : null;
            if (stock is null ? phase.Cost.Count > 0 : !stock.TryTake(site.Island, phase.Cost, spot))
            {
                Wait(village, site, Materials);
                return;
            }
            site.Paid = true;
            site.Source = source;
            site.Delivered = phase.Cost.Count == 0;
            village.Publish(new PhaseStarted(tick, site.Id, phase.Id));
        }
        // who is back from fetching it, in this very tick, has brought it
        if (!site.Delivered && village.Find<DayModule>() is { } day
            && crew.Any(member => day.BackFrom(member.Resident) is { Kind: Activity.Fetch })) site.Delivered = true;
        if (!site.Delivered)
        {
            if (Fetch(village, site, phase, crew)) return;
            // nobody to fetch it, or nowhere to fetch it from
            if (crew.Count == 0)
            {
                Wait(village, site, Builders);
                return;
            }
            site.Delivered = true;
        }
        var working = Math.Min(crew.Sum(member => member.Share), _crew.Crew * Full);
        if (working == 0)
        {
            Wait(village, site, Builders);
            return;
        }
        site.Waiting = null;
        site.Units += working;
        site.Progress = site.Units / (_crew.Crew * Full);
        if (site.Units < phase.Ticks * _crew.Crew * Full) return;

        site.Phase++;
        site.Units = 0;
        site.Progress = 0;
        site.Paid = false;
        site.Delivered = false;
        site.Source = null;
        if (!site.Done) return;
        village.Publish(new StructureDone(tick, site.Id));
        village.Log(new LogEntry(tick, Built, site.Blueprint, Id(site)));
        if (HomesIn(site) > 0) Scatter(village, site.Island, site.Anchor, _settings.ThingsPerHouse);
    }

    // who is at the site goes for the most of what the phase costs to the store it was taken from, the nearest that
    // had it (in a save from before there were stores: where the island gathers); true while someone is on his way
    private bool Fetch(ModuleContext village, Structure site, Phase phase, List<(int Resident, int Share)> crew)
    {
        if (village.Find<DayModule>() is not { } day || (site.Source ?? GatheringOn(site.Island)) is not { } store) return false;
        var spot = Spot(site);
        // a store at the site: it is here already
        if (store == spot) return false;
        var material = Most(phase.Cost);
        var sent = false;
        foreach (var (resident, _) in crew) sent |= day.Send(resident, new Errand(Activity.Fetch, store, null, material));
        var fetching = sent || village.Find<PeopleModule>()?.All.Any(resident =>
            day.TripOf(resident.Id) is { Errand.Kind: Activity.Fetch } trip && trip.Home == spot && resident.Island == site.Island) == true;
        if (fetching) site.Waiting = null;
        return fetching;
    }

    // the material there is the most of; of equal amounts the first by name
    private static string Most(IReadOnlyDictionary<string, int> cost) =>
        cost.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.Ordinal).First().Key;

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
        _inTheWay.Clear();
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
        if (_crew.Crew <= 0 || _settings.PutBackMinutes < 1 || _settings.HelpShare is < 0 or > 1 || _settings.MaxStartHuts < 0 || _settings.ThingsAtStart < 0 || _settings.ThingsPerHouse < 0
            || community.MaxSites < 0 || community.LowStockDays < 0 || community.DecideTicks <= 0)
            throw new InvalidDataException($"The data section '{Section}' or the crew in 'economy' has a number out of range.");
        foreach (var blueprint in _settings.Eddie.Where(blueprint => !_blueprints.ContainsKey(blueprint)))
            throw new InvalidDataException($"The data section '{Section}' gives Eddie the unknown blueprint '{blueprint}'.");
        if (_settings.Fireplace is { } fireplace && !_blueprints.ContainsKey(fireplace))
            throw new InvalidDataException($"The data section '{Section}' has the unknown fireplace '{fireplace}'.");
        foreach (var workplace in (_settings.StartWorkplaces ?? new Dictionary<string, IReadOnlyList<string>>()).Values.SelectMany(names => names).Where(name => !_blueprints.ContainsKey(name)))
            throw new InvalidDataException($"The data section '{Section}' has the unknown start workplace '{workplace}'.");
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
                structure.Phase, structure.Units, structure.Paid, [.. structure.Living], [.. structure.Told], structure.Waiting, structure.Delivered, structure.Source))],
            [.. _things],
            _inTheWay.Count > 0 ? _inTheWay : null), SaveJson.Options)!;

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
            // version 1 counted the work in builder ticks
            structure.Units = version < 2 ? part.Units * Full : part.Units;
            structure.Progress = structure.Units / (_crew.Crew * Full);
            structure.Paid = part.Paid;
            structure.Delivered = part.Delivered;
            structure.Source = part.Source;
            structure.Waiting = part.Waiting;
            structure.Living.AddRange(part.Households);
            foreach (var reason in part.Told) structure.Told.Add(reason);
        }
        _things.AddRange(saved.Things);
        foreach (var (thing, since) in saved.InTheWay ?? new Dictionary<int, long>()) _inTheWay[thing] = since;
        _nextStructure = saved.NextStructure;
        _nextThing = saved.NextThing;
    }

    private sealed record SavedBuilding(int NextStructure, int NextThing, IReadOnlyList<SavedStructure> Structures, IReadOnlyList<LooseThing> Things,
        IReadOnlyDictionary<int, long>? InTheWay = null);

    private sealed record SavedStructure(int Id, string Blueprint, Cell Anchor, int Rotation, int Phase, int Units, bool Paid,
        IReadOnlyList<int> Households, IReadOnlyList<string> Told, string? Waiting = null, bool Delivered = false, Cell? Source = null);
}
