using System.Text.Json.Nodes;
using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Time;
using Codery.Village.Laws;
using Codery.Village.Life.Folk;
using Codery.Village.Life.Living;
using FolkModule = Codery.Village.Life.Folk.Folk;
using LivingModule = Codery.Village.Life.Living.Living;

namespace Codery.Village.Life.Tests;

/// <summary>
/// Living through the engine, on the real world data with a section changed where a test needs it. Risks: a drought or a
/// winter that changes nothing, or harms everyone alike, makes the chronicle's hard times untrue; sickness that does not
/// keep to households spreads unlike any village; a trade that works a recipe nobody knows makes knowledge meaningless for
/// Knowing and Inventing; food that keeps regardless of its durability makes the laws' durability pointless.
/// </summary>
public class LivingTests
{
    private static readonly DateTimeOffset Spring = new(2027, 3, 1, 0, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateTimeOffset Summer = new(2027, 6, 1, 0, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset December = new(2027, 12, 1, 0, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateTimeOffset January = new(2028, 1, 3, 0, 0, 0, TimeSpan.FromHours(1));

    private const long Day = 24 * Hourly.TicksPerHour;

    private static long TicksPerYear => World.Data.Section<LifeSettings>("life").TicksPerLifeYear;

    [Fact]
    public void A_drought_shrinks_the_harvest_and_brings_shortage()
    {
        // no food at the start and nobody milling, so the grain in stock is what the fields gave less what was eaten
        DataSet Weather(double chance) => WithLiving(living =>
        {
            living["start"] = new JsonObject();
            living["output"]!["farmer"] = 20;
            living["output"]!["miller"] = 0;
            living["output"]!["baker"] = 0;
            living["drought"]!["chance"] = chance;
            living["drought"]!["minDays"] = 21;
            living["drought"]!["maxDays"] = 21;
        });
        var seed = SeedWith(Weather(0), Summer, "farmer");

        var dry = Village(Weather(1), Summer, seed);
        Run(dry.Engine, 21 * Day);
        var wet = Village(Weather(0), Summer, seed);
        Run(wet.Engine, 21 * Day);

        Assert.Single(dry.Events.Of<DroughtBegan>());
        Assert.Empty(wet.Events.Of<DroughtBegan>());
        Assert.True(dry.Living.Stock["grain"] < wet.Living.Stock["grain"] / 2,
            $"grain after a drought {dry.Living.Stock["grain"]:F0}, without {wet.Living.Stock["grain"]:F0}");
        Assert.Contains(dry.Events.Of<Shortage>(), s => s.What == "food");
    }

    [Fact]
    public void Winter_without_fuel_harms_the_old_first()
    {
        // no woodcutters, no wood in stock, old and younger pairs
        var start = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "world", "start.json")))!;
        var trades = start["trades"]!.AsArray();
        trades.Remove(trades.Single(t => (string?)t!["id"] == "woodcutter"));
        var people = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "world", "people.json")))!;
        people["start"]!["pairAges"] = JsonNode.Parse("""[{ "from": 68, "to": 70, "weight": 1 }, { "from": 25, "to": 35, "weight": 1 }]""");
        var living = Living();
        living["start"] = JsonNode.Parse("""{ "grain": 3000 }""");
        var world = World.With(new Dictionary<string, string>
        {
            ["start"] = start.ToJsonString(),
            ["people"] = people.ToJsonString(),
            ["living"] = living.ToJsonString(),
        });
        var seed = SeedWith(world, December, folk => folk.Living.Any(p => Age(folk, p) >= 67) && folk.Living.Any(p => Age(folk, p) is >= 18 and < 67));
        var village = Village(world, December, seed);
        var adults = village.Folk.Living.Where(p => Age(village.Folk, p) is >= 18 and < 67).Select(p => p.Id).ToList();

        Run(village.Engine, 2 * TicksPerYear);

        Assert.Contains(village.Events.Of<Shortage>(), s => s.What == "fuel");
        var cold = village.Events.Of<Died>().Where(d => d.Cause == "cold").ToList();
        Assert.NotEmpty(cold);
        var first = village.Folk.Get(cold[0].Person)!;
        Assert.True(village.Folk.AgeOf(first, cold[0].Tick) >= 67, $"the first to die of cold was {village.Folk.AgeOf(first, cold[0].Tick):F0}");
        Assert.All(adults, id => Assert.False(village.Folk.Get(id)!.DiedTick <= cold[0].Tick, "an adult died before the old"));
    }

    [Fact]
    public void Sickness_passes_within_a_household_more_than_beyond()
    {
        var world = WithLiving(living => living["sickness"]!["chance"] = 0.02);
        var (exposedDays, exposedIll, otherDays, otherIll) = (0, 0, 0, 0);
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var village = Village(world, Spring, seed);
            Run(village.Engine, 7 * Hourly.TicksPerHour);
            var ill = village.Living.Ill.ToHashSet();
            var households = village.Folk.Living.ToDictionary(p => p.Id, p => p.Household);
            for (var day = 0; day < 4 * 7; day++)
            {
                Run(village.Engine, Day);
                foreach (var (person, household) in households)
                {
                    if (ill.Contains(person) || village.Folk.Get(person)!.DiedTick is not null) continue;
                    var exposed = households.Any(h => h.Key != person && h.Value == household && ill.Contains(h.Key));
                    var fell = village.Living.Ill.Contains(person);
                    if (exposed) (exposedDays, exposedIll) = (exposedDays + 1, exposedIll + (fell ? 1 : 0));
                    else (otherDays, otherIll) = (otherDays + 1, otherIll + (fell ? 1 : 0));
                }
                ill = village.Living.Ill.ToHashSet();
                households = village.Folk.Living.ToDictionary(p => p.Id, p => p.Household);
            }
        }

        Assert.True(exposedDays > 0 && otherDays > 0);
        var within = (double)exposedIll / exposedDays;
        var beyond = (double)otherIll / otherDays;
        Assert.True(within > 2 * beyond, $"ill with an ill housemate {within:P1} a day ({exposedIll}/{exposedDays}), without {beyond:P1} ({otherIll}/{otherDays})");
    }

    [Fact]
    public void A_trade_stops_when_nobody_knows_its_recipe()
    {
        // winter: no harvest, only the grain in stock
        var world = WithLiving(living => living["start"] = JsonNode.Parse("""{ "grain": 600, "wood": 2000 }"""));
        var bread = World.Laws.Start.Recipes.Single(r => r.Out == World.Laws.Labels["bread"]).Id;
        var seed = SeedWith(world, January, "miller", "baker");

        var forgotten = Village(world, January, seed, unknown: bread);
        Run(forgotten.Engine, 6 * TicksPerYear);
        var known = Village(world, January, seed);
        Run(known.Engine, 6 * TicksPerYear);

        Assert.DoesNotContain(forgotten.Events.Of<Made>(), m => m.Substance == World.Laws.Labels["bread"]);
        Assert.Contains(known.Events.Of<Made>(), m => m.Substance == World.Laws.Labels["bread"]);
        var hungry = forgotten.Events.Of<Shortage>().FirstOrDefault(s => s.What == "food");
        Assert.NotNull(hungry);
        var later = known.Events.Of<Shortage>().FirstOrDefault(s => s.What == "food");
        Assert.True(later is null || later.Tick > hungry.Tick, "bread stretches the grain: the shortage comes later");
    }

    [Fact]
    public void Food_spoils_by_its_durability()
    {
        var world = WithLiving(living => living["start"] = JsonNode.Parse("""{ "berries": 1000, "grain": 1000, "wood": 500 }"""));
        var village = Village(world, Spring, 1);

        Run(village.Engine, Day);

        var spoiled = village.Events.Of<Spoiled>().ToList();
        var berries = spoiled.Where(s => s.Substance == "berries").Sum(s => s.Amount);
        var grain = spoiled.Where(s => s.Substance == "grain").Sum(s => s.Amount);
        Assert.True(grain > 0, "grain spoils too, only slower");
        Assert.True(berries > 2 * grain, $"berries spoiled {berries}, grain {grain}");
    }

    // ── a village to watch ──

    private sealed record Watched(Engine Engine, FolkModule Folk, LivingModule Living, Recorder Events);

    /// <summary>Folk and Living on <paramref name="world"/>; everyone knows the start recipes of their trade but <paramref name="unknown"/>.</summary>
    private static Watched Village(DataSet world, DateTimeOffset start, ulong seed, string? unknown = null)
    {
        var laws = WorldData.From(world);
        var folk = new FolkModule();
        var living = new LivingModule((person, item) =>
            item != unknown && folk.Get(person)?.Trade is { } trade && laws.Start.Trades.Single(t => t.Id == trade).Recipes.Contains(item));
        var events = new Recorder();
        var engine = new Engine([folk, living, events], world, World.Map, seed, new SimulatedTime(start));
        engine.Start();
        return new Watched(engine, folk, living, events);
    }

    // the first seed whose start has an adult in each of the trades
    private static ulong SeedWith(DataSet world, DateTimeOffset start, params string[] trades) =>
        SeedWith(world, start, folk => trades.All(t => folk.Living.Any(p => p.Trade == t && Age(folk, p) < 67)));

    private static ulong SeedWith(DataSet world, DateTimeOffset start, Func<FolkModule, bool> fits)
    {
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var folk = new FolkModule();
            new Engine([folk], world, World.Map, seed, new SimulatedTime(start)).Start();
            if (fits(folk)) return seed;
        }
        throw new InvalidOperationException("No seed up to 200 fits.");
    }

    private static double Age(FolkModule folk, Person p) => folk.AgeOf(p, 0);

    private static JsonNode Living() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "world", "living.json")))!;

    private static DataSet WithLiving(Action<JsonNode> change)
    {
        var living = Living();
        change(living);
        return World.With(new Dictionary<string, string> { ["living"] = living.ToJsonString() });
    }

    private static void Run(Engine engine, long ticks)
    {
        for (long i = 0; i < ticks; i++) engine.Step();
    }

    /// <summary>Keeps every event the village publishes.</summary>
    private sealed class Recorder : IModule
    {
        private readonly List<VillageEvent> _events = [];

        public string Name => "recorder";
        public int Version => 1;

        public IEnumerable<T> Of<T>() => _events.OfType<T>();

        public void Start(ModuleContext village) { }

        public void Step(ModuleContext village)
        {
            if (village.Received.Count > 0) _events.AddRange(village.Received);
        }

        public JsonNode Save() => new JsonObject();

        public void Load(ModuleContext village, JsonNode data, int version) { }
    }
}
