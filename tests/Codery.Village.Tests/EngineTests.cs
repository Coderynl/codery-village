using System.Text.Json.Nodes;
using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Time;

namespace Codery.Village.Tests;

public class EngineTests
{
    private static Engine Village(IReadOnlyList<IModule> modules, ulong seed = 42, TimeProvider? time = null) =>
        new(modules, TestVillage.Data(), TestVillage.Map(), seed, time ?? new SimulatedTime(TestVillage.Start));

    private static void Run(Engine engine, int ticks)
    {
        for (var i = 0; i < ticks; i++) engine.Step();
    }

    [Fact]
    public void Modules_step_in_order()
    {
        var trace = new List<string>();
        var engine = Village([new RecordingModule("a", trace), new RecordingModule("b", trace)]);

        engine.Start();
        Run(engine, 2);

        Assert.Equal(["a", "b", "a", "b"], trace);
    }

    [Fact]
    public void Events_arrive_the_next_tick()
    {
        var a = new PublishingModule("a", 1, "bell");
        var b = new ListeningModule("b");
        var engine = Village([a, b]);

        engine.Start();
        Run(engine, 3);

        Assert.Empty(b.Heard[1]);
        Assert.Equal([new Rang(1, "bell")], b.Heard[2]);
        Assert.Empty(b.Heard[3]);
    }

    [Fact]
    public void Events_reach_every_module_in_the_order_they_were_published()
    {
        var before = new ListeningModule("before");
        var after = new ListeningModule("after");
        var engine = Village([before, new PublishingModule("a", 1, "one", "two"), new PublishingModule("b", 1, "three"), after]);

        engine.Start();
        Run(engine, 2);

        VillageEvent[] expected = [new Rang(1, "one"), new Rang(1, "two"), new Rang(1, "three")];
        Assert.Equal(expected, before.Heard[2]);
        Assert.Equal(expected, after.Heard[2]);
    }

    [Fact]
    public void A_failing_module_is_stopped_and_the_village_goes_on()
    {
        var trace = new List<string>();
        var a = new RecordingModule("a", trace);
        var b = new FailingModule("b", 3);
        var engine = Village([a, b]);

        engine.Start();
        Run(engine, 5);

        Assert.Equal([1L, 2, 3, 4, 5], a.Ticks);
        Assert.Equal(2, b.Steps);
        Assert.Equal([a], engine.Running);
        var stopped = Assert.Single(engine.Stopped);
        Assert.Equal("b", stopped.Module);
        Assert.Contains("the wheel came off", stopped.Error);
        var line = Assert.Single(engine.Log, entry => entry.Kind == "module-stopped");
        Assert.Equal(new LogEntry(3, "module-stopped", "b", stopped.Error), line);
    }

    [Fact]
    public void A_module_that_fails_to_start_is_stopped_too()
    {
        var trace = new List<string>();
        var broken = new BrokenAtStartModule("broken");
        var engine = Village([broken, new RecordingModule("a", trace)]);

        engine.Start();
        Run(engine, 2);

        Assert.False(broken.Stepped);
        Assert.Equal(["a", "a"], trace);
        Assert.Equal("broken", Assert.Single(engine.Stopped).Module);
        Assert.Contains(engine.Log, entry => entry is { Tick: 0, Kind: "module-stopped", Subject: "broken" });
    }

    [Fact]
    public void Same_seed_same_village()
    {
        var first = new DrawingModule("dice");
        var second = new DrawingModule("dice");
        var other = new DrawingModule("dice");
        var engines = new[]
        {
            Village([first], seed: 7, time: new SimulatedTime(TestVillage.Start)),
            Village([second], seed: 7, time: new SimulatedTime(TestVillage.Start)),
            Village([other], seed: 8, time: new SimulatedTime(TestVillage.Start)),
        };

        foreach (var engine in engines)
        {
            engine.Start();
            Run(engine, 1000);
        }

        Assert.Equal(1001, first.Drawn.Count);
        Assert.Equal(first.Save().ToJsonString(), second.Save().ToJsonString());
        Assert.NotEqual(first.Save().ToJsonString(), other.Save().ToJsonString());
    }

    [Fact]
    public void Each_module_draws_from_its_own_stream()
    {
        var alone = new DrawingModule("dice");
        var accompanied = new DrawingModule("dice");
        var neighbour = new DrawingModule("cards");
        var one = Village([alone]);
        var two = Village([neighbour, accompanied]);

        one.Start();
        two.Start();
        Run(one, 50);
        Run(two, 50);

        Assert.Equal(alone.Drawn, accompanied.Drawn);
        Assert.NotEqual(alone.Drawn, neighbour.Drawn);
        var expected = Rng.For(42, "dice");
        Assert.Equal(expected.Next(1000), alone.Drawn[0]);
    }

    [Fact]
    public void Two_modules_cannot_share_a_name()
    {
        Assert.Throws<ArgumentException>(() => Village([new CountingModule("same"), new DrawingModule("same")]));
    }

    [Fact]
    public void A_module_finds_another_running_module()
    {
        var counter = new CountingModule("counter");
        var asking = new AskingModule("asking");
        var engine = Village([asking, counter]);

        engine.Start();
        engine.Step();

        Assert.Same(counter, asking.Found);
        Assert.Null(asking.Missing);
        Assert.IsType<InvalidOperationException>(asking.Thrown);
    }

    [Fact]
    public void A_stopped_module_is_not_found()
    {
        var asking = new AskingModule("asking");
        var engine = Village([new BrokenAtStartModule("broken"), asking]);

        engine.Start();
        engine.Step();

        Assert.Null(asking.Missing);
        Assert.Null(engine.Running.OfType<BrokenAtStartModule>().FirstOrDefault());
    }

    [Fact]
    public void A_step_moves_the_tick_and_a_simulated_clock_a_quarter_second()
    {
        var time = new SimulatedTime(TestVillage.Start);
        var engine = Village([new CountingModule("counter")], time: time);

        engine.Start();
        Assert.Equal(0, engine.Tick);
        Run(engine, 4);

        Assert.Equal(4, engine.Tick);
        Assert.Equal(TestVillage.Start.AddSeconds(1), time.GetUtcNow());
    }

    [Fact]
    public void The_life_clock_runs_at_the_rate_in_the_data()
    {
        var data = new DataSet(new Dictionary<string, string> { ["life"] = """{ "ticksPerLifeYear": 100 }""" });
        var life = new LifeModule();
        var engine = new Engine([life], data, TestVillage.Map(), 1, new SimulatedTime(TestVillage.Start));

        engine.Start();
        Run(engine, 50);

        Assert.Equal(0.5, life.Years);
    }

    [Fact]
    public void The_data_gives_a_section_as_a_record()
    {
        var data = TestVillage.Data();

        Assert.Equal(new LifeSettings(2_419_200), data.Section<LifeSettings>("life"));
        Assert.Throws<KeyNotFoundException>(() => data.Section<LifeSettings>("circus"));
    }

    [Fact]
    public void The_log_numbers_its_lines_and_forgets_old_ones()
    {
        var engine = Village([new LoggingModule("scribe")]);

        engine.Start();
        Run(engine, 3000);

        Assert.Equal(0, engine.LogStart);
        Assert.Equal(3000, engine.LogEnd);
        var (sequence, entry) = engine.LogFrom(2998).First();
        Assert.Equal(2998, sequence);
        Assert.Equal(new LogEntry(2999, "noted", "scribe", "2999"), entry);

        engine.Forget();

        // lines from the last 2400 ticks stay: ticks 601..3000 are lines 600..2999
        Assert.Equal(600, engine.LogStart);
        Assert.Equal(3000, engine.LogEnd);
        Assert.Equal(2400, engine.Log.Count);
        Assert.Equal(600, engine.LogFrom(0).First().Sequence);
        Assert.Equal(601, engine.Log[0].Tick);
    }

    [Fact]
    public void Forget_keeps_the_last_hundred_lines_however_old()
    {
        var engine = Village([new LoggingModule("scribe", until: 150)]);

        engine.Start();
        Run(engine, 3000);
        engine.Forget();

        Assert.Equal(50, engine.LogStart);
        Assert.Equal(100, engine.Log.Count);
        Assert.Equal(51, engine.Log[0].Tick);
    }

    [Fact]
    public void The_engine_stamps_its_own_tick_on_every_line()
    {
        var engine = Village([new WrongTickModule()]);

        engine.Start();
        Run(engine, 3000);

        Assert.Equal(Enumerable.Range(1, 3000).Select(tick => (long)tick), engine.Log.Select(entry => entry.Tick));

        engine.Forget();

        Assert.Equal(600, engine.LogStart);
        Assert.Equal(601, engine.Log[0].Tick);
    }

    /// <summary>Logs with a tick of its own: far ahead on odd ticks, 0 on even ones.</summary>
    private sealed class WrongTickModule : IModule
    {
        public string Name => "wrong";
        public int Version => 1;
        public void Start(ModuleContext village) { }
        public void Step(ModuleContext village) =>
            village.Log(new LogEntry(village.Clocks.Tick % 2 == 1 ? village.Clocks.Tick + 10_000 : 0, "noted", Name));
        public JsonNode Save() => new JsonObject();
        public void Load(ModuleContext village, JsonNode data, int version) { }
    }

    private sealed class LifeModule : IModule
    {
        public double Years { get; private set; }
        public string Name => "life";
        public int Version => 1;
        public void Start(ModuleContext village) { }
        public void Step(ModuleContext village) => Years = village.Clocks.Life.YearsAt(village.Clocks.Tick);
        public JsonNode Save() => new JsonObject();
        public void Load(ModuleContext village, JsonNode data, int version) { }
    }
}
