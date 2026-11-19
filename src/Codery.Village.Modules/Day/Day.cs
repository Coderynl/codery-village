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

namespace Codery.Village.Modules.Day;

/// <summary>
/// The rhythm of the day. Every quarter of an hour of the Dutch calendar each resident gets an activity from
/// the schedule in the data, after his phase, the shift of his job, the weekend and his character, and walks
/// to its place. Free time and wandering go to a place where there is something: the fire, the water, a tree, a
/// neighbour's door; a friendly resident spends his free time helping at a building site. An ordinary island is
/// never empty: when too few are outside, the most outgoing one who is awake goes for a wander; Eddie's islet keeps
/// Eddie's own rhythm. Other modules send residents on errands (<see cref="Send"/>): to a target and back to what
/// they were doing. Every step wears the tile a little; a well-walked tile becomes a path, which is walked faster
/// and preferred. After a stop the village does at once what the hour asks.
/// </summary>
public sealed class Day : IModule
{
    private const string Free = "free";
    private const string Out = "out";
    private const string Idle = "idle";
    private static readonly string[] RhythmNames = ["toddler", "pupil", "student", "day", "early", "night", Idle, "retired", "eddie"];
    private static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);
    private static readonly IReadOnlyList<OutingChoice> AtTheWater = [new(Outing.Water, 1)];
    private static readonly IReadOnlyList<LifePhase> Grown = [LifePhase.Working, LifePhase.Retired];

    private readonly Dictionary<int, Plan> _plans = [];
    private readonly Wear _wear = new();
    // who came back from an errand in this tick, with the errand; not saved, it is gone at the next tick
    private readonly Dictionary<int, Errand> _back = [];
    private ModuleContext? _village;
    // the quarter the activities belong to
    private DateTimeOffset _quarter;
    // started, and not yet stepped; not saved
    private bool _started;
    private DaySettings _settings = null!;
    private LifeCourse _course = null!;
    private Dictionary<string, Schedule> _rhythms = [];
    private Dictionary<int, Grounds> _grounds = [];
    private TimeOnly _dayFrom;
    private TimeOnly _dayTo;
    // per month, when it gets dark and light; null when the data does not say
    private TimeOnly[]? _dusk;
    private TimeOnly[]? _dawn;

    public string Name => "day";

    public int Version => 1;

    /// <summary>What the resident does now. Throws <see cref="KeyNotFoundException"/> for someone who does not live here.</summary>
    public Doing Of(int resident) =>
        _plans.TryGetValue(resident, out var plan) ? plan.Doing : throw new KeyNotFoundException($"No resident {resident} lives in the village.");

    /// <summary>The errand the resident is on, or null.</summary>
    public Trip? TripOf(int resident) => _plans.TryGetValue(resident, out var plan) ? plan.Trip : null;

    /// <summary>
    /// The errand the resident came back from in this tick, or null. A module that steps after Day sees it in the
    /// same tick, before the <see cref="Returned"/> event reaches it in the next.
    /// </summary>
    public Errand? BackFrom(int resident) => _back.GetValueOrDefault(resident);

    /// <summary>The tiles that are paths now.</summary>
    public IReadOnlyCollection<Cell> Paths => _wear.Paths;

    /// <summary>
    /// Sends the resident on an errand from where he is now: to its target, and back to the place he was at or going
    /// to, where he goes on with what he was doing. He says <see cref="Reached"/> at the target and
    /// <see cref="Returned"/> when he is back. False when he does not live here, is on an errand already, or the
    /// target is not on his island. An errand lasts while his block does; a new block or a stop ends it.
    /// </summary>
    public bool Send(int resident, Errand errand)
    {
        if (errand.Kind is not (Activity.Fetch or Activity.Bring or Activity.Tend))
            throw new ArgumentException($"{errand.Kind} is no errand.", nameof(errand));
        if (_village is not { } village || !_plans.TryGetValue(resident, out var plan) || plan.Trip is not null) return false;
        var tick = village.Clocks.Tick;
        var from = Walker.Position(plan.Doing, tick);
        if (!village.Map.IsWalkable(errand.Target) || village.Map.IslandOf(errand.Target) != village.Map.IslandOf(from)) return false;
        plan.Trip = new Trip(errand, plan.Doing.Activity, plan.Doing.Place, Back: false);
        plan.Doing = Walker.Go(village.Map, errand.Kind, from, errand.Target, false, true, tick, _settings.TicksPerCell, Cost, errand.There);
        if (plan.Doing.Walk is null) TurnBack(village, resident, plan, tick);
        return true;
    }

    public void Start(ModuleContext village)
    {
        Read(village);
        _wear.Load(null);
        Settle(village);
        _started = true;
    }

    public void Step(ModuleContext village)
    {
        _village = village;
        _back.Clear();
        if (_started)
        {
            // at the start the modules after Day had not started yet: now that the jobs are known, everyone does what
            // they ask; in a new quarter that follows below anyway
            _started = false;
            if (QuarterOf(village) == _quarter) Choose(village, walk: false, newQuarter: false);
        }
        var tick = village.Clocks.Tick;
        var today = DateOnly.FromDateTime(village.Clocks.Calendar.Now.DateTime);
        _wear.Turn(today, _settings.PathWindowDays, _settings.PathKeep);
        foreach (var id in _plans.Keys.Order().ToList())
        {
            var plan = _plans[id];
            if (plan.Doing.Walk is not { } walk) continue;
            if (Walker.SteppedOn(walk, tick) is { } stepped) _wear.Step(stepped, today, _settings.PathWindowDays, _settings.PathThreshold, _settings.PathKeep);
            if (!Walker.Arrived(walk, tick)) continue;
            if (plan.Trip is { Back: false }) TurnBack(village, id, plan, tick);
            else if (plan.Trip is { Back: true }) Finish(village, id, plan, tick);
            else plan.Doing = plan.Doing with { At = plan.Doing.Place, Walk = null, Inside = plan.InsideThere, Carry = null };
        }

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

        if (plan?.Trip is { } trip)
        {
            // an errand goes on while its block does; else he leaves it where he is, for what he was doing
            if (plan.Block == key && now.Walk) return;
            plan.Trip = null;
            plan.Doing = now.Walk
                ? Walker.Go(now.Map, trip.Was, Walker.Position(plan.Doing, now.Tick), trip.Home, plan.InsideThere, true, now.Tick, _settings.TicksPerCell, Cost)
                : new Doing(trip.Was, trip.Home, trip.Home, null, plan.InsideThere);
        }

        if (plan is not null && plan.Block == key)
        {
            // the same block goes on; one who had no job when it began goes to work once he has one, a wanderer
            // wanders on to another place each quarter, a helper goes where the work is now, a worker whose work
            // moved, such as a builder sent to a new site, goes to where it is now; when it gets dark, one out for
            // his free time picks a place again, mostly the fire, and a chat at the door goes inside
            var doing = plan.Doing;
            if (job is not null && doing.Activity != Activity.Work && IsWork(block))
            {
                var (working, there, indoors) = PlaceOf(now, resident, Activity.Work, job);
                Go(now, resident, key, working, there, indoors);
            }
            else if (doing.Activity == Activity.Wander && newQuarter) Go(now, resident, key, Activity.Wander, PlaceToGo(now, resident), inside: false);
            else if (doing.Activity == Activity.Help && newQuarter && now.Places?.HelpOn(resident.Island) != doing.Place)
            {
                var (free, there, indoors) = PlaceOf(now, resident, Activity.Leisure, job);
                Go(now, resident, key, free, there, indoors);
            }
            else if (doing.Activity == Activity.Work && PlaceOf(now, resident, Activity.Work, job) is var (working, moved, indoors) && (working != Activity.Work || moved != doing.Place))
                Go(now, resident, key, working, moved, indoors);
            else if (doing.Activity == Activity.Leisure && newQuarter && FallsDark()) Go(now, resident, key, Activity.Leisure, PlaceToGo(now, resident), inside: false);
            else if (doing.Activity == Activity.Visit && !plan.InsideThere && newQuarter && IsDark() == true && HostBy(now, resident, doing.Place) is { } host)
                Go(now, resident, key, Activity.Visit, host, inside: true);
            else Go(now, resident, key, doing.Activity, doing.Place, plan.InsideThere);
            return;
        }

        var activity = block.Do switch
        {
            Free => PickFree(now.Random),
            Out => GoOut(now.Random, resident),
            var named => Enum.Parse<Activity>(named, ignoreCase: true),
        };
        // outside an "out" block, free time out of doors follows the month too: one draw, always made, keeps the rest
        // at home, so the stream does not depend on the outcome
        var stays = block.Do != Out && _settings.OutdoorByMonth is not null && now.Random.NextDouble() >= Outdoors();
        var (chosen, place, inside) = PlaceOf(now, resident, activity, job);
        if (stays && chosen is Activity.Leisure or Activity.Wander) (chosen, place, inside) = PlaceOf(now, resident, Activity.Home, job);
        Go(now, resident, key, chosen, place, inside);
    }

    private static bool IsWork(Block block) => string.Equals(block.Do, nameof(Activity.Work), StringComparison.OrdinalIgnoreCase);

    // whether it is dark this quarter, or at another moment; null when the data has no dusk and dawn
    private bool? IsDark() => IsDark(_quarter);

    private bool? IsDark(DateTimeOffset at)
    {
        if (_dusk is null || _dawn is null) return null;
        var time = TimeOnly.FromDateTime(at.DateTime);
        return time >= _dusk[at.Month - 1] || time < _dawn[at.Month - 1];
    }

    // whether this is the first dark quarter of the evening
    private bool FallsDark() => IsDark() == true && IsDark(_quarter - Quarter) == false;

    // how much free time out of doors this month has, against an ordinary one
    private double Outdoors() => _settings.OutdoorByMonth is { } months ? months[_quarter.Month - 1] : 1;

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
        var chance = Math.Clamp((evening.Chance + evening.PerExtraversion * (resident.Traits.Extraversion - 50) / 50.0) * Outdoors(), 0, 1);
        var goes = random.NextDouble() < chance;
        var visits = random.NextDouble() < evening.Visit;
        return !goes ? Activity.Home : visits ? Activity.Visit : Activity.Leisure;
    }

    /// <summary>The block the resident's rhythm is in this quarter, and a key that names it for this day.</summary>
    private (Block Block, string Key) BlockOf(Now now, Resident resident, JobPlace? job)
    {
        var name = RhythmOf(now.People, resident, job);
        if (!_rhythms.TryGetValue(name, out var schedule))
            throw new InvalidDataException($"The data section 'day' has no rhythm '{name}'.");

        // the careless live a little later: they get up later and go to bed later
        var late = _settings.LateMinutes * Math.Max(0, 50 - resident.Traits.Conscientiousness) / 50.0;
        var moment = _quarter.DateTime - TimeSpan.FromMinutes(late);
        var date = moment.Date;
        var (weekend, blocks) = BlocksOn(schedule, date);
        var time = TimeOnly.FromDateTime(moment);
        var at = blocks.FindLastIndex(block => block.From <= time);
        if (at < 0)
        {
            // before the first block of the day, the last block of the day before goes on, from that day's list
            date = date.AddDays(-1);
            (weekend, blocks) = BlocksOn(schedule, date);
            at = blocks.Count - 1;
        }
        var started = date + blocks[at].From.ToTimeSpan();
        return (blocks[at].Block, string.Create(CultureInfo.InvariantCulture, $"{name} {(weekend ? "weekend" : "weekday")} {started:yyyy-MM-dd HH:mm}"));
    }

    private static (bool Weekend, List<Timed> Blocks) BlocksOn(Schedule schedule, DateTime date) =>
        date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday && schedule.Weekend is { } weekend
            ? (true, weekend)
            : (false, schedule.Weekday);

    private string RhythmOf(People.People people, Resident resident, JobPlace? job)
    {
        if (resident.IsEddie) return "eddie";
        return resident.Phase switch
        {
            LifePhase.Child => people.AgeOf(resident) < _course.SchoolFrom ? "toddler" : "pupil",
            LifePhase.Student => "student",
            LifePhase.Working => job is null ? Idle : job.Shift.ToString().ToLowerInvariant(),
            _ => "retired",
        };
    }

    // ── places ──

    /// <summary>
    /// Where the activity is done and whether that is inside. Work without a job is free time; a visit is a chat at
    /// the host's door while it is light and inside after dark; a visit without anyone to visit stays home.
    /// </summary>
    private (Activity Activity, Cell Place, bool Inside) PlaceOf(Now now, Resident resident, Activity activity, JobPlace? job)
    {
        var island = resident.Island;
        var home = HomeOf(now, resident);
        var (chosen, place, inside) = activity switch
        {
            Activity.Sleep or Activity.Home or Activity.Study => (activity, home, true),
            // without a job, he has free time
            Activity.Work => job is null ? PlaceOf(now, resident, PickFree(now.Random), job) : (activity, job.Place, !job.Outdoor),
            Activity.School => now.Places?.SchoolOn(island) is { } school ? (activity, school, true) : (activity, Gathering(now, island), false),
            Activity.Shop => (activity, Gathering(now, island), true),
            Activity.Visit => VisitFrom(now, resident) is not { } host ? (Activity.Home, home, true)
                : IsDark() == false && DoorOf(now, host, island) is { } door ? (activity, door, false)
                : (activity, host, true),
            // free time: a friendly one helps where the island builds, the others go where there is something
            Activity.Leisure or Activity.Help => resident.Traits.Agreeableness >= _settings.HelpFrom && (_settings.HelpPhases ?? Grown).Contains(resident.Phase)
                && now.Places?.HelpOn(island) is { } site
                ? (Activity.Help, site, false)
                : (Activity.Leisure, PlaceToGo(now, resident), false),
            _ => (activity, PlaceToGo(now, resident), false),
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

    // where the island gathers, if that is land of the island; else its middle
    private Cell Gathering(Now now, int island) =>
        now.Places?.GatheringOn(island) is { } gathering && now.Map.IsWalkable(gathering) && now.Map.IslandOf(gathering) == island
            ? gathering
            : _grounds[island].Centre;

    // the home of someone of another household on the island
    private Cell? VisitFrom(Now now, Resident resident)
    {
        var own = resident.Household ?? -1 - resident.Id;
        var hosts = now.People.All.Where(other => other.Island == resident.Island && (other.Household ?? -1 - other.Id) != own).ToList();
        return hosts.Count == 0 ? null : HomeOf(now, hosts[now.Random.Next(hosts.Count)]);
    }

    /// <summary>A place where there is something, of a kind drawn by the weights of the data; his home when there is none.</summary>
    private Cell PlaceToGo(Now now, Resident resident)
    {
        var grounds = _grounds[resident.Island];
        var options = new List<(int Weight, IReadOnlyList<Cell> Cells)>();
        var outings = IsDark() == true && _settings.OutingsAfterDark is { } afterDark ? afterDark : _settings.Outings ?? AtTheWater;
        foreach (var choice in outings)
        {
            IReadOnlyList<Cell> cells = choice.To switch
            {
                Outing.Fire => [Gathering(now, resident.Island)],
                Outing.Water => grounds.Coast,
                Outing.Tree => grounds.Trees,
                _ => Doors(now, resident),
            };
            if (cells.Count > 0) options.Add((choice.Weight, cells));
        }
        if (options.Count == 0) return HomeOf(now, resident);
        var roll = now.Random.Next(options.Sum(option => option.Weight));
        foreach (var (weight, cells) in options)
        {
            if (roll >= weight)
            {
                roll -= weight;
                continue;
            }
            return cells[now.Random.Next(cells.Count)];
        }
        return HomeOf(now, resident);
    }

    // the tiles by the doors of the homes of other households of the island
    private static List<Cell> Doors(Now now, Resident resident)
    {
        var doors = new List<Cell>();
        foreach (var home in HomesOfOthers(now, resident))
            if (DoorOf(now, home, resident.Island) is { } door && !doors.Contains(door)) doors.Add(door);
        return doors;
    }

    // the homes of other households of the island, once each
    private static IEnumerable<Cell> HomesOfOthers(Now now, Resident resident)
    {
        var own = resident.Household ?? -1 - resident.Id;
        var homes = new HashSet<Cell>();
        foreach (var other in now.People.All)
        {
            if (other.Island != resident.Island || (other.Household ?? -1 - other.Id) == own) continue;
            if (now.Places?.HomeOf(other.Id) is { } home && homes.Add(home)) yield return home;
        }
    }

    // the tile by the door of a home: the first land beside it of the island that is no lot
    private static Cell? DoorOf(Now now, Cell home, int island)
    {
        foreach (var next in home.Neighbours())
            if (now.Map.IsWalkable(next) && now.Map.IslandOf(next) == island && now.Map.At(next) != Ground.Lot) return next;
        return null;
    }

    // the home of another household by whose door the visitor stands
    private static Cell? HostBy(Now now, Resident resident, Cell door)
    {
        foreach (var home in HomesOfOthers(now, resident))
            if (DoorOf(now, home, resident.Island) == door) return home;
        return null;
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
        var doing = Walker.Go(now.Map, activity, from, place, inside, now.Walk, now.Tick, _settings.TicksPerCell, Cost);
        _plans[resident.Id] = new Plan(key, inside, doing);
    }

    // what a step onto the tile costs: less on a path
    private int Cost(Cell cell) => _wear.IsPath(cell) ? (int)Math.Round(Walker.Step / _settings.PathSpeed, MidpointRounding.AwayFromZero) : Walker.Step;

    // at the target of his errand: he says so, and goes back with what he carries back
    private void TurnBack(ModuleContext village, int resident, Plan plan, long tick)
    {
        var trip = plan.Trip!;
        var errand = trip.Errand;
        village.Publish(new Reached(tick, resident, errand.Kind, errand.Target, errand.There));
        plan.Trip = trip with { Back = true };
        plan.Doing = Walker.Go(village.Map, errand.Kind, errand.Target, trip.Home, plan.InsideThere, true, tick, _settings.TicksPerCell, Cost, errand.Back);
        if (plan.Doing.Walk is null) Finish(village, resident, plan, tick);
    }

    // back from his errand: he goes on with what he was doing
    private void Finish(ModuleContext village, int resident, Plan plan, long tick)
    {
        var trip = plan.Trip!;
        _back[resident] = trip.Errand;
        village.Publish(new Returned(tick, resident, trip.Errand.Kind, trip.Home, trip.Errand.Back));
        plan.Trip = null;
        plan.Doing = new Doing(trip.Was, trip.Home, trip.Home, null, plan.InsideThere);
    }

    /// <summary>
    /// Too few outside on an ordinary island: the most outgoing one who is inside and awake goes for a wander; when
    /// nobody is free, someone of the night shift, and else whoever is most outgoing. He wanders until his
    /// block ends.
    /// </summary>
    private void KeepOutside(Now now)
    {
        var time = TimeOnly.FromDateTime(_quarter.DateTime);
        var least = time >= _dayFrom && time < _dayTo ? _settings.Outside.ByDay : _settings.Outside.Always;
        // Eddie's islet keeps his own rhythm: he sleeps at night like anyone
        foreach (var island in now.Map.Islands.Where(island => !island.Eddie))
        {
            var on = now.People.All.Where(resident => resident.Island == island.Id).ToList();
            var outside = on.Count(resident => !_plans[resident.Id].InsideThere);
            var inside = on.Where(resident => _plans[resident.Id] is { InsideThere: true, Trip: null })
                .OrderBy(resident => Wakefulness(now, resident))
                .ThenByDescending(resident => resident.Traits.Extraversion)
                .ThenBy(resident => resident.Id)
                .ToList();
            for (var i = 0; outside < Math.Min(least, on.Count); i++, outside++)
            {
                var resident = inside[i];
                Go(now, resident, _plans[resident.Id].Block, Activity.Wander, PlaceToGo(now, resident), inside: false);
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
            Activity.Sleep => shift is Shift.Night or Shift.Early ? 4 : 3,
            Activity.Work or Activity.School or Activity.Study => shift == Shift.Night ? 1 : 2,
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
        _village = village;
        _settings = village.Data.Section<DaySettings>("day");
        _course = village.Data.Section<LifeCourse>("life");
        if (_settings.TicksPerCell <= 0) throw new InvalidDataException("The data section 'day' needs ticksPerCell above 0.");
        if (_settings.Free.Count == 0 || _settings.Free.Any(choice => choice.Weight <= 0))
            throw new InvalidDataException("The data section 'day' needs free choices with weights above 0.");
        if (new[] { _settings.Outings, _settings.OutingsAfterDark }.Any(outings => outings?.Any(choice => choice.Weight <= 0) == true))
            throw new InvalidDataException("The data section 'day' needs outings with weights above 0.");
        if (_settings.OutdoorByMonth is { } months && (months.Count != 12 || months.Any(factor => factor < 0)))
            throw new InvalidDataException("The data section 'day' needs outdoorByMonth as twelve factors of 0 or more.");
        if ((_settings.Dusk is null) != (_settings.Dawn is null) || _settings.Dusk is { Count: not 12 } || _settings.Dawn is { Count: not 12 })
            throw new InvalidDataException("The data section 'day' needs dusk and dawn for twelve months, or neither.");
        _dusk = _settings.Dusk?.Select(Time).ToArray();
        _dawn = _settings.Dawn?.Select(Time).ToArray();
        if (_settings.PathWindowDays < 1 || _settings.PathThreshold < 0 || _settings.PathKeep < 0 || _settings.PathSpeed <= 0)
            throw new InvalidDataException("The data section 'day' has a number for paths out of range.");
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
            // an errand or helping is never in a rhythm
            if (block.Do is not (Free or Out) && (!Enum.TryParse<Activity>(block.Do, ignoreCase: true, out var activity) || activity >= Activity.Fetch))
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
            new SavedDay(_quarter, [.. _plans.OrderBy(plan => plan.Key).Select(plan => new SavedPlan(plan.Key, plan.Value.Block, plan.Value.InsideThere, plan.Value.Doing, plan.Value.Trip))],
                _wear.Save()),
            SaveJson.Options)!;

    public void Load(ModuleContext village, JsonNode data, int version)
    {
        Read(village);
        var saved = data.Deserialize<SavedDay>(SaveJson.Options) ?? throw new InvalidDataException("The day part of the save is empty.");
        _quarter = saved.Quarter;
        _plans.Clear();
        foreach (var plan in saved.Plans) _plans[plan.Resident] = new Plan(plan.Block, plan.InsideThere, plan.Doing) { Trip = plan.Trip };
        _wear.Load(saved.Wear);
    }

    private sealed record SavedDay(DateTimeOffset Quarter, IReadOnlyList<SavedPlan> Plans, Wear.SavedWear? Wear = null);

    private sealed record SavedPlan(int Resident, string Block, bool InsideThere, Doing Doing, Trip? Trip = null);

    // ── inner parts ──

    /// <summary>A resident's doing, the block it belongs to, and whether its place is inside.</summary>
    private sealed class Plan(string block, bool insideThere, Doing doing)
    {
        public string Block { get; set; } = block;
        public bool InsideThere { get; } = insideThere;
        public Doing Doing { get; set; } = doing;
        // the errand he is on, if any
        public Trip? Trip { get; set; }
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

    /// <summary>
    /// The tiles of an island: lots (where buildings go), free land, free land by the water, the edge of its woods
    /// (forest beside open land), and the middle.
    /// </summary>
    private sealed record Grounds(IReadOnlyList<Cell> Lots, IReadOnlyList<Cell> Free, IReadOnlyList<Cell> Coast, IReadOnlyList<Cell> Trees, Cell Centre)
    {
        public static Grounds Of(IslandMap map, Island island)
        {
            var land = island.Cells.Where(map.IsWalkable).ToList();
            var lots = land.Where(cell => map.At(cell) == Ground.Lot).ToList();
            var free = land.Where(cell => map.At(cell) != Ground.Lot).ToList();
            if (free.Count == 0) free = land;
            var coast = free.Where(cell => cell.Neighbours().Any(next => !map.IsWalkable(next))).ToList();
            if (coast.Count == 0) coast = free;
            var trees = land.Where(cell => map.At(cell) == Ground.Forest
                && cell.Neighbours().Any(next => map.IsWalkable(next) && map.At(next) is not (Ground.Forest or Ground.Lot))).ToList();
            return new Grounds(lots, free, coast, trees, Middle(land));
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
