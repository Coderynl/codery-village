using System.Text.Json;
using Codery.Village.Core;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;
using Codery.Village.Laws;
using Codery.Village.Life.Chronicle;
using Codery.Village.Life.Folk;
using ChronicleModule = Codery.Village.Life.Chronicle.Chronicle;
using FeelingsModule = Codery.Village.Life.Feelings.Feelings;
using FolkModule = Codery.Village.Life.Folk.Folk;
using InventingModule = Codery.Village.Life.Inventing.Inventing;

namespace Codery.Village.Life.Tests;

/// <summary>
/// The chronicle through the engine on the real world. Risks: a chronicle that differs between two runs of one seed, a
/// discovery told without who, how, from what or what for, a wish without its cause, someone missing from the cast, or a
/// chronicle that forgets itself on a save, would each leave Michael unable to judge, or to reproduce, whether the village lives.
/// </summary>
public class ChronicleTests
{
    private static readonly DateTimeOffset Spring = new(2027, 3, 1, 0, 0, 0, TimeSpan.FromHours(1));

    // one run of seed 3 for six life years, shared by the tests that only read it
    private static readonly Lazy<Engine> SixYears = new(() =>
    {
        var engine = LifeVillage.Start(3, World.Data, World.Map, Spring);
        Run(engine, 6 * TicksPerYear);
        return engine;
    });

    [Fact]
    public void Same_seed_writes_the_same_chronicle()
    {
        var again = LifeVillage.Start(3, World.Data, World.Map, Spring);
        Run(again, 6 * TicksPerYear);

        var first = Write(SixYears.Value, "nl");
        Assert.Contains("## Jaar 6 (", first);
        Assert.Equal(first, Write(again, "nl"));
    }

    [Fact]
    public void Year_headings_do_not_overlap_when_the_clocks_change()
    {
        // the clocks go forward on 28 March 2027, so the fifth life year begins at 01:00 on the 29th
        var chronicle = Write(SixYears.Value, "en");

        Assert.Contains("## Year 4 (22–28 March", chronicle);
        Assert.Contains("## Year 5 (29 March", chronicle);
    }

    [Fact]
    public void A_discovery_line_says_who_how_and_what_for()
    {
        var engine = SixYears.Value;
        var inventing = Module<InventingModule>(engine);
        var folk = Module<FolkModule>(engine);
        var found = inventing.Discoveries.FirstOrDefault();
        Assert.NotNull(found);
        var entry = Module<ChronicleModule>(engine).Entries.Single(e => e.Kind == "discovered" && e.Tick == found.Tick && e.People[0] == found.By);

        var world = WorldData.From(World.Data);
        var how = found.How == "trying" ? "by trying" : "by accident";
        Assert.Contains(folk.Get(found.By)!.Name, entry.En);
        Assert.Contains(how, entry.En);
        foreach (var input in inventing.Recipes[found.Recipe].In) Assert.Contains(inventing.NameOf(input.Substance, "en"), entry.En);
        Assert.Contains(ProcessName(inventing.Recipes[found.Recipe].Process, "nl"), entry.Nl);
        Assert.Contains(world.Uses.Single(u => u.Id == found.Use).En, entry.En);
        Assert.Contains(world.Uses.Single(u => u.Id == found.Use).Nl, entry.Nl);
        Assert.DoesNotContain(found.Substance, entry.Nl);
    }

    [Fact]
    public void A_wish_line_names_its_cause()
    {
        var wishes = Module<ChronicleModule>(SixYears.Value).Entries.Where(e => e.Kind == "wish").ToList();
        Assert.NotEmpty(wishes);

        var causes = Because("nl");
        Assert.All(wishes, w => Assert.True(causes.Any(w.Nl.Contains), $"no cause in: {w.Nl}"));
        Assert.All(wishes, w => Assert.True(w.People.Count > 0));
    }

    [Fact]
    public void The_cast_lists_everyone_who_lived()
    {
        var engine = SixYears.Value;
        var folk = Module<FolkModule>(engine);
        var everyone = Everyone(folk);
        Assert.Contains(everyone, p => p.BornTick >= 0);

        var markdown = Write(engine, "en");
        var cast = markdown[markdown.IndexOf("\n## Who lived", StringComparison.Ordinal)..];
        cast = cast[..cast.IndexOf("\n## ", 5, StringComparison.Ordinal)];
        var lines = cast.Split('\n').Where(l => l.StartsWith("- **", StringComparison.Ordinal)).ToList();

        Assert.Equal(everyone.Count, lines.Count);
        foreach (var p in everyone) Assert.Contains(lines, l => l.StartsWith($"- **{p.Name}**", StringComparison.Ordinal));
    }

    [Fact]
    public void The_chronicle_comes_back_the_same_after_saving()
    {
        var first = LifeVillage.Start(3, World.Data, World.Map, Spring);
        Run(first, 5 * TicksPerYear);
        Assert.NotEmpty(Module<ChronicleModule>(first).Entries);
        var saved = SaveJson.FromJson(SaveJson.ToJson(first.Save()));
        var loaded = Engine.Load(saved, LifeVillage.Modules(), World.Data, World.Map, new SimulatedTime(saved.Calendar));
        Run(loaded, TicksPerYear);

        Assert.Empty(loaded.Stopped);
        Assert.Contains(loaded.Running, m => m is ChronicleModule);
        Assert.Equal(Write(SixYears.Value, "nl"), Write(loaded, "nl"));
    }

    [Fact]
    public void An_unchanged_wish_is_not_told_every_year()
    {
        // four adults come to want an ornament, which nobody on the island can make, so the wish stays as it is
        var engine = LifeVillage.Start(3, World.Data, World.Map, Spring);
        var folk = Module<FolkModule>(engine);
        var wanting = folk.Living.Where(p => folk.StageOf(p, 0) == LifeStage.Adult).Take(4).Select(p => p.Id).ToList();
        foreach (var id in wanting) Module<FeelingsModule>(engine).Want(id, "ornament", "habituation", null);
        Run(engine, 7 * TicksPerYear);

        var told = Module<ChronicleModule>(engine).Entries
            .Where(e => e.Kind.StartsWith("wish", StringComparison.Ordinal) && e.En.Contains("an ornament", StringComparison.Ordinal)).ToList();
        Assert.Equal([1, 6], told.Select(e => e.LifeYear));
        Assert.Equal(["wish", "wish-still"], told.Select(e => e.Kind));
        Assert.StartsWith("Nog steeds", told[1].Nl, StringComparison.Ordinal);
    }

    // ── helpers ──

    private static string Write(Engine engine, string language) =>
        Markdown.Write(Module<ChronicleModule>(engine), Module<FolkModule>(engine), Module<FeelingsModule>(engine), language);

    private static T Module<T>(Engine engine) => engine.Running.OfType<T>().Single();

    private static List<Person> Everyone(FolkModule folk)
    {
        var everyone = new List<Person>();
        for (var id = 0; folk.Get(id) is { } p; id++) everyone.Add(p);
        return everyone;
    }

    // the text of every cause in chronicle.json, up to its first placeholder
    private static IReadOnlyList<string> Because(string language)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "world", "chronicle.json")));
        return [.. json.RootElement.GetProperty("sentences").EnumerateObject()
            .Where(s => s.Name.StartsWith("because.", StringComparison.Ordinal))
            .Select(s => s.Value.GetProperty(language).GetString()!.Split('{')[0])];
    }

    // a process by its own name in processes.json: a use can share its id
    private static string ProcessName(string id, string language)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "world", "processes.json")));
        return json.RootElement.EnumerateArray().Single(p => p.GetProperty("id").GetString() == id).GetProperty(language).GetString()!;
    }

    private static long TicksPerYear => World.Data.Section<LifeSettings>("life").TicksPerLifeYear;

    private static void Run(Engine engine, long ticks)
    {
        for (long i = 0; i < ticks; i++) engine.Step();
    }
}
