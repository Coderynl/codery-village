using System.Net;
using System.Net.Http.Json;
using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;
using Codery.Village.Modules.Talk;
using Codery.Village.Server;
using Codery.Village.Server.Feed;
using Codery.Village.Server.Store;
using Codery.Village.Server.Talk;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BuildingModule = Codery.Village.Modules.Building.Building;
using PeopleModule = Codery.Village.Modules.People.People;

namespace Codery.Village.Tests;

internal sealed record VersionAnswer(string Commit, string[] Releases);

/// <summary>A store in memory that can be told to be away, to fail or to take its time.</summary>
internal sealed class FakeStore : IVillageStore
{
    private readonly Lock _gate = new();
    private readonly List<(long Village, ulong Seed, List<SavedVillage> Saves)> _villages = [];
    private readonly Dictionary<DateOnly, decimal> _talk = [];
    private int _saving;

    /// <summary>Prepare fails: the database cannot be reached.</summary>
    public volatile bool Away;
    /// <summary>Save fails.</summary>
    public volatile bool Failing;
    /// <summary>When set, a save waits until it is done.</summary>
    public volatile TaskCompletionSource? Hold;
    public int Attempts;
    public int MostAtOnce;

    public Task Prepare(CancellationToken cancellation)
    {
        Interlocked.Increment(ref Attempts);
        return Away ? throw new InvalidOperationException("The database is away.") : Task.CompletedTask;
    }

    public Task<StoredVillage?> Latest(CancellationToken cancellation)
    {
        lock (_gate)
        {
            if (_villages.Count == 0) return Task.FromResult<StoredVillage?>(null);
            var (village, seed, saves) = _villages[^1];
            return Task.FromResult<StoredVillage?>(new StoredVillage(village, seed, saves[^1]));
        }
    }

    public Task<long> Begin(ulong seed, SavedVillage first, CancellationToken cancellation)
    {
        lock (_gate)
        {
            _villages.Add((_villages.Count + 1, seed, [first]));
            return Task.FromResult((long)_villages.Count);
        }
    }

    public async Task Save(long village, SavedVillage save, CancellationToken cancellation)
    {
        var now = Interlocked.Increment(ref _saving);
        lock (_gate) MostAtOnce = Math.Max(MostAtOnce, now);
        try
        {
            if (Hold is { } hold) await hold.Task.WaitAsync(cancellation);
            if (Failing) throw new InvalidOperationException("The database is away.");
            lock (_gate) _villages.Single(v => v.Village == village).Saves.Add(save);
        }
        finally
        {
            Interlocked.Decrement(ref _saving);
        }
    }

    public Task<decimal> TalkSpent(DateOnly day, CancellationToken cancellation)
    {
        lock (_gate) return Task.FromResult(_talk.GetValueOrDefault(day));
    }

    public Task SaveTalkSpent(DateOnly day, decimal spent, CancellationToken cancellation)
    {
        if (Failing) throw new InvalidOperationException("The database is away.");
        lock (_gate) _talk[day] = Math.Max(_talk.GetValueOrDefault(day), spent);
        return Task.CompletedTask;
    }

    public int Saving => Volatile.Read(ref _saving);

    public List<(long Village, ulong Seed, List<SavedVillage> Saves)> Villages
    {
        get
        {
            lock (_gate) return [.. _villages.Select(v => (v.Village, v.Seed, v.Saves.ToList()))];
        }
    }
}

/// <summary>Writes every conversation the same way, so a test can find it back.</summary>
internal sealed class EchoTalker : ITalker
{
    public Task<TalkLines?> Write(TalkRequest request, CancellationToken cancel) =>
        Task.FromResult<TalkLines?>(new TalkLines(
            [$"{request.Facts.A.Name}: Echo.", $"{request.Facts.B.Name}: Echo!"],
            [$"{request.Facts.A.Name}: Echo.", $"{request.Facts.B.Name}: Echo!"],
            FromTemplate: false));
}

/// <summary>
/// The tests that run the real server and wait on the real clock for it to tick or answer. They run on their own, after
/// the others: next to the tests that simulate weeks of village, a small machine (CI has four cores) does not tick or
/// answer fast enough for their waits.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealTimeCollection
{
    public const string Name = "real time";
}

/// <summary>What the server does with a store, a clock and the talker: load at the start, save as it goes, never stop for a database that is away.</summary>
[Collection(RealTimeCollection.Name)]
public class HostTests
{
    internal const string Site = "http://localhost:3100";

    private static WebApplicationFactory<Program> Server(FakeStore? store = null, string saveSeconds = "3600", TimeProvider? time = null,
        ITalker? talker = null, ILoggerProvider? log = null, params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                if (log is not null) logging.AddProvider(log);
            }).UseSetting("Village:SaveSeconds", saveSeconds);
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
            builder.ConfigureTestServices(services =>
            {
                if (store is not null) services.AddSingleton<IVillageStore>(store);
                if (time is not null) services.AddSingleton(time);
                if (talker is not null) services.AddSingleton(talker);
            });
        });

    internal static async Task<VillageHost> Started(WebApplicationFactory<Program> server)
    {
        _ = server.Server;
        var host = server.Services.GetRequiredService<VillageHost>();
        await host.Ready.WaitAsync(TimeSpan.FromSeconds(30));
        return host;
    }

    internal static async Task Until(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Waited in vain for {what}.");
            await Task.Delay(20);
        }
    }

    internal static long Tick(VillageHost host) => host.Inspect(engine => engine.Tick);

    internal static HubConnection Connect(WebApplicationFactory<Program> server, string? origin = Site) =>
        new HubConnectionBuilder()
            .WithUrl("ws://localhost/hub", options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, cancellation) =>
                {
                    var client = server.Server.CreateWebSocketClient();
                    if (origin is not null) client.ConfigureRequest = request => request.Headers.Origin = origin;
                    return await client.ConnectAsync(context.Uri, cancellation);
                };
            })
            .AddJsonProtocol(json => Protocol.Configure(json.PayloadSerializerOptions))
            .Build();

    [Fact]
    public async Task Without_a_connection_string_the_village_lives_in_memory_and_is_there_at_once()
    {
        await using var server = new WebApplicationFactory<Program>();
        _ = server.Server;
        Assert.Null(server.Services.GetService<IVillageStore>());
        Assert.True(server.Services.GetRequiredService<VillageHost>().Ready.IsCompleted);
    }

    [Fact]
    public async Task The_version_names_the_commit_and_every_release()
    {
        await using var server = Server(settings: ("VILLAGE_COMMIT", "abc123"));
        await Started(server);
        var version = await server.CreateClient().GetFromJsonAsync<VersionAnswer>("/version", Protocol.Json);
        Assert.Equal("abc123", version!.Commit);
        Assert.Equal(Ladder.Files().Select(file => Path.GetFileNameWithoutExtension(file)), version.Releases);
    }

    [Fact]
    public async Task The_public_save_loads_as_a_village()
    {
        await using var server = Server();
        await Started(server);
        var saved = await server.CreateClient().GetFromJsonAsync<SavedVillage>("/save", Protocol.Json);
        var engine = Engine.Load(saved!, VillageSetup.Modules(), VillageSetup.Live(), VillageSetup.Map(), TimeProvider.System);
        Assert.Empty(engine.Stopped);
    }

    [Fact]
    public async Task A_new_village_is_saved_at_once()
    {
        var store = new FakeStore();
        await using var server = Server(store);
        await Started(server);
        var village = Assert.Single(store.Villages);
        Assert.Equal(1UL, village.Seed);
        Assert.Equal(0, Assert.Single(village.Saves).Tick);
    }

    [Fact]
    public async Task When_the_village_cannot_be_saved_it_runs_on_and_its_log_goes_along_the_next_time()
    {
        var store = new FakeStore();
        await using var server = Server(store);
        var host = await Started(server);
        host.SetSpeed(64);
        await Until(() => Tick(host) > 600, "six hundred ticks");
        var logEnd = host.Inspect(engine => engine.LogEnd);

        store.Failing = true;
        Assert.False(await host.Save(CancellationToken.None));
        // the clock does not wait for the database
        var failedAt = Tick(host);
        await Until(() => Tick(host) > failedAt + 100, "the clock to run on");

        store.Failing = false;
        Assert.True(await host.Save(CancellationToken.None));
        var last = store.Villages.Single().Saves[^1];
        Assert.True(last.Tick > failedAt);
        Assert.True(last.LogStart + last.Log.Count >= logEnd);
    }

    [Fact]
    public async Task One_save_at_a_time_and_the_clock_runs_on_while_it_is_written()
    {
        var store = new FakeStore();
        await using var server = Server(store);
        var host = await Started(server);
        // something to save: a village that has not changed since it was saved is not saved again
        await Until(() => Tick(host) > 0, "the first tick");
        var hold = store.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var first = host.Save(CancellationToken.None);
            var second = host.Save(CancellationToken.None);
            await Until(() => store.Saving == 1, "a save to be under way");

            // the lock is not held while the store takes its time
            var writing = Tick(host);
            await Until(() => Tick(host) > writing + 2, "the clock to tick during a save");

            hold.SetResult();
            Assert.True(await first);
            Assert.True(await second);
            Assert.Equal(1, store.MostAtOnce);
            Assert.Equal(3, store.Villages.Single().Saves.Count);
        }
        finally
        {
            hold.TrySetResult();
        }
    }

    [Fact]
    public async Task A_village_that_did_not_change_is_not_saved_again()
    {
        var store = new FakeStore();
        await using var server = Server(store);
        var host = await Started(server);
        host.SetSpeed(0);
        Assert.True(await host.Save(CancellationToken.None));
        var saves = store.Villages.Single().Saves.Count;
        Assert.True(await host.Save(CancellationToken.None));
        Assert.Equal(saves, store.Villages.Single().Saves.Count);
    }

    [Fact]
    public async Task It_saves_every_so_many_seconds_and_once_more_when_it_stops()
    {
        var store = new FakeStore();
        long stoppedAt;
        await using (var server = Server(store, saveSeconds: "1"))
        {
            var host = await Started(server);
            await Until(() => store.Villages.Single().Saves.Count >= 3, "two saves on the clock");
            host.SetSpeed(0);
            stoppedAt = Tick(host);
        }
        Assert.Equal(stoppedAt, store.Villages.Single().Saves[^1].Tick);
    }

    [Fact]
    public async Task While_the_database_is_away_a_page_is_told_the_village_is_not_there_and_the_server_carries_on()
    {
        var store = new FakeStore { Away = true };
        await using var server = Server(store);
        var http = server.CreateClient();
        await using var connection = Connect(server);
        await connection.StartAsync();

        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<JoinMessage>("Join"));
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("/state")).StatusCode);
        // it keeps trying, a little later each time
        await Until(() => store.Attempts >= 3, "three attempts");

        store.Away = false;
        await server.Services.GetRequiredService<VillageHost>().Ready.WaitAsync(TimeSpan.FromSeconds(30));
        var join = await connection.InvokeAsync<JoinMessage>("Join");
        Assert.NotEmpty(join.Sites);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/state")).StatusCode);
    }

    // Risk: production cannot read its own save after a deploy and begins a new village in its place: the village is lost.
    [Fact]
    public async Task A_saved_village_that_cannot_be_read_is_never_replaced()
    {
        var store = new FakeStore();
        var lived = VillageSetup.Begin(VillageSetup.Data([]), VillageSetup.Map(), 1, new SimulatedTime(Builders.June(2, 10)));
        await store.Begin(1, lived.Save() with { Format = SavedVillage.CurrentFormat + 1 }, CancellationToken.None);
        var log = new ServerTests.Recorder();

        await using (var server = Server(store, log: log))
        {
            var http = server.CreateClient();
            await Until(() => log.Lines.Any(line => line.Level == LogLevel.Error), "the host to give up on the save");

            var health = await http.GetAsync("/health");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
            Assert.Equal("cannot load the saved village", await health.Content.ReadAsStringAsync());
            Assert.False(server.Services.GetRequiredService<VillageHost>().Ready.IsCompleted);
            Assert.Contains(log.Lines, line => line.Level == LogLevel.Error && line.Text.Contains($"format {SavedVillage.CurrentFormat + 1}"));
        }

        // stopped, it did not save over it either
        var village = Assert.Single(store.Villages);
        Assert.Single(village.Saves);
    }

    // Risk: the deploy that brings the ladder throws away the village production saved on the base, or loads it without the releases.
    // Risk: a live village whose saved Work cannot be read runs on without an economy instead of failing a test.
    [Fact]
    public async Task A_village_saved_on_the_base_goes_on_with_the_whole_ladder()
    {
        var onBase = VillageSetup.Data([]);
        var now = Builders.June(3, 10);
        var day = TimeSpan.FromDays(1);
        static IReadOnlyList<(int, string)> Residents(Engine engine) =>
            [.. engine.Running.OfType<PeopleModule>().Single().All.Select(resident => (resident.Id, resident.Name))];
        var lived = VillageSetup.Aged(onBase, VillageSetup.Map(), 1, new SimulatedTime(now), day);
        var saved = SaveJson.FromJson(SaveJson.ToJson(lived.Save()));
        var store = new FakeStore();
        var village = await store.Begin(1, saved, CancellationToken.None);

        await using var server = Server(store, time: new SimulatedTime(now));
        var host = await Started(server);
        host.SetSpeed(0);

        var (tick, residents) = host.Inspect(engine => (engine.Tick, Residents(engine)));
        Assert.Equal(saved.Tick, tick);
        Assert.Equal(Residents(lived), residents);
        Assert.Equal(village, Assert.Single(store.Villages).Village);

        // a day on, the village builds what only the releases bring
        var known = onBase.Section<Catalog>("catalog").Blueprints.Select(blueprint => blueprint.Id).ToHashSet();
        var ladder = host.Inspect(engine =>
        {
            for (var step = 0; step < day.TotalSeconds * Clocks.TicksPerSecond; step++)
            {
                engine.Step();
                if (engine.Tick % 240 == 0) engine.Forget();
            }
            Assert.Empty(engine.Stopped);
            Assert.DoesNotContain(engine.Log, entry => entry.Kind == "module-dormant");
            return engine.Running.OfType<BuildingModule>().Single().All.Select(structure => structure.Blueprint).Where(blueprint => !known.Contains(blueprint)).ToList();
        });
        Assert.NotEmpty(ladder);
    }

    // Risk: Eddie takes a release away, and the save production loads names what the data no longer knows: the
    // buildings sleep for good, or the hunters stand at a lodge that is not there.
    [Fact]
    public void A_village_goes_on_when_a_release_is_taken_away()
    {
        // two weeks, and then until the hunters have game in stock and a hunter carries it, with a parasol of the café about
        var hunting = Scenario.Start(1, Sections.Of(VillageSetup.Live()));
        hunting.Days(14);
        bool Carried(Scenario village) => village.People.All.Any(resident =>
            village.Day.Of(resident.Id).Carry == "game" || village.Day.TripOf(resident.Id)?.Errand is { There: "game" } or { Back: "game" });
        bool Ready(Scenario village) => village.Map.Islands.Any(island => village.Work.Count(island.Id, "game") > 0) && Carried(village)
            && village.Building.Things.Any(thing => thing.Kind == "parasol");
        for (var quarter = 0; quarter < 4 * 96 && !Ready(hunting); quarter++) hunting.NextQuarter();
        Assert.True(Ready(hunting), "no game in stock and carried, with a parasol about, in four days after the first two weeks");
        var hunters = hunting.People.All.Where(resident => hunting.Work.JobOf(resident.Id)?.Role == "hunter").Select(resident => resident.Id).ToList();
        Assert.NotEmpty(hunters);
        var saved = SaveJson.FromJson(SaveJson.ToJson(hunting.Engine.Save()));

        // the hunters' lodge and the café taken away
        string[] gone = ["hunting", "cafe"];
        var without = VillageSetup.Data(VillageSetup.Releases().Except(gone).Select(release => $"releases/{release}.json"));
        var village = Scenario.Load(saved, Sections.Of(without));

        Assert.Empty(village.Engine.Stopped);
        Assert.DoesNotContain(village.Engine.Log, entry => entry.Kind == "module-dormant");
        Assert.DoesNotContain(village.Building.All, structure => structure.Blueprint is "hunters-lodge" or "cafe");
        Assert.DoesNotContain(village.Building.Things, thing => thing.Kind == "parasol");
        Assert.All(village.Map.Islands, island => Assert.DoesNotContain("game", village.Work.Stock(island.Id).Keys));
        Assert.False(Carried(village));
        // once per id in a module, however many lodges, hunters and parasols there were
        var forgotten = village.Engine.Log.Where(entry => entry.Kind == "forgotten").Select(entry => entry.Subject).ToList();
        Assert.Single(forgotten, "hunters-lodge");
        Assert.Single(forgotten, "hunter");
        Assert.Single(forgotten, "parasol");
        Assert.Contains("game", forgotten);

        // a day on, as the four weeks of the scenarios hold it
        var most = 100L * (int)Sections.Of(without)["economy"]["startSubsidy"]!;
        village.Days(1, each: day =>
        {
            foreach (var island in day.Ordinary)
            {
                var (residents, cash, median) = (day.People.On(island.Id).Count(), day.Work.Of(island.Id).Cash, day.Median(island.Id));
                var shopping = day.Work.LastShopping(island.Id);
                Assert.True(residents is >= 6 and <= 60 && cash >= 0 && cash <= most && median is >= 20 and <= 90 && shopping.Short["food"] * 4 <= shopping.Households,
                    $"{island.Name} on {day.Now:yyyy-MM-dd HH:mm}: {residents} residents, {cash} in the treasury, median happiness {median}, " +
                    $"{shopping.Short["food"]} of {shopping.Households} households without all their food");
            }
        });
        Assert.Empty(village.Engine.Stopped);
        Assert.All(hunters, hunter => Assert.NotEqual("hunter", village.Work.JobOf(hunter)?.Role));
    }

    [Fact]
    public async Task Speed_hurries_the_tick_and_the_life_clock_but_not_the_calendar()
    {
        await using var server = Server();
        var host = await Started(server);
        var (tick, calendar) = host.Inspect(engine => (engine.Tick, engine.Save().Calendar));

        host.SetSpeed(32);
        await Until(() => Tick(host) > tick + 400, "four hundred ticks");
        var (later, then) = host.Inspect(engine => (engine.Tick, engine.Save().Calendar));
        host.SetSpeed(1);

        // the life clock counts ticks; the calendar is the real time
        var simulated = (later - tick) / 4.0;
        var real = (then - calendar).TotalSeconds;
        Assert.True(simulated > 8 * real, $"{simulated} simulated seconds in {real} calendar seconds");
    }

    [Fact]
    public async Task Beginning_again_on_the_flat_map_begins_a_new_village_in_the_store()
    {
        var store = new FakeStore();
        await using var server = Server(store);
        var host = await Started(server);
        host.SetSpeed(16);
        await Until(() => Tick(host) > 50, "fifty ticks");
        host.SetSpeed(0);

        var answer = await server.CreateClient().PostAsJsonAsync("/reset", new { seed = 9 });
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal([1UL, 9UL], store.Villages.Select(v => v.Seed));
        Assert.Equal(0, store.Villages[1].Saves.Single().Tick);
        Assert.Equal(0, Tick(host));
    }

    [Fact]
    public async Task In_development_the_clock_can_start_at_another_time()
    {
        await using var server = Server(settings: ("Village:ClockStart", "2026-06-01T23:00:00+02:00"));
        var host = await Started(server);
        host.SetSpeed(0);

        var calendar = host.Inspect(engine => engine.Save().Calendar);

        Assert.InRange(calendar, Builders.June(1, 23), Builders.June(1, 23, 1));
    }

    // Risk: the hero is judged on a fresh village where nobody works the land, or on days that run on past the start moment.
    [Fact]
    public async Task In_development_the_village_can_begin_a_few_days_old_with_its_days_ending_at_the_start()
    {
        var start = Builders.June(3, 13);
        await using var server = Server(time: new SimulatedTime(start), settings: ("Village:Advance", "2d"));
        var host = await Started(server);
        host.SetSpeed(0);

        var (tick, calendar, built) = host.Inspect(engine =>
            (engine.Tick, engine.Save().Calendar, engine.Running.OfType<BuildingModule>().Single().All.Count));
        var fresh = VillageSetup.Begin(VillageSetup.Data([]), VillageSetup.Map(), 1, new SimulatedTime(start))
            .Running.OfType<BuildingModule>().Single().All.Count;

        const long twoDays = 2 * 24 * 60 * 60 * VillageFeed.TicksPerSecond;
        Assert.InRange(tick, twoDays, twoDays + 40);
        Assert.InRange(calendar, start, start.AddSeconds(10));
        Assert.True(built > fresh, $"{built} structures after two days, {fresh} in a fresh village");
    }

    // Risk: a saved village is overwritten by a younger or older one because a development setting was left on.
    [Fact]
    public async Task With_a_saved_village_the_saved_one_goes_on_and_Advance_is_ignored()
    {
        var store = new FakeStore();
        const long hour = 60 * 60 * VillageFeed.TicksPerSecond;
        await using (var first = Server(store, settings: ("Village:Advance", "1h")))
        {
            var host = await Started(first);
            host.SetSpeed(0);
        }
        // the empty store got the village an hour old
        Assert.Equal(hour, store.Villages.Single().Saves[0].Tick);

        var log = new ServerTests.Recorder();
        await using var second = Server(store, log: log, settings: ("Village:Advance", "2d"));
        var again = await Started(second);
        again.SetSpeed(0);

        Assert.Single(store.Villages);
        Assert.InRange(Tick(again), hour, hour + 40);
        Assert.Contains(log.Lines, line => line.Text.Contains("Village:Advance"));
    }

    // Risk: a typo in the setting starts a fresh village unnoticed, and the screenshots judge the wrong thing.
    [Fact]
    public async Task A_malformed_Advance_keeps_the_server_from_starting()
    {
        await using var server = Server(settings: ("Village:Advance", "two days"));

        var refused = Assert.ThrowsAny<Exception>(() => server.Server);

        Assert.Contains("Village:Advance", refused.ToString());
    }

    // Risk: the live village spends minutes simulating at start-up and serves days that never happened.
    [Fact]
    public async Task Outside_development_Advance_is_ignored()
    {
        await using var server = Server(settings: [("environment", "Production"), ("Village:Advance", "2d")]);
        var host = await Started(server);

        Assert.InRange(Tick(host), 0, 40);
    }

    [Fact]
    public async Task Conversations_are_written_by_the_talker_and_said_in_the_tick()
    {
        // at eight in the evening residents are at leisure together, so the first moment to talk comes at once;
        // at two in the afternoon it took 18,000 ticks, more than a minute at full speed
        await using var server = Server(time: new SimulatedTime(Builders.June(6, 20)), talker: new EchoTalker(), settings: ("Village:Speed", "64"));
        await using var connection = Connect(server);
        var said = new TaskCompletionSource<Said>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<TickMessage>("tick", message =>
        {
            if (message.Said?.FirstOrDefault(s => s.B is not null) is { } conversation) said.TrySetResult(conversation);
        });
        await connection.StartAsync();
        await Started(server);

        var conversation = await said.Task.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.EndsWith(": Echo.", conversation.Nl[0]);
        Assert.EndsWith(": Echo!", conversation.En[1]);
    }

    [Fact]
    public async Task A_moved_thing_gets_a_word_from_the_nearest_resident_who_is_awake_within_a_minute()
    {
        await using var server = Server();
        var host = await Started(server);
        host.SetSpeed(0);
        await using var connection = Connect(server);
        var said = new TaskCompletionSource<(long Tick, Said Said)>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<TickMessage>("tick", message =>
        {
            if (message.Said?.FirstOrDefault(s => s.B is null) is { } word) said.TrySetResult((message.Tick, word));
        });
        await connection.StartAsync();
        var join = await connection.InvokeAsync<JoinMessage>("Join");
        var (thing, row, col) = FreeTileFor(host, join);

        Assert.True((await connection.InvokeAsync<InterventionResult>("Move", thing.Id, row, col)).Ok);
        var movedAt = Tick(host);
        host.SetSpeed(16);
        var (tick, word) = await said.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.InRange(tick - movedAt, 1, 60 * 4);
        var speaker = host.Inspect(engine => engine.Running.OfType<PeopleModule>().Single().Get(word.A));
        Assert.Equal(join.Map.Islands.Single(island => island.Cells.Contains(row * join.Map.Cols + col)).Id, speaker.Island);
        var name = join.Map.Things.Single(kind => kind.Id == thing.Kind);
        Assert.Equal([$"{speaker.Name}: Hé, wie heeft de {name.Nl} verzet?"], word.Nl);
        Assert.Equal([$"{speaker.Name}: Hey, who moved the {name.En}?"], word.En);
    }

    [Fact]
    public async Task A_tick_the_feed_cannot_pass_on_is_logged_and_the_village_runs_on()
    {
        var log = new ServerTests.Recorder();
        await using var server = Server(log: log);
        var host = await Started(server);
        await using var connection = Connect(server);
        var heard = 0L;
        connection.On<TickMessage>("tick", message => Interlocked.Exchange(ref heard, message.Tick));
        await connection.StartAsync();
        host.SetSpeed(8);

        var passOn = host.PassOn;
        host.PassOn = (_, _, _) => throw new InvalidOperationException("the feed broke");
        var broken = Tick(host);
        await Until(() => Tick(host) > broken + 40, "the clock to run on with a broken feed");
        Assert.Contains(log.Lines, line => line.Level == LogLevel.Error && line.Text.Contains("the feed broke"));

        // the feed works again: the pages hear the ticks again
        host.PassOn = passOn;
        var mended = Tick(host);
        await Until(() => Interlocked.Read(ref heard) > mended, "a tick after the feed works again");
    }

    [Fact]
    public async Task A_thing_put_down_where_it_stands_is_refused_and_nobody_says_a_word()
    {
        await using var server = Server();
        var host = await Started(server);
        host.SetSpeed(0);
        await using var connection = Connect(server);
        var said = 0;
        connection.On<TickMessage>("tick", message => Interlocked.Add(ref said, message.Said?.Count(s => s.B is null) ?? 0));
        await connection.StartAsync();
        var join = await connection.InvokeAsync<JoinMessage>("Join");
        var thing = join.Things[0];

        var result = await connection.InvokeAsync<InterventionResult>("Move", thing.Id, thing.Cell / join.Map.Cols, thing.Cell % join.Map.Cols);
        Assert.Equal(new InterventionResult(false, "Taken", null), result);
        // a refusal costs nothing: both moves a visitor has go through after it
        foreach (var skip in new[] { 1, 2 })
        {
            await Task.Delay(TimeSpan.FromSeconds(1.1));
            var (other, row, col) = FreeTileFor(host, join, skip);
            Assert.True((await connection.InvokeAsync<InterventionResult>("Move", other.Id, row, col)).Ok);
        }

        host.SetSpeed(16);
        var start = Tick(host);
        await Until(() => Tick(host) > start + 60 * 4, "a minute of ticks");
        // a word about each real move, none about the thing that stayed where it was
        Assert.Equal(2, Volatile.Read(ref said));
    }

    /// <summary>A loose thing and a free land tile of its island to put it on.</summary>
    internal static (Thing Thing, int Row, int Col) FreeTileFor(VillageHost host, JoinMessage join, int skip = 0) =>
        host.Inspect(engine =>
        {
            var building = engine.Running.OfType<BuildingModule>().Single();
            var thing = join.Things[skip];
            var island = join.Map.Islands.Single(island => island.Cells.Contains(thing.Cell));
            var cell = island.Cells.Select(cell => new Core.Map.Cell(cell / join.Map.Cols, cell % join.Map.Cols))
                .Where(cell => building.CanPlace("campfire", cell, 0)).Skip(skip).First();
            return (thing, cell.Row, cell.Col);
        });

    [Fact]
    public void The_api_key_is_never_printed()
    {
        var options = new TalkOptions("sk-ant-very-secret", 3, 10);

        Assert.DoesNotContain("very-secret", options.ToString());
        Assert.Contains("DailyUsd = 3", options.ToString());
    }
}

/// <summary>The server against a real Postgres: stopped and started again, it goes on where it was.</summary>
[Collection(PostgresCollection.Name)]
public class HostDatabaseTests(PostgresFixture postgres)
{
    private static WebApplicationFactory<Program> Server(string database, ulong seed, TimeProvider? time = null, string speed = "1") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.ClearProviders())
                .UseSetting("ConnectionStrings:Village", database)
                .UseSetting("Village:Seed", seed.ToString())
                .UseSetting("Village:Speed", speed)
                .UseSetting("Village:SaveSeconds", "3600");
            if (time is not null) builder.ConfigureTestServices(services => services.AddSingleton(time));
        });

    /// <summary>Runs a server for a while and stops it; gives the tick it stopped at.</summary>
    private static async Task<long> RunAndStop(string database, ulong seed, long ticks, TimeProvider? time = null)
    {
        await using var server = Server(database, seed, time, speed: "64");
        var host = await HostTests.Started(server);
        await HostTests.Until(() => HostTests.Tick(host) > ticks, $"{ticks} ticks");
        host.SetSpeed(0);
        return HostTests.Tick(host);
    }

    /// <summary>Everything that is kept of a village, as text: to see that nothing of it changed.</summary>
    private static async Task<string> Kept(string database, long village) =>
        string.Join('\n', await PostgresFixture.Strings(database, $"""
            SELECT 'save ' || id || ' ' || tick || ' ' || format || ' ' || saved || ' ' || md5(state::text) FROM village_save WHERE village = {village}
            UNION ALL
            SELECT 'log ' || seq || ' ' || tick || ' ' || kind || ' ' || subject FROM village_log WHERE village = {village}
            UNION ALL
            SELECT 'village ' || id || ' ' || seed || ' ' || started FROM village WHERE id = {village}
            ORDER BY 1
            """));

    [Fact]
    public async Task Stopped_and_started_a_day_later_the_village_goes_on_at_its_tick_with_the_calendar_a_day_on()
    {
        var database = await postgres.NewDatabase();
        var start = Builders.June(2, 10);
        var stoppedAt = await RunAndStop(database, seed: 3, ticks: 500, new SimulatedTime(start));
        await using var store = new PostgresVillageStore(database);
        var saved = (await store.Latest(CancellationToken.None))!.Save!;
        Assert.Equal(stoppedAt, saved.Tick);

        var dayLater = saved.Calendar.AddDays(1);
        await using var server = Server(database, seed: 3, new SimulatedTime(dayLater), speed: "0");
        var host = await HostTests.Started(server);
        var (tick, calendar, save) = host.Inspect(engine => (engine.Tick, engine.Save().Calendar, SaveJson.ToJson(engine.Save())));

        Assert.Equal(stoppedAt, tick);
        Assert.Equal(dayLater, calendar);
        Assert.Equal(saved.Calendar.Date.AddDays(1), calendar.Date);
        // it is the saved village, taken up again
        var expected = Engine.Load(saved, VillageSetup.Modules(), VillageSetup.Live(), VillageSetup.Map(), new SimulatedTime(dayLater));
        Assert.Equal(SaveJson.ToJson(expected.Save()), save);
    }

    [Fact]
    public async Task Another_seed_begins_a_new_village_and_leaves_the_old_one_as_it_was()
    {
        var database = await postgres.NewDatabase();
        await RunAndStop(database, seed: 3, ticks: 200);
        await using var store = new PostgresVillageStore(database);
        var old = (await store.Latest(CancellationToken.None))!;
        var before = await Kept(database, old.Id);

        await RunAndStop(database, seed: 4, ticks: 10);

        var latest = (await store.Latest(CancellationToken.None))!;
        Assert.NotEqual(old.Id, latest.Id);
        Assert.Equal(4UL, latest.Seed);
        Assert.Equal(before, await Kept(database, old.Id));
    }

    [Theory]
    [InlineData("another format")]
    [InlineData("not a save")]
    public async Task A_save_that_cannot_be_loaded_is_left_as_it_was_and_no_new_village_begins(string trouble)
    {
        var database = await postgres.NewDatabase();
        await using var store = new PostgresVillageStore(database);
        await store.Prepare(CancellationToken.None);
        var engine = VillageSetup.Begin(VillageSetup.Data([]), VillageSetup.Map(), 3, new SimulatedTime(Builders.June(2, 10)));
        var old = await store.Begin(3, engine.Save(), CancellationToken.None);
        await PostgresFixture.Count(database, trouble == "another format"
            ? $"UPDATE village_save SET state = jsonb_set(state, '{{format}}', '99') WHERE village = {old}"
            : $"UPDATE village_save SET state = '{{\"format\": 1}}'::jsonb WHERE village = {old}");
        var before = await Kept(database, old);

        await using (var server = Server(database, seed: 3))
        {
            var http = server.CreateClient();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while ((await http.GetAsync("/health")).StatusCode != HttpStatusCode.ServiceUnavailable)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Waited in vain for the health to say the village cannot be loaded.");
                await Task.Delay(20);
            }
        }

        Assert.Equal(["1"], await PostgresFixture.Strings(database, "SELECT count(*)::text FROM village"));
        Assert.Equal(before, await Kept(database, old));
    }

    [Fact]
    public async Task What_the_conversations_of_today_cost_is_still_known_after_a_restart()
    {
        var database = await postgres.NewDatabase();
        var time = new SimulatedTime(Builders.June(2, 10));
        await using (var server = Server(database, seed: 3, time, speed: "0"))
        {
            var host = await HostTests.Started(server);
            var budget = server.Services.GetRequiredService<TalkBudget>();
            Assert.True(budget.TryReserve(2m, out var reservation));
            budget.Settle(reservation, 1.25m);
            Assert.True(await host.Save(CancellationToken.None));
        }

        await using (var server = Server(database, seed: 3, time, speed: "0"))
        {
            await HostTests.Started(server);
            Assert.Equal(1.25m, server.Services.GetRequiredService<TalkBudget>().Spent);
        }

        // the next day starts empty
        time.Advance(TimeSpan.FromDays(1));
        await using (var server = Server(database, seed: 3, time, speed: "0"))
        {
            await HostTests.Started(server);
            Assert.Equal(0m, server.Services.GetRequiredService<TalkBudget>().Spent);
        }
    }
}
