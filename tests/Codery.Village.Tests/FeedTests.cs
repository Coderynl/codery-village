using System.Text.Json;
using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Time;
using Codery.Village.Modules.Wishes;
using Codery.Village.Server;
using Codery.Village.Server.Feed;
using Xunit.Abstractions;
using BuildingModule = Codery.Village.Modules.Building.Building;

namespace Codery.Village.Tests;

/// <summary>Protocol 1: what a page gets, how little of it, and that it adds up to the village.</summary>
public class FeedTests(ITestOutputHelper output)
{
    private static readonly DataSet Data = VillageSetup.Data([]);
    private static readonly IslandMap Map = VillageSetup.Map();

    private sealed record Village(Engine Engine, SimulatedTime Time, VillageFeed Feed)
    {
        public void Run(long ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                Engine.Step();
                // as the host does on every save
                if (Engine.Tick % 240 == 0) Engine.Forget();
            }
        }

        public T Module<T>() => Engine.Running.OfType<T>().Single();
    }

    private static Village Start(DateTimeOffset at, ulong seed = 1)
    {
        var time = new SimulatedTime(at);
        var engine = VillageSetup.Begin(Data, Map, seed, time);
        return new Village(engine, time, new VillageFeed(Data, Map, time));
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, Protocol.Json);

    [Fact]
    public void Traffic_stays_under_2_kB_a_second()
    {
        // a week old, so that it has built and grown; then one calendar day of messages
        var village = Start(Builders.June(1, 0));
        village.Run(7L * 24 * 3600 * VillageFeed.TicksPerSecond);
        village.Feed.Join(village.Engine);

        var seconds = new List<int>();
        var bytes = 0;
        for (var tick = 1; tick <= 24 * 3600 * VillageFeed.TicksPerSecond; tick++)
        {
            village.Run(1);
            bytes += ProtocolJson.Bytes(village.Feed.Next(village.Engine));
            if (tick % VillageFeed.TicksPerSecond != 0) continue;
            seconds.Add(bytes);
            bytes = 0;
        }

        var average = seconds.Average();
        output.WriteLine($"{seconds.Count} s: {average:F0} B/s on average, {seconds.Max()} B in the busiest second, {seconds.Count(s => s > 2048)} s above 2 kB");
        Assert.True(average < 2048, $"{average:F0} bytes a second on average");
        Assert.True(seconds.Max() <= 8192, $"{seconds.Max()} bytes in one second");
    }

    [Fact]
    public void Join_then_ticks_gives_the_same_picture()
    {
        // a Saturday afternoon: many walk and talk
        var village = Start(Builders.June(6, 14));
        var join = village.Feed.Join(village.Engine);
        var people = join.People.ToDictionary(person => person.Id);
        var moves = join.Moves.ToDictionary(move => move.Person);
        var sites = join.Sites.ToDictionary(site => site.Id);
        var things = join.Things.ToDictionary(thing => thing.Id);
        var calendar = join.Calendar;
        var task = join.Task;
        var building = village.Module<BuildingModule>();
        var walked = false;

        for (var tick = 1; tick <= 3 * 3600 * VillageFeed.TicksPerSecond; tick++)
        {
            village.Run(1);
            if (tick == 600)
            {
                // a visitor moves a thing
                var thing = building.Things[0];
                var to = Map.Islands.Single(island => island.Id == Map.IslandOf(thing.At)).Cells.First(cell => building.CanPlace("campfire", cell, 0));
                Assert.Null(building.Move(thing.Id, to));
            }
            var message = village.Feed.Next(village.Engine);
            Assert.Equal(village.Engine.Tick, message.Tick);
            foreach (var person in message.People ?? []) people[person.Id] = person;
            foreach (var move in message.Moves ?? []) moves[move.Person] = move;
            walked |= message.Moves?.Any(move => move.Cells.Count > 1) == true;
            foreach (var id in message.Left ?? [])
            {
                people.Remove(id);
                moves.Remove(id);
            }
            foreach (var site in message.Sites ?? []) sites[site.Id] = site;
            foreach (var id in message.Gone ?? []) sites.Remove(id);
            foreach (var thing in message.Things ?? []) things[thing.Id] = thing;
            calendar = message.Calendar ?? calendar;
            if (message.NoTask == true) task = null;
            task = message.Task ?? task;
        }

        var now = new VillageFeed(Data, Map, village.Time).Join(village.Engine);
        Assert.Equal(Json(now.People.OrderBy(person => person.Id)), Json(people.Values.OrderBy(person => person.Id)));
        Assert.Equal(Json(now.Sites.OrderBy(site => site.Id)), Json(sites.Values.OrderBy(site => site.Id)));
        Assert.Equal(Json(now.Things.OrderBy(thing => thing.Id)), Json(things.Values.OrderBy(thing => thing.Id)));
        Assert.Equal(now.Calendar, calendar);
        Assert.Equal(now.Task, task);
        // everyone stands, or walks, where the join puts him
        Assert.Equal(now.Moves.Select(move => move.Person).Order(), moves.Keys.Order());
        foreach (var move in now.Moves)
            Assert.Equal(Where(move, now.Tick), Where(moves[move.Person], now.Tick));
        Assert.True(walked);
    }

    /// <summary>Where a move has someone at a tick, and whether he is inside then.</summary>
    private static (int Cell, bool Inside) Where(Move move, long tick)
    {
        var steps = (int)Math.Clamp((tick - move.Start) / move.TicksPerCell, 0, move.Cells.Count - 1);
        return (move.Cells[steps], move.Inside && steps == move.Cells.Count - 1);
    }

    [Fact]
    public void A_page_that_joins_gets_protocol_1_and_the_whole_map()
    {
        var village = Start(Builders.June(1, 10));
        village.Run(10);

        var join = village.Feed.Join(village.Engine);

        Assert.Equal(1, join.Protocol);
        Assert.Equal(4, join.TicksPerSecond);
        Assert.Equal(village.Engine.Tick, join.Tick);
        var rows = File.ReadAllLines(Path.Combine(VillageSetup.Folder, "map.txt"))
            .Where(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith("island ")).ToList();
        Assert.Equal((rows.Count, rows[0].Length), (join.Map.Rows, join.Map.Cols));
        Assert.Equal(string.Concat(rows), join.Map.Ground);
        Assert.Equal(["West", "Oost", "Eddie"], join.Map.Islands.Select(island => island.Name));
        Assert.True(Assert.Single(join.Map.Islands, island => island.Eddie).Cells.Count > 0);
        var catalog = Data.Section<Catalog>("catalog");
        Assert.Equal(catalog.Blueprints.Select(blueprint => blueprint.Id), join.Map.Blueprints.Select(blueprint => blueprint.Id));
        Assert.Equal(["foundation", "frame", "roof", "walls", "garden"], join.Map.Blueprints.Single(blueprint => blueprint.Id == "hut").Phases);
        Assert.Equal(catalog.Roles.Select(role => role.Id), join.Map.Roles.Select(role => role.Id));
        Assert.Equal(catalog.Things.Select(thing => thing.Id), join.Map.Things.Select(thing => thing.Id));

        Assert.Equal(village.Module<Codery.Village.Modules.People.People>().All.Count, join.People.Count);
        Assert.Single(join.People, person => person.Eddie);
        Assert.All(join.People, person =>
        {
            Assert.Equal(0, person.Happiness % 5);
            Assert.Contains(person.Phase, new[] { "child", "student", "working", "retired" });
            Assert.Contains(person.Sex, new[] { "man", "woman" });
            Assert.All(person.Notable, trait => Assert.Matches("^[a-z]+-(high|low)$", trait));
        });
        Assert.Contains(join.People, person => person.Job is not null);
        Assert.Equal(join.People.Count, join.Moves.Count);
        Assert.All(join.Sites, site => Assert.True(site.Progress % 10 == 0 && site.Progress is >= 0 and <= 100));
        Assert.Contains(join.Sites, site => site.Blueprint == "eddie-house" && site.Done);
        Assert.NotEmpty(join.Things);
        Assert.Equal("2026-06-01T10:00", join.Calendar.Local);
        Assert.False(join.Calendar.Night);
    }

    [Fact]
    public void A_quiet_tick_is_only_its_number_and_the_calendar_comes_each_quarter()
    {
        var village = Start(Builders.June(1, 23, 14).AddSeconds(30));
        village.Feed.Join(village.Engine);
        var messages = new List<TickMessage>();
        for (var tick = 0; tick < 4 * 60; tick++)
        {
            village.Run(1);
            messages.Add(village.Feed.Next(village.Engine));
        }

        Assert.Contains(messages, message => Json(message) == $$"""{"tick":{{message.Tick}}}""");
        var calendar = Assert.Single(messages, message => message.Calendar is not null).Calendar!;
        Assert.Equal(new CalendarView("2026-06-01T23:15", Night: true), calendar);
    }

    [Fact]
    public void The_log_speaks_in_words_and_names_never_ids()
    {
        var catalog = Data.Section<Catalog>("catalog");
        var text = new LogText(Data, catalog, Data.Section<WishSettings>("wishes").Wishes);

        Assert.Equal(("Er staat een nieuw bouwsel: hut.", "A new building stands: hut."), text.Render(new LogEntry(1, "built", "hut", "3")));
        Assert.Equal(("Het dorp schrijft een taak uit voor Eddie: een café.", "The village writes a task for Eddie: a café."),
            text.Render(new LogEntry(1, "task-opened", "cafe")));
        Assert.Equal(("Anna zet de jonge boom terug.", "Anna puts the sapling back."), text.Render(new LogEntry(1, "put-back", "Anna", "sapling")));
        Assert.Equal(("Anna gaat met pensioen.", "Anna retires."), text.Render(new LogEntry(1, "phase-retired", "Anna")));
        Assert.Null(text.Render(new LogEntry(1, "module-stopped", "work", "the wheel came off")));
    }

    [Fact]
    public void A_village_writes_its_diary_in_words_and_says_what_was_said()
    {
        var village = Start(Builders.June(6, 14));
        var join = village.Feed.Join(village.Engine);
        var events = new List<FeedEvent>();
        var said = new List<Said>();
        for (var tick = 0; tick < 2 * 3600 * VillageFeed.TicksPerSecond && said.Count == 0; tick++)
        {
            village.Run(1);
            var message = village.Feed.Next(village.Engine);
            events.AddRange(message.Events ?? []);
            said.AddRange(message.Said ?? []);
        }

        var conversation = said[0];
        Assert.NotNull(conversation.B);
        Assert.InRange(conversation.Nl.Count, 2, 4);
        var talked = events.First(e => e.Kind == "talked");
        Assert.EndsWith("maken een praatje.", talked.Nl);
        Assert.EndsWith("have a chat.", talked.En);
        Assert.All(events, e => Assert.DoesNotContain("{", e.Nl + e.En));
        Assert.True(join.Log.Count <= VillageFeed.HistoryLines);
    }

    [Fact]
    public void What_a_hand_moved_is_sent_once_and_the_rest_stays_quiet()
    {
        var village = Start(Builders.June(1, 10));
        village.Run(1);
        village.Feed.Join(village.Engine);
        var building = village.Module<BuildingModule>();
        var thing = building.Things[0];
        var to = Map.Islands.Single(island => island.Id == Map.IslandOf(thing.At)).Cells.First(cell => building.CanPlace("campfire", cell, 0));

        Assert.Null(building.Move(thing.Id, to));
        village.Run(1);
        var first = village.Feed.Next(village.Engine);
        village.Run(1);
        var second = village.Feed.Next(village.Engine);

        Assert.Equal([new Thing(thing.Id, thing.Kind, to.Row * Map.Cols + to.Col)], first.Things);
        Assert.Null(second.Things);
    }

    [Fact]
    public void Export_writes_the_village_as_it_begins_and_as_it_has_grown()
    {
        var folder = Path.Combine(Path.GetTempPath(), "village-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            VillageSetup.Export(folder, TimeSpan.FromMinutes(10), seed: 1, extras: []);

            var start = JsonSerializer.Deserialize<JoinMessage>(File.ReadAllText(Path.Combine(folder, "start.json")), Protocol.Json)!;
            var grown = JsonSerializer.Deserialize<JoinMessage>(File.ReadAllText(Path.Combine(folder, "grown.json")), Protocol.Json)!;
            Assert.Equal((1, 0L), (start.Protocol, start.Tick));
            Assert.Equal((1, 10L * 60 * 4), (grown.Protocol, grown.Tick));
            Assert.NotEmpty(start.People);
            Assert.Equal(start.Map.Ground, grown.Map.Ground);
            Assert.DoesNotContain("null", File.ReadAllText(Path.Combine(folder, "grown.json")));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
