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
using Codery.Village.Modules.Work;

namespace Codery.Village.Tests;

public class WorkTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly IslandMap RealMap = MapText.Parse(File.ReadAllText(Path.Combine(Folder, "map.txt")));
    private static readonly int West = RealMap.Islands.Single(island => island.Name == "West").Id;
    private static readonly int Oost = RealMap.Islands.Single(island => island.Name == "Oost").Id;
    private static readonly int Islet = RealMap.Islands.Single(island => island.Eddie).Id;

    /// <summary>A Dutch summer time on a day of June 2026; the first of June is a Monday.</summary>
    private static DateTimeOffset June(int day, int hour, int minute = 0) => new(2026, 6, day, hour, minute, 0, TimeSpan.FromHours(2));

    /// <summary>The real data with the whole ladder of releases, changed as the test needs.</summary>
    private static DataSet Data(Action<JsonObject>? economy = null, Action<JsonObject>? people = null, Action<JsonObject>? life = null,
        Action<JsonObject>? catalog = null)
    {
        JsonObject Read(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, name + ".json")))!.AsObject();
        var economyJson = Read("economy");
        var peopleJson = Read("people");
        var lifeJson = Read("life");
        var catalogJson = Read("catalog");
        life?.Invoke(lifeJson);
        catalog?.Invoke(catalogJson);
        economy?.Invoke(economyJson);
        people?.Invoke(peopleJson);
        return DataSet.FromTexts(new Dictionary<string, string>
        {
            ["life"] = lifeJson.ToJsonString(),
            ["people"] = peopleJson.ToJsonString(),
            ["day"] = Read("day").ToJsonString(),
            ["catalog"] = catalogJson.ToJsonString(),
            ["economy"] = economyJson.ToJsonString(),
        }, Ladder.All());
    }

    private static StructureView Built(int id, string blueprint, int island, int row, int col)
    {
        var place = new Cell(row, col);
        Assert.True(RealMap.IslandOf(place) == island || RealMap.At(place) == Ground.Fishing, $"{blueprint} at {place} is not on island {island}");
        return new StructureView(id, blueprint, island, place);
    }

    // a working economy on both islands
    private static StructureView[] Economy() =>
    [
        Built(1, "woodcutter", West, 2, 4),
        Built(2, "sawmill", West, 3, 12),
        Built(3, "farm", West, 5, 13),
        Built(4, "bakery", West, 4, 13),
        Built(5, "quarry", Oost, 2, 28),
        Built(6, "fisher", Oost, 2, 34),
        Built(7, "brickworks", Oost, 3, 32),
    ];

    private sealed record Village(Engine Engine, SimulatedTime Time, People People, Day Day, Work Work, FakeStructures Structures, Ear Ear)
    {
        /// <summary>Moves the calendar to <paramref name="at"/> and runs one tick there; Day settles, as after a stop.</summary>
        public void JumpTo(DateTimeOffset at)
        {
            Time.Advance(at - Time.GetUtcNow());
            Engine.Step();
        }

        public void Run(int ticks)
        {
            for (var i = 0; i < ticks; i++) Engine.Step();
        }

        public IEnumerable<Resident> InRole(string role) => People.All.Where(resident => Work.JobOf(resident.Id)?.Role == role);

        /// <summary>All the money in the village: the treasuries and the residents.</summary>
        public long Money => RealMap.Islands.Sum(island => Work.Of(island.Id).Cash) + People.All.Sum(resident => resident.Money);
    }

    private static Village Start(ulong seed, DateTimeOffset at, DataSet? data = null, StructureView[]? standing = null, params IModule[] more)
    {
        var time = new SimulatedTime(at);
        var structures = new FakeStructures();
        structures.Standing.AddRange(standing ?? []);
        IModule[] modules = [new People(), structures, .. more, new Day(), new Work(), new Ear()];
        var engine = new Engine(modules, data ?? Data(), RealMap, seed, time);
        engine.Start();
        return Of(engine, time, modules);
    }

    private static Village Of(Engine engine, SimulatedTime time, IReadOnlyList<IModule> modules) =>
        new(engine, time, modules.OfType<People>().Single(), modules.OfType<Day>().Single(), modules.OfType<Work>().Single(),
            modules.OfType<FakeStructures>().Single(), modules.OfType<Ear>().Single());

    public static TheoryData<ulong> Seeds => [1, 2, 3];

    [Fact]
    public void Wages_need_a_worker_in_place()
    {
        var data = Data(economy: e =>
        {
            e["builders"] = 0;
            e["start"]!["stock"]!["West"] = new JsonObject { ["flour"] = 100 };
        });
        var village = Start(1, June(1, 14), data, [Built(1, "bakery", West, 4, 13)]);
        // the bakers work the early shift: in the afternoon they sleep
        village.JumpTo(June(1, 15));
        var bakers = village.InRole("baker").ToList();
        Assert.Equal(2, bakers.Count);
        var cash = village.Work.Of(West).Cash;

        village.Run(1000);

        Assert.Equal(0, village.Work.Count(West, "bread"));
        Assert.Equal(100, village.Work.Count(West, "flour"));
        Assert.All(bakers, baker => Assert.Equal(0, baker.Money));
        Assert.Equal(cash, village.Work.Of(West).Cash);
        Assert.DoesNotContain(village.Ear.Heard, e => e is Produced);

        // in their shift they bake, and are paid for it
        village.JumpTo(June(2, 6));
        village.Run(1000);

        var bread = village.Work.Count(West, "bread");
        Assert.True(bread > 0);
        Assert.Contains(village.Ear.Heard, e => e is Produced { Material: "bread" } produced && produced.Island == West);
        Assert.Contains(bakers, baker => baker.Money > 0);
        // a batch takes its flour when it starts and gives its bread when it is done
        Assert.Contains(100 - village.Work.Count(West, "flour"), new[] { bread / 4 * 2, bread / 4 * 2 + 2 });
    }

    [Fact]
    public void On_the_way_to_work_is_not_at_work()
    {
        var data = Data(economy: e =>
        {
            e["builders"] = 0;
            e["wagePerHour"] = 14400 * 100;
        });
        var village = Start(1, June(1, 3), data, [Built(1, "bakery", West, 4, 13)]);
        village.JumpTo(June(1, 3, 45));
        var bakers = village.InRole("baker").ToList();
        Assert.Equal(2, bakers.Count);

        // into the quarter of the early shift: they walk to the bakery
        village.Time.Advance(June(1, 4) - village.Time.GetUtcNow());
        var walked = false;
        for (var tick = 0; tick < 400; tick++)
        {
            var before = bakers.Select(baker => baker.Money).ToList();
            village.Engine.Step();
            for (var i = 0; i < bakers.Count; i++)
            {
                if (village.Day.Of(bakers[i].Id).Walk is null) continue;
                walked = true;
                Assert.Equal(before[i], bakers[i].Money);
            }
        }

        Assert.True(walked);
        Assert.All(bakers, baker => Assert.Equal(Activity.Work, village.Day.Of(baker.Id).Activity));
        Assert.Contains(bakers, baker => baker.Money > 0);
    }

    [Fact]
    public void A_saved_economy_goes_on_as_if_it_never_stopped()
    {
        var data = Data(economy: e => e["forestRegrowTicks"] = 3000);
        IModule[] Modules()
        {
            var structures = new FakeStructures();
            structures.Standing.AddRange(Economy());
            structures.Sites.Add(Built(10, "house", West, 3, 8));
            return [new People(), structures, new Day(), new Work(), new Ear()];
        }
        var time = new SimulatedTime(June(1, 10));
        var modules = Modules();
        var running = new Engine(modules, data, RealMap, 7, time);
        running.Start();
        var village = Of(running, time, modules);
        village.JumpTo(June(1, 10, 15));
        village.Run(1300);

        var saved = SaveJson.FromJson(SaveJson.ToJson(running.Save()));
        var work = Assert.Single(saved.Modules, part => part.Name == "work").Data;
        Assert.NotEmpty(work["felled"]!.AsArray());
        Assert.NotEmpty(work["batches"]!.AsArray());
        Assert.NotEmpty(work["jobs"]!.AsArray());
        var loaded = Engine.Load(saved, Modules(), data, RealMap, new SimulatedTime(saved.Calendar));

        for (var i = 0; i < 2000; i++)
        {
            running.Step();
            loaded.Step();
        }
        Assert.Equal(SaveJson.ToJson(running.Save()), SaveJson.ToJson(loaded.Save()));
    }

    // Risk: the village saved on production before workers changed jobs (Work version 2) no longer loads, or loses its jobs.
    [Fact]
    public void A_work_save_of_version_2_loads_and_goes_on()
    {
        var data = Data();
        IModule[] Modules()
        {
            var structures = new FakeStructures();
            structures.Standing.AddRange(Economy());
            return [new People(), structures, new Day(), new Work(), new Ear()];
        }
        var time = new SimulatedTime(June(1, 10));
        var modules = Modules();
        var running = new Engine(modules, data, RealMap, 7, time);
        running.Start();
        var village = Of(running, time, modules);
        village.Run(1300);

        // as version 2 wrote it: no day the workers moved, and no tick a job was taken
        var saved = SaveJson.FromJson(SaveJson.ToJson(running.Save()));
        var part = Assert.Single(saved.Modules, part => part.Name == "work");
        var work = part.Data.AsObject();
        work.Remove("moved");
        foreach (var job in work["jobs"]!.AsArray()) job!.AsObject().Remove("since");
        saved = saved with { Modules = [.. saved.Modules.Select(module => module.Name == "work" ? module with { Version = 2 } : module)] };

        var loadedModules = Modules();
        var loaded = Of(Engine.Load(SaveJson.FromJson(SaveJson.ToJson(saved)), loadedModules, data, RealMap, new SimulatedTime(saved.Calendar)), new SimulatedTime(saved.Calendar), loadedModules);
        var jobs = village.People.All.ToDictionary(resident => resident.Id, resident => village.Work.JobOf(resident.Id)?.Role);
        Assert.Contains(jobs.Values, role => role is not null);
        Assert.Equal(jobs, loaded.People.All.ToDictionary(resident => resident.Id, resident => loaded.Work.JobOf(resident.Id)?.Role));

        // and it goes on, and saves in the form of now
        loaded.Run(2000);
        Assert.Empty(loaded.Engine.Stopped);
        Assert.Equal(3, Assert.Single(loaded.Engine.Save().Modules, module => module.Name == "work").Version);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Money_moves_but_is_not_made(ulong seed)
    {
        var village = Start(seed, June(1, 22), standing: Economy());
        village.Structures.Sites.AddRange([Built(10, "house", West, 3, 8), Built(11, "stone-house", Oost, 3, 27)]);
        village.JumpTo(June(1, 22, 15));
        // something in every purse, so there is something left to spend whatever the prices
        foreach (var resident in village.People.All.Where(resident => !resident.IsEddie)) resident.Money = 5_000;
        var before = village.Money;
        var books = Books(village);

        // a calendar day, a little of every quarter
        for (var quarter = 1; quarter <= 96; quarter++)
        {
            village.JumpTo(June(1, 22, 15) + TimeSpan.FromMinutes(15 * quarter));
            village.Run(40);
        }

        var (subsidy, upkeep, spent, departed) = Books(village);
        Assert.Equal(before + (subsidy - books.Subsidy) - (upkeep - books.Upkeep) - (spent - books.Spent) - (departed - books.Departed), village.Money);
        // and it did move
        Assert.True(subsidy > books.Subsidy && upkeep > books.Upkeep && spent > books.Spent);
        Assert.True(village.Ear.Heard.OfType<Paid>().Sum(paid => paid.Wages) > 0);
        Assert.True(village.Ear.Heard.OfType<Paid>().Sum(paid => paid.Pensions + paid.Allowances) > 0);
    }

    private static (long Subsidy, long Upkeep, long Spent, long Departed) Books(Village village)
    {
        var treasuries = RealMap.Islands.Select(island => village.Work.Of(island.Id)).ToList();
        return (treasuries.Sum(t => t.Subsidy), treasuries.Sum(t => t.Upkeep), treasuries.Sum(t => t.Spent), treasuries.Sum(t => t.Departed));
    }

    [Fact]
    public void Who_leaves_takes_his_money_out_of_the_village()
    {
        // singles of 69 in a fast life: they move to the coast within a life year, around midnight
        var data = Data(
            people: p =>
            {
                p["households"] = new JsonArray(new JsonObject { ["adults"] = 1, ["children"] = 0, ["weight"] = 1 });
                p["adultAges"] = new JsonArray(new JsonObject { ["from"] = 69, ["to"] = 69, ["weight"] = 1 });
            },
            life: l => l["ticksPerLifeYear"] = 4000);
        var village = Start(1, June(1, 23, 50), data, Economy());
        foreach (var resident in village.People.All.Where(resident => !resident.IsEddie)) resident.Money = 10_000 + resident.Id;
        var leaving = village.People.All.Count(resident => !resident.IsEddie);
        var before = village.Money;
        var books = Books(village);

        village.Run(4100);

        Assert.Equal(leaving, village.Ear.Heard.OfType<ResidentLeft>().Count());
        var left = village.Ear.Heard.OfType<ResidentLeft>().ToList();
        Assert.Contains(left, e => e.Money > 0);
        var (subsidy, upkeep, spent, departed) = Books(village);
        Assert.Equal(left.Sum(e => e.Money), departed - books.Departed);
        Assert.Equal(before + (subsidy - books.Subsidy) - (upkeep - books.Upkeep) - (spent - books.Spent) - (departed - books.Departed), village.Money);
        Assert.True(subsidy > books.Subsidy && spent > books.Spent);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void A_thrifty_resident_saves_more(ulong seed)
    {
        Village Week(int thrift)
        {
            var data = Data(
                economy: e =>
                {
                    // a cent per tick, from a treasury that does not run dry
                    e["wagePerHour"] = 14400 * 100;
                    e["start"]!["cash"] = 1_000_000_000;
                },
                people: p => p["personality"]!["thrift"] = new JsonObject { ["mean"] = thrift, ["spread"] = 0 });
            var village = Start(seed, June(1, 0), data, Economy());
            for (var day = 0; day < 7; day++)
                foreach (var hour in new[] { 5, 10, 15, 23 })
                {
                    village.JumpTo(June(1 + day, hour));
                    village.Run(100);
                }
            return village;
        }

        var thrifty = Week(90);
        var spender = Week(10);

        Assert.All(thrifty.People.All, resident => Assert.Equal(90, resident.IsEddie ? 90 : resident.Traits.Thrift));
        var earners = thrifty.People.All.Where(resident => !resident.IsEddie && resident.Money > 0).ToList();
        Assert.Contains(earners, resident => thrifty.Work.JobOf(resident.Id) is not null);
        foreach (var resident in earners)
        {
            // the same people with the same jobs, so the same wages
            Assert.Equal(thrifty.Work.JobOf(resident.Id), spender.Work.JobOf(resident.Id));
            Assert.True(resident.Money > spender.People.Get(resident.Id).Money,
                $"{resident.Name}: {resident.Money} saved at thrift 90, {spender.People.Get(resident.Id).Money} at thrift 10");
        }
    }

    [Fact]
    public void The_forest_grows_back()
    {
        var village = Start(1, June(1, 10), Data(economy: e => e["forestRegrowTicks"] = 500), [Built(1, "woodcutter", West, 2, 4)]);
        village.JumpTo(June(1, 10, 15));
        Assert.NotEmpty(village.InRole("woodcutter"));

        var felled = default(Cell?);
        var at = 0L;
        for (var i = 0; i < 2000 && felled is null; i++)
        {
            village.Engine.Step();
            if (village.Work.Felled.Count > 0) (felled, at) = (village.Work.Felled.Single(), village.Engine.Tick);
        }

        Assert.NotNull(felled);
        var tree = felled.Value;
        Assert.Equal(Ground.Forest, RealMap.At(tree));
        Assert.Equal(West, RealMap.IslandOf(tree));
        Assert.NotEqual(new Cell(2, 4), tree);
        Assert.False(village.Work.IsForest(tree));

        while (village.Engine.Tick < at + 499) village.Engine.Step();
        Assert.False(village.Work.IsForest(tree));
        village.Engine.Step();
        Assert.True(village.Work.IsForest(tree));
        Assert.Contains(village.Ear.Heard, e => e is Produced { Material: "wood" });
    }

    [Fact]
    public void Gathering_in_the_forest_fells_nothing_only_a_recipe_that_fells_does()
    {
        var village = Start(1, June(1, 10), standing: [Built(1, "brushwood", West, 2, 4)]);
        village.JumpTo(June(1, 10, 15));
        Assert.NotEmpty(village.InRole("gatherer"));

        for (var i = 0; i < 30_000 && !village.Ear.Heard.OfType<Produced>().Any(); i++)
        {
            village.Engine.Step();
            Assert.Empty(village.Work.Felled);
        }

        Assert.Contains(village.Ear.Heard, e => e is Produced { Material: "branches" });
        Assert.Contains(village.Ear.Heard, e => e is Produced { Material: "firewood" });
    }

    [Fact]
    public void A_resident_takes_the_place_that_fits_his_interests_best()
    {
        var interests = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, "people.json")))!["interests"]!.AsArray()
            .Select(interest => (JsonNode?)JsonValue.Create((string)interest!["id"]!)).ToArray();
        var data = Data(economy: e =>
        {
            e["builders"] = 0;
            // every interest fits the baker, none the farmer
            e["fits"] = new JsonObject { ["baker"] = new JsonArray(interests), ["farmer"] = new JsonArray() };
        });
        var village = Start(1, June(1, 10), data);
        var working = village.People.On(West).Where(resident => resident is { Phase: LifePhase.Working, IsEddie: false }).ToList();
        Assert.True(working.Count >= 5, $"{working.Count} working on West");
        Assert.All(working, resident => Assert.Null(village.Work.JobOf(resident.Id)));

        // the farm comes first in the list, but the bakery fits
        village.Structures.Standing.AddRange([Built(1, "farm", West, 5, 13), Built(2, "bakery", West, 4, 13)]);
        village.Run(2);

        Assert.Equal(["baker", "baker", "farmer", "farmer", "farmer"], working.Take(5).Select(resident => village.Work.JobOf(resident.Id)?.Role));
        Assert.All(working.Skip(5), resident => Assert.Null(village.Work.JobOf(resident.Id)));
        Assert.Equal(working.Take(5).Select(resident => resident.Id), village.Ear.Heard.OfType<Hired>().Select(hired => hired.Resident));
    }

    // Risk: an island out of food staffs its new workplaces in list order, and the bakery stays empty while its households go hungry.
    [Fact]
    public void While_food_is_short_the_places_that_make_food_go_first()
    {
        var data = Data(economy: e =>
        {
            e["builders"] = 0;
            e["fits"] = new JsonObject();
            e["start"]!["stock"]!["West"]!["berries"] = 0;
        });
        var village = Start(1, June(1, 10), data);
        var working = village.People.On(West).Where(resident => resident is { Phase: LifePhase.Working, IsEddie: false }).ToList();
        Assert.True(working.Count >= 5, $"{working.Count} working on West");

        // the farm comes first in the list, but its grain feeds nobody; the bread of the bakery does
        village.Structures.Standing.AddRange([Built(1, "farm", West, 5, 13), Built(2, "bakery", West, 4, 13)]);
        village.Run(2);

        Assert.Equal(["baker", "baker", "farmer", "farmer", "farmer"], working.Take(5).Select(resident => village.Work.JobOf(resident.Id)?.Role));
    }

    // Risk: every worker already has a job, so a new workplace stays empty for weeks and the release it came with never runs.
    [Fact]
    public void Once_a_day_an_empty_workplace_takes_a_worker_from_the_fullest()
    {
        // berry patches of three places, with food enough that they make nothing the island is short of
        void Economy(JsonObject e, int builders)
        {
            e["builders"] = builders;
            e["fits"] = new JsonObject();
            e["start"]!["stock"]!["West"] = new JsonObject { ["berries"] = 1000, ["flour"] = 100 };
        }
        void ThreeSeats(JsonObject c) =>
            c["blueprints"]!.AsArray().Single(blueprint => (string)blueprint!["id"]! == "berry-patch")!["jobs"]![0]!["count"] = 3;
        var working = Start(1, June(1, 10)).People.On(West).Count(resident => resident is { Phase: LifePhase.Working, IsEddie: false });
        Assert.True(working >= 6, $"{working} working on West");
        // the builders take who the two patches leave
        var data = Data(economy: e => Economy(e, working - 6), catalog: ThreeSeats);
        var fertile = RealMap.Islands.Single(island => island.Id == West).Cells.Where(cell => RealMap.At(cell) == Ground.Fertile).ToList();
        var village = Start(1, June(1, 10), data, [Built(1, "berry-patch", West, fertile[0].Row, fertile[0].Col), Built(2, "berry-patch", West, fertile[^1].Row, fertile[^1].Col)]);
        int At(int building) => village.People.All.Count(resident => village.Work.JobOf(resident.Id)?.Building == building);
        village.Run(2);
        Assert.Equal((3, 3), (At(1), At(2)));
        Assert.All(village.People.On(West).Where(resident => resident is { Phase: LifePhase.Working, IsEddie: false }),
            resident => Assert.NotNull(village.Work.JobOf(resident.Id)));

        // a bakery comes, and nobody is free for it
        village.Structures.Standing.Add(Built(3, "bakery", West, 4, 13));
        village.JumpTo(June(2, 12));
        village.Run(2);

        Assert.Equal(["baker"], village.People.All.Where(resident => village.Work.JobOf(resident.Id)?.Building == 3).Select(resident => village.Work.JobOf(resident.Id)!.Role));
        Assert.Equal([2, 3], new[] { At(1), At(2) }.Order());
    }

    [Fact]
    public void Of_equal_places_the_one_free_the_longest_goes_first()
    {
        var data = Data(economy: e =>
        {
            e["builders"] = 0;
            e["fits"] = new JsonObject();
        });
        var village = Start(1, June(1, 10), data);
        var working = village.People.On(West).Count(resident => resident is { Phase: LifePhase.Working, IsEddie: false });

        // two places per bakery: one or two stay free, in the last ones
        var bakeries = (working + 2) / 2;
        for (var i = 0; i < bakeries; i++) village.Structures.Standing.Add(Built(100 + i, "bakery", West, 4, 13));
        village.Run(5);
        var open = Enumerable.Range(100, bakeries).Where(bakery => village.People.All.Count(resident => village.Work.JobOf(resident.Id)?.Building == bakery) < 2).ToList();
        Assert.NotEmpty(open);
        Assert.Equal(working, village.People.All.Count(resident => village.Work.JobOf(resident.Id) is not null));

        // a bakery comes later; it is first in the list, but its places are newer
        village.Structures.Standing.Insert(0, Built(50, "bakery", West, 4, 13));
        village.Run(5);
        var released = village.People.All.Where(resident => village.Work.JobOf(resident.Id)?.Building == 100).ToList();
        Assert.Equal(2, released.Count);
        village.Structures.Standing.RemoveAll(structure => structure.Id == 100);
        village.Run(1);

        Assert.Contains(village.Work.JobOf(released[0].Id)!.Building, open);
    }

    [Fact]
    public void Builders_work_at_a_site_on_their_island()
    {
        var data = Data(economy: e => e["builders"] = 2);
        var village = Start(1, June(1, 10), data);
        // without a site and with no workplace to help at, a builder has no job this quarter, but keeps his seat
        Assert.Empty(village.InRole("builder"));

        // with sites, on their own island, spread over them
        village.Structures.Sites.AddRange([Built(7, "house", West, 3, 8), Built(8, "stone-house", Oost, 3, 27), Built(9, "house", West, 4, 9)]);
        village.Run(1);
        var builders = village.InRole("builder").ToList();
        Assert.Equal(2, builders.Count(resident => resident.Island == West));
        Assert.Equal(2, builders.Count(resident => resident.Island == Oost));
        var west = builders.Where(resident => resident.Island == West).Select(resident => village.Work.JobOf(resident.Id)!).ToList();
        Assert.Equal([7, 9], west.Select(job => job.Building).Order());
        Assert.Equal([new Cell(3, 8), new Cell(4, 9)], west.OrderBy(job => job.Building).Select(job => job.Place));
        Assert.All(builders.Where(resident => resident.Island == Oost), resident => Assert.Equal(new JobPlace(8, new Cell(3, 27), "builder", Shift.Day, true), village.Work.JobOf(resident.Id)));
    }

    [Fact]
    public void A_job_on_the_water_is_done_from_the_shore()
    {
        var village = Start(1, June(1, 10), standing: [Built(6, "fisher", Oost, 2, 34)]);
        var fisher = village.InRole("fisher").First();
        var place = village.Work.JobOf(fisher.Id)!.Place;

        Assert.True(RealMap.IsWalkable(place));
        Assert.Equal(Oost, RealMap.IslandOf(place));
        Assert.Equal(1, place.DistanceTo(new Cell(2, 34)));
    }

    [Fact]
    public void Eddie_works_in_his_workshop_for_nothing()
    {
        var village = Start(1, June(1, 10));
        var eddie = village.People.All.Single(resident => resident.IsEddie);
        Assert.Null(village.Work.JobOf(eddie.Id));

        village.Structures.Standing.Add(Built(3, "eddie-workshop", Islet, 6, 19));
        village.JumpTo(June(1, 10, 15));
        Assert.Equal(new JobPlace(3, new Cell(6, 19), "developer", Shift.Day, false), village.Work.JobOf(eddie.Id));
        Assert.DoesNotContain(village.Ear.Heard, e => e is Hired hired && hired.Resident == eddie.Id);

        village.Run(1000);
        village.JumpTo(June(2, 10));
        village.Run(1000);

        Assert.Equal(0, eddie.Money);
        Assert.Equal(0, village.Work.Of(Islet).Cash);
        Assert.DoesNotContain(village.Ear.Heard, e => e is Paid paid && paid.Island == Islet);
    }

    [Fact]
    public void The_stock_gives_all_or_nothing()
    {
        var village = Start(1, June(1, 10));
        IStock stock = village.Work;

        var (branches, reed, stones) = (stock.Count(West, "branches"), stock.Count(West, "reed"), stock.Count(Oost, "stones"));
        Assert.True(branches >= 2 && reed >= 3 && stones > 0);
        Assert.Equal(0, stock.Count(West, "stones"));

        Assert.False(stock.TryTake(West, new Dictionary<string, int> { ["branches"] = 2, ["stones"] = 1 }, new Cell(5, 8)));
        Assert.Equal(branches, stock.Count(West, "branches"));
        Assert.True(stock.TryTake(West, new Dictionary<string, int> { ["branches"] = 2, ["reed"] = 3 }, new Cell(5, 8)));
        Assert.Equal(branches - 2, stock.Count(West, "branches"));
        Assert.Equal(reed - 3, village.Work.Stock(West)["reed"]);
        Assert.Equal(stones, stock.Count(Oost, "stones"));
    }

    [Fact]
    public void The_start_holdings_lie_at_the_workplaces_that_make_them_and_are_taken_nearest_first()
    {
        // two brushwood spots on West, far apart; nothing on West makes reed
        var (north, south) = (new Cell(2, 4), new Cell(8, 2));
        var village = Start(1, June(1, 10), standing: [Built(1, "brushwood", West, north.Row, north.Col), Built(2, "brushwood", West, south.Row, south.Col)]);
        village.Run(1);
        IStock stock = village.Work;
        var start = EconomyData("start")["stock"]!["West"]!;
        var (firewood, branches, reed) = ((int)start["firewood"]!, (int)start["branches"]!, (int)start["reed"]!);

        // what the brushwood makes is spread over both, and nothing of it is where the island gathers
        Assert.Equal(firewood, village.Work.StockAt(1)["firewood"] + village.Work.StockAt(2)["firewood"]);
        Assert.Equal(branches, village.Work.StockAt(1)["branches"] + village.Work.StockAt(2)["branches"]);
        Assert.Equal(firewood, stock.Count(West, "firewood"));
        Assert.Equal(north, stock.SourceOf(West, "firewood", north));
        Assert.Equal(south, stock.SourceOf(West, "firewood", south));
        // the reed lies where the island gathers
        var gathering = stock.SourceOf(West, "reed", north);
        Assert.NotNull(gathering);
        Assert.NotEqual(north, gathering);
        Assert.NotEqual(south, gathering);
        Assert.Equal(reed, stock.Count(West, "reed"));
        Assert.Null(stock.SourceOf(West, "stones", north));

        // taken near the south spot: all it has there first, the rest from the north
        var atSouth = village.Work.StockAt(2)["firewood"];
        Assert.True(stock.TryTake(West, new Dictionary<string, int> { ["firewood"] = atSouth + 1 }, south));
        Assert.False(village.Work.StockAt(2).ContainsKey("firewood"));
        Assert.Equal(firewood - atSouth - 1, village.Work.StockAt(1)["firewood"]);
        Assert.Equal(north, stock.SourceOf(West, "firewood", south));
    }

    private static JsonNode EconomyData(string key) => JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, "economy.json")))![key]!;

    [Fact]
    public void At_midnight_the_island_settles_its_day()
    {
        var data = Data(economy: e =>
        {
            e["wagePerHour"] = 0;
            e["spendPercent"] = 0;
            e["supportPerDay"] = 500;
        });
        var village = Start(1, June(1, 23), data, [Built(1, "woodcutter", West, 2, 4), Built(4, "bakery", West, 4, 13), Built(5, "quarry", Oost, 2, 28)]);
        village.JumpTo(June(1, 23, 30));
        foreach (var resident in village.People.All.Where(resident => !resident.IsEddie)) resident.Money = 100_000;
        var before = RealMap.Islands.ToDictionary(island => island.Id, island => (Books: Snapshot(village.Work.Of(island.Id)), Stock: village.Work.Stock(island.Id).ToDictionary()));
        var money = village.Money;

        village.JumpTo(June(2, 0));
        var after = RealMap.Islands.ToDictionary(island => island.Id, island => (Books: Snapshot(village.Work.Of(island.Id)), Stock: village.Work.Stock(island.Id).ToDictionary()));
        village.Run(1);

        var economy = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, "economy.json")))!;
        var prices = economy["prices"]!.AsObject().ToDictionary(price => price.Key, price => (long)price.Value!);
        var (upkeep, allowance, pension) = ((long)economy["upkeepPerDay"]!, (long)economy["allowancePerDay"]!, (long)economy["pensionPerDay"]!);
        var (startSubsidy, damping) = ((int)economy["startSubsidy"]!, (double)economy["subsidyDamping"]!);
        foreach (var (island, standing) in new[] { (West, 2), (Oost, 1) })
        {
            var residents = village.People.On(island).ToList();
            var households = residents.Select(resident => resident.Household).Distinct().Count();
            // everyone could pay: no household went without
            Assert.Equal(households, village.Work.LastShopping(island).Households);
            Assert.All(village.Work.LastShopping(island).Short, item => Assert.Equal(0, item.Value));
            var (was, stockWas) = before[island];
            var (now, stockNow) = after[island];
            int Used(string material) => stockWas.GetValueOrDefault(material) - stockNow.GetValueOrDefault(material);

            var subsidy = (long)Math.Floor(startSubsidy / (1 + residents.Count / damping));
            Assert.Equal(subsidy, now.Subsidy - was.Subsidy);
            Assert.Equal(upkeep * standing, now.Upkeep - was.Upkeep);
            Assert.Equal(0, now.Spent - was.Spent);

            var paid = Assert.Single(village.Ear.Heard.OfType<Paid>(), paid => paid.Island == island);
            Assert.Equal(0, paid.Wages);
            // children and students get their allowance, and who is of working age his support less his wage of the
            // day: here nobody earns a wage, so all of them, with a job or without
            var working = residents.Count(resident => resident.Phase == LifePhase.Working);
            Assert.Contains(residents, resident => resident.Phase == LifePhase.Working && village.Work.JobOf(resident.Id) is null);
            Assert.Contains(residents, resident => village.Work.JobOf(resident.Id) is not null);
            Assert.Equal(allowance * residents.Count(resident => resident.Phase is LifePhase.Child or LifePhase.Student) + 500L * working, paid.Allowances);
            Assert.Equal(pension * residents.Count(resident => resident.Phase == LifePhase.Retired), paid.Pensions);

            // one food each and one heat per household, from the island's own stock: what its ground gives
            var food = island == West ? "berries" : "fish";
            var heat = island == West ? "firewood" : "driftwood";
            Assert.Equal(residents.Count, Used(food));
            Assert.Equal(households, Used(heat));
            var bought = residents.Count * prices[food] + households * prices[heat];
            Assert.Equal(subsidy - upkeep * standing - paid.Allowances - paid.Pensions + bought, now.Cash - was.Cash);
        }
        Assert.Equal(money + (after.Values.Sum(a => a.Books.Subsidy) - before.Values.Sum(b => b.Books.Subsidy))
            - (after.Values.Sum(a => a.Books.Upkeep) - before.Values.Sum(b => b.Books.Upkeep)), village.Money);
    }

    [Fact]
    public void The_support_makes_up_for_a_wage_that_did_not_reach_it_that_day()
    {
        // a Monday of work; a support that a full day's wage reaches, but less does not
        var data = Data(economy: e =>
        {
            e["spendPercent"] = 0;
            e["supportPerDay"] = 500;
        });
        // the woodcutters work by day; the fire keeper by night, so by day he earns nothing
        var village = Start(1, June(1, 8), data, [Built(1, "woodcutter", West, 2, 4), Built(2, "campfire", West, 5, 13)]);
        village.Run(1);
        village.JumpTo(June(1, 9));
        var morning = village.People.All.ToDictionary(resident => resident.Id, resident => resident.Money);
        for (var hour = 9; hour < 18; hour++) village.Run(14400);
        var earned = village.People.All.ToDictionary(resident => resident.Id, resident => resident.Money - morning[resident.Id]);

        village.JumpTo(June(2, 0));
        village.Run(1);

        var allowance = (long)JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, "economy.json")))!["allowancePerDay"]!;
        var residents = village.People.On(West).ToList();
        var working = residents.Where(resident => resident.Phase == LifePhase.Working).ToList();
        // some earned a day's wage and get nothing, the others what their wage did not reach
        Assert.Contains(working, resident => earned[resident.Id] >= 500);
        Assert.Contains(working, resident => earned[resident.Id] == 0 && village.Work.JobOf(resident.Id) is null);
        Assert.Contains(working, resident => earned[resident.Id] < 500 && village.Work.JobOf(resident.Id) is { Role: "fire-keeper" });
        var paid = Assert.Single(village.Ear.Heard.OfType<Paid>(), paid => paid.Island == West);
        Assert.Equal(allowance * residents.Count(resident => resident.Phase is LifePhase.Child or LifePhase.Student)
            + working.Sum(resident => Math.Max(0, 500 - earned[resident.Id])), paid.Allowances);
    }

    [Fact]
    public void A_household_that_cannot_pay_for_its_food_is_counted_once_a_day()
    {
        // nobody gets money but what the test gives
        var data = Data(economy: e =>
        {
            foreach (var pay in new[] { "wagePerHour", "allowancePerDay", "pensionPerDay", "supportPerDay" }) e[pay] = 0;
        });
        var village = Start(1, June(1, 23), data);
        village.JumpTo(June(1, 23, 30));
        // before the first midnight nothing has been bought yet
        Assert.Equal(0, village.Work.LastShopping(West).Households);

        // on West only the first household has money, and enough for all it needs; on Oost everyone has
        var west = village.People.On(West).GroupBy(resident => resident.Household).ToList();
        foreach (var resident in village.People.All) resident.Money = 0;
        west[0].First().Money = 100_000;
        foreach (var resident in village.People.On(Oost)) resident.Money = 100_000;
        village.JumpTo(June(2, 0));

        var shopping = village.Work.LastShopping(West);
        Assert.Equal(west.Count, shopping.Households);
        Assert.Equal(west.Count - 1, shopping.Short["food"]);
        Assert.Equal(0, village.Work.LastShopping(Oost).Short["food"]);

        // it is saved with the village
        var saved = SaveJson.FromJson(SaveJson.ToJson(village.Engine.Save()));
        IModule[] modules = [new People(), new FakeStructures(), new Day(), new Work(), new Ear()];
        Engine.Load(saved, modules, data, RealMap, new SimulatedTime(saved.Calendar));
        Assert.Equal(shopping.Short["food"], modules.OfType<Work>().Single().LastShopping(West).Short["food"]);
    }

    private static (long Cash, long Subsidy, long Upkeep, long Spent) Snapshot(Treasury treasury) =>
        (treasury.Cash, treasury.Subsidy, treasury.Upkeep, treasury.Spent);
}

/// <summary>Buildings as the test puts them: <see cref="Standing"/> and <see cref="Sites"/> can be changed at any tick.</summary>
public sealed class FakeStructures : IModule, IStructures
{
    public string Name => "fake-structures";
    public int Version => 1;
    public List<StructureView> Standing { get; } = [];
    public List<StructureView> Sites { get; } = [];
    IReadOnlyList<StructureView> IStructures.Standing() => [.. Standing.OrderBy(structure => structure.Id)];
    IReadOnlyList<StructureView> IStructures.Sites() => [.. Sites.OrderBy(structure => structure.Id)];
    public void Start(ModuleContext village) { }
    public void Step(ModuleContext village) { }
    public JsonNode Save() => new JsonObject();
    public void Load(ModuleContext village, JsonNode data, int version) { }
}

/// <summary>Keeps everything it hears.</summary>
public sealed class Ear : IModule
{
    public string Name => "ear";
    public int Version => 1;
    public List<VillageEvent> Heard { get; } = [];
    public void Start(ModuleContext village) { }
    public void Step(ModuleContext village) => Heard.AddRange(village.Received);
    public JsonNode Save() => new JsonObject();
    public void Load(ModuleContext village, JsonNode data, int version) { }
}
