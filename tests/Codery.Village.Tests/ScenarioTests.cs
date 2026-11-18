using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;
using Codery.Village.Modules.People;
using Codery.Village.Server;
using Xunit.Abstractions;
using BuildingModule = Codery.Village.Modules.Building.Building;
using DayModule = Codery.Village.Modules.Day.Day;
using HappinessModule = Codery.Village.Modules.Happiness.Happiness;
using PeopleModule = Codery.Village.Modules.People.People;
using WorkModule = Codery.Village.Modules.Work.Work;

namespace Codery.Village.Tests;

/// <summary>
/// The whole village over weeks and many seeds: it stays within sane bounds, whatever the seed. A bound that fails is
/// put right in the data, not here. These run for a while; <c>--filter "Category!=Scenario"</c> leaves them out.
/// </summary>
[Trait("Category", "Scenario")]
public class ScenarioTests(ITestOutputHelper output)
{
    private const int Seeds = 30;
    private const int Weeks = 4;

    [Fact]
    public void Four_weeks_of_life_stay_within_bounds()
    {
        var sections = new Sections();
        var most = 100L * (int)sections["economy"]["startSubsidy"]!;
        var ends = new ConcurrentDictionary<ulong, string>();

        EverySeed(seed =>
        {
            var scenario = Scenario.Start(seed, sections);
            var atStart = scenario.Building.All.Where(structure => structure.Done).Select(structure => structure.Id).ToHashSet();
            scenario.Days(7 * Weeks, each: village =>
            {
                foreach (var island in village.Ordinary)
                {
                    var (residents, cash, median) = (village.People.On(island.Id).Count(), village.Work.Of(island.Id).Cash, village.Median(island.Id));
                    // poverty may be there, hunger not as a rule: at most a quarter of the households goes without food
                    var shopping = village.Work.LastShopping(island.Id);
                    var hungry = shopping.Short["food"];
                    if (residents is >= 6 and <= 60 && cash >= 0 && cash <= most && median is >= 20 and <= 90 && hungry * 4 <= shopping.Households) continue;
                    Assert.Fail($"{island.Name} on {village.Now:yyyy-MM-dd HH:mm}: {residents} residents, {cash} in the treasury, median happiness {median}, " +
                        $"{hungry} of {shopping.Households} households without all their food, {village.Food(island.Id)} food in stock");
                }
            });

            foreach (var island in scenario.Ordinary)
                Assert.True(scenario.Building.All.Any(structure => structure.Island == island.Id && structure.Done && !atStart.Contains(structure.Id)),
                    $"nothing was finished on {island.Name} in {Weeks} weeks");
            Assert.Empty(scenario.Engine.Stopped);
            ends[seed] = scenario.Summary();
        });

        foreach (var seed in ends.Keys.Order().Take(3)) output.WriteLine($"seed {seed} after {Weeks} weeks:{Environment.NewLine}{ends[seed]}");
    }

    [Fact]
    public void Everyone_saves()
    {
        var sections = new Sections();
        sections["people"]["personality"]!["thrift"] = new JsonObject { ["mean"] = 100, ["spread"] = 0 };

        EverySeed(seed =>
        {
            var scenario = Scenario.Start(seed, sections);
            var atStart = scenario.Building.All.Where(structure => structure.Done).Select(structure => structure.Id).ToHashSet();
            scenario.Days(7 * Weeks, each: village =>
            {
                foreach (var island in village.Ordinary)
                    Assert.True(village.Work.Of(island.Id).Cash > 0, $"the treasury of {island.Name} is empty at {village.Now:yyyy-MM-dd HH:mm}");
            });

            foreach (var island in scenario.Ordinary)
            {
                // nobody spent anything: all they earned, they kept
                Assert.Equal(0, scenario.Work.Of(island.Id).Spent);
                Assert.True(scenario.Building.All.Any(structure => structure.Island == island.Id && structure.Done && !atStart.Contains(structure.Id)),
                    $"nothing was finished on {island.Name} in {Weeks} weeks");
            }
            Assert.Empty(scenario.Engine.Stopped);
        });
    }

    [Fact]
    public void Food_runs_out()
    {
        const int Days = 7;
        var normal = new Sections();
        var hungry = new Sections();
        hungry.Map = Sections.Barren(hungry.Map, "West");
        var west = hungry.Island().Islands.Single(island => island.Name == "West").Id;
        var oost = hungry.Island().Islands.Single(island => island.Name == "Oost").Id;
        var life = normal["life"];
        var leaveAt = (int)life["retireFrom"]! + (int)life["retiredYears"]!;
        var leavers = 0;

        EverySeed(seed =>
        {
            var village = Scenario.Start(seed, hungry);
            for (var row = 0; row < village.Map.Rows; row++)
                for (var col = 0; col < village.Map.Cols; col++)
                    Assert.False(village.Map.At(new Cell(row, col)) is Ground.Fertile or Ground.Fishing && village.Building.IslandAt(new Cell(row, col)) == west);
            var control = Scenario.Start(seed, normal);
            var westAtStart = village.Median(west);

            for (var quarter = 0; quarter < Days * 96; quarter++)
            {
                var ages = village.People.All.ToDictionary(resident => resident.Id, resident => village.People.AgeOf(resident));
                village.NextQuarter();
                control.NextQuarter();

                // who left, left for the coast, at the age for it
                foreach (var (id, age) in ages.Where(item => village.People.All.All(resident => resident.Id != item.Key)))
                    Assert.True(age >= leaveAt - 0.01, $"resident {id} left at {age:0.00} on {village.Now:yyyy-MM-dd HH:mm}");
                // the other island has its food, and is as happy as it would be
                Assert.True(village.Food(oost) > 0, $"Oost has no food at {village.Now:yyyy-MM-dd HH:mm}");
                if (quarter % 96 == 95)
                    Assert.True(Math.Abs(village.Median(oost) - control.Median(oost)) <= 10,
                        $"Oost: median {village.Median(oost)} hungry, {control.Median(oost)} not, on {village.Now:yyyy-MM-dd}");
            }

            Assert.All(village.Left, left => Assert.Equal(PeopleModule.Coast, left.Reason));
            Interlocked.Add(ref leavers, village.Left.Count);
            Assert.Equal(0, village.Food(west));
            Assert.All(village.People.On(west), resident => Assert.Contains(village.Happiness.Reasons(resident.Id), reason => reason is ("food", < 0)));
            Assert.True(village.Median(west) <= westAtStart - 15, $"West: median {westAtStart} at the start, {village.Median(west)} after a week without food");
            Assert.True(village.Median(west) <= control.Median(west) - 15, $"West: median {village.Median(west)} hungry, {control.Median(west)} not");
            Assert.Empty(village.Engine.Stopped);
        });
        // in a week of the calendar the village ages a life year: some leave
        Assert.True(leavers > 0);
    }

    [Fact]
    public void Whatever_a_visitor_does_the_village_recovers()
    {
        var data = VillageSetup.Data([]);
        var map = VillageSetup.Map();
        var after = (long)(int)new Sections()["building"]["putBackMinutes"]! * 60 * Clocks.TicksPerSecond;
        const int Visit = 15 * 60 * Clocks.TicksPerSecond;
        const int Hour = 60 * 60 * Clocks.TicksPerSecond;

        EverySeed(seed =>
        {
            var time = new SimulatedTime(VillageSetup.ExportStart);
            var engine = VillageSetup.Begin(data, map, seed, time);
            var building = engine.Running.OfType<BuildingModule>().Single();
            var day = engine.Running.OfType<DayModule>().Single();
            var people = engine.Running.OfType<PeopleModule>().Single();
            var visitor = Rng.For(seed, "visitor");

            // a quarter of an hour of a visitor who moves anything anywhere, ten times a second at most
            var moved = 0;
            for (var tick = 0; tick < Visit; tick++)
            {
                engine.Step();
                if (visitor.Next(40) != 0) continue;
                var things = building.Things;
                var id = visitor.Next(things.Count + 2) - 1;
                var to = visitor.Next(2) == 0 || id < 0 || id >= things.Count
                    ? new Cell(visitor.Next(map.Rows), visitor.Next(map.Cols))
                    : Pick(visitor, map.Islands.Single(island => island.Id == map.IslandOf(things[id].Home)).Cells);
                if (building.Move(id < 0 || id >= things.Count ? id + 1000 : things[id].Id, to) is null) moved++;
            }
            Assert.True(moved > 10, $"the visitor moved only {moved} things");

            // an hour on: nothing stands in someone's way for longer than it takes to put it back
            var since = new Dictionary<int, long>();
            for (var tick = 0; tick < Hour; tick++)
            {
                engine.Step();
                var ways = new HashSet<Cell>();
                foreach (var resident in people.All)
                {
                    var doing = day.Of(resident.Id);
                    ways.Add(doing.Place);
                    if (doing.Walk is { } walk) ways.UnionWith(walk.Cells);
                }
                foreach (var thing in building.Things)
                {
                    if (thing.At == thing.Home || !ways.Contains(thing.At))
                    {
                        since.Remove(thing.Id);
                        continue;
                    }
                    since.TryAdd(thing.Id, engine.Tick);
                    Assert.True(engine.Tick - since[thing.Id] <= after + 4,
                        $"the {thing.Kind} at {thing.At} has been in the way since tick {since[thing.Id]}, now {engine.Tick}");
                }
            }
            var built = building.All.SelectMany(structure => structure.Cells).ToHashSet();
            Assert.All(building.Things, thing =>
            {
                Assert.Equal(map.IslandOf(thing.Home), map.IslandOf(thing.At));
                Assert.True(map.IsWalkable(thing.At) && map.At(thing.At) != Ground.Lot && !built.Contains(thing.At), $"the {thing.Kind} stands at {thing.At}");
            });
            Assert.Empty(engine.Stopped);

            // and the village round-trips as it stands
            var saved = SaveJson.FromJson(SaveJson.ToJson(engine.Save()));
            var loaded = Engine.Load(saved, VillageSetup.Modules(), data, map, new SimulatedTime(saved.Calendar));
            for (var tick = 0; tick < 2000; tick++)
            {
                engine.Step();
                loaded.Step();
            }
            var (expected, actual) = (engine.Save(), loaded.Save());
            foreach (var part in expected.Modules)
                Assert.Equal(JsonSerializer.Serialize(part, SaveJson.Options), JsonSerializer.Serialize(actual.Modules.Single(other => other.Name == part.Name), SaveJson.Options));
            Assert.Equal(SaveJson.ToJson(expected), SaveJson.ToJson(actual));
        });
    }

    private static Cell Pick(Rng random, IReadOnlyList<Cell> cells) => cells[random.Next(cells.Count)];

    /// <summary>Runs the check for every seed at once, and names the seeds it failed for.</summary>
    private static void EverySeed(Action<ulong> check)
    {
        var failures = new ConcurrentBag<(ulong Seed, string Why)>();
        Parallel.For(1, Seeds + 1, seed =>
        {
            try
            {
                check((ulong)seed);
            }
            catch (Exception e)
            {
                failures.Add(((ulong)seed, e.Message));
            }
        });
        Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures.OrderBy(failure => failure.Seed).Select(failure => $"seed {failure.Seed}: {failure.Why}")));
    }
}

/// <summary>The data of the village as JSON and the map as text, to change in a test before they are read.</summary>
internal sealed class Sections
{
    private static readonly string[] Names = ["life", "people", "day", "catalog", "economy", "building", "wishes", "talk"];

    public Dictionary<string, JsonObject> Json { get; } = Names.ToDictionary(name => name,
        name => JsonNode.Parse(File.ReadAllText(Path.Combine(Builders.Folder, name + ".json")))!.AsObject());

    public string Map { get; set; } = File.ReadAllText(Path.Combine(Builders.Folder, "map.txt"));

    public JsonObject this[string name] => Json[name];

    public IslandMap Island() => MapText.Parse(Map);

    /// <summary>
    /// The same village with every number counted in ticks divided by <paramref name="speed"/>, and the wage per tick
    /// multiplied by it: an hour of the calendar in 14400 / speed ticks, with all the village does in it.
    /// </summary>
    public DataSet Quick(int speed)
    {
        var json = Json.ToDictionary(item => item.Key, item => item.Value.DeepClone().AsObject());
        int Less(JsonNode? ticks) => Math.Max(1, (int)Math.Round((double)(int)ticks! / speed, MidpointRounding.AwayFromZero));

        json["life"]["ticksPerLifeYear"] = Less(json["life"]["ticksPerLifeYear"]);
        json["day"]["ticksPerCell"] = Less(json["day"]["ticksPerCell"]);
        json["economy"]["wagePerHour"] = (int)json["economy"]["wagePerHour"]! * speed;
        json["economy"]["forestRegrowTicks"] = Less(json["economy"]["forestRegrowTicks"]);
        json["building"]["community"]!["decideTicks"] = Less(json["building"]["community"]!["decideTicks"]);
        json["wishes"]["everyTicks"] = Less(json["wishes"]["everyTicks"]);
        foreach (var blueprint in json["catalog"]["blueprints"]!.AsArray())
        {
            foreach (var phase in blueprint!["phases"]!.AsArray()) phase!["ticks"] = Less(phase["ticks"]);
            if (blueprint["recipe"] is { } recipe) recipe["ticks"] = Less(recipe["ticks"]);
        }
        return DataSet.FromTexts(json.ToDictionary(item => item.Key, item => item.Value.ToJsonString()), []);
    }

    /// <summary>The map with the island called <paramref name="name"/> without fertile ground, and the sea off it without fishing ground.</summary>
    public static string Barren(string map, string name)
    {
        var parsed = MapText.Parse(map);
        var island = parsed.Islands.Single(island => island.Name == name);
        var near = island.Cells.Concat(island.Cells.SelectMany(cell => cell.Neighbours())).ToHashSet();
        var lines = map.Split('\n');
        var row = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("island ")) continue;
            var tiles = line.ToCharArray();
            for (var col = 0; col < tiles.Length; col++)
            {
                if (!near.Contains(new Cell(row, col))) continue;
                if (tiles[col] == 'F') tiles[col] = '.';
                if (tiles[col] == 'v') tiles[col] = '~';
            }
            lines[i] = new string(tiles) + (lines[i].EndsWith('\r') ? "\r" : "");
            row++;
        }
        return string.Join('\n', lines);
    }
}

/// <summary>Holds on to its context, so the scenario can let Day settle a quarter.</summary>
internal sealed class Settler : IModule
{
    public ModuleContext Context { get; private set; } = null!;
    public string Name => "settler";
    public int Version => 1;
    public void Start(ModuleContext village) => Context = village;
    public void Step(ModuleContext village) { }
    public JsonNode Save() => new JsonObject();
    public void Load(ModuleContext village, JsonNode data, int version) => Context = village;
}

/// <summary>Keeps who left the village, and why.</summary>
internal sealed class Leavers : IModule
{
    public ConcurrentQueue<ResidentLeft> Left { get; } = new();
    public string Name => "leavers";
    public int Version => 1;
    public void Start(ModuleContext village) { }
    public void Step(ModuleContext village)
    {
        foreach (var left in village.Received.OfType<ResidentLeft>()) Left.Enqueue(left);
    }
    public JsonNode Save() => new JsonObject();
    public void Load(ModuleContext village, JsonNode data, int version) { }
}

/// <summary>
/// The whole village, sped up: every quarter of an hour of the calendar Day settles at once, without walking, and the
/// quarter runs in 3600 / <see cref="Speed"/> ticks of the data that <see cref="Sections.Quick"/> makes of it.
/// </summary>
internal sealed class Scenario
{
    public const int Speed = 60;
    private const int TicksPerQuarter = 3600 / Speed;
    private static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);
    private static readonly string[] Needs = ["food", "heat"];

    private readonly Settler _settler;
    private readonly IReadOnlyDictionary<string, HashSet<string>> _goods;
    private DateTimeOffset _quarter;

    private Scenario(Engine engine, SimulatedTime time, IReadOnlyList<IModule> modules, IslandMap map, Catalog catalog)
    {
        Engine = engine;
        Time = time;
        Modules = modules;
        Map = map;
        _settler = modules.OfType<Settler>().Single();
        _goods = Needs.ToDictionary(need => need,
            need => catalog.Materials.Where(material => material.Satisfies?.Contains(need) == true).Select(material => material.Id).ToHashSet());
        _quarter = time.GetUtcNow();
    }

    public Engine Engine { get; }
    public SimulatedTime Time { get; }
    public IReadOnlyList<IModule> Modules { get; }
    public IslandMap Map { get; }
    public PeopleModule People => Modules.OfType<PeopleModule>().Single();
    public WorkModule Work => Modules.OfType<WorkModule>().Single();
    public BuildingModule Building => Modules.OfType<BuildingModule>().Single();
    public HappinessModule Happiness => Modules.OfType<HappinessModule>().Single();
    public IReadOnlyCollection<ResidentLeft> Left => Modules.OfType<Leavers>().Single().Left;
    public IEnumerable<Island> Ordinary => Map.Islands.Where(island => !island.Eddie);
    /// <summary>The calendar, in Dutch summer time: for messages.</summary>
    public DateTimeOffset Now => Time.GetUtcNow().ToOffset(TimeSpan.FromHours(2));

    /// <summary>A new village on a Monday morning in June, with its first quarter run.</summary>
    public static Scenario Start(ulong seed, Sections sections)
    {
        var map = sections.Island();
        var data = sections.Quick(Speed);
        var time = new SimulatedTime(VillageSetup.ExportStart);
        IModule[] modules = [.. VillageSetup.Modules(), new Settler(), new Leavers()];
        var engine = new Engine(modules, data, map, seed, time);
        engine.Start();
        var scenario = new Scenario(engine, time, modules, map, data.Section<Catalog>("catalog"));
        scenario.Run();
        return scenario;
    }

    /// <summary>The next quarter: the calendar moves to its start, Day settles, and its ticks run.</summary>
    public void NextQuarter()
    {
        _quarter += Quarter;
        Time.Advance(_quarter - Time.GetUtcNow());
        Modules.OfType<DayModule>().Single().Settle(_settler.Context);
        Run();
    }

    // as the host does, the village lets go of its old log lines
    private void Run()
    {
        for (var tick = 0; tick < TicksPerQuarter; tick++) Engine.Step();
        Engine.Forget();
    }

    /// <summary>Runs <paramref name="days"/> calendar days, and calls <paramref name="each"/> after every quarter.</summary>
    public void Days(int days, Action<Scenario>? each = null)
    {
        for (var quarter = 0; quarter < days * 96; quarter++)
        {
            NextQuarter();
            each?.Invoke(this);
        }
    }

    public int Median(int island)
    {
        var values = People.On(island).Select(resident => Happiness.Of(resident.Id)).Order().ToList();
        return values.Count == 0 ? 0 : values[values.Count / 2];
    }

    public int Food(int island) => _goods["food"].Sum(material => Work.Count(island, material));

    public int Heat(int island) => _goods["heat"].Sum(material => Work.Count(island, material));

    /// <summary>Per ordinary island: residents, homes, the treasury, the median happiness and the stock of food and heat.</summary>
    public string Summary() => string.Join(Environment.NewLine, Ordinary.Select(island =>
        $"  {island.Name}: {People.On(island.Id).Count()} residents, " +
        $"{Building.All.Count(structure => structure.Island == island.Id && structure.Done && structure.Households.Count > 0)} homes lived in, " +
        $"{Work.Of(island.Id).Cash / 100} shells in the treasury, median happiness {Median(island.Id)}, " +
        $"{Work.LastShopping(island.Id).Short["food"]} of {Work.LastShopping(island.Id).Households} households without all their food, " +
        $"food {Food(island.Id)}, heat {Heat(island.Id)}, stock {string.Join(" ", Work.Stock(island.Id).Select(item => $"{item.Key} {item.Value}"))}"));
}
