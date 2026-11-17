using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codery.Village.Core;
using Codery.Village.Core.Map;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Modules.Contracts;
using Codery.Village.Modules.People;

namespace Codery.Village.Modules.Day;

/// <summary>
/// The rhythm of the day. Every quarter of an hour of the Dutch calendar each resident gets an activity from
/// the schedule in the data, after his phase, the shift of his job, the weekend and his character, and walks
/// to its place. An island is never empty: when too few are outside, the most outgoing one who is awake goes
/// for a wander. After a stop the village does at once what the hour asks.
/// </summary>
public sealed class Day : IModule
{
    private const string Free = "free";
    private const string Out = "out";
    private const string Idle = "idle";
    private const string Early = "early";
    private const string Night = "night";
    private static readonly string[] RhythmNames = ["toddler", "pupil", "student", "day", Early, Night, Idle, "retired", "eddie"];
    private static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);

    private readonly Dictionary<int, Plan> _plans = [];
    // the quarter the activities belong to
    private DateTimeOffset _quarter;
    private DaySettings _settings = null!;
    private LifeCourse _course = null!;
    private Dictionary<string, Schedule> _rhythms = [];
    private Dictionary<int, Grounds> _grounds = [];
    private TimeOnly _dayFrom;
    private TimeOnly _dayTo;

    public string Name => "day";

    public int Version => 1;

    /// <summary>What the resident does now. Throws <see cref="KeyNotFoundException"/> for someone who does not live here.</summary>
    public Doing Of(int resident) =>
        _plans.TryGetValue(resident, out var plan) ? plan.Doing : throw new KeyNotFoundException($"No resident {resident} lives in the village.");

    public void Start(ModuleContext village)
    {
        Read(village);
        Settle(village);
    }

    public void Step(ModuleContext village)
    {
        var tick = village.Clocks.Tick;
        foreach (var plan in _plans.Values)
            if (plan.Doing.Walk is { } walk && Walker.Arrived(walk, tick))
                plan.Doing = plan.Doing with { At = plan.Doing.Place, Walk = null, Inside = plan.InsideThere };

        var quarter = QuarterOf(village);
        if (quarter != _quarter)
        {
            // the next quarter is walked to; after a stop nothing goes on from before it
            if (quarter - _quarter == Quarter) Choose(village, walk: true, newQuarter: true);
            else Settle(village);
            return;
        }
        var people = village.Module<People.People>();
        if (people.All.Count != _plans.Count || people.All.Any(resident => !_plans.ContainsKey(resident.Id)))
            Choose(village, walk: true, newQuarter: false);
    }

    /// <summary>
    /// Chooses the activities of the current quarter and puts everyone at their place at once, without walking.
    /// The module does this after a stop; a test does it to jump a quarter without running its ticks.
    /// </summary>
    public void Settle(ModuleContext village) => Choose(village, walk: false, newQuarter: true);

    // ── choosing ──

    private void Choose(ModuleContext village, bool walk, bool newQuarter)
    {
        var now = new Now(village, village.Module<People.People>(), village.Find<IJobs>(), village.Find<IPlaces>(), walk);
        _quarter = QuarterOf(village);

        var living = now.People.All.Select(resident => resident.Id).ToHashSet();
        foreach (var gone in _plans.Keys.Where(id => !living.Contains(id)).ToList()) _plans.Remove(gone);

        var before = _plans.ToDictionary(plan => plan.Key, plan => plan.Value.Doing);
        foreach (var resident in now.People.All) Decide(now, resident, newQuarter);
        KeepOutside(now);

        // one word per resident whose doing changed, about where he ended up
        foreach (var resident in now.People.All)
        {
            var doing = _plans[resident.Id].Doing;
            if (!before.TryGetValue(resident.Id, out var was) || was != doing)
                village.Publish(new DoingChanged(now.Tick, resident.Id, doing));
        }
    }

    private void Decide(Now now, Resident resident, bool newQuarter)
    {
        var job = now.Jobs?.JobOf(resident.Id);
        var (block, key) = BlockOf(now, resident, job);
        _plans.TryGetValue(resident.Id, out var plan);

        if (plan is not null && plan.Block == key)
        {
            // the same block goes on; a wanderer wanders on to another tile each quarter
            var doing = plan.Doing;
            if (doing.Activity == Activity.Wander && newQuarter) Go(now, resident, key, Activity.Wander, RandomTile(now, resident.Island, coast: false), inside: false);
            else Go(now, resident, key, doing.Activity, doing.Place, plan.InsideThere);
            return;
        }

        var activity = block.Do switch
        {
            Free => PickFree(now.Random),
            Out => GoOut(now.Random, resident),
            var named => Enum.Parse<Activity>(named, ignoreCase: true),
        };
        var (chosen, place, inside) = PlaceOf(now, resident, activity, job);
        Go(now, resident, key, chosen, place, inside);
    }

    private Activity PickFree(Rng random)
    {
        var roll = random.Next(_settings.Free.Sum(choice => choice.Weight));
        foreach (var choice in _settings.Free)
        {
            roll -= choice.Weight;
            if (roll < 0) return choice.Do;
        }
        return _settings.Free[^1].Do;
    }

    // both draws are always made, so the stream does not depend on the outcome
    private Activity GoOut(Rng random, Resident resident)
    {
        var evening = _settings.Evening;
        var chance = Math.Clamp(evening.Chance + evening.PerExtraversion * (resident.Traits.Extraversion - 50) / 50.0, 0, 1);
        var goes = random.NextDouble() < chance;
        var visits = random.NextDouble() < evening.Visit;
        return !goes ? Activity.Home : visits ? Activity.Visit : Activity.Leisure;
    }

    /// <summary>The block the resident's rhythm is in this quarter, and a key that names it for this day.</summary>
    private (Block Block, string Key) BlockOf(Now now, Resident resident, Job? job)
    {
        var name = RhythmOf(now.People, resident, job);
        if (!_rhythms.TryGetValue(name, out var schedule))
            throw new InvalidDataException($"The data section 'day' has no rhythm '{name}'.");

        // the careless live a little later: they get up later and go to bed later
        var late = _settings.LateMinutes * Math.Max(0, 50 - resident.Traits.Conscientiousness) / 50.0;
        var moment = _quarter.DateTime - TimeSpan.FromMinutes(late);
        var weekend = moment.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday && schedule.Weekend is not null;
        var blocks = weekend ? schedule.Weekend! : schedule.Weekday;

        var time = TimeOnly.FromDateTime(moment);
        var at = blocks.FindLastIndex(block => block.From <= time);
        // before the first block of the day, the last one of the day before goes on
        var started = at < 0 ? moment.Date.AddDays(-1) + blocks[^1].From.ToTimeSpan() : moment.Date + blocks[at].From.ToTimeSpan();
        var block = blocks[at < 0 ? blocks.Count - 1 : at].Block;
        return (block, string.Create(CultureInfo.InvariantCulture, $"{name} {(weekend ? "weekend" : "weekday")} {started:yyyy-MM-dd HH:mm}"));
    }

    private string RhythmOf(People.People people, Resident resident, Job? job)
    {
        if (resident.IsEddie) return "eddie";
        return resident.Phase switch
        {
            LifePhase.Child => people.AgeOf(resident) < _course.SchoolFrom ? "toddler" : "pupil",
            LifePhase.Student => "student",
            LifePhase.Working => job?.Shift ?? Idle,
            _ => "retired",
        };
    }

    // ── places ──

    /// <summary>Where the activity is done and whether that is inside; a visit without anyone to visit stays home.</summary>
    private (Activity Activity, Cell Place, bool Inside) PlaceOf(Now now, Resident resident, Activity activity, Job? job)
    {
        var island = resident.Island;
        var home = HomeOf(now, resident);
        var (chosen, place, inside) = activity switch
        {
            Activity.Sleep or Activity.Home or Activity.Study => (activity, home, true),
            // without a job, he works at home, in the yard
            Activity.Work => job is null ? (activity, home, false) : (activity, job.Place, !job.Outdoor),
            Activity.School => now.Places?.SchoolOn(island) is { } school ? (activity, school, true) : (activity, Gathering(now, island), false),
            Activity.Shop => (activity, Gathering(now, island), true),
            Activity.Visit => VisitFrom(now, resident) is { } host ? (activity, host, true) : (Activity.Home, home, true),
            Activity.Leisure => (activity, RandomTile(now, island, coast: true), false),
            _ => (activity, RandomTile(now, island, coast: false), false),
        };
        // a place he cannot walk to is not his
        if (!now.Map.IsWalkable(place) || now.Map.IslandOf(place) != island) place = home;
        return (chosen, place, inside);
    }

    private Cell HomeOf(Now now, Resident resident)
    {
        if (now.Places?.HomeOf(resident.Id) is { } home && now.Map.IslandOf(home) == resident.Island && now.Map.IsWalkable(home)) return home;
        // a lot of his island that follows from his household, so a household shares it
        var grounds = _grounds[resident.Island];
        var key = resident.Household ?? resident.Id;
        var cells = grounds.Lots.Count > 0 ? grounds.Lots : grounds.Free;
        return cells[key * 7 % cells.Count];
    }

    private Cell Gathering(Now now, int island) =>
        now.Places?.GatheringOn(island) ?? _grounds[island].Centre;

    // the home of someone of another household on the island
    private Cell? VisitFrom(Now now, Resident resident)
    {
        var own = resident.Household ?? -1 - resident.Id;
        var hosts = now.People.All.Where(other => other.Island == resident.Island && (other.Household ?? -1 - other.Id) != own).ToList();
        return hosts.Count == 0 ? null : HomeOf(now, hosts[now.Random.Next(hosts.Count)]);
    }

    // a free tile of the island, by the water or anywhere
    private Cell RandomTile(Now now, int island, bool coast)
    {
        var grounds = _grounds[island];
        var cells = coast ? grounds.Coast : grounds.Free;
        return cells[now.Random.Next(cells.Count)];
    }

    // ── going there ──

    private void Go(Now now, Resident resident, string key, Activity activity, Cell place, bool inside)
    {
        _plans.TryGetValue(resident.Id, out var plan);
        if (plan is not null && plan.Doing.Activity == activity && plan.Doing.Place == place && (now.Walk || plan.Doing.Walk is null))
        {
            // nothing changes but the block it belongs to
            plan.Block = key;
            return;
        }
        var from = plan is null ? place : Walker.Position(plan.Doing, now.Tick);
        var doing = Walker.Go(now.Map, activity, from, place, inside, now.Walk, now.Tick, _settings.TicksPerCell);
        _plans[resident.Id] = new Plan(key, inside, doing);
    }

    /// <summary>
    /// Too few outside on an island: the most outgoing one who is inside and awake goes for a wander; when
    /// nobody is free, someone of the night shift, and else whoever is most outgoing. He wanders until his
    /// block ends.
    /// </summary>
    private void KeepOutside(Now now)
    {
        var time = TimeOnly.FromDateTime(_quarter.DateTime);
        var least = time >= _dayFrom && time < _dayTo ? _settings.Outside.ByDay : _settings.Outside.Always;
        foreach (var island in now.Map.Islands)
        {
            var on = now.People.All.Where(resident => resident.Island == island.Id).ToList();
            var outside = on.Count(resident => !_plans[resident.Id].InsideThere);
            var inside = on.Where(resident => _plans[resident.Id].InsideThere)
                .OrderBy(resident => Wakefulness(now, resident))
                .ThenByDescending(resident => resident.Traits.Extraversion)
                .ThenBy(resident => resident.Id)
                .ToList();
            for (var i = 0; outside < Math.Min(least, on.Count); i++, outside++)
            {
                var resident = inside[i];
                Go(now, resident, _plans[resident.Id].Block, Activity.Wander, RandomTile(now, island.Id, coast: false), inside: false);
            }
        }
    }

    // who goes first: the awake in their own time, the night shift at work, the rest at work or school,
    // then sleepers, those of the early and night shift last, since they sleep by day
    private int Wakefulness(Now now, Resident resident)
    {
        var shift = now.Jobs?.JobOf(resident.Id)?.Shift;
        return _plans[resident.Id].Doing.Activity switch
        {
            Activity.Sleep => shift is Night or Early ? 4 : 3,
            Activity.Work or Activity.School or Activity.Study => shift == Night ? 1 : 2,
            _ => 0,
        };
    }

    private static DateTimeOffset QuarterOf(ModuleContext village)
    {
        var now = village.Clocks.Calendar.Now;
        return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute - now.Minute % 15, 0, now.Offset);
    }

    // ── the data ──

    private void Read(ModuleContext village)
    {
        _settings = village.Data.Section<DaySettings>("day");
        _course = village.Data.Section<LifeCourse>("life");
        if (_settings.TicksPerCell <= 0) throw new InvalidDataException("The data section 'day' needs ticksPerCell above 0.");
        if (_settings.Free.Count == 0 || _settings.Free.Any(choice => choice.Weight <= 0))
            throw new InvalidDataException("The data section 'day' needs free choices with weights above 0.");
        _dayFrom = Time(_settings.Outside.DayFrom);
        _dayTo = Time(_settings.Outside.DayTo);

        var missing = RhythmNames.Where(name => !_settings.Rhythms.ContainsKey(name)).ToList();
        if (missing.Count > 0) throw new InvalidDataException($"The data section 'day' has no rhythm for {string.Join(", ", missing)}.");
        _rhythms = _settings.Rhythms.ToDictionary(rhythm => rhythm.Key, rhythm =>
            new Schedule(Blocks(rhythm.Key, rhythm.Value.Weekday), rhythm.Value.Weekend is { } weekend ? Blocks(rhythm.Key, weekend) : null));

        _grounds = village.Map.Islands.ToDictionary(island => island.Id, island => Grounds.Of(village.Map, island));
    }

    private static List<Timed> Blocks(string rhythm, IReadOnlyList<Block> blocks)
    {
        if (blocks.Count == 0) throw new InvalidDataException($"The rhythm '{rhythm}' has no blocks.");
        var timed = blocks.Select(block => new Timed(Time(block.From), block)).ToList();
        for (var i = 1; i < timed.Count; i++)
            if (timed[i].From <= timed[i - 1].From) throw new InvalidDataException($"The blocks of the rhythm '{rhythm}' are not in the order of the day.");
        foreach (var block in blocks)
            if (block.Do is not (Free or Out) && !Enum.TryParse<Activity>(block.Do, ignoreCase: true, out _))
                throw new InvalidDataException($"The rhythm '{rhythm}' does '{block.Do}', which is no activity.");
        return timed;
    }

    private static TimeOnly Time(string text) =>
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw new InvalidDataException($"The data section 'day' has the time '{text}', which is not HH:mm.");

    // ── saving ──

    public JsonNode Save() =>
        JsonSerializer.SerializeToNode(
            new SavedDay(_quarter, [.. _plans.OrderBy(plan => plan.Key).Select(plan => new SavedPlan(plan.Key, plan.Value.Block, plan.Value.InsideThere, plan.Value.Doing))]),
            SaveJson.Options)!;

    public void Load(ModuleContext village, JsonNode data, int version)
    {
        Read(village);
        var saved = data.Deserialize<SavedDay>(SaveJson.Options) ?? throw new InvalidDataException("The day part of the save is empty.");
        _quarter = saved.Quarter;
        _plans.Clear();
        foreach (var plan in saved.Plans) _plans[plan.Resident] = new Plan(plan.Block, plan.InsideThere, plan.Doing);
    }

    private sealed record SavedDay(DateTimeOffset Quarter, IReadOnlyList<SavedPlan> Plans);

    private sealed record SavedPlan(int Resident, string Block, bool InsideThere, Doing Doing);

    // ── inner parts ──

    /// <summary>A resident's doing, the block it belongs to, and whether its place is inside.</summary>
    private sealed class Plan(string block, bool insideThere, Doing doing)
    {
        public string Block { get; set; } = block;
        public bool InsideThere { get; } = insideThere;
        public Doing Doing { get; set; } = doing;
    }

    private sealed record Timed(TimeOnly From, Block Block);

    private sealed record Schedule(List<Timed> Weekday, List<Timed>? Weekend);

    /// <summary>What a choice needs to know, gathered once per quarter.</summary>
    private sealed record Now(ModuleContext Village, People.People People, IJobs? Jobs, IPlaces? Places, bool Walk)
    {
        public IslandMap Map => Village.Map;
        public Rng Random => Village.Random;
        public long Tick => Village.Clocks.Tick;
    }

    /// <summary>The tiles of an island: lots (where buildings go), free land, free land by the water, and the middle.</summary>
    private sealed record Grounds(IReadOnlyList<Cell> Lots, IReadOnlyList<Cell> Free, IReadOnlyList<Cell> Coast, Cell Centre)
    {
        public static Grounds Of(IslandMap map, Island island)
        {
            var land = island.Cells.Where(map.IsWalkable).ToList();
            var lots = land.Where(cell => map.At(cell) == Ground.Lot).ToList();
            var free = land.Where(cell => map.At(cell) != Ground.Lot).ToList();
            if (free.Count == 0) free = land;
            var coast = free.Where(cell => cell.Neighbours().Any(next => !map.IsWalkable(next))).ToList();
            if (coast.Count == 0) coast = free;
            return new Grounds(lots, free, coast, Middle(land));
        }

        // the walkable tile nearest the middle of the island
        private static Cell Middle(List<Cell> land)
        {
            var row = land.Average(cell => cell.Row);
            var col = land.Average(cell => cell.Col);
            return land.MinBy(cell => (cell.Row - row) * (cell.Row - row) + (cell.Col - col) * (cell.Col - col));
        }
    }
}
