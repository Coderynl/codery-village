using System.Text.Json.Nodes;
using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;
using Codery.Village.Laws;
using Recipe = Codery.Village.Laws.Recipe;
using Codery.Village.Life.Folk;
using Codery.Village.Life.Knowing;
using FolkModule = Codery.Village.Life.Folk.Folk;
using KnowingModule = Codery.Village.Life.Knowing.Knowing;
using LivingModule = Codery.Village.Life.Living.Living;

namespace Codery.Village.Life.Tests;

/// <summary>
/// Knowing through the engine, on the real world data. Risks: a child who does not take up what a parent knows leaves a trade
/// without heirs and the village without bread; knowledge that outlives everyone who held it makes the chronicle's losses
/// untrue and lets Inventing build on what nobody knows; a school that teaches no more than a household does makes building
/// one pointless.
/// </summary>
public class KnowingTests
{
    private static readonly DateTimeOffset Spring = new(2027, 3, 1, 0, 0, 0, TimeSpan.FromHours(1));

    private const long Day = 24 * Hourly.TicksPerHour;

    private static long TicksPerYear => World.Data.Section<LifeSettings>("life").TicksPerLifeYear;

    [Fact]
    public void A_child_learns_the_trade_of_a_parent()
    {
        var flour = World.Laws.Start.Recipes.Single(r => r.Out == World.Laws.Labels["flour"]).Id;
        // a miller young enough to live on, with a child young enough to grow up beside them
        var seed = SeedWith(World.Data, folk => folk.Living.Any(miller => miller.Trade == "miller" && Age(folk, miller) < 55
            && folk.Living.Any(c => (c.Mother == miller.Id || c.Father == miller.Id) && Age(folk, c) < 10)));
        var village = Village(World.Data, seed);
        var child = village.Folk.Living
            .Where(c => village.Folk.Living.Any(m => m.Trade == "miller" && (c.Mother == m.Id || c.Father == m.Id)) && Age(village.Folk, c) < 10)
            .MinBy(c => c.BornTick)!;
        Assert.False(village.Knowing.Knows(child.Id, flour));

        Run(village.Engine, 18 * TicksPerYear);

        Assert.True(village.Knowing.Knows(child.Id, flour), $"the child knows flour at level {village.Knowing.Level(child.Id, flour)}");
        var learned = Assert.Single(village.Events.Of<Learned>(), l => l.Person == child.Id && l.Item == flour);
        Assert.Contains(learned.How, new[] { "parent", "master" });
    }

    [Fact]
    public void Knowledge_dies_with_its_last_untaught_bearer()
    {
        // everyone starts 95, so nobody has children to teach
        var people = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "world", "people.json")))!;
        people["start"]!["pairAges"] = JsonNode.Parse("""[{ "from": 95, "to": 95, "weight": 1 }]""");
        var village = Village(World.With(new Dictionary<string, string> { ["people"] = people.ToJsonString() }), 1);
        var bearer = village.Folk.Living[0].Id;
        village.Knowing.Learn(bearer, "fire:ceramic", 80, "found");
        Assert.Contains("fire:ceramic", village.Knowing.KnownOnIsland());

        for (var day = 0; day < 6 * 7 && village.Folk.Get(bearer)!.DiedTick is null; day++) Run(village.Engine, Day);
        Run(village.Engine, Day);

        var died = village.Folk.Get(bearer)!.DiedTick;
        Assert.NotNull(died);
        var forgotten = Assert.Single(village.Events.Of<Forgotten>(), f => f.Item == "fire:ceramic");
        Assert.Equal(bearer, forgotten.LastBearer);
        Assert.True(forgotten.Tick >= died);
        Assert.DoesNotContain("fire:ceramic", village.Knowing.KnownOnIsland());
    }

    [Fact]
    public void A_school_spreads_one_teacher_to_a_generation()
    {
        // a teacher without children of their own, so only a school passes on what they know
        DataSet Schooling(int pupils)
        {
            var living = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "world", "living.json")))!;
            living["school"]!["pupils"] = pupils;
            return World.With(new Dictionary<string, string> { ["living"] = living.ToJsonString() });
        }
        var seed = SeedWith(World.Data, folk => folk.Living.Any(t => t.Trade == "teacher" && Age(folk, t) < 55
            && !folk.Living.Any(c => c.Mother == t.Id || c.Father == t.Id))
            && folk.Living.Count(p => folk.StageOf(p, 0) is LifeStage.Child or LifeStage.Pupil) >= 3);

        double Share(DataSet world)
        {
            var village = Village(world, seed);
            var teacher = village.Folk.Living.First(t => t.Trade == "teacher" && Age(village.Folk, t) < 55);
            village.Knowing.Learn(teacher.Id, "weave", 90, "found");
            Run(village.Engine, 4 * TicksPerYear);
            var pupils = village.Folk.Living.Where(p => village.Folk.StageOf(p, village.Engine.Tick) == LifeStage.Pupil).ToList();
            Assert.NotEmpty(pupils);
            return (double)pupils.Count(p => village.Knowing.Knows(p.Id, "weave")) / pupils.Count;
        }

        var with = Share(Schooling(1));
        var without = Share(Schooling(1000));

        Assert.True(with >= 0.8, $"with a school {with:P0} of the pupils know weaving");
        Assert.True(without < 0.4, $"without a school {without:P0} of the pupils know weaving");
    }

    [Fact]
    public void Practice_raises_what_a_worker_uses()
    {
        var bread = World.Laws.Start.Recipes.Single(r => r.Out == World.Laws.Labels["bread"]).Id;
        var village = Village(World.Data, 1);
        // the best baker: nobody teaches them more, so only practice can raise their bread
        var baker = village.Folk.Living.Where(p => p.Trade == "baker").MaxBy(p => village.Knowing.Level(p.Id, bread))!;
        var before = village.Knowing.Level(baker.Id, bread);

        Run(village.Engine, 7 * Day);

        Assert.True(village.Knowing.Level(baker.Id, bread) > before, $"bread stays at level {before}");
    }

    [Fact]
    public void Those_who_raised_a_kiln_know_how_to_fire_in_it()
    {
        var laws = World.Laws;
        var village = Village(World.Data, 1);
        var tempered = laws.Nature.Apply("mix", [new(laws.Substances["clay"], 3), new(laws.Substances["sand"], 1)])!;
        var brick = laws.Nature.Apply("fire", [new(tempered, 1)])!;
        var mix = new Recipe($"mix:{tempered.Id}", "mix", [new("clay", 3), new("sand", 1)], tempered.Id);
        var fire = new Recipe($"fire:{brick.Id}", "fire", [new(tempered.Id, 1)], brick.Id);
        village.Living.Learn(mix, tempered);
        village.Living.Learn(fire, brick);
        Assert.NotEmpty(village.Living.TilesOf("clay"));
        Assert.NotEmpty(village.Living.TilesOf("sand"));
        var builders = village.Folk.Living.Where(p => p.Trade == "builder" && village.Folk.StageOf(p, 0) >= LifeStage.Adult).ToList();
        Assert.NotEmpty(builders);
        foreach (var b in builders)
        {
            village.Knowing.Learn(b.Id, mix.Id, 80, "found");
            village.Knowing.Learn(b.Id, fire.Id, 80, "found");
        }

        for (var day = 0; day < 120 && !village.Living.Structures.Contains("kiln"); day++) Run(village.Engine, Day);
        Assert.Contains("kiln", village.Living.Structures);
        Run(village.Engine, Day);

        foreach (var b in builders.Where(b => village.Folk.Get(b.Id)!.DiedTick is null))
            Assert.True(village.Knowing.Knows(b.Id, "kiln"), $"builder {b.Id} knows the kiln at level {village.Knowing.Level(b.Id, "kiln")}");
    }

    [Fact]
    public void Knowing_comes_back_the_same_after_saving()
    {
        var unbroken = LifeVillage.Start(1, World.Data, World.Map, Spring);
        Run(unbroken, 6 * TicksPerYear);

        var first = LifeVillage.Start(1, World.Data, World.Map, Spring);
        var children = FolkOf(first).Living.Where(p => FolkOf(first).StageOf(p, 0) < LifeStage.Adult).Select(p => p.Id).ToList();
        Run(first, 5 * TicksPerYear);
        Assert.Contains(children, id => Items(first).Any(item => KnowingOf(first).Knows(id, item)));
        var saved = SaveJson.FromJson(SaveJson.ToJson(first.Save()));
        var loaded = Engine.Load(saved, LifeVillage.Modules(), World.Data, World.Map, new SimulatedTime(saved.Calendar));
        Run(loaded, TicksPerYear);

        Assert.Equal(KnowingOf(unbroken).KnownOnIsland(), KnowingOf(loaded).KnownOnIsland());
        Assert.Equal(Knowledge(unbroken), Knowledge(loaded));
    }

    // ── a village to watch ──

    private sealed record Watched(Engine Engine, FolkModule Folk, LivingModule Living, KnowingModule Knowing, Recorder Events);

    private static Watched Village(DataSet world, ulong seed)
    {
        var events = new Recorder();
        var engine = new Engine([.. LifeVillage.Modules(), events], world, World.Map, seed, new SimulatedTime(Spring));
        engine.Start();
        return new Watched(engine, engine.Running.OfType<FolkModule>().Single(), engine.Running.OfType<LivingModule>().Single(),
            engine.Running.OfType<KnowingModule>().Single(), events);
    }

    private static ulong SeedWith(DataSet world, Func<FolkModule, bool> fits)
    {
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var folk = new FolkModule();
            new Engine([folk], world, World.Map, seed, new SimulatedTime(Spring)).Start();
            if (fits(folk)) return seed;
        }
        throw new InvalidOperationException("No seed up to 200 fits.");
    }

    private static FolkModule FolkOf(Engine engine) => engine.Running.OfType<FolkModule>().Single();

    private static KnowingModule KnowingOf(Engine engine) => engine.Running.OfType<KnowingModule>().Single();

    // every item anyone could hold here: start processes, uses and recipes, every use, and what the island knows now
    private static SortedSet<string> Items(Engine engine)
    {
        var laws = World.Laws;
        var items = new SortedSet<string>(StringComparer.Ordinal);
        items.UnionWith(laws.Start.Processes);
        items.UnionWith(laws.Start.Recipes.Select(r => r.Id));
        items.UnionWith(laws.Uses.Select(u => u.Id));
        items.UnionWith(KnowingOf(engine).KnownOnIsland());
        return items;
    }

    // for every living person and every item: whether they know it, and at what level
    private static List<(int Person, string Item, bool Knows, int Level)> Knowledge(Engine engine) =>
        [.. FolkOf(engine).Living.SelectMany(p => Items(engine).Select(item =>
            (p.Id, item, KnowingOf(engine).Knows(p.Id, item), KnowingOf(engine).Level(p.Id, item))))];

    private static double Age(FolkModule folk, Person p) => folk.AgeOf(p, 0);

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
