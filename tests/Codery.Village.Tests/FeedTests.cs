using System.Text.Json;
using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Time;
using Codery.Village.Modules.Wishes;
using Codery.Village.Server;
using Codery.Village.Server.Feed;
using Xunit.Abstractions;
using System.Text.Json.Nodes;
using Codery.Village.Modules.Contracts;
using Codery.Village.Modules.Day;
using BuildingModule = Codery.Village.Modules.Building.Building;
using DayModule = Codery.Village.Modules.Day.Day;
using FireModule = Codery.Village.Modules.Fire.Fire;
using PeopleModule = Codery.Village.Modules.People.People;
using WorkModule = Codery.Village.Modules.Work.Work;

namespace Codery.Village.Tests;

/// <summary>Protocol 2: what a page gets, how little of it, and that it adds up to the village.</summary>
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

    private static Village Start(DateTimeOffset at, ulong seed = 1, DataSet? data = null)
    {
        var time = new SimulatedTime(at);
        var engine = VillageSetup.Begin(data ?? Data, Map, seed, time);
        return new Village(engine, time, new VillageFeed(data ?? Data, Map, time));
    }

    /// <summary>The real data with the section "day" changed as the test needs.</summary>
    private static DataSet WithDay(Action<JsonObject> change) => With("day", change);

    /// <summary>The real data with one section changed as the test needs.</summary>
    private static DataSet With(string name, Action<JsonObject> change, Action<JsonObject>? building = null)
    {
        var sections = Directory.GetFiles(VillageSetup.Folder, "*.json").ToDictionary(file => Path.GetFileNameWithoutExtension(file), File.ReadAllText);
        var section = JsonNode.Parse(sections[name])!.AsObject();
        change(section);
        sections[name] = section.ToJsonString();
        if (building is not null)
        {
            var plan = JsonNode.Parse(sections["building"])!.AsObject();
            building(plan);
            sections["building"] = plan.ToJsonString();
        }
        return DataSet.FromTexts(sections, []);
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, Protocol.Json);

    [Fact]
    public void Traffic_stays_under_2_kB_a_second()
    {
        // a week old, so that it has built and grown; then an hour of messages every four hours of a day, each after a
        // page joins: night and day count alike, at a quarter of the work of measuring the whole day
        const int hour = 3600 * VillageFeed.TicksPerSecond;
        var village = Start(Builders.June(1, 0));
        village.Run(7L * 24 * hour);

        var seconds = new List<int>();
        // the first second after a join catches up with what the join leaves out: part of joining, not of the steady
        // traffic the busiest second is about
        var joining = new List<int>();
        for (var window = 0; window < 6; window++)
        {
            if (window > 0) village.Run(3L * hour);
            village.Feed.Join(village.Engine);
            var bytes = 0;
            for (var tick = 1; tick <= hour; tick++)
            {
                village.Run(1);
                bytes += ProtocolJson.Bytes(village.Feed.Next(village.Engine));
                if (tick % VillageFeed.TicksPerSecond != 0) continue;
                (tick == VillageFeed.TicksPerSecond ? joining : seconds).Add(bytes);
                bytes = 0;
            }
        }

        var average = seconds.Concat(joining).Average();
        output.WriteLine($"{seconds.Count + joining.Count} s: {average:F0} B/s on average, {seconds.Max()} B in the busiest second, {seconds.Count(s => s > 2048)} s above 2 kB; after a join {string.Join(", ", joining)} B");
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
            Assert.Equal((Where(move, now.Tick), move.Doing), (Where(moves[move.Person], now.Tick), moves[move.Person].Doing));
        Assert.True(walked);
    }

    [Fact]
    public void What_is_carried_the_paths_the_fires_and_the_homes_reach_the_page()
    {
        // paths wear in after a few walks here; the evening before dusk
        var data = WithDay(day =>
        {
            day["pathThreshold"] = 3;
            day["pathKeep"] = 1;
            day["pathWindowDays"] = 2;
        });
        var village = Start(Builders.June(1, 21, 40), data: data);
        // the fireplaces are there from the start
        Assert.Equal(2, village.Feed.Join(village.Engine).Fires.Count);
        village.Run(1);
        var join = village.Feed.Join(village.Engine);
        var building = village.Module<BuildingModule>();
        var fire = village.Module<FireModule>();
        var day = village.Module<DayModule>();
        int Flat(Cell cell) => cell.Row * Map.Cols + cell.Col;

        // who lives where: the tile of his home
        Assert.All(join.People, person => Assert.Equal(((IPlaces)building).HomeOf(person.Id) is { } home ? Flat(home) : null, person.Home));
        Assert.Contains(join.People, person => person.Home is not null);
        // every fireplace, with how strong it burns
        var campfires = building.All.Where(structure => structure.Blueprint == "campfire").Select(structure => structure.Id).Order().ToList();
        Assert.Equal(campfires, join.Fires.Select(item => item.Structure));
        Assert.All(join.Fires, item => Assert.Equal(0, item.Strength));
        Assert.Empty(join.Map.Paths ?? []);

        // someone carries stones to the far end of West, four times: his way there wears into a path
        var walker = village.Module<PeopleModule>().All.First(resident => resident.Island == Builders.West && day.Of(resident.Id) is { Walk: null, Inside: false });
        var far = Map.Islands.Single(island => island.Id == Builders.West).Cells.Where(Map.IsWalkable)
            .OrderByDescending(cell => cell.DistanceTo(day.Of(walker.Id).At)).ThenBy(cell => cell.Row).ThenBy(cell => cell.Col).First();
        var fires = join.Fires.ToDictionary(item => item.Structure, item => item.Strength);
        var paths = new HashSet<int>(join.Map.Paths ?? []);
        var carried = false;
        void Follow(TickMessage message)
        {
            carried |= message.Moves?.Any(move => move.Person == walker.Id && move.Carry == "stones" && move.Cells.Count > 1) == true;
            foreach (var item in message.Fires ?? []) fires[item.Structure] = item.Strength;
            foreach (var cell in message.Paths ?? []) Assert.True(paths.Add(cell));
            foreach (var cell in message.PathsGone ?? []) Assert.True(paths.Remove(cell));
        }
        for (var trip = 0; trip < 4; trip++)
        {
            Assert.True(day.Send(walker.Id, new Errand(Activity.Bring, far, "stones")));
            for (var tick = 0; tick < 4000 && day.TripOf(walker.Id) is not null; tick++)
            {
                village.Run(1);
                Follow(village.Feed.Next(village.Engine));
            }
        }
        Assert.True(carried);
        Assert.NotEmpty(paths);

        // past dusk the fires burn
        for (var tick = 0; tick < 30 * 60 * VillageFeed.TicksPerSecond; tick++)
        {
            village.Run(1);
            Follow(village.Feed.Next(village.Engine));
        }
        Assert.Contains(fires.Values, strength => strength > 0);
        var now = village.Feed.Join(village.Engine);
        Assert.Equal(now.Fires.ToDictionary(item => item.Structure, item => item.Strength), fires);
        Assert.Equal(fire.All.Select(item => new FireView(item.Structure, item.Strength)), now.Fires);
        Assert.Equal(day.Paths.Select(Flat).Order(), now.Map.Paths!.Order());
        Assert.Equal(now.Map.Paths!.Order(), paths.Order());

        // days later, nobody walked there: the paths grow over
        village.Time.Advance(TimeSpan.FromDays(3));
        village.Run(1);
        Follow(village.Feed.Next(village.Engine));
        Assert.Equal(day.Paths.Select(Flat).Order(), paths.Order());
        Assert.True(paths.Count < now.Map.Paths!.Count);
    }

    [Fact]
    public void Bare_tiles_reach_the_page()
    {
        // a berry patch that makes a batch in 30 ticks works its tiles bare within the hour
        var data = With("catalog", catalog => catalog["blueprints"]!.AsArray().Single(blueprint => (string)blueprint!["id"]! == "berry-patch")!["recipe"]!["ticks"] = 30,
            building => building.Remove("startWorkplaces"));
        var village = Start(Builders.June(1, 9), data: data);
        village.Run(1);
        var building = village.Module<BuildingModule>();
        var work = village.Module<WorkModule>();
        var (anchor, rotation) = building.FindPlace("berry-patch", Builders.West)!.Value;
        var patch = building.Place("berry-patch", anchor, rotation);
        village.Time.Advance(TimeSpan.FromHours(1));
        village.Run(1000);
        Assert.True(building.All.Single(structure => structure.Id == patch).Done);
        int Flat(Cell cell) => cell.Row * Map.Cols + cell.Col;

        var join = village.Feed.Join(village.Engine);
        Assert.Empty(join.Map.Bare ?? []);
        var bare = new HashSet<int>(join.Map.Bare ?? []);
        void Follow(TickMessage message)
        {
            foreach (var cell in message.Bare ?? []) Assert.True(bare.Add(cell));
            foreach (var cell in message.BareGone ?? []) Assert.True(bare.Remove(cell));
        }
        for (var tick = 0; tick < 20_000 && work.Bare.Count == 0; tick++)
        {
            village.Run(1);
            Follow(village.Feed.Next(village.Engine));
        }
        Assert.NotEmpty(work.Bare);
        Assert.Equal(work.Bare.Select(Flat).Order(), bare.Order());

        // a page that joins now gets them
        var now = village.Feed.Join(village.Engine);
        Assert.Equal(work.Bare.Select(Flat).Order(), now.Map.Bare!.Order());

        // days later it has grown back
        village.Time.Advance(TimeSpan.FromDays(3));
        village.Run(1);
        var grown = village.Feed.Next(village.Engine);
        Follow(grown);
        Assert.NotEmpty(grown.BareGone ?? []);
        Assert.Equal(work.Bare.Select(Flat).Order(), bare.Order());
    }

    /// <summary>Where a move has someone at a tick, and whether he is inside then.</summary>
    private static (int Cell, bool Inside) Where(Move move, long tick)
    {
        var steps = (int)Math.Clamp((tick - move.Start) / move.TicksPerCell, 0, move.Cells.Count - 1);
        return (move.Cells[steps], move.Inside && steps == move.Cells.Count - 1);
    }

    [Fact]
    public void A_page_that_joins_gets_protocol_2_and_the_whole_map()
    {
        var village = Start(Builders.June(1, 10));
        village.Run(10);

        var join = village.Feed.Join(village.Engine);

        Assert.Equal(2, join.Protocol);
        Assert.Equal(4, join.TicksPerSecond);
        Assert.Equal(village.Engine.Tick, join.Tick);
        var lines = File.ReadAllLines(Path.Combine(VillageSetup.Folder, "map.txt"))
            .Where(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith("island ")).ToList();
        var rows = lines.TakeWhile(line => line != "levels").ToList();
        var levels = lines.SkipWhile(line => line != "levels").Skip(1).ToList();
        Assert.Equal((rows.Count, rows[0].Length), (join.Map.Rows, join.Map.Cols));
        Assert.Equal(string.Concat(rows), join.Map.Ground);
        // the hills as the map draws them, water at 0
        Assert.Equal(rows.Count, levels.Count);
        Assert.Contains('2', join.Map.Levels!);
        Assert.Equal(string.Concat(levels).Replace('~', '0'), join.Map.Levels);
        Assert.Equal(["West", "Oost", "Eddie"], join.Map.Islands.Select(island => island.Name));
        Assert.True(Assert.Single(join.Map.Islands, island => island.Eddie).Cells.Count > 0);
        var catalog = Data.Section<Catalog>("catalog");
        Assert.Equal(catalog.Blueprints.Select(blueprint => blueprint.Id), join.Map.Blueprints.Select(blueprint => blueprint.Id));
        Assert.Equal(["foundation", "frame", "roof", "walls", "garden"], join.Map.Blueprints.Single(blueprint => blueprint.Id == "hut").Phases);
        // the phases by name, in both languages, as the data gives them
        Assert.Equal(
            [new NameView("foundation", "fundering", "foundation"), new NameView("frame", "geraamte", "frame"), new NameView("roof", "dak", "roof"),
             new NameView("walls", "wanden", "walls"), new NameView("garden", "erf", "garden")],
            join.Map.Blueprints.Single(blueprint => blueprint.Id == "hut").PhaseNames);
        Assert.All(join.Map.Blueprints, blueprint => Assert.Equal(blueprint.Phases, blueprint.PhaseNames.Select(phase => phase.Id)));
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
    public void A_move_says_what_the_resident_is_doing()
    {
        // a Monday morning: most grown-ups are at work, walking there or standing at it
        var village = Start(Builders.June(1, 11));
        village.Run(1);
        var day = village.Module<DayModule>();

        var join = village.Feed.Join(village.Engine);

        var working = join.Moves.Where(move => day.Of(move.Person).Activity == Activity.Work).ToList();
        Assert.NotEmpty(working);
        Assert.All(working, move => Assert.Equal("work", move.Doing));
        Assert.Contains(working, move => move.Cells.Count == 1);
        Assert.Contains("\"doing\":\"work\"", Json(working[0]));
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
            VillageSetup.Export(folder, TimeSpan.FromMinutes(10), seed: 1);

            var start = JsonSerializer.Deserialize<JoinMessage>(File.ReadAllText(Path.Combine(folder, "start.json")), Protocol.Json)!;
            var grown = JsonSerializer.Deserialize<JoinMessage>(File.ReadAllText(Path.Combine(folder, "grown.json")), Protocol.Json)!;
            Assert.Equal((2, 0L), (start.Protocol, start.Tick));
            Assert.Equal((2, 10L * 60 * 4), (grown.Protocol, grown.Tick));
            Assert.NotEmpty(start.People);
            Assert.Equal(start.Map.Ground, grown.Map.Ground);
            Assert.DoesNotContain("null", File.ReadAllText(Path.Combine(folder, "grown.json")));
            Assert.False(File.Exists(Path.Combine(folder, "feed.json")));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Export_can_record_the_ticks_after_the_grown_village()
    {
        var folder = Path.Combine(Path.GetTempPath(), "village-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            VillageSetup.Export(folder, TimeSpan.FromMinutes(10), seed: 1, feedTicks: 40);

            using var feed = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "feed.json")));
            var join = feed.RootElement.GetProperty("join").Deserialize<JoinMessage>(Protocol.Json)!;
            var ticks = feed.RootElement.GetProperty("ticks").Deserialize<TickMessage[]>(Protocol.Json)!;
            var last = feed.RootElement.GetProperty("last").Deserialize<JoinMessage>(Protocol.Json)!;
            var grown = JsonSerializer.Deserialize<JoinMessage>(File.ReadAllText(Path.Combine(folder, "grown.json")), Protocol.Json)!;

            // the recording begins where the grown village stands, and runs on tick by tick
            Assert.Equal(grown.Tick, join.Tick);
            Assert.Equal(Enumerable.Range(1, 40).Select(i => join.Tick + i), ticks.Select(tick => tick.Tick));
            Assert.Equal(join.Tick + 40, last.Tick);
            Assert.Equal(2, last.Protocol);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
