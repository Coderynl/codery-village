using System.Text.Json.Nodes;
using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;
using Codery.Village.Life.Folk;
using FolkModule = Codery.Village.Life.Folk.Folk;

namespace Codery.Village.Life.Tests;

/// <summary>
/// Folk through the engine, on the real world data. Risks: children without a pair or unlike their parents make the
/// chronicle's families false; the old who never die, or a village that empties or explodes within a generation, make
/// every later module run on a village that cannot be; a save that does not give the same people back breaks the server.
/// </summary>
public class FolkTests
{
    private static readonly DateTimeOffset Spring = new(2027, 3, 1, 0, 0, 0, TimeSpan.FromHours(1));

    private static long TicksPerYear => World.Data.Section<LifeSettings>("life").TicksPerLifeYear;

    [Fact]
    public void Children_are_born_to_pairs_and_take_after_their_parents()
    {
        var events = new Recorder();
        var engine = new Engine([.. LifeVillage.Modules(), events], World.Data, World.Map, 1, new SimulatedTime(Spring));
        engine.Start();
        var folk = FolkOf(engine);
        var pairs = folk.Living.Where(p => p.Partner is not null).Select(p => Pair(p.Id, p.Partner!.Value)).ToHashSet();

        Run(engine, 10 * TicksPerYear);

        pairs.UnionWith(events.Of<Paired>().Select(e => Pair(e.A, e.B)));
        var born = events.Of<Born>().ToList();
        Assert.NotEmpty(born);
        foreach (var birth in born)
        {
            Assert.Contains(Pair(birth.Mother, birth.Father), pairs);
            var child = folk.Get(birth.Person)!.Traits.Values;
            var mother = folk.Get(birth.Mother)!.Traits.Values;
            var father = folk.Get(birth.Father)!.Traits.Values;
            for (var i = 0; i < child.Count; i++)
                Assert.InRange(child[i], (mother[i] + father[i]) / 2.0 - 15, (mother[i] + father[i]) / 2.0 + 15);
        }
    }

    [Fact]
    public void The_old_die_and_are_gone_from_the_living()
    {
        var events = new Recorder();
        var engine = new Engine([.. LifeVillage.Modules(), events], OldWorld(), World.Map, 1, new SimulatedTime(Spring));
        engine.Start();
        var folk = FolkOf(engine);
        var old = folk.Living.Select(p => p.Id).ToList();
        Assert.All(old, id => Assert.InRange(folk.AgeOf(folk.Get(id)!, engine.Tick), 95, 96));

        Run(engine, 5 * TicksPerYear);

        var died = events.Of<Died>().ToList();
        Assert.All(old, id => Assert.Contains(died, e => e.Person == id && e.Cause == "age"));
        Assert.Empty(folk.Living);
    }

    [Fact]
    public void A_village_lives_on_over_a_generation()
    {
        Parallel.For(1, 6, seed =>
        {
            var engine = LifeVillage.Start((ulong)seed, World.Data, World.Map, Spring);
            var folk = FolkOf(engine);
            var founders = folk.Living.Max(p => p.Id);

            Run(engine, 26 * TicksPerYear);

            Assert.InRange(folk.Living.Count, 6, 80);
            Assert.Contains(folk.Living, p => p.Id > founders && folk.StageOf(p, engine.Tick) is LifeStage.Adult or LifeStage.Old);
        });
    }

    [Fact]
    public void Folk_comes_back_the_same_after_saving()
    {
        var unbroken = LifeVillage.Start(1, World.Data, World.Map, Spring);
        Run(unbroken, 10 * TicksPerYear);

        var first = LifeVillage.Start(1, World.Data, World.Map, Spring);
        Run(first, 5 * TicksPerYear);
        var saved = SaveJson.FromJson(SaveJson.ToJson(first.Save()));
        var loaded = Engine.Load(saved, LifeVillage.Modules(), World.Data, World.Map, new SimulatedTime(saved.Calendar));
        Run(loaded, 5 * TicksPerYear);

        Assert.Equal(Snapshot(FolkOf(unbroken)), Snapshot(FolkOf(loaded)));
    }

    [Fact]
    public void The_dead_come_back_the_same_after_saving()
    {
        var unbroken = LifeVillage.Start(1, OldWorld(), World.Map, Spring);
        Run(unbroken, 2 * TicksPerYear);

        var first = LifeVillage.Start(1, OldWorld(), World.Map, Spring);
        var founders = FolkOf(first).Living.Count;
        Run(first, TicksPerYear);
        Assert.InRange(FolkOf(first).Living.Count, 1, founders - 1);
        var saved = SaveJson.FromJson(SaveJson.ToJson(first.Save()));
        var loaded = Engine.Load(saved, LifeVillage.Modules(), OldWorld(), World.Map, new SimulatedTime(saved.Calendar));
        Run(loaded, TicksPerYear);

        Assert.Equal(Snapshot(FolkOf(unbroken)), Snapshot(FolkOf(loaded)));
        Assert.Equal(Everyone(FolkOf(unbroken)), Everyone(FolkOf(loaded)));
    }

    // the world with everyone at the start 95
    private static DataSet OldWorld()
    {
        var people = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "world", "people.json")))!;
        people["start"]!["pairAges"] = JsonNode.Parse("""[{ "from": 95, "to": 95, "weight": 1 }]""");
        return World.With(new Dictionary<string, string> { ["people"] = people.ToJsonString() });
    }

    private static List<(int Id, long? DiedTick, int Health, int Household)> Everyone(FolkModule folk)
    {
        var everyone = new List<(int, long?, int, int)>();
        for (var id = 0; folk.Get(id) is { } p; id++) everyone.Add((p.Id, p.DiedTick, p.Health, p.Household));
        return everyone;
    }

    private static FolkModule FolkOf(Engine engine) => engine.Running.OfType<FolkModule>().Single();

    private static (int, int) Pair(int a, int b) => (Math.Min(a, b), Math.Max(a, b));

    private static List<(int Id, string Name, int? Partner)> Snapshot(FolkModule folk) => [.. folk.Living.Select(p => (p.Id, p.Name, p.Partner))];

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
