using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Modules;
using Codery.Village.Modules.Building;
using Codery.Village.Modules.Community;
using Codery.Village.Modules.Day;
using Codery.Village.Modules.Happiness;
using Codery.Village.Modules.People;
using Codery.Village.Modules.Wishes;
using Codery.Village.Modules.Work;
using Codery.Village.Server.Feed;
using Codery.Village.Core.Time;
using TalkModule = Codery.Village.Modules.Talk.Talk;

namespace Codery.Village.Server;

/// <summary>What the village is made of: its modules in the order they step, its data and its map.</summary>
public static class VillageSetup
{
    /// <summary>The data folder next to the server.</summary>
    public static string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>
    /// The modules of the village, fresh on every call, in the order they step. This is the only place where that
    /// order is written down.
    /// </summary>
    public static IReadOnlyList<IModule> Modules() =>
        [new People(), new Day(), new Work(), new Building(), new Community(), new Wishes(), new Happiness(), new TalkModule()];

    /// <summary>The data in <see cref="Folder"/>, with the releases <paramref name="extras"/> names (such as <c>releases/cafe.json</c>).</summary>
    public static DataSet Data(IEnumerable<string> extras) => DataFolder.Load(Folder, extras);

    public static IslandMap Map() => MapText.Parse(File.ReadAllText(Path.Combine(Folder, "map.txt")));

    /// <summary>A new village, started.</summary>
    public static Engine Begin(DataSet data, IslandMap map, ulong seed, TimeProvider time)
    {
        var engine = new Engine(Modules(), data, map, seed, time);
        engine.Start();
        return engine;
    }

    /// <summary>When an exported village begins: a Monday morning in June.</summary>
    public static readonly DateTimeOffset ExportStart = new(2026, 6, 1, 8, 0, 0, TimeSpan.FromHours(2));

    /// <summary>
    /// Writes the village as it begins (<c>start.json</c>) and after <paramref name="duration"/> on the simulated
    /// clock (<c>grown.json</c>), as a page gets it when it joins: for the still village in the hero.
    /// </summary>
    public static void Export(string folder, TimeSpan duration, ulong seed, IEnumerable<string> extras)
    {
        var data = Data(extras);
        if (data.Problems() is { Count: > 0 } problems) throw new InvalidDataException(string.Join(" ", problems));
        var map = Map();
        var time = new SimulatedTime(ExportStart);
        var engine = Begin(data, map, seed, time);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "start.json"), ProtocolJson.Write(new VillageFeed(data, map, time).Join(engine)));

        var ticks = (long)(duration.TotalSeconds * Clocks.TicksPerSecond);
        for (var tick = 1; tick <= ticks; tick++)
        {
            engine.Step();
            // as the host does when it saves
            if (tick % 240 == 0) engine.Forget();
        }
        File.WriteAllText(Path.Combine(folder, "grown.json"), ProtocolJson.Write(new VillageFeed(data, map, time).Join(engine)));
    }
}

/// <summary>The real time, moved to start at another moment: for a developer who wants to see the village at 23:00 by day.</summary>
public sealed class ShiftedTime(DateTimeOffset start, TimeProvider? real = null) : TimeProvider
{
    private readonly TimeProvider _real = real ?? System;
    private readonly TimeSpan _shift = start - (real ?? System).GetUtcNow();

    public override DateTimeOffset GetUtcNow() => _real.GetUtcNow() + _shift;
}
