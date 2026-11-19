using System.Text.Json.Nodes;
using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;
using Codery.Village.Modules.Contracts;
using Codery.Village.Modules.Day;
using Codery.Village.Modules.People;

namespace Codery.Village.Tests;

public class DayTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly IslandMap RealMap = MapText.Parse(File.ReadAllText(Path.Combine(Folder, "map.txt")));
    private static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);
    // one tick of the engine on the simulated clock
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    /// <summary>A Dutch summer time on a day of June 2026; the first of June is a Monday.</summary>
    private static DateTimeOffset June(int day, int hour, int minute = 0) => new(2026, 6, day, hour, minute, 0, TimeSpan.FromHours(2));

    /// <summary>The real life, people and day data, changed as the test needs.</summary>
    private static DataSet Data(Action<JsonObject>? day = null, Action<JsonObject>? people = null)
    {
        JsonObject Read(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, name + ".json")))!.AsObject();
        var dayJson = Read("day");
        var peopleJson = Read("people");
        day?.Invoke(dayJson);
        people?.Invoke(peopleJson);
        return new DataSet(new Dictionary<string, string>
        {
            ["life"] = Read("life").ToJsonString(),
            ["people"] = peopleJson.ToJsonString(),
            ["day"] = dayJson.ToJsonString(),
        });
    }

    private sealed record Village(Engine Engine, SimulatedTime Time, People People, Day Day, Probe Probe, FakeJobs? Jobs)
    {
        /// <summary>Moves the calendar a quarter on and lets Day settle it, without running the ticks between.</summary>
        public void SettleNextQuarter()
        {
            Time.Advance(Quarter);
            Day.Settle(Probe.Context);
        }

        /// <summary>Moves the calendar on to the next quarter and runs one tick there: Day sees the next quarter, as it would running.</summary>
        public void StepIntoNextQuarter()
        {
            Time.Advance(Quarter);
            Engine.Step();
        }

        public DateTimeOffset Now => Time.GetUtcNow().ToOffset(TimeSpan.FromHours(2));
    }

    private static IModule[] Modules(bool fakes, params IModule[] more) =>
        fakes
            ? [new People(), new FakeJobs(), new FakePlaces(), .. more, new Day(), new Probe()]
            : [new People(), .. more, new Day(), new Probe()];

    private static Village Of(Engine engine, SimulatedTime time, IReadOnlyList<IModule> modules) =>
        new(engine, time, modules.OfType<People>().Single(), modules.OfType<Day>().Single(), modules.OfType<Probe>().Single(),
            modules.OfType<FakeJobs>().SingleOrDefault());

    private static Village Start(ulong seed, DateTimeOffset at, bool fakes = true, DataSet? data = null, params IModule[] more)
    {
        var time = new SimulatedTime(at);
        var modules = Modules(fakes, more);
        var engine = new Engine(modules, data ?? Data(), RealMap, seed, time);
        engine.Start();
        return Of(engine, time, modules);
    }

    // the islands where someone is always outside: Eddie's islet keeps his own rhythm
    private static IEnumerable<int> Ordinary => RealMap.Islands.Where(island => !island.Eddie).Select(island => island.Id);

    public static TheoryData<ulong> Seeds => [.. Enumerable.Range(1, 10).Select(seed => (ulong)seed)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void By_day_some_are_always_outside(ulong seed)
    {
        var village = Start(seed, June(1, 0));

        for (var quarter = 0; quarter < 7 * 24 * 4; quarter++)
        {
            if (quarter > 0) village.SettleNextQuarter();
            var time = village.Now.TimeOfDay;
            // the night is honest: nobody is pulled out of bed to fill it
            if (time < TimeSpan.FromHours(8) || time >= TimeSpan.FromHours(18)) continue;
            foreach (var island in Ordinary)
            {
                var on = village.People.On(island).ToList();
                var outside = on.Count(resident => !village.Day.Of(resident.Id).Inside);
                Assert.True(outside >= 3, $"{village.Now:ddd HH:mm} island {island}: {outside} outside of {on.Count}, at least 3");
            }
        }
    }

    [Fact]
    public void Eddie_sleeps_at_night_and_is_up_on_his_islet_by_day()
    {
        foreach (var fakes in new[] { true, false })
        {
            var village = Start(1, June(2, 3), fakes);
            var eddie = village.People.All.Single(resident => resident.IsEddie);

            var doing = village.Day.Of(eddie.Id);
            Assert.Equal(Activity.Sleep, doing.Activity);
            Assert.True(doing.Inside);

            // by day he is up on his islet; without a workshop he has no work to go to
            village.Time.Advance(TimeSpan.FromHours(8));
            village.Day.Settle(village.Probe.Context);
            Assert.NotEqual(Activity.Sleep, village.Day.Of(eddie.Id).Activity);
            Assert.Equal(eddie.Island, RealMap.IslandOf(village.Day.Of(eddie.Id).Place));
        }
    }

    [Fact]
    public void Night_workers_sleep_by_day()
    {
        var checkedOnes = new HashSet<Shift>();
        foreach (var seed in new ulong[] { 1, 2, 3 })
        {
            var village = Start(seed, June(1, 0));
            for (var quarter = 0; quarter < 7 * 24 * 4; quarter++)
            {
                if (quarter > 0) village.SettleNextQuarter();
                var now = village.Now;
                // the early shift goes to bed after its work, the night shift after its night: both sleep in the early afternoon
                if (now.Hour != 14) continue;
                foreach (var resident in village.People.All)
                {
                    if (village.Jobs!.JobOf(resident.Id) is not { Shift: Shift.Night or Shift.Early } job) continue;
                    Assert.True(village.Day.Of(resident.Id).Activity == Activity.Sleep, $"{now:ddd HH:mm} {resident.Name} of the {job.Shift} shift: {village.Day.Of(resident.Id).Activity}");
                    Assert.True(village.Day.Of(resident.Id).Inside);
                    checkedOnes.Add(job.Shift);
                }
            }
        }
        Assert.Equal([Shift.Early, Shift.Night], checkedOnes.Order());
    }

    [Fact]
    public void After_a_restart_everyone_does_what_the_hour_asks()
    {
        var village = Start(1, June(2, 17));
        var working = village.People.All.Where(resident => resident.Phase == LifePhase.Working && !resident.IsEddie).ToList();
        Assert.True(working.Count(resident => village.Day.Of(resident.Id).Activity == Activity.Work) > working.Count / 2, "most work at five");

        var saved = SaveJson.FromJson(SaveJson.ToJson(village.Engine.Save()));
        var time = new SimulatedTime(June(3, 3));
        var modules = Modules(fakes: true);
        var loaded = Of(Engine.Load(saved, modules, Data(), RealMap, time), time, modules);
        loaded.Engine.Step();

        foreach (var resident in loaded.People.All)
        {
            var doing = loaded.Day.Of(resident.Id);
            Assert.Null(doing.Walk);
            Assert.Equal(doing.Place, doing.At);
            if (doing.Activity != Activity.Work) continue;
            Assert.True(loaded.Jobs!.JobOf(resident.Id) is { Shift: Shift.Night or Shift.Early }, $"{resident.Name} still works at three at night");
        }
    }

    [Fact]
    public void What_is_told_but_not_yet_heard_survives_a_save()
    {
        // at the start everyone gets a doing, told in the next tick; the save is made in between
        var village = Start(1, June(1, 8));
        var saved = SaveJson.FromJson(SaveJson.ToJson(village.Engine.Save()));
        var ear = new ListeningModule("ear");
        var time = new SimulatedTime(saved.Calendar);
        var loaded = Engine.Load(saved, Modules(fakes: true, ear), Data(), RealMap, time);

        loaded.Step();

        Assert.DoesNotContain(loaded.Log, line => line.Kind is "event-dropped" or "module-dormant");
        var told = ear.Heard[loaded.Tick].OfType<DoingChanged>().ToList();
        Assert.Equal(village.People.All.Select(resident => resident.Id), told.Select(e => e.Resident));
        Assert.Contains(told, e => e.Doing.Walk is null);
    }

    [Fact]
    public void A_walk_stays_on_the_island()
    {
        var walks = 0;
        // with jobs and homes, without, and with jobs, homes and schools on the other island
        foreach (var (fakes, more) in new (bool, IModule[])[] { (true, []), (false, []), (false, [new FakeJobs(abroad: true), new FakePlaces(abroad: true)]) })
        {
            var village = Start(1, June(1, 6), fakes, data: null, more);
            for (var quarter = 0; quarter < 24 * 4; quarter++)
            {
                village.StepIntoNextQuarter();
                foreach (var resident in village.People.All)
                {
                    var doing = village.Day.Of(resident.Id);
                    Assert.Equal(resident.Island, RealMap.IslandOf(doing.Place));
                    Assert.Equal(resident.Island, RealMap.IslandOf(doing.At));
                    if (doing.Walk is not { } walk) continue;
                    walks++;
                    Assert.All(walk.Cells, cell => Assert.True(RealMap.IsWalkable(cell) && RealMap.IslandOf(cell) == resident.Island, $"{cell} is not land of island {resident.Island}"));
                    Assert.Equal(doing.Place, walk.Cells[^1]);
                    Assert.Equal(1, doing.At.DistanceTo(walk.Cells[0]));
                }
            }
        }
        Assert.True(walks > 100, $"only {walks} walks");
    }

    [Fact]
    public void A_new_quarter_is_walked_to_and_told()
    {
        var ear = new ListeningModule("ear");
        var village = Start(1, June(1, 8, 15), fakes: true, data: null, ear);
        var before = village.People.All.ToDictionary(resident => resident.Id, resident => village.Day.Of(resident.Id));

        village.StepIntoNextQuarter();
        var tick = village.Engine.Tick;
        var walking = village.People.All.Where(resident => village.Day.Of(resident.Id).Walk is not null).ToList();
        Assert.NotEmpty(walking);
        foreach (var resident in walking)
        {
            var doing = village.Day.Of(resident.Id);
            Assert.False(doing.Inside);
            Assert.Equal(RealMap.FindRoute(doing.At, doing.Place)!.Cells, doing.Walk!.Cells);
            Assert.Equal(tick, doing.Walk.StartTick);
            Assert.Equal(8, doing.Walk.TicksPerCell);
        }

        // the next tick everyone hears what changed
        village.Engine.Step();
        var told = ear.Heard[village.Engine.Tick].OfType<DoingChanged>().ToList();
        var changed = village.People.All.Where(resident => village.Day.Of(resident.Id) is var now && (now.Activity != before[resident.Id].Activity || now.Place != before[resident.Id].Place)).ToList();
        Assert.NotEmpty(changed);
        Assert.Equal(changed.Select(resident => resident.Id), told.Select(e => e.Resident));
        Assert.All(told, e => Assert.Equal(village.Day.Of(e.Resident).Place, e.Doing.Place));

        // and once there, they stand at the place, inside if it is a building
        var longest = walking.Max(resident => village.Day.Of(resident.Id).Walk!.Cells.Count) * 8;
        for (var i = 0; i < longest; i++) village.Engine.Step();
        foreach (var resident in village.People.All)
        {
            var doing = village.Day.Of(resident.Id);
            Assert.Null(doing.Walk);
            Assert.Equal(doing.Place, doing.At);
            if (doing.Activity is Activity.Sleep or Activity.Home or Activity.Visit or Activity.Shop or Activity.Study) Assert.True(doing.Inside);
            if (doing.Activity is Activity.Wander or Activity.Leisure) Assert.False(doing.Inside);
        }
    }

    [Fact]
    public void The_outgoing_go_out_in_the_evening_and_the_shy_stay_home()
    {
        // the chance to go out is 0.5 + (extraversion - 50) / 50: always from 75, never up to 25
        var data = Data(
            day => day["evening"] = new JsonObject { ["chance"] = 0.5, ["perExtraversion"] = 1.0, ["visit"] = 0.5 },
            people => people["personality"]!["extraversion"]!["spread"] = 40);
        int outgoing = 0, shy = 0;
        foreach (var seed in new ulong[] { 1, 2, 3, 4, 5 })
        {
            var village = Start(seed, June(1, 0), fakes: false, data);
            for (var quarter = 0; quarter < 7 * 24 * 4; quarter++)
            {
                if (quarter > 0) village.SettleNextQuarter();
                if (village.Now.Hour != 20) continue;
                // grown-ups without a job go out from seven
                foreach (var resident in village.People.All.Where(resident => resident.Phase == LifePhase.Working && !resident.IsEddie))
                {
                    var activity = village.Day.Of(resident.Id).Activity;
                    var extraversion = resident.Traits.Extraversion;
                    if (extraversion >= 75)
                    {
                        // out: by the water, on a visit, or sent for a wander to keep the island lively
                        Assert.True(activity is Activity.Leisure or Activity.Visit or Activity.Wander, $"{village.Now:ddd HH:mm} {resident.Name} {activity}");
                        outgoing++;
                    }
                    else if (extraversion <= 25)
                    {
                        Assert.DoesNotContain(activity, new[] { Activity.Leisure, Activity.Visit });
                        shy++;
                    }
                }
            }
        }
        Assert.True(outgoing > 0 && shy > 0, $"{outgoing} outgoing, {shy} shy");
    }

    [Fact]
    public void The_careless_get_up_and_go_to_bed_later()
    {
        // everyone is at home from seven and asleep from eleven; an hour later at most, for conscientiousness 0
        var data = Data(
            day =>
            {
                day["lateMinutes"] = 60;
                foreach (var (_, rhythm) in day["rhythms"]!.AsObject())
                {
                    rhythm!.AsObject().Remove("weekend");
                    rhythm["weekday"] = new JsonArray(
                        new JsonObject { ["from"] = "07:00", ["do"] = "home" },
                        new JsonObject { ["from"] = "23:00", ["do"] = "sleep" });
                }
            },
            people => people["personality"]!["conscientiousness"]!["spread"] = 40);
        int careful = 0, careless = 0;
        foreach (var seed in new ulong[] { 1, 2, 3 })
        {
            var village = Start(seed, June(1, 7, 15), fakes: false, data);
            Check(village, morning: true);
            village.Time.Advance(TimeSpan.FromHours(16));
            village.Day.Settle(village.Probe.Context);
            Check(village, morning: false);
        }
        Assert.True(careful > 0 && careless > 0, $"{careful} careful, {careless} careless");

        void Check(Village village, bool morning)
        {
            foreach (var resident in village.People.All)
            {
                var activity = village.Day.Of(resident.Id).Activity;
                // a quarter past: those from 50 up have switched, those up to 20 are 36 minutes or more behind
                if (resident.Traits.Conscientiousness >= 50)
                {
                    Assert.True(morning ? activity != Activity.Sleep : activity is Activity.Sleep or Activity.Wander, $"{resident.Name} {activity}");
                    careful++;
                }
                else if (resident.Traits.Conscientiousness <= 20)
                {
                    Assert.True(morning ? activity is Activity.Sleep or Activity.Wander : activity != Activity.Sleep, $"{resident.Name} {activity}");
                    careless++;
                }
            }
        }
    }

    [Fact]
    public void A_free_choice_holds_until_its_block_ends()
    {
        // one free block a day: what someone picks stays all day, unless he is sent out for a wander; when it gets
        // dark, at ten, one out of doors picks a place again and a chat at the door goes inside, and that holds
        var data = Data(day =>
        {
            foreach (var (_, rhythm) in day["rhythms"]!.AsObject())
            {
                rhythm!.AsObject().Remove("weekend");
                rhythm["weekday"] = new JsonArray(new JsonObject { ["from"] = "00:00", ["do"] = "free" });
            }
        });
        var village = Start(1, June(1, 2), fakes: true, data);
        var first = village.People.All.ToDictionary(resident => resident.Id, resident => village.Day.Of(resident.Id));
        var kinds = new HashSet<Activity>();

        // until eleven at night, before the latest of them starts a new day
        while (village.Now.Hour < 23)
        {
            village.SettleNextQuarter();
            foreach (var resident in village.People.All)
            {
                var doing = village.Day.Of(resident.Id);
                kinds.Add(doing.Activity);
                if (doing.Activity == Activity.Wander) continue;
                Assert.Equal(first[resident.Id].Activity, doing.Activity);
                if (village.Now.TimeOfDay == TimeSpan.FromHours(22)) first[resident.Id] = doing;
                Assert.Equal(first[resident.Id].Place, doing.Place);
            }
        }
        Assert.True(kinds.Count >= 4, $"only {string.Join(", ", kinds)}");
    }

    [Fact]
    public void After_midnight_the_last_block_of_the_day_before_goes_on()
    {
        // weekdays: home from seven, asleep from ten; weekends: asleep from two, free from ten
        var data = Data(day =>
        {
            day["lateMinutes"] = 0;
            foreach (var (_, rhythm) in day["rhythms"]!.AsObject())
            {
                rhythm!["weekday"] = new JsonArray(
                    new JsonObject { ["from"] = "07:00", ["do"] = "home" },
                    new JsonObject { ["from"] = "22:00", ["do"] = "sleep" });
                rhythm["weekend"] = new JsonArray(
                    new JsonObject { ["from"] = "02:00", ["do"] = "sleep" },
                    new JsonObject { ["from"] = "10:00", ["do"] = "free" });
            }
        });
        var village = Start(1, June(5, 23), fakes: true, data);
        Dictionary<int, Doing> sunday = [];
        var awake = 0;

        // from Friday night to Monday night
        while (village.Now < June(8, 2))
        {
            village.SettleNextQuarter();
            var now = village.Now;
            if (now.DayOfWeek == DayOfWeek.Sunday && now.Hour == 23 && now.Minute == 45)
                sunday = village.People.All.ToDictionary(resident => resident.Id, resident => village.Day.Of(resident.Id));
            if (now.Hour >= 2) continue;
            foreach (var resident in village.People.All)
            {
                var doing = village.Day.Of(resident.Id);
                // Saturday night is still Friday's sleep, not the weekend's free time
                if (now.DayOfWeek == DayOfWeek.Saturday)
                    Assert.True(doing.Activity is Activity.Sleep or Activity.Wander, $"{now:ddd HH:mm} {resident.Name} {doing.Activity}");
                // Monday night is still Sunday's free choice, not the weekday's sleep
                if (now.DayOfWeek == DayOfWeek.Monday && doing.Activity != Activity.Wander)
                {
                    Assert.Equal(sunday[resident.Id].Activity, doing.Activity);
                    Assert.Equal(sunday[resident.Id].Place, doing.Place);
                    if (doing.Activity != Activity.Sleep) awake++;
                }
            }
        }
        Assert.True(awake > 0, "nobody was awake on Sunday night");
    }

    [Fact]
    public void Children_go_to_school_from_six()
    {
        int little = 0, pupils = 0;
        foreach (var seed in new ulong[] { 1, 2, 3, 4, 5 })
        {
            var village = Start(seed, June(1, 10));
            foreach (var child in village.People.All.Where(resident => resident.Phase == LifePhase.Child))
            {
                var activity = village.Day.Of(child.Id).Activity;
                if (village.People.AgeOf(child) < 6)
                {
                    Assert.NotEqual(Activity.School, activity);
                    little++;
                }
                else
                {
                    Assert.Contains(activity, new[] { Activity.School, Activity.Wander });
                    pupils++;
                }
            }
        }
        Assert.True(little > 0 && pupils > 0, $"{little} little ones, {pupils} pupils");
    }

    [Fact]
    public void Without_jobs_or_places_everyone_still_has_a_place()
    {
        var village = Start(1, June(1, 11), fakes: false);

        foreach (var resident in village.People.All)
        {
            var doing = village.Day.Of(resident.Id);
            Assert.True(RealMap.IsWalkable(doing.Place));
            Assert.Equal(resident.Island, RealMap.IslandOf(doing.Place));
            // without a job nobody works, Eddie neither
            Assert.NotEqual(Activity.Work, doing.Activity);
        }

        // at night a household sleeps under one roof
        village.Time.Advance(TimeSpan.FromHours(16));
        village.Day.Settle(village.Probe.Context);
        foreach (var household in village.People.All.GroupBy(resident => resident.Household))
        {
            var homes = household.Select(resident => village.Day.Of(resident.Id)).Where(doing => doing.Activity == Activity.Sleep).Select(doing => doing.Place).Distinct();
            Assert.True(homes.Count() <= 1, $"household {household.Key} sleeps in more than one home");
        }
    }

    [Fact]
    public void A_worker_whose_place_moves_walks_to_the_new_place_in_the_same_block()
    {
        var village = Start(1, June(1, 10));
        var workers = village.People.All.Where(resident => village.Day.Of(resident.Id).Activity == Activity.Work && village.Jobs!.JobOf(resident.Id) is { Shift: Shift.Day }).ToList();
        Assert.NotEmpty(workers);
        Assert.All(workers, worker => Assert.Equal(village.Jobs!.JobOf(worker.Id)!.Place, village.Day.Of(worker.Id).Place));

        // in the middle of the work block, the work is somewhere else
        village.Jobs!.Moved = 1;
        village.StepIntoNextQuarter();

        foreach (var worker in workers)
        {
            var doing = village.Day.Of(worker.Id);
            var job = village.Jobs.JobOf(worker.Id)!;
            if (doing.Activity != Activity.Work) continue;
            Assert.Equal(job.Place, doing.Place);
            Assert.NotNull(doing.Walk);
        }
        Assert.Contains(workers, worker => village.Day.Of(worker.Id).Activity == Activity.Work);
    }

    [Fact]
    public void Of_someone_who_does_not_live_here_throws()
    {
        var village = Start(1, June(1, 11));

        Assert.Throws<KeyNotFoundException>(() => village.Day.Of(9999));
    }

    // Risk: work as a free choice sends one without a job round in circles until the server falls over.
    [Fact]
    public void Work_as_free_time_is_refused_when_the_data_is_read()
    {
        var data = Data(day => day["free"]!.AsArray().Add(new JsonObject { ["do"] = "work", ["weight"] = 1 }));

        var village = Start(1, June(1, 11), data: data);

        var (module, error) = Assert.Single(village.Engine.Stopped);
        Assert.Equal("day", module);
        Assert.Contains("work among its free choices", error);
    }
}

/// <summary>Holds on to its context, so a test can hand it to <see cref="Day.Settle"/>.</summary>
public sealed class Probe : IModule
{
    public string Name => "probe";
    public int Version => 1;
    public ModuleContext Context { get; private set; } = null!;
    public void Start(ModuleContext village) => Context = village;
    public void Step(ModuleContext village) { }
    public JsonNode Save() => new JsonObject();
    public void Load(ModuleContext village, JsonNode data, int version) => Context = village;
}

/// <summary>
/// Gives every working resident but Eddie a job on a lot of his island, or of another one when
/// <paramref name="abroad"/>: one in seven works nights, one in seven the early shift, the rest by day; one in
/// four works outdoors.
/// </summary>
public sealed class FakeJobs(bool abroad = false) : IModule, IJobs
{
    private People _people = null!;
    /// <summary>Moves every job this many lots on, as when a builder is sent to another site.</summary>
    public int Moved { get; set; }
    private IslandMap _map = null!;
    public string Name => "fake-jobs";
    public int Version => 1;
    public void Start(ModuleContext village) => Take(village);
    public void Step(ModuleContext village) { }
    public JsonNode Save() => new JsonObject();
    public void Load(ModuleContext village, JsonNode data, int version) => Take(village);

    private void Take(ModuleContext village)
    {
        _people = village.Module<People>();
        _map = village.Map;
    }

    public JobPlace? JobOf(int resident)
    {
        var who = _people.All.FirstOrDefault(r => r.Id == resident);
        if (who is not { Phase: LifePhase.Working, IsEddie: false }) return null;
        var lots = FakePlaces.Lots(_map, abroad ? FakePlaces.Other(_map, who.Island) : who.Island);
        var shift = (resident % 7) switch { 0 => Shift.Night, 1 => Shift.Early, _ => Shift.Day };
        return new JobPlace(0, lots[(resident * 3 + Moved) % lots.Count], "worker", shift, resident % 4 == 0);
    }
}

/// <summary>
/// Homes on the lots of the island, by household; the school on the first lot, the gathering on the second.
/// When <paramref name="abroad"/>, all of them on another island.
/// </summary>
public sealed class FakePlaces(bool abroad = false) : IModule, IPlaces
{
    private People _people = null!;
    private IslandMap _map = null!;
    public string Name => "fake-places";
    public int Version => 1;
    public void Start(ModuleContext village) => Take(village);
    public void Step(ModuleContext village) { }
    public JsonNode Save() => new JsonObject();
    public void Load(ModuleContext village, JsonNode data, int version) => Take(village);

    private void Take(ModuleContext village)
    {
        _people = village.Module<People>();
        _map = village.Map;
    }

    public static IReadOnlyList<Cell> Lots(IslandMap map, int island) =>
        [.. map.Islands.Single(i => i.Id == island).Cells.Where(cell => map.At(cell) == Ground.Lot)];

    /// <summary>An ordinary island that is not this one.</summary>
    public static int Other(IslandMap map, int island) => map.Islands.First(i => !i.Eddie && i.Id != island).Id;

    private IReadOnlyList<Cell> LotsFor(int island) => Lots(_map, abroad ? Other(_map, island) : island);

    public Cell? HomeOf(int resident)
    {
        var who = _people.All.FirstOrDefault(r => r.Id == resident);
        if (who is null || who.IsEddie) return null;
        var lots = LotsFor(who.Island);
        return lots[(who.Household ?? who.Id) * 5 % lots.Count];
    }

    public Cell? SchoolOn(int island) => LotsFor(island) is { Count: > 0 } lots ? lots[0] : null;

    public Cell? GatheringOn(int island) => LotsFor(island) is { Count: > 1 } lots ? lots[1] : null;
}
