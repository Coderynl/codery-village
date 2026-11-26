using System.Text.Json.Nodes;
using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;
using Codery.Village.Laws;
using Codery.Village.Life.Inventing;
using FeelingsModule = Codery.Village.Life.Feelings.Feelings;
using FolkModule = Codery.Village.Life.Folk.Folk;
using InventingModule = Codery.Village.Life.Inventing.Inventing;

namespace Codery.Village.Life.Tests;

/// <summary>
/// Inventing through the engine. Risks: a village that never finds what it wants, a discovery the laws do not make (magic),
/// metal without a kiln, every seed finding the same things in the same order, or a need with no answer that nobody notices,
/// would each take away the surprise the design is for.
/// </summary>
public class InventingTests
{
    private static readonly DateTimeOffset Spring = new(2027, 3, 1, 0, 0, 0, TimeSpan.FromHours(1));

    [Fact]
    public void A_village_that_wants_pots_finds_fired_clay()
    {
        var found = Enumerable.Range(1, 10).AsParallel().Count(seed =>
        {
            var v = Village(World.Data, (ulong)seed);
            foreach (var p in v.Folk.Living) v.Feelings.Want(p.Id, "pot", "shortage", null);
            Run(v.Engine, 5.0);
            return v.Inventing.Discoveries.Any(d => d.Use == "pot" && v.Inventing.Found[d.Substance] is { Kind: "ceramic" } s
                && s.Profile[Property.Watertightness] >= 35);
        });

        Assert.True(found >= 7, $"{found} of 10 villages found fired clay");
    }

    [Fact]
    public void A_discovery_follows_from_the_laws()
    {
        var v = Village(World.Data, 1);
        Run(v.Engine, 10.0);

        Assert.NotEmpty(v.Inventing.Discoveries);
        foreach (var d in v.Inventing.Discoveries)
        {
            var recipe = v.Inventing.Recipes[d.Recipe];
            var inputs = recipe.In.Select(i => new Portion(v.Inventing.Substance(i.Substance)!, i.Parts)).ToList();
            Assert.Equal(v.Inventing.Found[d.Substance], v.World.Nature.Apply(recipe.Process, inputs));
        }
    }

    [Fact]
    public void Without_a_kiln_no_metal_is_found()
    {
        // no clay anywhere: nothing can become a kiln wall, so no kiln and no metal
        var world = WithSubstances(s =>
        {
            var deposits = s["deposits"]!.AsArray();
            foreach (var d in deposits.Where(d => (string)d!["substance"]! == "clay").ToList()) deposits.Remove(d);
            deposits.Add(new JsonObject { ["substance"] = "copper-ore", ["ground"] = "land", ["level"] = 2, ["chance"] = 1.0 });
        });
        var v = Village(world, 1);
        foreach (var p in v.Folk.Living) v.Feelings.Want(p.Id, "kettle", "shortage", null);
        Run(v.Engine, 26.0);

        Assert.DoesNotContain(v.Inventing.Found.Values, s => s.Kind is "metal" or "alloy");
        Assert.DoesNotContain("kiln", v.Living.Structures);
    }

    [Fact]
    public void Two_seeds_find_things_in_another_order()
    {
        var orders = new ulong[] { 1, 2 }.AsParallel().AsOrdered().Select(seed =>
        {
            var v = Village(World.Data, seed);
            Run(v.Engine, 26.0);
            return string.Join(" ", v.Inventing.Discoveries.Select(d => v.Inventing.Found[d.Substance].Kind));
        }).ToList();

        Assert.NotEqual("", orders[0]);
        Assert.NotEqual(orders[0], orders[1]);
    }

    [Fact]
    public void A_pressing_need_with_no_answer_hits_a_wall()
    {
        // an island of grain, stone and clay: nothing floats and holds water within one step
        var keep = new HashSet<string> { "grain", "berries", "stone", "clay", "sand", "flint", "chalk", "water", "peat" };
        var world = WithSubstances(s =>
        {
            var deposits = s["deposits"]!.AsArray();
            foreach (var d in deposits.Where(d => !keep.Contains((string)d!["substance"]!)).ToList()) deposits.Remove(d);
        }, own =>
        {
            own["min"]!["density"] = 60; // the island's own substance sinks too
        });
        var v = Village(world, 1);
        Run(v.Engine, 0.05);
        foreach (var p in v.Folk.Living) v.Feelings.Want(p.Id, "hull", "shortage", null);
        Run(v.Engine, 5.0);

        Assert.Single(v.Events.Of<HitAWall>(), w => w.Use == "hull");
    }

    [Fact]
    public void Inventing_comes_back_the_same_after_saving()
    {
        string Found(Engine engine)
        {
            var inventing = engine.Running.OfType<InventingModule>().Single();
            return string.Join("|", inventing.Discoveries.Select(d => $"{d.Tick} {d.By} {d.Substance} {d.How} {inventing.NameOf(d.Substance, "nl")}"));
        }

        var unbroken = LifeVillage.Start(4, World.Data, World.Map, Spring);
        Run(unbroken, 8.0);

        var first = LifeVillage.Start(4, World.Data, World.Map, Spring);
        Run(first, 5.0);
        Assert.NotEmpty(first.Running.OfType<InventingModule>().Single().Discoveries);
        var saved = SaveJson.FromJson(SaveJson.ToJson(first.Save()));
        var loaded = Engine.Load(saved, LifeVillage.Modules(), World.Data, World.Map, new SimulatedTime(saved.Calendar));
        Run(loaded, 3.0);

        Assert.Equal(Found(unbroken), Found(loaded));
    }

    // ── a village to watch ──

    private sealed record Watched(Engine Engine, FolkModule Folk, Living.Living Living, FeelingsModule Feelings, InventingModule Inventing,
        WorldData World, Recorder Events);

    private static Watched Village(DataSet world, ulong seed)
    {
        var modules = LifeVillage.Modules().ToList();
        var events = new Recorder();
        modules.Add(events);
        var engine = new Engine(modules, world, World.Map, seed, new SimulatedTime(Spring));
        engine.Start();
        return new Watched(engine, modules.OfType<FolkModule>().Single(), modules.OfType<Living.Living>().Single(),
            modules.OfType<FeelingsModule>().Single(), modules.OfType<InventingModule>().Single(), WorldData.From(world), events);
    }

    private static DataSet WithSubstances(Action<JsonNode> change, Action<JsonNode>? changeOwn = null)
    {
        JsonNode Read(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "world", name + ".json")))!;
        var substances = Read("substances");
        change(substances);
        var own = Read("own");
        changeOwn?.Invoke(own);
        return World.With(new Dictionary<string, string> { ["substances"] = substances.ToJsonString(), ["own"] = own.ToJsonString() });
    }

    private static long TicksPerYear => World.Data.Section<LifeSettings>("life").TicksPerLifeYear;

    private static void Run(Engine engine, double lifeYears)
    {
        var end = engine.Tick + (long)(lifeYears * TicksPerYear);
        while (engine.Tick < end) engine.Step();
        Assert.Empty(engine.Stopped);
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
