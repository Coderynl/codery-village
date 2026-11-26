using System.Text.Json.Nodes;
using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;
using Codery.Village.Life.Feelings;
using FeelingsModule = Codery.Village.Life.Feelings.Feelings;
using FolkModule = Codery.Village.Life.Folk.Folk;
using LivingModule = Codery.Village.Life.Living.Living;

namespace Codery.Village.Life.Tests;

/// <summary>
/// Feelings through the engine on the real world. Risks: joy that does not weigh the wait, joy that never fades, a loss that
/// hurts no more than the gain pleased, jealousy that ignores who is jealous, or a wish without a cause, would each make the
/// village's happiness the flat "+1 per fulfilled wish" the design replaces, and the chronicle's wishes untraceable.
/// </summary>
public class FeelingsTests
{
    private static readonly DateTimeOffset Spring = new(2027, 3, 1, 0, 0, 0, TimeSpan.FromHours(1));

    [Fact]
    public void A_long_wait_gives_more_joy_than_a_short_one()
    {
        var joy = new[] { 3.0, 0.2 }.Select(years =>
        {
            var v = Village(1);
            var p = Adult(v);
            v.Feelings.Want(p.Id, "pot", "comparison", null);
            Run(v, years);
            v.Living.Give(p.Household, "pot", "stone");
            Run(v, Hours(2));
            return Reason(v, p.Id, "joy:pot");
        }).ToList();

        Assert.True(joy[1] > 0, "a short wait still pleases");
        Assert.True(joy[0] > 2 * joy[1], $"joy after three years {joy[0]:F1}, after a fifth of one {joy[1]:F1}");
    }

    [Fact]
    public void Joy_fades_until_the_new_is_normal()
    {
        var v = Village(1);
        var p = Adult(v);
        v.Feelings.Want(p.Id, "pot", "comparison", null);
        Run(v, 2.0);
        v.Living.Give(p.Household, "pot", "stone");
        Run(v, Hours(2));
        var fresh = Reason(v, p.Id, "joy:pot");
        Run(v, v.Feelings.Rules.ExpectedAfter / 2);
        var half = Reason(v, p.Id, "joy:pot");
        Run(v, v.Feelings.Rules.ExpectedAfter / 2 + 0.05);

        Assert.True(fresh > half && half > 0, $"joy {fresh:F1}, halfway {half:F1}");
        Assert.Equal(0, Reason(v, p.Id, "joy:pot"));
    }

    [Fact]
    public void Losing_the_ordinary_hurts_more_than_getting_it_gave()
    {
        var v = Village(1);
        var p = Adult(v);
        v.Feelings.Want(p.Id, "pot", "comparison", null);
        Run(v, 2.0);
        v.Living.Give(p.Household, "pot", "stone");
        Run(v, Hours(2));
        var joy = Reason(v, p.Id, "joy:pot");
        Run(v, v.Feelings.Rules.ExpectedAfter + 0.05);
        v.Living.Take(p.Household, "pot");
        Run(v, Hours(2));
        var pain = -Reason(v, p.Id, "loss:pot");

        Assert.True(pain > joy, $"the loss hurts {pain:F1}, the pot pleased {joy:F1}");
        Assert.True(pain >= 0.9 * v.Feelings.Rules.LossAversion * joy);
        Assert.Contains(v.Feelings.Wishes(p.Id), w => w.Use == "pot" && w.Cause == "loss");
    }

    [Fact]
    public void The_neighbour_s_pot_makes_the_jealous_want_one()
    {
        var high = new Tally();
        var low = new Tally();
        foreach (var seed in Enumerable.Range(1, 20).Select(s => (ulong)s))
        {
            var v = Village(seed);
            // the first household with a pot: everyone near it can compare
            var proud = v.Folk.Living.Where(p => v.Folk.StageOf(p, 0) == Folk.LifeStage.Adult).Min(p => p.Household);
            v.Living.Give(proud, "pot", "stone");
            Run(v, 1.0);
            var jealous = v.Events.Of<Wished>().Where(w => w.Cause == "comparison" && w.Use == "pot").Select(w => w.Person).ToHashSet();
            foreach (var p in v.Folk.Living.Where(p => p.Household != proud && v.Folk.StageOf(p, 0) != Folk.LifeStage.Child))
            {
                var tally = p.Traits.Sensitivity + p.Traits.Status >= 100 ? high : low;
                tally.People++;
                if (jealous.Contains(p.Id)) tally.Wishing++;
            }
        }

        Assert.True(high.People > 20 && low.People > 20, $"{high.People} high, {low.People} low");
        Assert.True(high.Share >= 2 * low.Share, $"high {high.Share:P0}, low {low.Share:P0}");
    }

    [Fact]
    public void Every_wish_has_a_cause()
    {
        var v = Village(2);
        Run(v, 10.0);

        var wished = v.Events.Of<Wished>().ToList();
        Assert.NotEmpty(wished);
        Assert.All(wished, w => Assert.Contains(w.Cause, Wish.Causes));
        Assert.Contains(wished, w => w.Cause != "loss");
    }

    [Fact]
    public void Feelings_come_back_the_same_after_saving()
    {
        var unbroken = LifeVillage.Start(3, World.Data, World.Map, Spring);
        Run(unbroken, 6 * TicksPerYear);

        var first = LifeVillage.Start(3, World.Data, World.Map, Spring);
        Run(first, 5 * TicksPerYear);
        Assert.Contains(Module<FolkModule>(first).Living, p => Module<FeelingsModule>(first).Wishes(p.Id).Count > 0);
        var saved = SaveJson.FromJson(SaveJson.ToJson(first.Save()));
        var loaded = Engine.Load(saved, LifeVillage.Modules(), World.Data, World.Map, new SimulatedTime(saved.Calendar));
        Run(loaded, TicksPerYear);

        Assert.Equal(Hearts(unbroken), Hearts(loaded));
    }

    private static T Module<T>(Engine engine) => engine.Running.OfType<T>().Single();

    private static string Hearts(Engine engine)
    {
        var feelings = Module<FeelingsModule>(engine);
        return string.Join("|", Module<FolkModule>(engine).Living.Select(p =>
            $"{p.Id} {feelings.Happiness(p.Id)} {string.Join(",", feelings.Wishes(p.Id).Select(w => $"{w.Use}/{w.Cause}/{w.Because}/{w.Weight:F3}"))} {feelings.Memory(p.Id).Count}"));
    }

    private static void Run(Engine engine, long ticks)
    {
        for (long i = 0; i < ticks; i++) engine.Step();
    }

    // ── a village to watch ──

    private sealed record Watched(Engine Engine, FolkModule Folk, LivingModule Living, FeelingsModule Feelings, Recorder Events);

    private sealed class Tally
    {
        public int People;
        public int Wishing;
        public double Share => People == 0 ? 0 : (double)Wishing / People;
    }

    private static Watched Village(ulong seed)
    {
        var modules = LifeVillage.Modules().ToList();
        var events = new Recorder();
        modules.Add(events);
        var engine = new Engine(modules, World.Data, World.Map, seed, new SimulatedTime(Spring));
        engine.Start();
        return new Watched(engine, modules.OfType<FolkModule>().Single(), modules.OfType<LivingModule>().Single(),
            modules.OfType<FeelingsModule>().Single(), events);
    }

    private static Folk.Person Adult(Watched v) =>
        v.Folk.Living.Where(p => v.Folk.StageOf(p, v.Engine.Tick) == Folk.LifeStage.Adult).OrderBy(p => v.Folk.AgeOf(p, v.Engine.Tick)).First();

    private static double Reason(Watched v, int person, string reason) =>
        v.Feelings.Reasons(person).Where(r => r.Reason == reason).Sum(r => r.Points);

    private static long TicksPerYear => World.Data.Section<LifeSettings>("life").TicksPerLifeYear;

    private static double Hours(int hours) => hours * (double)Hourly.TicksPerHour / TicksPerYear;

    private static void Run(Watched v, double lifeYears)
    {
        var end = v.Engine.Tick + (long)(lifeYears * TicksPerYear);
        while (v.Engine.Tick < end) v.Engine.Step();
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
