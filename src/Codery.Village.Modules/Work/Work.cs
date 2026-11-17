using System.Text.Json;
using System.Text.Json.Nodes;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;
using Codery.Village.Modules.Contracts;
using Codery.Village.Modules.People;
using Activity = Codery.Village.Modules.Day.Activity;
using DayModule = Codery.Village.Modules.Day.Day;

namespace Codery.Village.Modules.Work;

/// <summary>
/// Work and money, in their simplest form. A building that stands offers the jobs of its blueprint and each
/// ordinary island has places for its own builders; a working resident without a job takes the free place on
/// his island that fits his interests best. A workplace makes its recipe while someone works there, from and
/// into the island's stock; a woodcutter fells a tree nearby, which grows back. The treasury of the island pays
/// a wage for every tick on the job, and once a calendar day, at 00:00, the island settles its day: the start
/// subsidy comes in, the upkeep of its buildings goes out, children, students and the retired get their money,
/// every household buys food and heat from the stock, and every resident spends after his thrift. Who leaves the
/// village takes his money with him; the island books it the tick after. Eddie works
/// in his workshop for nothing: he is outside the economy.
/// </summary>
public sealed class Work : IModule, IJobs, IStock
{
    private const string Section = "economy";
    private const long TicksPerHour = Clocks.TicksPerSecond * 3600L;

    private readonly Dictionary<int, Treasury> _treasuries = [];
    private readonly Dictionary<int, SortedDictionary<string, int>> _stock = [];
    // who works where, by resident
    private readonly SortedDictionary<int, Seat> _jobs = [];
    // per worker, the wage earned that is not yet a whole cent, in 1/TicksPerHour cents
    private readonly Dictionary<int, long> _owed = [];
    // places nobody has, with the tick they came free
    private readonly Dictionary<Seat, long> _vacant = [];
    // per island, the wages paid since the last Paid
    private readonly Dictionary<int, long> _wages = [];
    // per workplace with a batch in hand, the ticks worked on it
    private readonly SortedDictionary<int, int> _batches = [];
    // felled forest, with the tick it is forest again
    private readonly Dictionary<Cell, long> _felled = [];
    // the calendar day that was settled last
    private DateOnly _day;
    private ModuleContext? _village;
    private EconomySettings _settings = null!;
    private Dictionary<string, Blueprint> _blueprints = [];
    // per need, the materials that meet it with their price, in the order of the catalog
    private List<(string Need, Need Amount, List<(string Material, int Price)> Goods)> _needs = [];
    private Dictionary<int, Cell> _centres = [];

    public string Name => "work";

    public int Version => 1;

    /// <summary>The forest tiles that are felled now; they grow back after <c>forestRegrowTicks</c>.</summary>
    public IReadOnlyCollection<Cell> Felled => _felled.Keys;

    /// <summary>Forest on the map that is not felled now.</summary>
    public bool IsForest(Cell cell) => _village is { } village && village.Map.At(cell) == Ground.Forest && !_felled.ContainsKey(cell);

    /// <summary>The job the resident has now, or null. Eddie's is his workshop, once it stands.</summary>
    public JobPlace? JobOf(int resident)
    {
        // Day may ask before this module has started
        if (_village is null) return null;
        var world = See(_village);
        if (_jobs.TryGetValue(resident, out var seat)) return PlaceOf(world, seat);
        return world.Residents.TryGetValue(resident, out var who) && who.IsEddie ? EddieJob(world, who) : null;
    }

    /// <summary>The purse of the island. Throws <see cref="KeyNotFoundException"/> for an island that is not on the map.</summary>
    public Treasury Of(int island) =>
        _treasuries.TryGetValue(island, out var treasury) ? treasury : throw new KeyNotFoundException($"There is no island {island}.");

    /// <summary>What the island holds, by material; a material it has none of is not listed.</summary>
    public IReadOnlyDictionary<string, int> Stock(int island) =>
        _stock.TryGetValue(island, out var stock) ? stock : new SortedDictionary<string, int>(StringComparer.Ordinal);

    public int Count(int island, string material) =>
        _stock.TryGetValue(island, out var stock) && stock.TryGetValue(material, out var count) ? count : 0;

    public bool TryTake(int island, IReadOnlyDictionary<string, int> cost)
    {
        if (cost.Any(item => item.Value > Count(island, item.Key))) return false;
        foreach (var (material, amount) in cost) Add(island, material, -amount);
        return true;
    }

    public void Start(ModuleContext village)
    {
        Read(village);
        _day = Today(village);
        foreach (var island in village.Map.Islands)
        {
            _treasuries[island.Id] = new Treasury { Cash = island.Eddie ? 0 : _settings.Start.Cash };
            _stock[island.Id] = new SortedDictionary<string, int>(StringComparer.Ordinal);
            _wages[island.Id] = 0;
            if (island.Eddie || !_settings.Start.Stock.TryGetValue(island.Name, out var start)) continue;
            foreach (var (material, amount) in start) Add(island.Id, material, amount);
        }
        Staff(village, See(village));
    }

    public void Step(ModuleContext village)
    {
        _village = village;
        // who left in the last tick took his money out of the village
        foreach (var left in village.Received.OfType<ResidentLeft>())
            if (_treasuries.TryGetValue(left.Island, out var treasury)) treasury.Departed += left.Money;
        var world = See(village);
        var today = Today(village);
        if (today != _day)
        {
            _day = today;
            SettleDay(village, world);
        }
        Staff(village, world);
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
            _owed.Remove(resident);
            if (seats.Contains(seat)) _vacant[seat] = tick;
        }
        var taken = _jobs.Values.ToHashSet();
        foreach (var opening in openings)
            if (!taken.Contains(opening.Seat)) _vacant.TryAdd(opening.Seat, tick);
        if (_vacant.Count == 0) return;

        foreach (var resident in world.People.All)
        {
            if (resident is not { Phase: LifePhase.Working, IsEddie: false } || _jobs.ContainsKey(resident.Id)) continue;
            // the best fit; of equal fits the place free the longest; of those the first in the list
            Opening? best = null;
            var bestFit = -1;
            var bestFreed = long.MaxValue;
            foreach (var opening in openings)
            {
                if (opening.Seat.Island != resident.Island || !_vacant.TryGetValue(opening.Seat, out var freed)) continue;
                var fit = Fit(resident, opening.Role);
                if (fit < bestFit || fit == bestFit && freed >= bestFreed) continue;
                (best, bestFit, bestFreed) = (opening, fit, freed);
            }
            if (best is not { } chosen) continue;
            _jobs[resident.Id] = chosen.Seat;
            _vacant.Remove(chosen.Seat);
            village.Publish(new Hired(tick, resident.Id, resident.Island, chosen.Role));
        }
    }

    // the builders of every ordinary island, then the jobs of the buildings that stand, by id
    private List<Opening> Openings(IslandMap map, World world)
    {
        var openings = new List<Opening>();
        foreach (var island in map.Islands)
        {
            if (island.Eddie) continue;
            for (var index = 0; index < _settings.Builders; index++)
                openings.Add(new Opening(new Seat(island.Id, -1, 0, index), _settings.BuilderRole));
        }
        foreach (var structure in world.Standing)
        {
            if (!InEconomy(map, structure) || !_blueprints.TryGetValue(structure.Blueprint, out var blueprint) || blueprint.Jobs is not { } jobs) continue;
            for (var job = 0; job < jobs.Count; job++)
                for (var index = 0; index < jobs[job].Count; index++)
                    openings.Add(new Opening(new Seat(structure.Island, structure.Id, job, index), jobs[job].Role));
        }
        return openings;
    }

    private int Fit(Resident resident, string role) =>
        _settings.Fits.TryGetValue(role, out var interests) ? resident.Interests.Count(interests.Contains) : 0;

    private JobPlace? PlaceOf(World world, Seat seat)
    {
        if (seat.Structure < 0)
        {
            // a builder works at a site of his island, the builders spread over them; without one he waits where the island gathers
            var sites = world.Sites.Where(site => site.Island == seat.Island).ToList();
            if (sites.Count == 0) return new JobPlace(-1, Gathering(world, seat.Island), _settings.BuilderRole, _settings.BuilderShift, true);
            var site = sites[seat.Index % sites.Count];
            return new JobPlace(site.Id, Spot(site), _settings.BuilderRole, _settings.BuilderShift, true);
        }
        var structure = world.Standing.FirstOrDefault(standing => standing.Id == seat.Structure);
        if (structure is null || !_blueprints.TryGetValue(structure.Blueprint, out var blueprint) || blueprint.Jobs is not { } jobs || seat.Job >= jobs.Count) return null;
        var job = jobs[seat.Job];
        return new JobPlace(structure.Id, Spot(structure), job.Role, job.Shift, job.Outdoor);
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

    private Cell Gathering(World world, int island)
    {
        var map = _village!.Map;
        return world.Places?.GatheringOn(island) is { } gathering && map.IsWalkable(gathering) && map.IslandOf(gathering) == island
            ? gathering
            : _centres[island];
    }

    // ── working and making ──

    /// <summary>Pays everyone who is at his job and says which workplaces have someone there.</summary>
    private HashSet<int> Labour(World world)
    {
        var staffed = new HashSet<int>();
        if (world.Day is not { } day) return staffed;
        foreach (var (resident, seat) in _jobs)
        {
            if (PlaceOf(world, seat) is not { } job || !AtWork(day, resident, job.Place)) continue;
            if (seat.Structure >= 0) staffed.Add(seat.Structure);
            Pay(world.Residents[resident], seat.Island);
        }
        return staffed;
    }

    // working, and there: not on the way
    private static bool AtWork(DayModule day, int resident, Cell place)
    {
        try
        {
            var doing = day.Of(resident);
            return doing.Activity == Activity.Work && doing.Walk is null && doing.Place == place;
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
    }

    private void Produce(ModuleContext village, World world, HashSet<int> staffed)
    {
        var tick = village.Clocks.Tick;
        foreach (var structure in world.Standing)
        {
            if (!staffed.Contains(structure.Id) || !InEconomy(village.Map, structure)
                || !_blueprints.TryGetValue(structure.Blueprint, out var blueprint) || blueprint.Recipe is not { } recipe) continue;
            if (!_batches.TryGetValue(structure.Id, out var worked))
            {
                // a new batch: a tree to fell, and what goes in, from the stock
                Cell? tree = null;
                if (Fells(blueprint) && (tree = Tree(village.Map, world, structure)) is null) continue;
                if (!TryTake(structure.Island, recipe.In)) continue;
                if (tree is { } felled) _felled[felled] = tick + _settings.ForestRegrowTicks;
            }
            if (++worked < recipe.Ticks)
            {
                _batches[structure.Id] = worked;
                continue;
            }
            _batches.Remove(structure.Id);
            foreach (var (material, amount) in recipe.Out.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                Add(structure.Island, material, amount);
                village.Publish(new Produced(tick, structure.Island, material, amount));
            }
        }
        var standing = world.Standing.Select(structure => structure.Id).ToHashSet();
        foreach (var gone in _batches.Keys.Where(id => !standing.Contains(id)).ToList()) _batches.Remove(gone);
    }

    // a workplace that takes nothing from the stock and stands in the forest lives off the forest
    private static bool Fells(Blueprint blueprint) =>
        blueprint.Recipe is { In.Count: 0 } && blueprint.On.Contains(Ground.Forest);

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
                    _ => 0,
                };
                var paid = Math.Min(due, treasury.Cash);
                treasury.Cash -= paid;
                resident.Money += paid;
                if (resident.Phase == LifePhase.Retired) pensions += paid;
                else allowances += paid;
            }

            Shop(island.Id, residents, treasury);

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
    }

    // every household buys what it needs from the stock, of the first material in stock that it can pay
    private void Shop(int island, List<Resident> residents, Treasury treasury)
    {
        foreach (var household in residents.GroupBy(resident => resident.Household ?? -1 - resident.Id))
        {
            var members = household.ToList();
            foreach (var (_, amount, goods) in _needs)
            {
                var units = amount.PerPerson * members.Count + amount.PerHousehold;
                for (var unit = 0; unit < units; unit++)
                {
                    var purse = members.Sum(member => member.Money);
                    var (material, price) = goods.FirstOrDefault(good => good.Price <= purse && Count(island, good.Material) > 0);
                    if (material is null) break;
                    Add(island, material, -1);
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

    private void Add(int island, string material, int amount)
    {
        if (!_stock.TryGetValue(island, out var stock)) _stock[island] = stock = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var count = stock.GetValueOrDefault(material) + amount;
        if (count == 0) stock.Remove(material);
        else stock[material] = count;
    }

    private static IEnumerable<Cell> Land(IslandMap map, int island) =>
        map.Islands.Single(i => i.Id == island).Cells.Where(map.IsWalkable);

    private static DateOnly Today(ModuleContext village) => DateOnly.FromDateTime(village.Clocks.Calendar.Now.DateTime);

    private void Read(ModuleContext village)
    {
        _village = village;
        _settings = village.Data.Section<EconomySettings>(Section);
        var catalog = village.Data.Section<Catalog>("catalog");
        _blueprints = catalog.Blueprints.ToDictionary(blueprint => blueprint.Id);
        var materials = catalog.Materials.Select(material => material.Id).ToHashSet();

        if (_settings.Builders < 0 || _settings.ForestRegrowTicks <= 0 || _settings.ForestRange < 0 || _settings.SubsidyDamping <= 0
            || _settings.SpendPercent is < 0 or > 100 || _settings.WagePerHour < 0)
            throw new InvalidDataException($"The data section '{Section}' has a number out of range.");
        foreach (var (island, stock) in _settings.Start.Stock)
            foreach (var (material, amount) in stock)
                if (!materials.Contains(material) || amount < 0)
                    throw new InvalidDataException($"The data section '{Section}' starts {island} with {amount} of '{material}'.");

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
                item.Value.Spent, _wages.GetValueOrDefault(item.Key), Stock(item.Key), item.Value.Departed))],
            [.. _jobs.Select(job => new SavedJob(job.Key, job.Value, _owed.GetValueOrDefault(job.Key)))],
            [.. _vacant.OrderBy(item => (item.Key.Island, item.Key.Structure, item.Key.Job, item.Key.Index)).Select(item => new SavedVacancy(item.Key, item.Value))],
            [.. _batches.Select(batch => new SavedBatch(batch.Key, batch.Value))],
            [.. _felled.OrderBy(item => (item.Key.Row, item.Key.Col)).Select(item => new SavedFelled(item.Key, item.Value))]), SaveJson.Options)!;

    public void Load(ModuleContext village, JsonNode data, int version)
    {
        Read(village);
        var saved = data.Deserialize<SavedWork>(SaveJson.Options) ?? throw new InvalidDataException("The work part of the save is empty.");
        _day = saved.Day;
        _treasuries.Clear();
        _stock.Clear();
        _wages.Clear();
        _jobs.Clear();
        _owed.Clear();
        _vacant.Clear();
        _batches.Clear();
        _felled.Clear();
        foreach (var island in village.Map.Islands)
        {
            _treasuries[island.Id] = new Treasury();
            _wages[island.Id] = 0;
        }
        foreach (var island in saved.Islands)
        {
            _treasuries[island.Id] = new Treasury { Cash = island.Cash, Subsidy = island.Subsidy, Upkeep = island.Upkeep, Spent = island.Spent, Departed = island.Departed };
            _wages[island.Id] = island.Wages;
            _stock[island.Id] = new SortedDictionary<string, int>(island.Stock.ToDictionary(), StringComparer.Ordinal);
        }
        foreach (var job in saved.Jobs)
        {
            _jobs[job.Resident] = job.Seat;
            _owed[job.Resident] = job.Owed;
        }
        foreach (var vacancy in saved.Vacant) _vacant[vacancy.Seat] = vacancy.Freed;
        foreach (var batch in saved.Batches) _batches[batch.Structure] = batch.Worked;
        foreach (var felled in saved.Felled) _felled[felled.Cell] = felled.Until;
    }

    /// <summary>A place to work: a builder's of the island (<see cref="Structure"/> −1), or one of a building's jobs.</summary>
    private readonly record struct Seat(int Island, int Structure, int Job, int Index);

    private readonly record struct Opening(Seat Seat, string Role);

    private sealed record World(
        People.People People,
        Dictionary<int, Resident> Residents,
        DayModule? Day,
        IPlaces? Places,
        IReadOnlyList<StructureView> Standing,
        IReadOnlyList<StructureView> Sites);

    private sealed record SavedWork(DateOnly Day, IReadOnlyList<SavedIsland> Islands, IReadOnlyList<SavedJob> Jobs,
        IReadOnlyList<SavedVacancy> Vacant, IReadOnlyList<SavedBatch> Batches, IReadOnlyList<SavedFelled> Felled);

    private sealed record SavedIsland(int Id, long Cash, long Subsidy, long Upkeep, long Spent, long Wages, IReadOnlyDictionary<string, int> Stock, long Departed = 0);

    private sealed record SavedJob(int Resident, Seat Seat, long Owed);

    private sealed record SavedVacancy(Seat Seat, long Freed);

    private sealed record SavedBatch(int Structure, int Worked);

    private sealed record SavedFelled(Cell Cell, long Until);
}
