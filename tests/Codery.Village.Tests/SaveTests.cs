using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codery.Village.Core;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;

namespace Codery.Village.Tests;

public class SaveTests
{
    private const ulong Seed = 42;

    private static Engine Village(IReadOnlyList<IModule> modules, TimeProvider? time = null) =>
        new(modules, TestVillage.Data(), TestVillage.Map(), Seed, time ?? new SimulatedTime(TestVillage.Start));

    private static Engine Load(SavedVillage saved, IReadOnlyList<IModule> modules, TimeProvider? time = null) =>
        Engine.Load(saved, modules, TestVillage.Data(), TestVillage.Map(), time ?? new SimulatedTime(saved.Calendar));

    private static Engine Started(IReadOnlyList<IModule> modules, int ticks)
    {
        var engine = Village(modules);
        engine.Start();
        Run(engine, ticks);
        return engine;
    }

    private static void Run(Engine engine, int ticks)
    {
        for (var i = 0; i < ticks; i++) engine.Step();
    }

    // through JSON, the way a save goes to the store and back
    private static SavedVillage Written(SavedVillage saved) => SaveJson.FromJson(SaveJson.ToJson(saved));

    private static SavedModule Part(SavedVillage saved, string name) => Assert.Single(saved.Modules, part => part.Name == name);

    private static string Json(SavedModule part) => JsonSerializer.Serialize(part, SaveJson.Options);

    public static TheoryData<string> ModuleNames => [.. TestVillage.Modules().Select(module => module.Name)];

    [Theory]
    [MemberData(nameof(ModuleNames))]
    public void Every_module_round_trips(string name)
    {
        var original = Started(TestVillage.Modules(), 2000);
        var loaded = Load(Written(original.Save()), TestVillage.Modules());

        Run(original, 2000);
        Run(loaded, 2000);

        var expected = original.Save();
        var actual = loaded.Save();
        Assert.Equal(Json(Part(expected, name)), Json(Part(actual, name)));
        Assert.Equal(SaveJson.ToJson(expected), SaveJson.ToJson(actual));
    }

    [Fact]
    public void A_save_holds_the_village_as_it_stands()
    {
        var engine = Started([new DrawingModule("dice"), new PublishingModule("a", 3, "bell")], 3);

        var saved = engine.Save();

        Assert.Equal(SavedVillage.CurrentFormat, saved.Format);
        Assert.Equal(Seed, saved.Seed);
        Assert.Equal(3, saved.Tick);
        Assert.Equal(TestVillage.Start.AddSeconds(0.75), saved.Calendar);
        Assert.Equal(["dice", "a"], saved.Modules.Select(part => part.Name));
        var dice = Part(saved, "dice");
        Assert.Equal(1, dice.Version);
        var stream = Rng.For(Seed, "dice");
        for (var i = 0; i < 4; i++) stream.Next(1000);
        Assert.Equal(stream.State, dice.Random);
        var pending = Assert.Single(saved.Pending);
        Assert.Equal("Rang", pending.Type);
        Assert.Equal("""{"text":"bell","tick":3}""", pending.Data.ToJsonString());
    }

    [Fact]
    public void Events_on_their_way_to_the_next_tick_survive_a_save()
    {
        var listening = new ListeningModule("listening");
        var engine = Started([new PublishingModule("a", 5, "bell"), new ListeningModule("listening")], 5);

        var loaded = Load(Written(engine.Save()), [new PublishingModule("a", 5, "bell"), listening]);
        loaded.Step();

        Assert.Equal([new Rang(5, "bell")], listening.Heard[6]);
    }

    [Fact]
    public void An_unknown_event_is_dropped_and_logged()
    {
        var listening = new ListeningModule("listening");
        var saved = Started([new ListeningModule("listening")], 5).Save() with
        {
            Pending =
            [
                new SavedEvent("Fireworks", new JsonObject { ["tick"] = 5 }),
                new SavedEvent("Rang", new JsonObject { ["tick"] = 5, ["text"] = "bell" }),
                new SavedEvent("Rang", new JsonObject { ["tick"] = "five" }),
            ],
        };

        var loaded = Load(Written(saved), [listening]);
        loaded.Step();

        Assert.Equal([new Rang(5, "bell")], listening.Heard[6]);
        var dropped = loaded.Log.Where(entry => entry.Kind == "event-dropped").ToList();
        Assert.Equal(["Fireworks", "Rang"], dropped.Select(entry => entry.Subject));
        Assert.All(dropped, entry => Assert.Equal(5, entry.Tick));
    }

    [Fact]
    public void An_unknown_part_sleeps_and_survives()
    {
        var circus = new SavedModule("circus", 3, 77, new JsonObject { ["tents"] = 2, ["clowns"] = new JsonArray("Pim", "Pom") });
        var saved = Started([new CountingModule("counter")], 10).Save();
        saved = saved with { Modules = [.. saved.Modules, circus] };

        var loaded = Load(Written(saved), [new CountingModule("counter")]);
        Run(loaded, 10);

        var again = loaded.Save();
        Assert.Equal(20, (long)Part(again, "counter").Data["count"]!);
        Assert.Equal(Json(circus), Json(Part(again, "circus")));
        Assert.Contains(loaded.Log, entry => entry is { Tick: 10, Kind: "module-dormant", Subject: "circus" });
    }

    [Fact]
    public void A_failing_migration_sleeps_too()
    {
        var saved = Started([new MigratingModule("ledger"), new CountingModule("counter")], 10).Save();
        var old = Part(saved, "ledger") with { Version = 0 };
        saved = saved with { Modules = [old, Part(saved, "counter")] };
        var ledger = new MigratingModule("ledger");

        var loaded = Load(Written(saved), [ledger, new CountingModule("counter")]);
        Run(loaded, 10);

        Assert.Equal(0, ledger.Steps);
        Assert.DoesNotContain(ledger, loaded.Running);
        var line = Assert.Single(loaded.Log, entry => entry.Kind == "module-dormant");
        Assert.Equal("ledger", line.Subject);
        Assert.Contains("version 0", line.Detail);
        var again = loaded.Save();
        Assert.Equal(Json(old), Json(Part(again, "ledger")));
        Assert.Equal(["ledger", "counter"], again.Modules.Select(part => part.Name));
        Assert.Equal(20, (long)Part(again, "counter").Data["count"]!);
    }

    [Fact]
    public void A_new_module_starts_in_an_old_village()
    {
        var saved = Started([new CountingModule("counter")], 10).Save();
        var cards = new DrawingModule("cards");

        var loaded = Load(Written(saved), [new CountingModule("counter"), cards]);

        Assert.Equal([Rng.For(Seed, "cards").Next(1000)], cards.Drawn);
        loaded.Step();
        Assert.Equal(2, cards.Drawn.Count);
        Assert.DoesNotContain(loaded.Log, entry => entry.Kind == "module-dormant");
    }

    [Fact]
    public void Loading_after_a_day_moves_only_the_calendar()
    {
        var resident = new ResidentModule("resident");
        var engine = Started([resident, new CountingModule("counter")], 5000);
        var saved = engine.Save();
        var later = new ResidentModule("resident");

        var loaded = Load(Written(saved), [later, new CountingModule("counter")], new SimulatedTime(saved.Calendar.AddDays(1)));

        Assert.Equal(5000, loaded.Tick);
        Assert.Equal(saved.Calendar.AddDays(1), loaded.Save().Calendar);
        Assert.Equal(resident.Age, later.Age);
        Assert.Equal(5000L, (long)Part(loaded.Save(), "counter").Data["count"]!);
    }

    [Fact]
    public void The_log_comes_back_with_its_numbers_and_ticks()
    {
        var engine = Started([new LoggingModule("scribe")], 3000);
        engine.Forget();

        var loaded = Load(Written(engine.Save()), [new LoggingModule("scribe")]);

        Assert.Equal(600, loaded.LogStart);
        Assert.Equal(engine.Log, loaded.Log);
        loaded.Step();
        Assert.Equal((3000L, new LogEntry(3001, "noted", "scribe", "3001")), loaded.LogFrom(3000).Single());
    }

    [Fact]
    public void A_stopped_module_stays_stopped_after_loading()
    {
        var engine = Started([new FailingModule("wheel", 3), new CountingModule("counter")], 5);
        var saved = engine.Save();
        Assert.True(Part(saved, "wheel").Stopped);
        Assert.False(Part(saved, "counter").Stopped);
        var wheel = new FailingModule("wheel", 3);

        var loaded = Load(Written(saved), [wheel, new CountingModule("counter")]);
        Run(engine, 100);
        Run(loaded, 100);

        Assert.Equal(0, wheel.Steps);
        Assert.DoesNotContain(wheel, loaded.Running);
        Assert.Equal(SaveJson.ToJson(engine.Save()), SaveJson.ToJson(loaded.Save()));
    }

    [Fact]
    public void A_stopped_module_wakes_up_in_a_newer_version()
    {
        var saved = Started([new FailingModule("wheel", 3)], 5).Save();
        var wheel = new FailingModule("wheel", 3, version: 2);

        var loaded = Load(Written(saved), [wheel]);
        loaded.Step();

        Assert.Equal(3, wheel.Steps);
        Assert.Equal([wheel], loaded.Running);
        var part = Part(loaded.Save(), "wheel");
        Assert.False(part.Stopped);
        Assert.Equal(2, part.Version);
    }

    [Fact]
    public void A_module_that_never_saved_is_stopped_and_left_out()
    {
        var stuck = new UnsaveableModule("stuck");
        var engine = Started([stuck, new CountingModule("counter")], 3);

        var saved = engine.Save();

        Assert.Equal(["counter"], saved.Modules.Select(part => part.Name));
        Assert.DoesNotContain(stuck, engine.Running);
        Assert.Contains(engine.Log, entry => entry is { Kind: "module-stopped", Subject: "stuck" });
        engine.Step();
        Assert.Equal(3, stuck.Steps);
    }

    [Fact]
    public void A_module_that_stops_saving_keeps_its_last_good_part()
    {
        var flaky = new FlakyModule("flaky");
        var engine = Started([flaky, new CountingModule("counter")], 3);
        var good = engine.Save();
        Run(engine, 2);
        flaky.Broken = true;

        engine.Save();
        var saved = engine.Save();

        var part = Part(saved, "flaky");
        Assert.Equal(3, (int)part.Data["steps"]!);
        Assert.Equal(Json(Part(good, "flaky") with { Stopped = true }), Json(part));
        Assert.DoesNotContain(flaky, engine.Running);
        Assert.Single(engine.Log, entry => entry is { Kind: "module-not-saved", Subject: "flaky", Detail: "the drawer is stuck" });

        // the same version: the part stays as it was, and nothing starts afresh
        var same = new FlakyModule("flaky");
        var loaded = Load(Written(saved), [same, new CountingModule("counter")]);
        Assert.False(same.Started);
        Assert.Equal(Json(part), Json(Part(loaded.Save(), "flaky")));

        // a newer version takes up the last good part
        var newer = new FlakyModule("flaky", version: 2);
        Load(Written(saved), [newer, new CountingModule("counter")]).Step();
        Assert.False(newer.Started);
        Assert.Equal(4, newer.Steps);
    }

    [Fact]
    public void Every_part_is_loaded_before_a_new_module_starts()
    {
        var saved = Started([new GreeterModule("greeter"), new CountingModule("counter")], 10).Save();
        var newcomer = new GreeterModule("newcomer");

        Load(Written(saved), [newcomer, new GreeterModule("greeter"), new CountingModule("counter")]);

        Assert.Equal(10, newcomer.AtStart);
    }

    [Fact]
    public void A_part_does_not_find_a_new_module_that_has_not_started()
    {
        var saved = Started([new GreeterModule("greeter")], 10).Save();
        var greeter = new GreeterModule("greeter");

        Load(Written(saved), [greeter, new CountingModule("counter")]);

        Assert.Null(greeter.AtLoad);
    }

    [Fact]
    public void A_save_of_another_format_is_refused()
    {
        var saved = Started([new CountingModule("counter")], 1).Save() with { Format = SavedVillage.CurrentFormat + 1 };

        Assert.Throws<SaveException>(() => Load(saved, [new CountingModule("counter")]));
    }

    [Fact]
    public void A_save_that_is_not_one_is_refused()
    {
        Assert.Throws<SaveException>(() => SaveJson.FromJson("{ not json"));
        Assert.Throws<SaveException>(() => SaveJson.FromJson("""{ "format": 1 }"""));
    }

    [Fact]
    public void Two_event_types_cannot_share_a_name()
    {
        // the same test assembly a second time: its events have the same names as ours
        var twins = new AssemblyLoadContext("twins", isCollectible: true);
        try
        {
            var twin = twins.LoadFromAssemblyPath(typeof(TestVillage).Assembly.Location);
            var counter = (IModule)Activator.CreateInstance(twin.GetType(typeof(CountingModule).FullName!)!, "twin")!;

            var thrown = Assert.Throws<ArgumentException>(() => Village([new CountingModule("counter"), counter]));
            Assert.Contains("Chimed", thrown.Message);
        }
        finally
        {
            twins.Unload();
        }
    }
}
