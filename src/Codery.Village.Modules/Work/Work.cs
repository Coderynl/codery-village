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
using Activity = Codery.Village.Modules.Day.Activity;
using Errand = Codery.Village.Modules.Day.Errand;
using DayModule = Codery.Village.Modules.Day.Day;

namespace Codery.Village.Modules.Work;

/// <summary>
/// Work and money, in their simplest form. A building that stands offers the jobs of its blueprint and each
/// ordinary island has places for its own builders; a working resident without a job takes the free place on
/// his island that fits his interests best, the first places of every workplace before the second; while his island is
/// short of a need (less in stock than <c>shortDays</c> days of what its households buy), a place that makes it goes
/// before any other. Once a day, at the start of the day shift, every workplace without a worker takes one from the
/// workplace of its island with the most, if that one has two or more and makes nothing the island is short of: the
/// worker who fits least there, of equals the newest in the job, who takes the new place as a new hire would; when no
/// workplace can spare one, a builder does, as long as the island keeps one. A builder without a
/// site helps out on the land of a workplace (<see cref="Help"/>), paid but adding nothing; with nothing to help he has
/// no job that quarter. A workplace makes its recipe while someone works there, into its own
/// store (<see cref="Stores"/>); what goes in, one of its workers fetches first at the store nearest that has it. A
/// recipe that fells takes a tree nearby, which grows back. After every batch the workers out on its area bring it to
/// the workplace, and go back. What an island starts with lies at the workplaces that make it, the rest where the
/// island gathers. The treasury of the island pays
/// a wage for every tick on the job; an outdoor job with a recipe is done out on its work area, the ground of its blueprint
/// around the workplace (for one on the water, the shore by it), each worker on a tile of his own for a few quarters,
/// and walking from one tile to the next is work too. Such an area runs out where it is worked: every batch takes from
/// the tiles its workers stood on (<see cref="Nature"/>, after <c>nature</c> in the data), a worker moves on from a tile
/// gone bare at the next quarter, a workplace whose whole area is bare stands still with its workers at it, and the tiles
/// grow back once a day. Once a calendar day, at 00:00, the island settles its day: the start
/// subsidy comes in, the upkeep of its buildings goes out, children, students and the retired get their money, and who is
/// of working age the support, less what he earned that day (so a new worker who has had no wage yet, or one whose job
/// rests at the weekend, is not left without),
/// every household buys food and heat from the stores nearest where the island gathers, and every resident spends after his thrift.
/// With <c>fetchFood</c>, someone grown of every household that bought food walks to the store nearest home that has the food it
/// bought most and carries it home, once that day: the first who comes home, awake, from <c>fetchFoodFrom</c> on and never in
/// the night of the day section (the day shift at 17:30, the early shift at noon). A fetch the next block cuts short is not
/// done again that day, and with the food nowhere in store nobody walks. The purchase is booked at midnight all the same, also
/// for a household with nobody to send. Who leaves the
/// village takes his money with him; the island books it the tick after. Eddie works
/// in his workshop for nothing: he is outside the economy.
/// </summary>
public sealed class Work : IModule, IJobs, IStock
{
    private const string Section = "economy";
    private const long TicksPerHour = Clocks.TicksPerSecond * 3600L;
    private const long QuarterSeconds = 15 * 60;
    // the need whose purchase a household fetches
    private const string Food = "food";
    // the block of a rhythm in which a shift works
    private const string WorkBlock = "work";

    private readonly Dictionary<int, Treasury> _treasuries = [];
    // per island, its stores
    private readonly Dictionary<int, Stores> _stores = [];
    // what lies where the island gathers is to be spread over the workplaces that make it, at the next step: after
    // the start, and after loading a save from when an island had one stock
    private bool _spread;
    // who works where, by resident
    private readonly SortedDictionary<int, Seat> _jobs = [];
    // per worker, the tick he took the job he has
    private readonly Dictionary<int, long> _since = [];
    // the calendar day workers last moved to the workplaces without one, and the time of day they do
    private DateOnly _moved;
    private TimeOnly _moveAt;
    // per worker, the wage earned that is not yet a whole cent, in 1/TicksPerHour cents
    private readonly Dictionary<int, long> _owed = [];
    // per worker, the wage paid since the last settling of the day, in cents
    private readonly SortedDictionary<int, long> _earned = [];
    // places nobody has, with the tick they came free
    private readonly Dictionary<Seat, long> _vacant = [];
    // per island, the wages paid since the last Paid
    private readonly Dictionary<int, long> _wages = [];
    // per workplace with a batch in hand, the ticks worked on it
    private readonly SortedDictionary<int, int> _batches = [];
    // felled forest, with the tick it is forest again
    private readonly Dictionary<Cell, long> _felled = [];
    // per island, how the households did at the last settling of the day
    private readonly Dictionary<int, Shopping> _shopping = [];
    // per household that bought food at the last settling and has not been to fetch it, the food it bought most
    private readonly SortedDictionary<int, string> _fetch = [];
    // the quarter the households last looked for someone to fetch their food
    private long _fetchQuarter;
    // the grown who were at home and awake (or out on an errand from there) as that quarter began
    private SortedSet<int> _atHome = [];
    private TimeOnly _fetchFrom;
    // the night of the day section, when nobody goes for food; null without one
    private (TimeOnly From, TimeOnly To)? _night;
    // the calendar day that was settled last
    private DateOnly _day;
    private ModuleContext? _village;
    private EconomySettings _settings = null!;
    private Dictionary<string, Blueprint> _blueprints = [];
    // per need, the materials that meet it with their price, in the order of the catalog
    private List<(string Need, Need Amount, List<(string Material, int Price)> Goods)> _needs = [];
    private Dictionary<int, Cell> _centres = [];
    // who was at work, or on an errand for it, in the last tick: a walk over his work area is work only for him
    private SortedSet<int> _working = [];
    // per structure, its work area; it follows from the map and the data, so it is not saved
    private readonly Dictionary<int, (Cell Place, IReadOnlyList<Cell> Cells)> _areas = [];
    // per workplace with a batch in hand, the tiles of its area its workers stood on for it
    private readonly SortedDictionary<int, HashSet<Cell>> _stood = [];
    private Nature _nature = new(new Dictionary<Ground, NatureSettings>(), default);
    private Dictionary<Ground, NatureSettings> _natureSettings = [];

    public string Name => "work";

    public int Version => 3;

    /// <summary>The forest tiles that are felled now; they grow back after <c>forestRegrowTicks</c>.</summary>
    public IReadOnlyCollection<Cell> Felled => _felled.Keys;

    /// <summary>What a tile has left to give, from 100 (full) to 0; a tile nobody worked is full.</summary>
    public int YieldOf(Cell cell) => _nature.YieldOf(cell);

    /// <summary>
    /// The tiles that are bare now: worked below 20, and the felled forest. Forest gathered low keeps its trees: only
    /// felling makes it bare.
    /// </summary>
    public IReadOnlyCollection<Cell> Bare =>
        _nature.Bare.Where(cell => _village?.Map.At(cell) != Ground.Forest).Concat(_felled.Keys).ToHashSet();

    /// <summary>Forest on the map that is not felled now.</summary>
    public bool IsForest(Cell cell) => _village is { } village && village.Map.At(cell) == Ground.Forest && !_felled.ContainsKey(cell);

    /// <summary>The job the resident has now, or null. Eddie's is his workshop, once it stands.</summary>
    public JobPlace? JobOf(int resident)
    {
        // Day may ask before this module has started
        if (_village is null) return null;
        var world = See(_village);
        if (_jobs.TryGetValue(resident, out var seat)) return PlaceOf(world, resident, seat)?.Job;
        return world.Residents.TryGetValue(resident, out var who) && who.IsEddie ? EddieJob(world, who) : null;
    }

    /// <summary>The tiles where the outdoor jobs of the structure are done; empty when they are done at the structure.</summary>
    public IReadOnlyList<Cell> WorkAreaOf(int structure)
    {
        if (_village is null) return [];
        var world = See(_village);
        return world.Standing.FirstOrDefault(standing => standing.Id == structure) is { } found && _blueprints.TryGetValue(found.Blueprint, out var blueprint)
            ? AreaOf(found, blueprint)
            : [];
    }

    /// <summary>The purse of the island. Throws <see cref="KeyNotFoundException"/> for an island that is not on the map.</summary>
    public Treasury Of(int island) =>
        _treasuries.TryGetValue(island, out var treasury) ? treasury : throw new KeyNotFoundException($"There is no island {island}.");

    /// <summary>What the island holds, by material; a material it has none of is not listed.</summary>
    public IReadOnlyDictionary<string, int> Stock(int island) =>
        _stores.TryGetValue(island, out var stores) ? stores.Total : new SortedDictionary<string, int>(StringComparer.Ordinal);

    /// <summary>What the store of the workplace holds, by material; a material it has none of is not listed.</summary>
    public IReadOnlyDictionary<string, int> StockAt(int structure) =>
        _stores.Values.Select(stores => stores.At(structure)).FirstOrDefault(stock => stock.Count > 0)
            ?? new SortedDictionary<string, int>(StringComparer.Ordinal);

    /// <summary>
    /// How the households of the island did at the last settling of the day: how many there were, and per need how
    /// many could not buy all of it, for want of money or of stock. Before the first midnight there were none.
    /// </summary>
    public Shopping LastShopping(int island) =>
        _shopping.TryGetValue(island, out var shopping) ? shopping : new Shopping(0, _needs.ToDictionary(need => need.Need, _ => 0));

    public int Count(int island, string material) => _stores.TryGetValue(island, out var stores) ? stores.Count(material) : 0;

    public bool TryTake(int island, IReadOnlyDictionary<string, int> cost, Cell near) => StoresOf(island).Take(cost, near);

    public Cell? SourceOf(int island, string material, Cell near) =>
        _stores.TryGetValue(island, out var stores) ? stores.SourceOf(material, near) : null;

    public void Start(ModuleContext village)
    {
        Read(village);
        _day = Today(village);
        _nature = new Nature(_natureSettings, _day);
        foreach (var island in village.Map.Islands)
        {
            _treasuries[island.Id] = new Treasury { Cash = island.Eddie ? 0 : _settings.Start.Cash };
            _stores[island.Id] = NewStores(island.Id);
            _wages[island.Id] = 0;
            if (island.Eddie || !_settings.Start.Stock.TryGetValue(island.Name, out var start)) continue;
            foreach (var (material, amount) in start) _stores[island.Id].Add(Stores.Gathering, material, amount);
        }
        _spread = true;
        Staff(village, See(village));
    }

    public void Step(ModuleContext village)
    {
        _village = village;
        // who left in the last tick took his money out of the village
        foreach (var left in village.Received.OfType<ResidentLeft>())
            if (_treasuries.TryGetValue(left.Island, out var treasury)) treasury.Departed += left.Money;
        var world = See(village);
        if (_spread) Spread(village.Map, world);
        var today = Today(village);
        if (today != _day)
        {
            _day = today;
            _nature.Grow(today);
            SettleDay(village, world);
        }
        FetchFood(village, world);
        Staff(village, world);
        Move(village, world);
        Produce(village, world, Labour(world));
        Regrow(village.Clocks.Tick);
    }

    // ── jobs ──

    private void Staff(ModuleContext village, World world)
    {
        var tick = village.Clocks.Tick;
        var openings = Openings(village.Map, world);
        var seats = openings.Select(opening => opening.Seat).ToHashSet();
        foreach (var gone in _vacant.Keys.Where(seat => !seats.Contains(seat)).ToList()) _vacant.Remove(gone);

        // who stops working or whose place is gone lets go of his job
        foreach (var (resident, seat) in _jobs.ToList())
        {
            if (seats.Contains(seat) && world.Residents.TryGetValue(resident, out var who) && who is { Phase: LifePhase.Working, IsEddie: false }) continue;
            _jobs.Remove(resident);
            _since.Remove(resident);
            _owed.Remove(resident);
            if (seats.Contains(seat)) _vacant[seat] = tick;
        }
        var taken = _jobs.Values.ToHashSet();
        foreach (var opening in openings)
            if (!taken.Contains(opening.Seat)) _vacant.TryAdd(opening.Seat, tick);
        if (_vacant.Count == 0) return;

        Dictionary<int, HashSet<string>>? wanted = null;
        foreach (var resident in world.People.All)
        {
            if (resident is not { Phase: LifePhase.Working, IsEddie: false } || _jobs.ContainsKey(resident.Id)) continue;
            wanted ??= Wanted(world);
            var wants = wanted.GetValueOrDefault(resident.Island);
            // a place that makes what the island is short of; then the best fit; of equal fits the place free the
            // longest; of those the first in the list, so the first seats of every workplace go before the second
            Opening? best = null;
            var bestRank = (Meets: -1, Fit: -1);
            var bestFreed = long.MaxValue;
            foreach (var opening in openings)
            {
                if (opening.Seat.Island != resident.Island || !_vacant.TryGetValue(opening.Seat, out var freed)) continue;
                var rank = (Meets: Meets(opening.Recipe, wants) ? 1 : 0, Fit: Fit(resident, opening.Role));
                if (rank.CompareTo(bestRank) < 0 || rank == bestRank && freed >= bestFreed) continue;
                (best, bestRank, bestFreed) = (opening, rank, freed);
            }
            if (best is not { } chosen) continue;
            _jobs[resident.Id] = chosen.Seat;
            _since[resident.Id] = tick;
            _vacant.Remove(chosen.Seat);
            village.Publish(new Hired(tick, resident.Id, resident.Island, chosen.Role));
        }
    }

    // in rounds: the first seat of the builders of every ordinary island and of every job of the buildings that stand
    // (by id), then the second seat of each, and so on
    private List<Opening> Openings(IslandMap map, World world)
    {
        var openings = new List<Opening>();
        foreach (var island in map.Islands)
        {
            if (island.Eddie) continue;
            for (var index = 0; index < _settings.Builders; index++)
                openings.Add(new Opening(new Seat(island.Id, -1, 0, index), _settings.BuilderRole, null));
        }
        foreach (var structure in world.Standing)
        {
            if (!InEconomy(map, structure) || !_blueprints.TryGetValue(structure.Blueprint, out var blueprint) || blueprint.Jobs is not { } jobs) continue;
            for (var job = 0; job < jobs.Count; job++)
                for (var index = 0; index < jobs[job].Count; index++)
                    openings.Add(new Opening(new Seat(structure.Island, structure.Id, job, index), jobs[job].Role, blueprint.Recipe));
        }
        return [.. openings.OrderBy(opening => opening.Seat.Index)];
    }

    // per island, the goods of every need it is short of: less in stock than shortDays days of what its households buy
    private Dictionary<int, HashSet<string>> Wanted(World world)
    {
        var wanted = new Dictionary<int, HashSet<string>>();
        if (_settings.ShortDays <= 0) return wanted;
        foreach (var island in world.People.All.GroupBy(resident => resident.Island))
        {
            var households = island.Select(HouseholdOf).Distinct().Count();
            foreach (var (_, amount, goods) in _needs)
            {
                var daily = amount.PerPerson * island.Count() + amount.PerHousehold * households;
                if (goods.Sum(good => Count(island.Key, good.Material)) >= (long)_settings.ShortDays * daily) continue;
                if (!wanted.TryGetValue(island.Key, out var goodsShort)) wanted[island.Key] = goodsShort = [];
                goodsShort.UnionWith(goods.Select(good => good.Material));
            }
        }
        return wanted;
    }

    // whether the recipe makes something of what the island is short of
    private static bool Meets(Recipe? recipe, HashSet<string>? wants) =>
        wants is not null && recipe is not null && recipe.Out.Keys.Any(wants.Contains);

    /// <summary>
    /// Once a day, at the start of the day shift: every workplace without a worker, in the order they stand, takes one
    /// from the workplace of its island with the most (of equals the first), when that one has two or more and makes
    /// nothing the island is short of; when none can, from the builders of the island while it keeps one. Who fits least
    /// there goes, of equals the newest in the job; he takes the first place of the workplace as a new hire, and his old
    /// place comes free.
    /// </summary>
    private void Move(ModuleContext village, World world)
    {
        var now = village.Clocks.Calendar.Moment;
        if (now.Time < _moveAt || now.Date == _moved) return;
        _moved = now.Date;
        var tick = village.Clocks.Tick;
        var workers = new Dictionary<int, List<int>>();
        foreach (var (resident, seat) in _jobs)
        {
            if (seat.Structure < 0) continue;
            if (!workers.TryGetValue(seat.Structure, out var at)) workers[seat.Structure] = at = [];
            at.Add(resident);
        }
        var workplaces = world.Standing.Where(structure => InEconomy(village.Map, structure)
            && _blueprints.TryGetValue(structure.Blueprint, out var blueprint) && blueprint.Jobs?.Any(job => job.Count > 0) == true).ToList();
        Dictionary<int, HashSet<string>>? wanted = null;
        foreach (var empty in workplaces)
        {
            if (workers.ContainsKey(empty.Id)) continue;
            wanted ??= Wanted(world);
            var wants = wanted.GetValueOrDefault(empty.Island);
            StructureView? from = null;
            foreach (var other in workplaces)
            {
                if (other.Island != empty.Island || !workers.TryGetValue(other.Id, out var there) || there.Count < 2
                    || Meets(_blueprints[other.Blueprint].Recipe, wants) || from is not null && there.Count <= workers[from.Id].Count) continue;
                from = other;
            }
            List<int> pool;
            Func<int, string> roleOf;
            if (from is not null)
            {
                var roles = _blueprints[from.Blueprint].Jobs!;
                pool = workers[from.Id];
                roleOf = resident => roles[_jobs[resident].Job].Role;
            }
            else
            {
                // no workplace can spare one: the builders can, as long as the island keeps one
                pool = [.. _jobs.Where(item => item.Value.Structure < 0 && item.Value.Island == empty.Island).Select(item => item.Key)];
                if (pool.Count < 2) continue;
                roleOf = _ => _settings.BuilderRole;
            }
            var mover = pool
                .OrderBy(resident => world.Residents.TryGetValue(resident, out var who) ? Fit(who, roleOf(resident)) : 0)
                .ThenByDescending(resident => _since.GetValueOrDefault(resident))
                .ThenByDescending(resident => resident)
                .First();
            var jobs = _blueprints[empty.Blueprint].Jobs!;
            var job = Enumerable.Range(0, jobs.Count).First(index => jobs[index].Count > 0);
            var seat = new Seat(empty.Island, empty.Id, job, 0);
            _vacant[_jobs[mover]] = tick;
            _vacant.Remove(seat);
            _jobs[mover] = seat;
            _since[mover] = tick;
            _owed.Remove(mover);
            _working.Remove(mover);
            pool.Remove(mover);
            workers[empty.Id] = [mover];
            village.Publish(new Hired(tick, mover, empty.Island, jobs[job].Role));
        }
    }

    private int Fit(Resident resident, string role) =>
        _settings.Fits.TryGetValue(role, out var interests) ? resident.Interests.Count(interests.Contains) : 0;

    // the job of the seat now, with the work area where walking from tile to tile is work too; null when there is none
    // this quarter
    private (JobPlace Job, IReadOnlyList<Cell> Area)? PlaceOf(World world, int resident, Seat seat, Dictionary<int, Dictionary<int, Cell>>? picked = null)
    {
        if (seat.Structure < 0)
        {
            // a builder works at a site of his island, the builders spread over them; without one he helps on the land
            var sites = world.Sites.Where(site => site.Island == seat.Island).ToList();
            if (sites.Count == 0)
                return Help(world, seat, picked) is var (tile, helped) ? (new JobPlace(-1, tile, _settings.BuilderRole, _settings.BuilderShift, true), helped) : null;
            var site = sites[seat.Index % sites.Count];
            return (new JobPlace(site.Id, Spot(site), _settings.BuilderRole, _settings.BuilderShift, true), []);
        }
        var structure = world.Standing.FirstOrDefault(standing => standing.Id == seat.Structure);
        if (structure is null || !_blueprints.TryGetValue(structure.Blueprint, out var blueprint) || blueprint.Jobs is not { } jobs || seat.Job >= jobs.Count) return null;
        var job = jobs[seat.Job];
        var place = Spot(structure);
        var area = job.Outdoor ? AreaOf(structure, blueprint) : [];
        if (area.Count > 0 && PicksOf(world, structure, blueprint, area, picked).TryGetValue(resident, out var own)) place = own;
        return (new JobPlace(structure.Id, place, job.Role, job.Shift, job.Outdoor), area);
    }

    private Dictionary<int, Cell> PicksOf(World world, StructureView structure, Blueprint blueprint, IReadOnlyList<Cell> area, Dictionary<int, Dictionary<int, Cell>>? picked)
    {
        if (picked?.TryGetValue(structure.Id, out var known) == true) return known;
        var picks = Picks(world, structure, blueprint, area);
        picked?.Add(structure.Id, picks);
        return picks;
    }

    /// <summary>
    /// Where a builder without a site helps: at one of the outdoor workplaces of his island whose area still yields, the
    /// builders spread over them by seat, on a tile of it that its own workers do not hold and that is not where the island
    /// gathers, each helper on his own while there is room, from a hash of his seat and the stretch of <c>stayQuarters</c>
    /// quarters it is now. He adds nothing to what it makes. Null when there is no such workplace.
    /// </summary>
    private (Cell Tile, IReadOnlyList<Cell> Area)? Help(World world, Seat seat, Dictionary<int, Dictionary<int, Cell>>? picked)
    {
        var quarter = Quarter(_village!);
        var gathering = Gathering(world, seat.Island);
        var workplaces = new List<(StructureView Structure, Blueprint Blueprint, IReadOnlyList<Cell> Area, List<Cell> Yielding)>();
        foreach (var structure in world.Standing)
        {
            if (structure.Island != seat.Island || !InEconomy(_village!.Map, structure) || !_blueprints.TryGetValue(structure.Blueprint, out var blueprint)) continue;
            var area = AreaOf(structure, blueprint);
            var yielding = area.Where(cell => cell != gathering && (Fells(blueprint) || _nature.Yields(cell, quarter))).ToList();
            if (yielding.Count > 0) workplaces.Add((structure, blueprint, area, yielding));
        }
        if (workplaces.Count == 0) return null;

        var (workplace, plan, cells, open) = workplaces[seat.Index % workplaces.Count];
        var held = PicksOf(world, workplace, plan, cells, picked).Values.ToHashSet();
        var free = open.Where(cell => !held.Contains(cell)).ToList();
        if (free.Count == 0) free = open;
        var stretch = _village!.Clocks.Calendar.Now.ToUnixTimeSeconds() / QuarterSeconds / _settings.StayQuarters;
        // the helpers here before him, by seat, take theirs first
        var taken = new HashSet<Cell>();
        var tile = free[0];
        for (var index = seat.Index % workplaces.Count; index <= seat.Index; index += workplaces.Count)
        {
            if (free.All(taken.Contains)) taken.Clear();
            var key = unchecked((ulong)index * 0x9E3779B97F4A7C15UL ^ (ulong)workplace.Id * 0xC2B2AE3D27D4EB4FUL ^ (ulong)stretch);
            var at = Rng.For(key, "help on the land").Next(free.Count);
            while (taken.Contains(free[at])) at = (at + 1) % free.Count;
            tile = free[at];
            taken.Add(tile);
        }
        return (tile, cells);
    }

    /// <summary>
    /// Where the outdoor jobs of the structure are done: the walkable tiles of its island within <c>workRange</c> whose
    /// ground is one it stands on, and those beside such ground that is water (for a fishing spot: the shore). A
    /// woodcutter's is the forest within <c>forestRange</c>, where he fells. Empty for a structure without a recipe, or
    /// with <c>workRange</c> 0: its jobs are done at it.
    /// </summary>
    private IReadOnlyList<Cell> AreaOf(StructureView structure, Blueprint blueprint)
    {
        if (_settings.WorkRange <= 0 || blueprint.Recipe is null || blueprint.Jobs?.Any(job => job.Outdoor) != true) return [];
        if (_areas.TryGetValue(structure.Id, out var known) && known.Place == structure.Place) return known.Cells;
        var map = _village!.Map;
        var on = blueprint.On.ToHashSet();
        bool Ours(Cell cell) => on.Contains(map.At(cell));
        var cells = map.Islands.FirstOrDefault(island => island.Id == structure.Island)?.Cells ?? [];
        IReadOnlyList<Cell> area = [.. (Fells(blueprint)
                ? cells.Where(cell => map.At(cell) == Ground.Forest && cell.DistanceTo(structure.Place) <= _settings.ForestRange)
                : cells.Where(cell => map.IsWalkable(cell) && cell.DistanceTo(structure.Place) <= _settings.WorkRange
                    && (Ours(cell) || cell.Neighbours().Any(next => !map.IsWalkable(next) && Ours(next)))))
            .OrderBy(cell => cell.Row).ThenBy(cell => cell.Col)];
        _areas[structure.Id] = (structure.Place, area);
        return area;
    }

    /// <summary>
    /// The tile of the area each outdoor worker of the structure works on now: from a hash of who he is, the structure
    /// and the stretch of <c>stayQuarters</c> quarters it is now, the next free one when it is taken, so they spread out
    /// while the area has room. Who is out and back does not change it, so it holds all through the stretch. A worker
    /// whose tile does not yield (it went bare before this quarter) takes, in the same way, one of the tiles that still
    /// yield and nobody has; the others keep theirs. A worker out on an errand for his work keeps the tile he left, even
    /// when another has it now. Without a tile that yields, nobody has one: they work at the structure.
    /// </summary>
    private Dictionary<int, Cell> Picks(World world, StructureView structure, Blueprint blueprint, IReadOnlyList<Cell> area)
    {
        var jobs = blueprint.Jobs!;
        var workers = _jobs.Where(job => job.Value.Structure == structure.Id && job.Value.Job < jobs.Count && jobs[job.Value.Job].Outdoor)
            .Select(job => job.Key).ToList();
        var picks = new Dictionary<int, Cell>();
        // the tiles there is work on now: those that still gave as the quarter began (felling has its own logic)
        var quarter = Quarter(_village!);
        bool Yields(Cell cell) => Fells(blueprint) || _nature.Yields(cell, quarter);
        var yielding = area.Where(Yields).ToList();
        if (yielding.Count == 0) return picks;
        var stretch = _village!.Clocks.Calendar.Now.ToUnixTimeSeconds() / QuarterSeconds / _settings.StayQuarters;
        ulong Key(int worker) => unchecked((ulong)worker * 0x9E3779B97F4A7C15UL ^ (ulong)structure.Id * 0xC2B2AE3D27D4EB4FUL ^ (ulong)stretch);

        // over the whole area, as if every tile yielded, so a tile that goes bare or grows back moves nobody else
        var taken = new HashSet<Cell>();
        foreach (var worker in workers)
        {
            if (area.All(taken.Contains)) taken.Clear();
            var at = Rng.For(Key(worker), "work area").Next(area.Count);
            while (taken.Contains(area[at])) at = (at + 1) % area.Count;
            picks[worker] = area[at];
            taken.Add(area[at]);
        }

        // who drew a tile that does not yield takes one that does, and that nobody has
        var held = picks.Values.Where(Yields).ToHashSet();
        foreach (var worker in workers.Where(worker => !Yields(picks[worker])))
        {
            if (yielding.All(held.Contains)) held.Clear();
            var at = Rng.For(Key(worker), "work area, yielding").Next(yielding.Count);
            while (held.Contains(yielding[at])) at = (at + 1) % yielding.Count;
            picks[worker] = yielding[at];
            held.Add(yielding[at]);
        }

        foreach (var worker in workers)
            if (world.Day?.TripOf(worker) is { Was: Activity.Work } trip) picks[worker] = trip.Home;
        return picks;
    }

    private JobPlace? EddieJob(World world, Resident eddie) =>
        world.Standing.FirstOrDefault(structure => structure.Blueprint == _settings.EddieWorkshop && structure.Island == eddie.Island) is { } workshop
            ? new JobPlace(workshop.Id, Spot(workshop), _settings.EddieRole, Shift.Day, false)
            : null;

    // where the work is done: on the building's tile, or for one on the water on the nearest land of its island
    private Cell Spot(StructureView structure)
    {
        var map = _village!.Map;
        if (map.IsWalkable(structure.Place) && map.IslandOf(structure.Place) == structure.Island) return structure.Place;
        return Land(map, structure.Island)
            .OrderBy(cell => cell.DistanceTo(structure.Place)).ThenBy(cell => cell.Row).ThenBy(cell => cell.Col)
            .FirstOrDefault(_centres[structure.Island]);
    }

    private Cell Gathering(World world, int island) => Gathering(world.Places, island);

    private Cell Gathering(IPlaces? places, int island)
    {
        var map = _village!.Map;
        return places?.GatheringOn(island) is { } gathering && map.IsWalkable(gathering) && map.IslandOf(gathering) == island
            ? gathering
            : _centres[island];
    }

    // ── stores ──

    private Stores StoresOf(int island)
    {
        if (!_stores.TryGetValue(island, out var stores)) _stores[island] = stores = NewStores(island);
        return stores;
    }

    // a workplace's store lies where its work is done; the island's own, and that of a workplace that is gone, where it gathers
    private Stores NewStores(int island) => new(store =>
        store != Stores.Gathering && _village?.Find<IStructures>()?.Standing().FirstOrDefault(structure => structure.Id == store) is { } workplace
            ? Spot(workplace)
            : Gathering(_village!.Find<IPlaces>(), island));

    // what lies where the island gathers goes to the workplaces of the island that make it, shared out evenly
    private void Spread(IslandMap map, World world)
    {
        _spread = false;
        foreach (var (island, stores) in _stores)
            foreach (var (material, amount) in stores.At(Stores.Gathering).ToList())
            {
                var makers = world.Standing.Where(structure => structure.Island == island && InEconomy(map, structure)
                    && _blueprints.TryGetValue(structure.Blueprint, out var blueprint) && blueprint.Recipe?.Out.ContainsKey(material) == true).ToList();
                if (makers.Count == 0) continue;
                stores.Add(Stores.Gathering, material, -amount);
                for (var maker = 0; maker < makers.Count; maker++)
                    stores.Add(makers[maker].Id, material, amount / makers.Count + (maker < amount % makers.Count ? 1 : 0));
            }
    }

    // ── working and making ──

    /// <summary>
    /// Pays everyone who is at his job, or on an errand for it (fetching or bringing to the store), and says who is at
    /// work at which workplace.
    /// </summary>
    private Dictionary<int, List<int>> Labour(World world)
    {
        var staffed = new Dictionary<int, List<int>>();
        if (world.Day is not { } day) return staffed;
        var picked = new Dictionary<int, Dictionary<int, Cell>>();
        var working = new SortedSet<int>();
        foreach (var (resident, seat) in _jobs)
        {
            // without a job this quarter, such as a builder with nothing to help, he is not paid
            if (PlaceOf(world, resident, seat, picked) is not var (job, area)) continue;
            if (!AtWork(day, resident, job.Place, area, _working.Contains(resident)))
            {
                if (day.TripOf(resident) is { Errand.Kind: Activity.Fetch or Activity.Bring, Was: Activity.Work } trip && trip.Home == job.Place)
                {
                    Pay(world.Residents[resident], seat.Island);
                    working.Add(resident);
                }
                continue;
            }
            working.Add(resident);
            if (seat.Structure >= 0)
            {
                if (!staffed.TryGetValue(seat.Structure, out var workers)) staffed[seat.Structure] = workers = [];
                workers.Add(resident);
            }
            Pay(world.Residents[resident], seat.Island);
        }
        _working = working;
        return staffed;
    }

    // working, and there: not on the way, unless he was at work and goes on to a tile of his work area, wherever he set off
    // from (not on his way to work); standing on another tile of it, as when he is back from an errand and his tile has
    // moved on, is work too
    private static bool AtWork(DayModule day, int resident, Cell place, IReadOnlyList<Cell> area, bool wasWorking)
    {
        try
        {
            var doing = day.Of(resident);
            if (doing.Activity != Activity.Work) return false;
            if (doing.Walk is null) return doing.Place == place || area.Contains(doing.Place);
            return wasWorking && area.Contains(doing.Place);
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    // wagePerHour / 14400 a tick, kept until it is a whole cent; what the treasury does not have is not paid
    private void Pay(Resident worker, int island)
    {
        var owed = _owed.GetValueOrDefault(worker.Id) + _settings.WagePerHour;
        var whole = owed / TicksPerHour;
        _owed[worker.Id] = owed % TicksPerHour;
        if (whole == 0) return;
        var treasury = _treasuries[island];
        var paid = Math.Min(whole, treasury.Cash);
        treasury.Cash -= paid;
        worker.Money += paid;
        _wages[island] += paid;
        _earned[worker.Id] = _earned.GetValueOrDefault(worker.Id) + paid;
    }

    private void Produce(ModuleContext village, World world, Dictionary<int, List<int>> staffed)
    {
        var tick = village.Clocks.Tick;
        var quarter = Quarter(village);
        foreach (var structure in world.Standing)
        {
            if (!staffed.ContainsKey(structure.Id) || !InEconomy(village.Map, structure)
                || !_blueprints.TryGetValue(structure.Blueprint, out var blueprint) || blueprint.Recipe is not { } recipe) continue;
            // the area that runs out as it is worked; one worked bare stands still until a tile of it grows back
            var area = Fells(blueprint) ? [] : AreaOf(structure, blueprint);
            if (area.Count > 0 && !area.Any(cell => _nature.Yields(cell, quarter))) continue;
            if (!_batches.TryGetValue(structure.Id, out var worked))
            {
                // a new batch: a tree to fell, and what goes in, fetched and taken from the stores nearest the workplace
                Cell? tree = null;
                if (Fells(blueprint) && (tree = Tree(village.Map, world, structure)) is null) continue;
                var spot = Spot(structure);
                if (recipe.In.Any(item => item.Value > Count(structure.Island, item.Key))) continue;
                if (!Fetched(world, structure, recipe, spot, staffed[structure.Id]) || !TryTake(structure.Island, recipe.In, spot)) continue;
                if (tree is { } felled) _felled[felled] = tick + _settings.ForestRegrowTicks;
            }
            if (area.Count > 0) Stood(world, structure.Id, staffed[structure.Id], area);
            if (++worked < recipe.Ticks)
            {
                _batches[structure.Id] = worked;
                continue;
            }
            _batches.Remove(structure.Id);
            // the batch is taken from every tile it was worked on
            if (_stood.Remove(structure.Id, out var tiles))
                foreach (var cell in tiles) _nature.Take(cell, GroundOf(village.Map, blueprint, cell), quarter);
            foreach (var (material, amount) in recipe.Out.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                StoresOf(structure.Island).Add(structure.Id, material, amount);
                village.Publish(new Produced(tick, structure.Island, material, amount));
            }
            Bring(world, structure, recipe, staffed[structure.Id]);
        }
        var standing = world.Standing.Select(structure => structure.Id).ToHashSet();
        foreach (var gone in _batches.Keys.Where(id => !standing.Contains(id)).ToList()) _batches.Remove(gone);
        foreach (var gone in _stood.Keys.Where(id => !_batches.ContainsKey(id)).ToList()) _stood.Remove(gone);
    }

    // the tiles of the area the workers stand on, at work
    private void Stood(World world, int structure, List<int> workers, IReadOnlyList<Cell> area)
    {
        if (world.Day is not { } day) return;
        foreach (var worker in workers)
        {
            if (day.Of(worker) is not { Walk: null } doing || !area.Contains(doing.At)) continue;
            if (!_stood.TryGetValue(structure, out var tiles)) _stood[structure] = tiles = [];
            tiles.Add(doing.At);
        }
    }

    // the ground a tile of the area is worked as: its own, or for one by the water (a shore) that of the water
    private static Ground GroundOf(IslandMap map, Blueprint blueprint, Cell cell)
    {
        var ground = map.At(cell);
        if (blueprint.On.Contains(ground)) return ground;
        foreach (var next in cell.Neighbours())
            if (!map.IsWalkable(next) && blueprint.On.Contains(map.At(next))) return map.At(next);
        return ground;
    }

    // the stock stays a number; the workers out on the area carry the most of the batch to the workplace, for all to see
    private void Bring(World world, StructureView structure, Recipe recipe, List<int> workers)
    {
        if (world.Day is not { } day || recipe.Out.Count == 0) return;
        var store = Spot(structure);
        var carry = Most(recipe.Out);
        foreach (var worker in workers)
            if (day.Of(worker) is not { Walk: null } doing || doing.At != store) day.Send(worker, new Errand(Activity.Bring, store, carry));
    }

    // before a batch that something goes into, one worker fetches the most of it at the store nearest the workplace that
    // has it; true when he is back with it in this tick, or there is nothing to fetch or nobody to walk it
    private bool Fetched(World world, StructureView structure, Recipe recipe, Cell spot, List<int> workers)
    {
        if (recipe.In.Count == 0 || world.Day is not { } day) return true;
        if (workers.Any(worker => day.BackFrom(worker) is { Kind: Activity.Fetch })) return true;
        // one of them is on his way
        if (_jobs.Any(job => job.Value.Structure == structure.Id && day.TripOf(job.Key) is { Errand.Kind: Activity.Fetch, Was: Activity.Work })) return false;
        var input = Most(recipe.In);
        if (SourceOf(structure.Island, input, spot) is not { } source) return false;
        day.Send(workers[0], new Errand(Activity.Fetch, source, null, input));
        // from a store where he stands he is back at once
        return day.BackFrom(workers[0]) is { Kind: Activity.Fetch };
    }

    // the material there is the most of; of equal amounts the first by name
    private static string Most(IReadOnlyDictionary<string, int> materials) =>
        materials.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.Ordinal).First().Key;

    // a recipe that fells, such as a woodcutter's; gathering branches leaves the forest standing
    private static bool Fells(Blueprint blueprint) => blueprint.Recipe is { Fells: true };

    // the nearest standing tree of the island within reach, that no building stands on
    private Cell? Tree(IslandMap map, World world, StructureView hut)
    {
        var built = world.Standing.Concat(world.Sites).Select(structure => structure.Place).ToHashSet();
        Cell? nearest = null;
        foreach (var cell in map.Islands.Single(island => island.Id == hut.Island).Cells)
        {
            if (map.At(cell) != Ground.Forest || _felled.ContainsKey(cell) || built.Contains(cell)) continue;
            var distance = cell.DistanceTo(hut.Place);
            if (distance > _settings.ForestRange) continue;
            if (nearest is { } best && (distance, cell.Row, cell.Col).CompareTo((best.DistanceTo(hut.Place), best.Row, best.Col)) >= 0) continue;
            nearest = cell;
        }
        return nearest;
    }

    private void Regrow(long tick)
    {
        foreach (var (cell, until) in _felled.ToList())
            if (until <= tick) _felled.Remove(cell);
    }

    // ── the day ──

    private void SettleDay(ModuleContext village, World world)
    {
        _fetch.Clear();
        foreach (var island in village.Map.Islands)
        {
            if (island.Eddie) continue;
            var treasury = _treasuries[island.Id];
            var residents = world.People.On(island.Id).Where(resident => !resident.IsEddie).ToList();

            var subsidy = (long)Math.Floor(_settings.StartSubsidy / (1 + residents.Count / _settings.SubsidyDamping));
            treasury.Cash += subsidy;
            treasury.Subsidy += subsidy;
            var upkeep = Math.Min((long)_settings.UpkeepPerDay * world.Standing.Count(structure => structure.Island == island.Id), treasury.Cash);
            treasury.Cash -= upkeep;
            treasury.Upkeep += upkeep;

            long allowances = 0, pensions = 0;
            foreach (var resident in residents)
            {
                var due = resident.Phase switch
                {
                    LifePhase.Child or LifePhase.Student => _settings.AllowancePerDay,
                    LifePhase.Retired => _settings.PensionPerDay,
                    _ => Math.Max(0, _settings.SupportPerDay - _earned.GetValueOrDefault(resident.Id)),
                };
                var paid = Math.Min(due, treasury.Cash);
                treasury.Cash -= paid;
                resident.Money += paid;
                if (resident.Phase == LifePhase.Retired) pensions += paid;
                else allowances += paid;
            }

            _shopping[island.Id] = Shop(island.Id, residents, treasury, Gathering(world, island.Id));

            // what is left, a resident spends after his thrift: it leaves the village
            foreach (var resident in residents)
            {
                var spent = resident.Money * _settings.SpendPercent * (100 - resident.Traits.Thrift) / 10_000;
                resident.Money -= spent;
                treasury.Spent += spent;
            }

            village.Publish(new Paid(village.Clocks.Tick, island.Id, _wages[island.Id], allowances, pensions));
            _wages[island.Id] = 0;
        }
        _earned.Clear();
    }

    // every household buys what it needs from the stores nearest where the island gathers, of the first material in
    // stock that it can pay; who cannot buy all of a need goes without
    private Shopping Shop(int island, List<Resident> residents, Treasury treasury, Cell gathering)
    {
        var shortOf = _needs.ToDictionary(need => need.Need, _ => 0);
        var households = residents.GroupBy(HouseholdOf).ToList();
        foreach (var household in households)
        {
            var members = household.ToList();
            var food = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var (need, amount, goods) in _needs)
            {
                var units = amount.PerPerson * members.Count + amount.PerHousehold;
                for (var unit = 0; unit < units; unit++)
                {
                    var purse = members.Sum(member => member.Money);
                    var (material, price) = goods.FirstOrDefault(good => good.Price <= purse && Count(island, good.Material) > 0);
                    if (material is null)
                    {
                        shortOf[need]++;
                        break;
                    }
                    TryTake(island, new Dictionary<string, int> { [material] = 1 }, gathering);
                    if (need == Food) food[material] = food.GetValueOrDefault(material) + 1;
                    var due = (long)price;
                    foreach (var member in members)
                    {
                        var part = Math.Min(member.Money, due);
                        member.Money -= part;
                        due -= part;
                    }
                    treasury.Cash += price;
                }
            }
            if (_settings.FetchFood && food.Count > 0) _fetch[household.Key] = Most(food);
        }
        return new Shopping(households.Count, shortOf);
    }

    private static int HouseholdOf(Resident resident) => resident.Household ?? -1 - resident.Id;

    // as a quarter begins, who came home in it (from anything else, sleep too) and is grown, awake and not out on an errand
    // goes for the food of his household if it has not been yet, from where he is to the store nearest home that has it,
    // and home with it; from fetchFoodFrom on, not at night. With none of it left anywhere, nobody goes that day
    private void FetchFood(ModuleContext village, World world)
    {
        var quarter = Quarter(village);
        if (quarter == _fetchQuarter) return;
        _fetchQuarter = quarter;
        if (world.Day is not { } day) return;
        var wasHome = _atHome;
        _atHome = [];
        foreach (var resident in world.People.All)
            if (resident is { Phase: LifePhase.Working or LifePhase.Retired, IsEddie: false } && AtHome(day, resident.Id)) _atHome.Add(resident.Id);
        if (_fetch.Count == 0) return;
        var time = TimeOnly.FromDateTime(village.Clocks.Calendar.Now.DateTime);
        if (time < _fetchFrom || _night is { } night && (night.From <= night.To ? time >= night.From && time < night.To : time >= night.From || time < night.To)) return;
        foreach (var id in _atHome)
        {
            var household = HouseholdOf(world.Residents[id]);
            if (wasHome.Contains(id) || !_fetch.TryGetValue(household, out var food) || day.TripOf(id) is not null) continue;
            if (SourceOf(world.Residents[id].Island, food, day.Of(id).Place) is { } source && !day.Send(id, new Errand(Activity.Fetch, source, null, food))) continue;
            _fetch.Remove(household);
        }
    }

    private static bool AtHome(DayModule day, int resident)
    {
        if (day.TripOf(resident) is { } trip) return trip.Was == Activity.Home;
        try
        {
            return day.Of(resident).Activity == Activity.Home;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    // ── helpers ──

    private World See(ModuleContext village)
    {
        var people = village.Module<People.People>();
        var structures = village.Find<IStructures>();
        return new World(
            people,
            people.All.ToDictionary(resident => resident.Id),
            village.Find<DayModule>(),
            village.Find<IPlaces>(),
            structures is null ? [] : [.. structures.Standing().OrderBy(structure => structure.Id)],
            structures is null ? [] : [.. structures.Sites().OrderBy(structure => structure.Id)]);
    }

    // Eddie's islet is outside the economy
    private static bool InEconomy(IslandMap map, StructureView structure) =>
        map.Islands.FirstOrDefault(island => island.Id == structure.Island) is { Eddie: false };

    private static IEnumerable<Cell> Land(IslandMap map, int island) =>
        map.Islands.Single(i => i.Id == island).Cells.Where(map.IsWalkable);

    private static DateOnly Today(ModuleContext village) => DateOnly.FromDateTime(village.Clocks.Calendar.Now.DateTime);

    // the quarter of an hour it is now, counted since 1970
    private static long Quarter(ModuleContext village) => village.Clocks.Calendar.Now.ToUnixTimeSeconds() / QuarterSeconds;

    private static TimeOnly Time(string text) =>
        TimeOnly.TryParseExact(text, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var time)
            ? time
            : throw new InvalidDataException($"The data has the time '{text}', which is not HH:mm.");

    private void Read(ModuleContext village)
    {
        _village = village;
        _settings = village.Data.Section<EconomySettings>(Section);
        _fetchFrom = Time(_settings.FetchFoodFrom);
        var day = village.Data.Names.Contains("day") ? village.Data.Section<WorkDay>("day") : null;
        _night = day?.Night is { } night ? (Time(night.From), Time(night.To)) : null;
        // workers move when the day shift starts work on a weekday; without that rhythm, at midnight
        _moveAt = day?.Rhythms?.GetValueOrDefault(nameof(Shift.Day).ToLowerInvariant())?.Weekday.FirstOrDefault(block => block.Do == WorkBlock)?.From is { } from
            ? Time(from) : TimeOnly.MinValue;
        var catalog = village.Data.Section<Catalog>("catalog");
        _blueprints = catalog.Blueprints.ToDictionary(blueprint => blueprint.Id);
        var materials = catalog.Materials.Select(material => material.Id).ToHashSet();

        if (_settings.Builders < 0 || _settings.ForestRegrowTicks <= 0 || _settings.ForestRange < 0 || _settings.SubsidyDamping <= 0
            || _settings.WorkRange < 0 || _settings.StayQuarters < 1 || _settings.ShortDays < 0
            || _settings.SpendPercent is < 0 or > 100 || _settings.WagePerHour < 0 || _settings.SupportPerDay < 0)
            throw new InvalidDataException($"The data section '{Section}' has a number out of range.");
        foreach (var (island, stock) in _settings.Start.Stock)
            foreach (var (material, amount) in stock)
                if (!materials.Contains(material) || amount < 0)
                    throw new InvalidDataException($"The data section '{Section}' starts {island} with {amount} of '{material}'.");

        _natureSettings = [];
        foreach (var (name, nature) in _settings.Nature ?? new Dictionary<string, NatureSettings>())
        {
            if (!Enum.TryParse<Ground>(name, ignoreCase: true, out var ground) || nature.PerBatch < 0 || nature.RegrowDays < 1
                || nature.GrowsInMonths?.Any(month => month is < 1 or > 12) == true)
                throw new InvalidDataException($"The data section '{Section}' has nature for '{name}' that cannot be.");
            _natureSettings[ground] = nature;
        }

        _needs = [];
        foreach (var (need, amount) in _settings.Needs)
        {
            var goods = new List<(string, int)>();
            foreach (var material in catalog.Materials.Where(material => material.Satisfies?.Contains(need) == true))
            {
                if (!_settings.Prices.TryGetValue(material.Id, out var price) || price < 0)
                    throw new InvalidDataException($"The data section '{Section}' has no price for '{material.Id}', which meets {need}.");
                goods.Add((material.Id, price));
            }
            _needs.Add((need, amount, goods));
        }

        _centres = village.Map.Islands.ToDictionary(island => island.Id, island =>
        {
            var land = Land(village.Map, island.Id).ToList();
            var row = land.Average(cell => cell.Row);
            var col = land.Average(cell => cell.Col);
            return land.MinBy(cell => (cell.Row - row) * (cell.Row - row) + (cell.Col - col) * (cell.Col - col));
        });
    }

    // ── saving ──

    public JsonNode Save() =>
        JsonSerializer.SerializeToNode(new SavedWork(
            _day,
            [.. _treasuries.OrderBy(item => item.Key).Select(item => new SavedIsland(item.Key, item.Value.Cash, item.Value.Subsidy, item.Value.Upkeep,
                item.Value.Spent, _wages.GetValueOrDefault(item.Key), null, item.Value.Departed,
                _shopping.TryGetValue(item.Key, out var shopping) ? shopping : null,
                [.. StoresOf(item.Key).All.Select(store => new SavedStore(store.Store, store.Stock))]))],
            [.. _jobs.Select(job => new SavedJob(job.Key, job.Value, _owed.GetValueOrDefault(job.Key), _since.GetValueOrDefault(job.Key)))],
            [.. _vacant.OrderBy(item => (item.Key.Island, item.Key.Structure, item.Key.Job, item.Key.Index)).Select(item => new SavedVacancy(item.Key, item.Value))],
            [.. _batches.Select(batch => new SavedBatch(batch.Key, batch.Value))],
            [.. _felled.OrderBy(item => (item.Key.Row, item.Key.Col)).Select(item => new SavedFelled(item.Key, item.Value))],
            _earned.Count > 0 ? [.. _earned.Select(item => new SavedEarned(item.Key, item.Value))] : null,
            _working.Count > 0 ? [.. _working] : null,
            new SavedNature(_nature.Day, _nature.Tiles),
            _stood.Count > 0 ? [.. _stood.Select(item => new SavedStood(item.Key, [.. item.Value.OrderBy(cell => cell.Row).ThenBy(cell => cell.Col)]))] : null,
            _spread ? true : null,
            _fetch.Count > 0 ? [.. _fetch.Select(item => new SavedFetch(item.Key, item.Value))] : null,
            _fetchQuarter != 0 ? _fetchQuarter : null,
            _atHome.Count > 0 ? [.. _atHome] : null,
            _moved != default ? _moved : null), SaveJson.Options)!;

    public void Load(ModuleContext village, JsonNode data, int version)
    {
        Read(village);
        var saved = data.Deserialize<SavedWork>(SaveJson.Options) ?? throw new InvalidDataException("The work part of the save is empty.");
        _day = saved.Day;
        _treasuries.Clear();
        _stores.Clear();
        _wages.Clear();
        _jobs.Clear();
        _since.Clear();
        _owed.Clear();
        _vacant.Clear();
        _batches.Clear();
        _felled.Clear();
        _shopping.Clear();
        _earned.Clear();
        _stood.Clear();
        _fetch.Clear();
        // a save from before nature ran out has every tile full
        _nature = saved.Nature is { } nature ? new Nature(_natureSettings, nature.Day, nature.Tiles) : new Nature(_natureSettings, saved.Day);
        foreach (var island in village.Map.Islands)
        {
            _treasuries[island.Id] = new Treasury();
            _wages[island.Id] = 0;
        }
        foreach (var island in saved.Islands)
        {
            _treasuries[island.Id] = new Treasury { Cash = island.Cash, Subsidy = island.Subsidy, Upkeep = island.Upkeep, Spent = island.Spent, Departed = island.Departed };
            _wages[island.Id] = island.Wages;
            var stores = StoresOf(island.Id);
            // a save from when an island had one stock: it lies where the island gathers, until it is spread
            foreach (var (material, amount) in island.Stock ?? new Dictionary<string, int>()) stores.Add(Stores.Gathering, material, amount);
            foreach (var store in island.Stores ?? [])
                foreach (var (material, amount) in store.Stock) stores.Add(store.Store, material, amount);
            if (island.Shopping is { } shopping) _shopping[island.Id] = shopping;
        }
        _spread = version < 2 || saved.Spread == true;
        foreach (var job in saved.Jobs)
        {
            _jobs[job.Resident] = job.Seat;
            _owed[job.Resident] = job.Owed;
            // up to version 2 nobody knew since when he had his job: all as from the start
            _since[job.Resident] = job.Since ?? 0;
        }
        foreach (var vacancy in saved.Vacant) _vacant[vacancy.Seat] = vacancy.Freed;
        foreach (var batch in saved.Batches) _batches[batch.Structure] = batch.Worked;
        foreach (var felled in saved.Felled) _felled[felled.Cell] = felled.Until;
        foreach (var earned in saved.Earned ?? []) _earned[earned.Resident] = earned.Cents;
        _working = [.. saved.Working ?? []];
        foreach (var stood in saved.Stood ?? []) _stood[stood.Structure] = [.. stood.Tiles];
        // a save from before households fetched their food has nothing to fetch
        foreach (var fetch in saved.Fetch ?? []) _fetch[fetch.Household] = fetch.Material;
        _fetchQuarter = saved.FetchQuarter ?? 0;
        _atHome = [.. saved.AtHome ?? []];
        // up to version 2 nobody moved: the first day shift after loading they do
        _moved = saved.Moved ?? default;
    }

    /// <summary>A place to work: a builder's of the island (<see cref="Structure"/> −1), or one of a building's jobs.</summary>
    private readonly record struct Seat(int Island, int Structure, int Job, int Index);

    // a place to fill, with the recipe of its workplace
    private readonly record struct Opening(Seat Seat, string Role, Recipe? Recipe);

    private sealed record World(
        People.People People,
        Dictionary<int, Resident> Residents,
        DayModule? Day,
        IPlaces? Places,
        IReadOnlyList<StructureView> Standing,
        IReadOnlyList<StructureView> Sites);

    private sealed record SavedWork(DateOnly Day, IReadOnlyList<SavedIsland> Islands, IReadOnlyList<SavedJob> Jobs,
        IReadOnlyList<SavedVacancy> Vacant, IReadOnlyList<SavedBatch> Batches, IReadOnlyList<SavedFelled> Felled, IReadOnlyList<SavedEarned>? Earned = null,
        IReadOnlyList<int>? Working = null, SavedNature? Nature = null, IReadOnlyList<SavedStood>? Stood = null, bool? Spread = null,
        IReadOnlyList<SavedFetch>? Fetch = null, long? FetchQuarter = null, IReadOnlyList<int>? AtHome = null, DateOnly? Moved = null);

    private sealed record SavedFetch(int Household, string Material);

    /// <summary>What Work reads from the section "day": the night, "HH:mm" from and to, and the rhythms of the shifts.</summary>
    private sealed record WorkDay(NightHours? Night = null, IReadOnlyDictionary<string, ShiftRhythm>? Rhythms = null);

    private sealed record ShiftRhythm(IReadOnlyList<ShiftBlock> Weekday);

    private sealed record ShiftBlock(string From, string Do);

    private sealed record NightHours(string From, string To);

    private sealed record SavedStore(int Store, IReadOnlyDictionary<string, int> Stock);

    private sealed record SavedNature(DateOnly Day, IReadOnlyList<NatureTile> Tiles);

    private sealed record SavedStood(int Structure, IReadOnlyList<Cell> Tiles);

    private sealed record SavedEarned(int Resident, long Cents);

    // up to version 1 an island had one Stock; from version 2 on it has Stores
    private sealed record SavedIsland(int Id, long Cash, long Subsidy, long Upkeep, long Spent, long Wages, IReadOnlyDictionary<string, int>? Stock = null, long Departed = 0,
        Shopping? Shopping = null, IReadOnlyList<SavedStore>? Stores = null);

    // up to version 2 a job had no Since
    private sealed record SavedJob(int Resident, Seat Seat, long Owed, long? Since = null);

    private sealed record SavedVacancy(Seat Seat, long Freed);

    private sealed record SavedBatch(int Structure, int Worked);

    private sealed record SavedFelled(Cell Cell, long Until);
}

/// <summary>How the households of an island did at a settling of the day: how many there were, and per need how many went without some of it.</summary>
public sealed record Shopping(int Households, IReadOnlyDictionary<string, int> Short);
