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

namespace Codery.Village.Tests;

public class PeopleTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly IslandMap RealMap = MapText.Parse(File.ReadAllText(Path.Combine(Folder, "map.txt")));
    // a life year of a thousand ticks, so a life fits in a test
    private const int FastYear = 1000;

    /// <summary>The real people and life data, changed as the test needs.</summary>
    private static DataSet Data(Action<JsonObject>? people = null, Action<JsonObject>? life = null)
    {
        var peopleJson = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, "people.json")))!.AsObject();
        var lifeJson = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, "life.json")))!.AsObject();
        people?.Invoke(peopleJson);
        life?.Invoke(lifeJson);
        return new DataSet(new Dictionary<string, string>
        {
            ["people"] = peopleJson.ToJsonString(),
            ["life"] = lifeJson.ToJsonString(),
        });
    }

    private static void Fast(JsonObject life) => life["ticksPerLifeYear"] = FastYear;

    private static JsonArray Band(int from, int to) => [new JsonObject { ["from"] = from, ["to"] = to, ["weight"] = 1 }];

    private static JsonArray Kind(int adults, int children) => [new JsonObject { ["adults"] = adults, ["children"] = children, ["weight"] = 1 }];

    private static (Engine Engine, People People) Village(ulong seed, DataSet? data = null, TimeProvider? time = null, params IModule[] more)
    {
        var people = new People();
        var engine = new Engine([people, .. more], data ?? Data(), RealMap, seed, time ?? new SimulatedTime(TestVillage.Start));
        engine.Start();
        return (engine, people);
    }

    private static void Run(Engine engine, long ticks)
    {
        for (var i = 0L; i < ticks; i++) engine.Step();
    }

    private static IEnumerable<T> Heard<T>(ListeningModule ear) => ear.Heard.Values.SelectMany(events => events).OfType<T>();

    private static int[] Values(Personality p) =>
        [p.Openness, p.Conscientiousness, p.Extraversion, p.Agreeableness, p.Sensitivity, p.Status, p.Comfort, p.Thrift];

    private static int OrdinaryIsland(string name) => RealMap.Islands.Single(island => island.Name == name).Id;

    public static TheoryData<ulong> Seeds => [.. Enumerable.Range(1, 20).Select(seed => (ulong)seed)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Each_island_starts_with_twelve_to_fifteen(ulong seed)
    {
        var (_, people) = Village(seed);

        foreach (var island in RealMap.Islands)
        {
            var on = people.On(island.Id).ToList();
            if (island.Eddie)
            {
                var eddie = Assert.Single(on);
                Assert.True(eddie.IsEddie);
            }
            else
            {
                Assert.InRange(on.Count, 12, 15);
                Assert.DoesNotContain(on, resident => resident.IsEddie);
            }
        }
        Assert.Equal(people.All.Count, people.All.Select(resident => resident.Id).Distinct().Count());
    }

    [Fact]
    public void The_start_is_couples_with_or_without_children_and_singles()
    {
        foreach (var seed in Enumerable.Range(1, 20).Select(seed => (ulong)seed))
        {
            var (_, people) = Village(seed);
            foreach (var household in people.All.Where(resident => !resident.IsEddie).GroupBy(resident => resident.Household))
            {
                Assert.NotNull(household.Key);
                Assert.Single(household.Select(resident => resident.Island).Distinct());
                var adults = household.Where(resident => people.AgeOf(resident) >= 18).ToList();
                var children = household.Where(resident => people.AgeOf(resident) < 18).ToList();
                Assert.InRange(adults.Count, 1, 2);
                if (children.Count == 0) continue;
                Assert.Equal(2, adults.Count);
                Assert.All(children, child => Assert.Equal(LifePhase.Child, child.Phase));
                Assert.All(children, child => Assert.True(adults.Min(people.AgeOf) - people.AgeOf(child) >= 19, "a parent is at least twenty years older"));
            }
            Assert.All(people.All, resident => Assert.InRange(resident.Interests.Count, 1, 3));
            Assert.All(people.All, resident => Assert.Equal(resident.Interests.Count, resident.Interests.Distinct().Count()));
            Assert.All(people.All, resident => Assert.InRange(people.AgeOf(resident), 0, 70));
        }
    }

    [Fact]
    public void Traits_stay_within_0_and_100()
    {
        // a spread so wide that many draws fall outside 0 to 100 and must be held there
        var data = Data(people =>
        {
            foreach (var (_, trait) in people["personality"]!.AsObject()) trait!["spread"] = 100;
        });
        var values = new List<int>();
        foreach (var seed in Enumerable.Range(1, 5).Select(seed => (ulong)seed))
            values.AddRange(Village(seed, data).People.All.SelectMany(resident => Values(resident.Traits)));

        Assert.All(values, value => Assert.InRange(value, 0, 100));
        Assert.Contains(0, values);
        Assert.Contains(100, values);
        // with the real spread the values are spread out but rarely at the edge
        var real = Village(1).People.All.SelectMany(resident => Values(resident.Traits)).ToList();
        Assert.All(real, value => Assert.InRange(value, 0, 100));
        Assert.True(real.Distinct().Count() > 20);
    }

    [Fact]
    public void Notable_names_the_traits_below_30_and_above_70()
    {
        var traits = new Personality(10, 50, 80, 30, 70, 29, 71, 50);

        Assert.Equal(
            new[] { ("openness", false), ("extraversion", true), ("status", false), ("comfort", true) },
            traits.Notable());
    }

    [Fact]
    public void Sex_only_chooses_the_name()
    {
        var plain = Data(life: Fast);
        var swapped = Data(people =>
        {
            var names = people["names"]!.AsObject();
            var man = names["man"]!.DeepClone();
            names["man"] = names["woman"]!.DeepClone();
            names["woman"] = man;
        }, Fast);
        var men = plain.Section<JsonObject>("people")["names"]!["man"]!.AsArray().Select(name => (string)name!).ToHashSet();
        var women = plain.Section<JsonObject>("people")["names"]!["woman"]!.AsArray().Select(name => (string)name!).ToHashSet();
        // lists of another length draw other names, and so other names that are taken
        Assert.NotEqual(men.Count, women.Count);
        var (engineA, a) = Village(7, plain);
        var (engineB, b) = Village(7, swapped);

        for (var round = 0; round < 3; round++)
        {
            Assert.Equal(a.All.Count, b.All.Count);
            foreach (var (one, other) in a.All.Zip(b.All))
            {
                Assert.Equal(one.Id, other.Id);
                Assert.Equal(one.Sex, other.Sex);
                Assert.Equal(one.Traits, other.Traits);
                Assert.Equal(one.Interests, other.Interests);
                Assert.Equal(one.Phase, other.Phase);
                Assert.Equal(one.BornTick, other.BornTick);
                if (one.IsEddie) continue;
                Assert.Contains(one.Name, one.Sex == Sex.Man ? men : women);
                Assert.Contains(other.Name, other.Sex == Sex.Man ? women : men);
            }
            Run(engineA, 15 * FastYear);
            Run(engineB, 15 * FastYear);
        }
        Assert.Contains(a.All, resident => resident.Sex == Sex.Man && !resident.IsEddie);
        Assert.Contains(a.All, resident => resident.Sex == Sex.Woman);
    }

    [Fact]
    public void A_child_goes_from_school_to_work()
    {
        var data = Data(people =>
        {
            people["households"] = Kind(2, 1);
            people["parentAges"] = Band(30, 30);
            people["childAges"] = Band(6, 6);
        }, Fast);
        var ear = new ListeningModule("ear");
        var (engine, people) = Village(3, data, null, ear);
        var children = people.All.Where(resident => resident.Phase == LifePhase.Child).ToList();
        Assert.NotEmpty(children);
        Assert.All(children, child => Assert.InRange(people.AgeOf(child), 6, 7));

        Run(engine, 12 * FastYear + 1);

        Assert.All(children, child => Assert.InRange(people.AgeOf(child), 18, 19));
        Assert.All(children, child => Assert.Contains(child.Phase, new[] { LifePhase.Student, LifePhase.Working }));
        Run(engine, 1);
        var changed = Heard<ResidentPhaseChanged>(ear).ToList();
        Assert.All(children, child => Assert.Contains(changed, e => e.Resident == child.Id && e.Phase == child.Phase));
    }

    [Fact]
    public void The_open_with_a_study_interest_study_first()
    {
        var study = Data().Section<JsonObject>("people")["interests"]!.AsArray()
            .Where(interest => (bool?)interest!["study"] == true).Select(interest => (string)interest!["id"]!).ToHashSet();
        var data = Data(people =>
        {
            people["households"] = Kind(2, 3);
            people["parentAges"] = Band(40, 40);
            people["childAges"] = Band(10, 10);
        }, Fast);
        var (engine, people) = Village(5, data);
        var children = people.All.Where(resident => resident.Phase == LifePhase.Child).ToList();
        bool Studies(Resident child) => child.Traits.Openness > 50 && child.Interests.Any(study.Contains);
        Assert.Contains(children, Studies);
        Assert.Contains(children, child => !Studies(child));

        Run(engine, 8 * FastYear + 1);   // 18 and a bit
        Assert.All(children, child => Assert.Equal(Studies(child) ? LifePhase.Student : LifePhase.Working, child.Phase));

        Run(engine, 3 * FastYear);       // 21 and a bit
        Assert.All(children, child => Assert.Equal(LifePhase.Working, child.Phase));
    }

    [Fact]
    public void The_retired_move_to_the_coast()
    {
        var data = Data(people =>
        {
            people["households"] = Kind(1, 0);
            people["adultAges"] = Band(67, 67);
        }, Fast);
        var ear = new ListeningModule("ear");
        var (engine, people) = Village(11, data, null, ear);
        var retired = people.All.Where(resident => !resident.IsEddie).ToList();
        Assert.All(retired, resident => Assert.Equal(LifePhase.Retired, resident.Phase));

        Run(engine, 3 * FastYear + 2);

        Assert.All(retired, resident => Assert.DoesNotContain(resident, people.All));
        var left = Heard<ResidentLeft>(ear).ToList();
        Assert.Equal(retired.Select(resident => resident.Id).Order(), left.Select(e => e.Resident).Order());
        Assert.All(left, e => Assert.Equal("coast", e.Reason));
        // nobody leaves before seventy
        Assert.All(left, e => Assert.True(e.Tick - retired.Single(resident => resident.Id == e.Resident).BornTick >= 70 * FastYear));
        Assert.Equal(retired.Select(resident => resident.Name).Order(),
            engine.Log.Where(line => line.Kind == "left-coast").Select(line => line.Subject).Order());
        Assert.Throws<KeyNotFoundException>(() => people.Get(retired[0].Id));

        // a whole village for a hundred years: everyone who leaves goes to the coast, at seventy or later
        var earB = new ListeningModule("ear");
        var (village, all) = Village(12, Data(life: Fast), null, earB);
        var born = all.All.ToDictionary(resident => resident.Id, resident => resident.BornTick);
        Run(village, 100 * FastYear);
        var leftB = Heard<ResidentLeft>(earB).ToList();
        Assert.NotEmpty(leftB);
        Assert.All(leftB, e => Assert.Equal("coast", e.Reason));
        Assert.All(leftB, e => Assert.True(e.Tick - born[e.Resident] >= 70 * FastYear));
    }

    [Fact]
    public void Eddie_never_ages_or_leaves()
    {
        var ear = new ListeningModule("ear");
        var (engine, people) = Village(2, Data(life: Fast), null, ear);
        var eddie = Assert.Single(people.All, resident => resident.IsEddie);
        Assert.Equal("Eddie", eddie.Name);
        Assert.Equal(RealMap.Islands.Single(island => island.Eddie).Id, eddie.Island);
        Assert.Equal(35, people.AgeOf(eddie));
        Assert.Equal(LifePhase.Working, eddie.Phase);

        Run(engine, 80 * FastYear);

        Assert.Same(eddie, people.Get(eddie.Id));
        Assert.Equal(35, people.AgeOf(eddie));
        Assert.Equal(LifePhase.Working, eddie.Phase);
        Assert.DoesNotContain(Heard<ResidentLeft>(ear), e => e.Resident == eddie.Id);
        Assert.DoesNotContain(Heard<ResidentPhaseChanged>(ear), e => e.Resident == eddie.Id);
        Assert.Single(people.All, resident => resident.IsEddie);
    }

    [Fact]
    public void Newcomers_need_a_free_home()
    {
        // a chance of one per calendar day
        var data = Data(people =>
        {
            people["arrivalChance"] = 1;
            people["arrivalDamping"] = 1e12;
        });
        var west = OrdinaryIsland("West");

        // without a housing module nobody comes
        var timeA = new SimulatedTime(TestVillage.Start);
        var (engineA, peopleA) = Village(4, data, timeA);
        var before = peopleA.All.Count;
        Days(engineA, timeA, 30);
        Assert.Equal(before, peopleA.All.Count);

        // without a free home nobody comes
        var timeB = new SimulatedTime(TestVillage.Start);
        var earB = new ListeningModule("ear");
        var (engineB, peopleB) = Village(4, data, timeB, new FakeHousing(), earB);
        before = peopleB.All.Count;
        Days(engineB, timeB, 30);
        Assert.Equal(before, peopleB.All.Count);
        Assert.Empty(Heard<ResidentArrived>(earB));

        // with a free home a household comes within a few days, and moves in
        var timeC = new SimulatedTime(TestVillage.Start);
        var earC = new ListeningModule("ear");
        var housing = new FakeHousing();
        housing.Free(west, 7);
        var (engineC, peopleC) = Village(4, data, timeC, housing, earC);
        before = peopleC.All.Count;
        Days(engineC, timeC, 3);
        var arrived = Heard<ResidentArrived>(earC).Select(e => peopleC.Get(e.Resident)).ToList();
        Assert.NotEmpty(arrived);
        Assert.Equal(before + arrived.Count, peopleC.All.Count);
        var household = Assert.Single(arrived.Select(resident => resident.Household).Distinct());
        Assert.Equal(7, housing.MovedIn[household!.Value]);
        Assert.All(arrived, resident => Assert.Equal(west, resident.Island));
        Assert.Contains(engineC.Log, line => line.Kind == "arrived");

        // the home is taken: nobody else comes
        Days(engineC, timeC, 30);
        Assert.Equal(before + arrived.Count, peopleC.All.Count);
    }

    [Fact]
    public void Newcomers_come_at_most_once_a_calendar_day()
    {
        var data = Data(people =>
        {
            people["arrivalChance"] = 1;
            people["arrivalDamping"] = 1e12;
        });
        var time = new SimulatedTime(TestVillage.Start);
        var ear = new ListeningModule("ear");
        var housing = new FakeHousing();
        foreach (var island in RealMap.Islands) housing.Free(island.Id, [.. Enumerable.Range(island.Id * 1000, 50)]);
        var (engine, people) = Village(4, data, time, housing, ear);
        var start = people.All.Count;

        Run(engine, 1000);   // within the first day
        Assert.Equal(start, people.All.Count);

        Days(engine, time, 1);
        Run(engine, 1000);
        var households = Heard<ResidentArrived>(ear).Select(e => people.Get(e.Resident)).GroupBy(resident => resident.Household).ToList();
        Assert.Equal(RealMap.Islands.Count(island => !island.Eddie), households.Count);
        Assert.Equal(households.Count, households.Select(household => household.First().Island).Distinct().Count());
    }

    [Fact]
    public void A_household_without_a_home_moves_into_a_free_one()
    {
        var west = OrdinaryIsland("West");
        var housing = new FakeHousing { EveryoneHoused = false };
        housing.Free(west, 1, 2);
        var (engine, people) = Village(6, null, null, housing);

        Run(engine, 2);

        var onWest = people.On(west).Select(resident => resident.Household!.Value).Distinct().Order().ToList();
        Assert.Equal(new Dictionary<int, int> { [onWest[0]] = 1, [onWest[1]] = 2 }, housing.MovedIn);
    }

    [Fact]
    public void Every_line_in_the_log_has_a_sentence()
    {
        var data = Data(people => people["arrivalChance"] = 1, Fast);
        var sentences = data.Section<JsonObject>("people")["log"]!.AsObject();
        var time = new SimulatedTime(TestVillage.Start);
        var housing = new FakeHousing();
        foreach (var island in RealMap.Islands) housing.Free(island.Id, [.. Enumerable.Range(island.Id * 1000, 50)]);
        var (engine, _) = Village(8, data, time, housing);

        for (var year = 0; year < 80; year++)
        {
            time.Advance(TimeSpan.FromDays(1));
            Run(engine, FastYear);
        }

        var kinds = engine.Log.Select(line => line.Kind).ToHashSet();
        Assert.Superset(new HashSet<string> { "arrived", "left-coast", "phase-student", "phase-working", "phase-retired" }, kinds);
        Assert.All(kinds, kind =>
        {
            var sentence = sentences[kind];
            Assert.NotNull(sentence);
            Assert.Contains("{subject}", (string)sentence["nl"]!);
            Assert.Contains("{subject}", (string)sentence["en"]!);
        });
    }

    [Fact]
    public void A_saved_village_goes_on_as_if_it_never_stopped()
    {
        var data = Data(people => people["arrivalChance"] = 0.7, Fast);
        static FakeHousing Housing() => new() { EveryoneHoused = false };
        var housing = Housing();
        foreach (var island in RealMap.Islands) housing.Free(island.Id, [.. Enumerable.Range(island.Id * 1000, 50)]);
        var time = new SimulatedTime(TestVillage.Start);
        var (original, _) = Village(9, data, time, housing);
        // a day per life year; saved halfway through a day
        void Years(Engine engine, SimulatedTime clock, int years)
        {
            for (var year = 0; year < years; year++)
            {
                clock.Advance(TimeSpan.FromDays(1));
                Run(engine, FastYear);
            }
        }
        Years(original, time, 5);
        var saved = SaveJson.FromJson(SaveJson.ToJson(original.Save()));
        var loadedTime = new SimulatedTime(saved.Calendar);
        var loaded = Engine.Load(saved, [new People(), Housing()], data, RealMap, loadedTime);

        Run(original, 1);
        Run(loaded, 1);
        Years(original, time, 40);
        Years(loaded, loadedTime, 40);

        Assert.Equal(SaveJson.ToJson(original.Save()), SaveJson.ToJson(loaded.Save()));
        Assert.Contains(original.Log, line => line.Kind == "left-coast");
        Assert.Contains(original.Log, line => line.Kind == "arrived");
    }

    private static void Days(Engine engine, SimulatedTime time, int days)
    {
        for (var day = 0; day < days; day++)
        {
            time.Advance(TimeSpan.FromDays(1));
            engine.Step();
        }
    }
}

/// <summary>Homes for the people tests until Building is there: the homes it is told are free, per island.</summary>
public sealed class FakeHousing(string name = "housing") : IModule, IHousing
{
    private readonly Dictionary<int, List<int>> _free = [];

    /// <summary>Every household counts as housed, so free homes go to newcomers.</summary>
    public bool EveryoneHoused { get; set; } = true;

    public Dictionary<int, int> MovedIn { get; } = [];

    public void Free(int island, params int[] homes)
    {
        if (!_free.TryGetValue(island, out var free)) _free[island] = free = [];
        free.AddRange(homes);
    }

    public int? FreeHome(int island) => _free.TryGetValue(island, out var free) && free.Count > 0 ? free[0] : null;

    public void MoveIn(int household, int home)
    {
        foreach (var free in _free.Values) free.Remove(home);
        MovedIn[household] = home;
    }

    public bool HasHome(int household) => EveryoneHoused || MovedIn.ContainsKey(household);

    public string Name => name;
    public int Version => 1;
    public void Start(ModuleContext village) { }
    public void Step(ModuleContext village) { }
    public JsonNode Save() => JsonSerializer.SerializeToNode(new Saved(_free, MovedIn, EveryoneHoused))!;

    public void Load(ModuleContext village, JsonNode data, int version)
    {
        var saved = data.Deserialize<Saved>()!;
        _free.Clear();
        foreach (var (island, homes) in saved.Free) _free[island] = homes;
        MovedIn.Clear();
        foreach (var (household, home) in saved.MovedIn) MovedIn[household] = home;
        EveryoneHoused = saved.EveryoneHoused;
    }

    private sealed record Saved(Dictionary<int, List<int>> Free, Dictionary<int, int> MovedIn, bool EveryoneHoused);
}
