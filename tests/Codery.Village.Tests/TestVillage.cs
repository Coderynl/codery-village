using System.Text.Json.Nodes;
using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Modules;

namespace Codery.Village.Tests;

/// <summary>
/// A small village for the tests: a map, the data the engine needs, and the modules that every module test
/// runs over. <see cref="Modules"/> grows with each task until <c>VillageSetup.Modules</c> takes its place.
/// </summary>
public static class TestVillage
{
    public static readonly DateTimeOffset Start = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A fresh list of fresh modules on every call: modules hold state.</summary>
    public static IReadOnlyList<IModule> Modules() =>
        [new DrawingModule("dice"), new CountingModule("counter"), new ChimingModule("bell"), new HearingModule("ear"), new LoggingModule("scribe")];

    public static IslandMap Map() => MapText.Parse("""
        ~~~~~
        ~...~
        ~~~~~
        island 1 1 Proef
        """);

    public static DataSet Data() => new(new Dictionary<string, string>
    {
        ["life"] = """{ "ticksPerLifeYear": 2419200 }""",
    });
}

/// <summary>Writes its name to a shared list on every tick.</summary>
public sealed class RecordingModule(string name, List<string> trace) : IModule
{
    public string Name => name;
    public int Version => 1;
    public List<long> Ticks { get; } = [];
    public void Start(ModuleContext village) { }
    public void Step(ModuleContext village)
    {
        trace.Add(name);
        Ticks.Add(village.Clocks.Tick);
    }
    public JsonNode Save() => new JsonObject();
    public void Load(ModuleContext village, JsonNode data, int version) { }
}

public sealed record Rang(long Tick, string Text) : VillageEvent(Tick);

/// <summary>Publishes the given texts on the given tick; on tick 0 that is in <see cref="Start"/>.</summary>
public sealed class PublishingModule(string name, long onTick, params string[] texts) : IModule
{
    public string Name => name;
    public int Version => 1;
    public void Start(ModuleContext village) => Step(village);
    public void Step(ModuleContext village)
    {
        if (village.Clocks.Tick != onTick) return;
        foreach (var text in texts) village.Publish(new Rang(village.Clocks.Tick, text));
    }
    public JsonNode Save() => new JsonObject();
    public void Load(ModuleContext village, JsonNode data, int version) { }
}

/// <summary>Remembers what it received on each tick.</summary>
public sealed class ListeningModule(string name) : IModule
{
    public string Name => name;
    public int Version => 1;
    public Dictionary<long, List<VillageEvent>> Heard { get; } = [];
    public void Start(ModuleContext village) { }
    public void Step(ModuleContext village) => Heard[village.Clocks.Tick] = [.. village.Received];
    public JsonNode Save() => new JsonObject();
    public void Load(ModuleContext village, JsonNode data, int version) { }
}

/// <summary>Throws on one tick; counts the ticks it did.</summary>
public sealed class FailingModule(string name, long failOnTick) : IModule
{
    public string Name => name;
    public int Version => 1;
    public int Steps { get; private set; }
    public void Start(ModuleContext village) { }
    public void Step(ModuleContext village)
    {
        if (village.Clocks.Tick == failOnTick) throw new InvalidOperationException("the wheel came off");
        Steps++;
    }
    public JsonNode Save() => new JsonObject { ["steps"] = Steps };
    public void Load(ModuleContext village, JsonNode data, int version) => Steps = (int)data["steps"]!;
}

/// <summary>Throws as soon as the village starts.</summary>
public sealed class BrokenAtStartModule(string name) : IModule
{
    public string Name => name;
    public int Version => 1;
    public bool Stepped { get; private set; }
    public void Start(ModuleContext village) => throw new InvalidOperationException("no foundation");
    public void Step(ModuleContext village) => Stepped = true;
    public JsonNode Save() => new JsonObject();
    public void Load(ModuleContext village, JsonNode data, int version) { }
}

/// <summary>Draws a number on every tick and keeps them all, so two villages can be compared.</summary>
public sealed class DrawingModule(string name) : IModule
{
    private List<int> _drawn = [];
    public string Name => name;
    public int Version => 1;
    public IReadOnlyList<int> Drawn => _drawn;
    public void Start(ModuleContext village) => _drawn = [village.Random.Next(1000)];
    public void Step(ModuleContext village) => _drawn.Add(village.Random.Next(1000));
    public JsonNode Save() => new JsonArray([.. _drawn.Select(n => JsonValue.Create(n))]);
    public void Load(ModuleContext village, JsonNode data, int version) =>
        _drawn = [.. data.AsArray().Select(n => (int)n!)];
}

/// <summary>Counts ticks and the life years they make.</summary>
public sealed class CountingModule(string name) : IModule
{
    public string Name => name;
    public int Version => 1;
    public long Count { get; private set; }
    public void Start(ModuleContext village) => Count = 0;
    public void Step(ModuleContext village) => Count++;
    public JsonNode Save() => new JsonObject { ["count"] = Count };
    public void Load(ModuleContext village, JsonNode data, int version) => Count = (long)data["count"]!;
}

/// <summary>Writes a log line on every tick, up to and including <paramref name="until"/>.</summary>
public sealed class LoggingModule(string name, long until = long.MaxValue) : IModule
{
    public string Name => name;
    public int Version => 1;
    public void Start(ModuleContext village) { }
    public void Step(ModuleContext village)
    {
        if (village.Clocks.Tick <= until)
            village.Log(new LogEntry(village.Clocks.Tick, "noted", name, village.Clocks.Tick.ToString()));
    }
    public JsonNode Save() => new JsonObject();
    public void Load(ModuleContext village, JsonNode data, int version) { }
}

/// <summary>Looks up another module through the context, the way later modules reach each other.</summary>
public sealed class AskingModule(string name) : IModule
{
    public string Name => name;
    public int Version => 1;
    public CountingModule? Found { get; private set; }
    public RecordingModule? Missing { get; private set; }
    public Exception? Thrown { get; private set; }
    public void Start(ModuleContext village) { }
    public void Step(ModuleContext village)
    {
        Found = village.Module<CountingModule>();
        Missing = village.Find<RecordingModule>();
        Thrown = Record.Exception(() => village.Module<RecordingModule>());
    }
    public JsonNode Save() => new JsonObject();
    public void Load(ModuleContext village, JsonNode data, int version) { }
}

/// <summary>Notes on every tick whether it finds a running <typeparamref name="T"/>.</summary>
public sealed class LookingModule<T>(string name) : IModule where T : class
{
    public string Name => name;
    public int Version => 1;
    public Dictionary<long, T?> Found { get; } = [];
    public void Start(ModuleContext village) { }
    public void Step(ModuleContext village) => Found[village.Clocks.Tick] = village.Find<T>();
    public JsonNode Save() => new JsonObject();
    public void Load(ModuleContext village, JsonNode data, int version) { }
}

/// <summary>Tries to empty what it received, as a module should not be able to.</summary>
public sealed class GreedyModule(string name) : IModule
{
    public string Name => name;
    public int Version => 1;
    public void Start(ModuleContext village) { }
    public void Step(ModuleContext village)
    {
        if (village.Received is ICollection<VillageEvent> { IsReadOnly: false } received) received.Clear();
    }
    public JsonNode Save() => new JsonObject();
    public void Load(ModuleContext village, JsonNode data, int version) { }
}

public sealed record Chimed(long Tick, int Strength) : VillageEvent(Tick);

/// <summary>Chimes on every tick, as hard as its stream says.</summary>
public sealed class ChimingModule(string name) : IModule
{
    public string Name => name;
    public int Version => 1;
    public void Start(ModuleContext village) => Step(village);
    public void Step(ModuleContext village) => village.Publish(new Chimed(village.Clocks.Tick, village.Random.Next(10)));
    public JsonNode Save() => new JsonObject();
    public void Load(ModuleContext village, JsonNode data, int version) { }
}

/// <summary>Adds up the chimes it hears, with the tick they were rung on.</summary>
public sealed class HearingModule(string name) : IModule
{
    public string Name => name;
    public int Version => 1;
    public long Heard { get; private set; }
    public long Sum { get; private set; }
    public void Start(ModuleContext village) { }
    public void Step(ModuleContext village)
    {
        foreach (var chime in village.Received.OfType<Chimed>())
        {
            Heard++;
            Sum += chime.Tick * chime.Strength;
        }
    }
    public JsonNode Save() => new JsonObject { ["heard"] = Heard, ["sum"] = Sum };
    public void Load(ModuleContext village, JsonNode data, int version)
    {
        Heard = (long)data["heard"]!;
        Sum = (long)data["sum"]!;
    }
}

/// <summary>At version 1; a part of version 0 it cannot read and throws on.</summary>
public sealed class MigratingModule(string name) : IModule
{
    public string Name => name;
    public int Version => 1;
    public int Steps { get; private set; }
    public void Start(ModuleContext village) { }
    public void Step(ModuleContext village) => Steps++;
    public JsonNode Save() => new JsonObject { ["steps"] = Steps };
    public void Load(ModuleContext village, JsonNode data, int version)
    {
        if (version == 0) throw new InvalidDataException("version 0 kept its steps elsewhere");
        Steps = (int)data["steps"]!;
    }
}

/// <summary>A resident born thirty life years before the village started; knows its age.</summary>
public sealed class ResidentModule(string name) : IModule
{
    public string Name => name;
    public int Version => 1;
    public long Born { get; private set; }
    public double Age { get; private set; }
    public void Start(ModuleContext village)
    {
        Born = village.Clocks.Tick - village.Clocks.Life.TickOfAge(30);
        Step(village);
    }
    public void Step(ModuleContext village) => Age = village.Clocks.Life.YearsAt(village.Clocks.Tick - Born);
    public JsonNode Save() => new JsonObject { ["born"] = Born };
    public void Load(ModuleContext village, JsonNode data, int version)
    {
        Born = (long)data["born"]!;
        Step(village);
    }
}

/// <summary>Runs, but throws when asked for its part of the save.</summary>
public sealed class UnsaveableModule(string name) : IModule
{
    public string Name => name;
    public int Version => 1;
    public int Steps { get; private set; }
    public void Start(ModuleContext village) { }
    public void Step(ModuleContext village) => Steps++;
    public JsonNode Save() => throw new InvalidOperationException("the drawer is stuck");
    public void Load(ModuleContext village, JsonNode data, int version) { }
}
