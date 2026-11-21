using System.Globalization;
using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Modules;
using Codery.Village.Modules.Building;
using Codery.Village.Modules.Community;
using Codery.Village.Modules.Day;
using Codery.Village.Modules.Fire;
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
        [new People(), new Day(), new Work(), new Building(), new Fire(), new Community(), new Wishes(), new Happiness(), new TalkModule()];

    /// <summary>The data in <see cref="Folder"/>, with the releases <paramref name="extras"/> names (such as <c>releases/cafe.json</c>): for the base or a part of the ladder.</summary>
    public static DataSet Data(IEnumerable<string> extras) => DataFolder.Load(Folder, extras);

    /// <summary>The data the live village runs on: the base in <see cref="Folder"/> with every release in its <c>releases</c> folder.</summary>
    public static DataSet Live() => DataFolder.LoadLive(Folder);

    /// <summary>The ids of the releases the live village runs on, in load order.</summary>
    public static IReadOnlyList<string> Releases() => DataFolder.Releases(Folder);

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
    /// Writes the live village as it begins (<c>start.json</c>) and after <paramref name="duration"/> on the simulated
    /// clock (<c>grown.json</c>), as a page gets it when it joins: for the still village in the hero. With
    /// <paramref name="feedTicks"/> it also records what a page gets after that (<c>feed.json</c>): the join, that
    /// many ticks, and the join at the end, for the hero's tests.
    /// </summary>
    public static void Export(string folder, TimeSpan duration, ulong seed, int feedTicks = 0)
    {
        var data = Live();
        if (data.Problems() is { Count: > 0 } problems) throw new InvalidDataException(string.Join(" ", problems));
        var map = Map();
        var time = new SimulatedTime(ExportStart);
        var engine = Begin(data, map, seed, time);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "start.json"), ProtocolJson.Write(new VillageFeed(data, map, time).Join(engine)));

        Grow(engine, duration);
        File.WriteAllText(Path.Combine(folder, "grown.json"), ProtocolJson.Write(new VillageFeed(data, map, time).Join(engine)));
        if (feedTicks <= 0) return;

        var feed = new VillageFeed(data, map, time);
        var join = feed.Join(engine);
        var recorded = new List<TickMessage>(feedTicks);
        for (var i = 0; i < feedTicks; i++)
        {
            engine.Step();
            if (engine.Tick % 240 == 0) engine.Forget();
            recorded.Add(feed.Next(engine));
        }
        var last = new VillageFeed(data, map, time).Join(engine);
        File.WriteAllText(Path.Combine(folder, "feed.json"), ProtocolJson.Write(new { join, ticks = recorded, last }));
    }

    /// <summary>
    /// A village that began <paramref name="age"/> before what <paramref name="time"/> says now, run on a simulated
    /// clock up to now, and then going on with <paramref name="time"/>: for a developer who wants to see it lived in.
    /// </summary>
    public static Engine Aged(DataSet data, IslandMap map, ulong seed, TimeProvider time, TimeSpan age)
    {
        var simulated = new SimulatedTime(time.GetUtcNow() - age);
        var engine = Begin(data, map, seed, simulated);
        Grow(engine, age);
        // taken up again on the given clock, as after a restart
        return Engine.Load(engine.Save(), Modules(), data, map, time);
    }

    /// <summary>Runs a village on a simulated clock for <paramref name="duration"/>, as fast as it goes.</summary>
    private static void Grow(Engine engine, TimeSpan duration)
    {
        var ticks = (long)(duration.TotalSeconds * Clocks.TicksPerSecond);
        for (long tick = 1; tick <= ticks; tick++)
        {
            engine.Step();
            // as the host does when it saves
            if (tick % 240 == 0) engine.Forget();
        }
    }

    /// <summary>A duration as export and <c>Village:Advance</c> write it: 7d, 12h, 30m, or a TimeSpan such as 7.00:00:00.</summary>
    public static TimeSpan Duration(string text)
    {
        try
        {
            return text.Trim() is { Length: > 0 } word
                ? word[^1] switch
                {
                    'd' => TimeSpan.FromDays(double.Parse(word[..^1], CultureInfo.InvariantCulture)),
                    'h' => TimeSpan.FromHours(double.Parse(word[..^1], CultureInfo.InvariantCulture)),
                    'm' => TimeSpan.FromMinutes(double.Parse(word[..^1], CultureInfo.InvariantCulture)),
                    _ => TimeSpan.Parse(word, CultureInfo.InvariantCulture),
                }
                : throw new FormatException();
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new FormatException($"'{text}' is not a duration: write 3d, 12h, 30m or 3.00:00:00.", exception);
        }
    }
}

/// <summary>The real time, moved to start at another moment: for a developer who wants to see the village at 23:00 by day.</summary>
public sealed class ShiftedTime(DateTimeOffset start, TimeProvider? real = null) : TimeProvider
{
    private readonly TimeProvider _real = real ?? System;
    private readonly TimeSpan _shift = start - (real ?? System).GetUtcNow();

    public override DateTimeOffset GetUtcNow() => _real.GetUtcNow() + _shift;
}
