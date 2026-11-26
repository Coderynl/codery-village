using System.Text.Json.Nodes;
using Codery.Village.Core;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Time;
using Codery.Village.Laws;
using Codery.Village.Life.Knowing;
using FeelingsModule = Codery.Village.Life.Feelings.Feelings;
using FolkModule = Codery.Village.Life.Folk.Folk;
using InventingModule = Codery.Village.Life.Inventing.Inventing;

namespace Codery.Village.Life.Tests;

/// <summary>
/// Guardrails over many seeds of 26 life years, the bounds design v6 puts in place of fixed ones. Risks: magic substances, an
/// island that dies in year one or never changes, discoveries far too rare or too many, happiness that converges, seeds
/// that all tell one history, and a village that can never fail — each would spoil the chronicle Michael judges.
/// </summary>
[Trait("Category", "Chronicle")]
public class GuardrailTests(GuardrailTests.Runs runs) : IClassFixture<GuardrailTests.Runs>
{
    [Fact]
    public void Every_substance_in_a_run_follows_from_the_laws()
    {
        foreach (var run in runs.All)
        {
            var inventing = run.Inventing;
            foreach (var (id, substance) in inventing.Found)
            {
                var recipe = inventing.Recipes.Values.First(r => r.Out == id);
                Assert.Equal(substance, Remake(run, recipe, 0));
            }
        }
    }

    // a found substance made again from raw ones (or the start's, or the island's own), every step by the laws
    private static Substance Remake(Run run, Recipe recipe, int depth)
    {
        Assert.True(depth < 20, $"{recipe.Id} goes round in circles");
        var inputs = recipe.In.Select(i =>
        {
            var input = run.Inventing.Found.ContainsKey(i.Substance)
                ? Remake(run, run.Inventing.Recipes.Values.First(r => r.Out == i.Substance), depth + 1)
                : run.Inventing.Substance(i.Substance) ?? throw new InvalidOperationException($"{i.Substance} comes from nowhere");
            return new Portion(input, i.Parts);
        }).ToList();
        return run.World.Nature.Apply(recipe.Process, inputs) ?? throw new InvalidOperationException($"{recipe.Id} makes nothing");
    }

    [Fact]
    public void The_island_neither_empties_early_nor_stands_still()
    {
        foreach (var run in runs.All)
        {
            Assert.True(run.Years.Take(5).All(y => y.People > 0), $"seed {run.Seed} emptied in its first five years");
            Assert.True(run.Years[^1].People != run.StartPeople || run.Born >= 3, $"seed {run.Seed} stood still");
        }
    }

    [Fact]
    public void Discoveries_come_at_a_living_pace()
    {
        foreach (var run in runs.All)
        {
            var found = run.Inventing.Discoveries.Count;
            Assert.InRange(found, 8, 60);
            Assert.Contains(run.Inventing.Discoveries, d => d.Tick < 5 * runs.TicksPerYear);
        }
    }

    [Fact]
    public void Happiness_spreads_and_moves()
    {
        foreach (var run in runs.All)
        {
            Assert.True(run.Years[^1].Spread >= 10, $"seed {run.Seed}: happiness ends within {run.Years[^1].Spread}");
            var moves = run.Years.Zip(run.Years.Skip(1), (a, b) => Math.Abs(b.Median - a.Median)).Max();
            Assert.True(moves >= 10, $"seed {run.Seed}: the median never moved by 10 in a year (at most {moves})");
        }
    }

    [Fact]
    public void Seeds_tell_different_histories()
    {
        var histories = runs.All.Select(run => string.Join(" ", run.Inventing.Discoveries.Take(5).Select(d => run.Inventing.Found[d.Substance].Kind)));
        Assert.True(histories.Distinct().Count() >= 4, string.Join(" | ", histories));
    }

    [Fact]
    [Trait("Category", "ChronicleLong")]
    public void Failure_is_possible()
    {
        var failed = Enumerable.Range(1, 30).AsParallel().Select(seed => Run.Of((ulong)seed, runs.TicksPerYear))
            .Count(run => run.Years[^1].People < run.StartPeople || run.Forgotten > 0);
        Assert.True(failed >= 1, "in thirty villages nothing was ever lost");
    }

    /// <summary>Six villages of 26 life years, run once for the class.</summary>
    public sealed class Runs
    {
        public Runs() => All = [.. Enumerable.Range(1, 6).AsParallel().AsOrdered().Select(seed => Run.Of((ulong)seed, TicksPerYear))];

        public long TicksPerYear => World.Data.Section<LifeSettings>("life").TicksPerLifeYear;

        public IReadOnlyList<Run> All { get; }
    }

    public sealed record Year(int People, int Median, int Spread);

    public sealed class Run
    {
        public required ulong Seed { get; init; }
        public required int StartPeople { get; init; }
        public required IReadOnlyList<Year> Years { get; init; }
        public required int Born { get; init; }
        public required int Forgotten { get; init; }
        public required InventingModule Inventing { get; init; }
        public required WorldData World { get; init; }

        public static Run Of(ulong seed, long ticksPerYear)
        {
            var modules = LifeVillage.Modules(seed).ToList();
            var events = new Counter();
            modules.Add(events);
            var engine = new Engine(modules, Tests.World.Data, Tests.World.Map, seed, new SimulatedTime(new DateTimeOffset(2027, 3, 1, 0, 0, 0, TimeSpan.FromHours(1))));
            engine.Start();
            var folk = modules.OfType<FolkModule>().Single();
            var feelings = modules.OfType<FeelingsModule>().Single();
            var start = folk.Living.Count;
            var years = new List<Year>();
            for (var year = 0; year < 26; year++)
            {
                for (long i = 0; i < ticksPerYear; i++) engine.Step();
                var happy = folk.Living.Select(p => feelings.Happiness(p.Id)).Order().ToList();
                int At(double q) => happy.Count == 0 ? 0 : happy[(int)Math.Floor(q * (happy.Count - 1))];
                years.Add(new Year(happy.Count, At(0.5), At(0.75) - At(0.25)));
            }
            Assert.Empty(engine.Stopped);
            return new Run
            {
                Seed = seed,
                StartPeople = start,
                Years = years,
                Born = events.Born,
                Forgotten = events.Forgotten,
                Inventing = modules.OfType<InventingModule>().Single(),
                World = WorldData.From(Tests.World.Data),
            };
        }
    }

    private sealed class Counter : IModule
    {
        public int Born;
        public int Forgotten;

        public string Name => "counter";
        public int Version => 1;

        public void Start(ModuleContext village) { }

        public void Step(ModuleContext village)
        {
            foreach (var e in village.Received)
            {
                if (e is Folk.Born) Born++;
                if (e is Forgotten) Forgotten++;
            }
        }

        public JsonNode Save() => new JsonObject();

        public void Load(ModuleContext village, JsonNode data, int version) { }
    }
}
