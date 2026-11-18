using System.Net;
using System.Reflection;
using System.Text.Json;
using Codery.Village.Core.Map;
using Codery.Village.Server;
using Codery.Village.Server.Feed;
using Codery.Village.Server.Store;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BuildingModule = Codery.Village.Modules.Building.Building;

namespace Codery.Village.Tests;

/// <summary>The server as a page meets it: over a WebSocket, from an origin.</summary>
[Collection(RealTimeCollection.Name)]
public class ServerTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Site = HostTests.Site;

    /// <summary>
    /// A village of its own for one test, with its clock standing still so nobody walks into the way, and with the
    /// settings the test gives, such as other limits.
    /// </summary>
    private async Task<WebApplicationFactory<Program>> Village(params (string Key, string Value)[] settings)
    {
        var server = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Village:Speed", "0");
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
        });
        await HostTests.Started(server);
        return server;
    }

    private static async Task<(HubConnection Connection, JoinMessage Join)> Joined(WebApplicationFactory<Program> server)
    {
        var connection = HostTests.Connect(server);
        await connection.StartAsync();
        return (connection, await connection.InvokeAsync<JoinMessage>("Join"));
    }

    private static VillageHost Host(WebApplicationFactory<Program> server) => server.Services.GetRequiredService<VillageHost>();

    private static Cell Where(VillageHost host, int thing) =>
        host.Inspect(engine => engine.Running.OfType<BuildingModule>().Single().Things.Single(item => item.Id == thing).At);

    [Fact]
    public async Task A_page_of_the_site_joins_and_gets_the_whole_village()
    {
        await using var connection = HostTests.Connect(factory);
        await connection.StartAsync();
        var join = await connection.InvokeAsync<JoinMessage>("Join");
        Assert.Equal(1, join.Protocol);
        Assert.Equal(14, join.Map.Rows);
        Assert.NotEmpty(join.People);
        Assert.NotEmpty(join.Sites);
        Assert.NotEmpty(join.Things);
        Assert.Equal(4, join.TicksPerSecond);
    }

    [Fact]
    public async Task After_joining_the_ticks_come_by_themselves()
    {
        await using var connection = HostTests.Connect(factory);
        var ticks = new List<long>();
        var three = new TaskCompletionSource();
        connection.On<TickMessage>("tick", message =>
        {
            lock (ticks)
            {
                ticks.Add(message.Tick);
                if (ticks.Count == 3) three.TrySetResult();
            }
        });
        await connection.StartAsync();
        await connection.InvokeAsync<JoinMessage>("Join");
        await three.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lock (ticks) Assert.Equal(ticks.OrderBy(t => t), ticks);
    }

    [Theory]
    [InlineData("https://example.org")]
    [InlineData(null)]
    public async Task A_page_from_somewhere_else_is_turned_away(string? origin)
    {
        await using var connection = HostTests.Connect(factory, origin);
        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public async Task A_page_can_call_join_and_move_and_nothing_else()
    {
        // what the hub offers: its own public methods
        var offered = typeof(VillageHub).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => !method.IsSpecialName && method.GetBaseDefinition().DeclaringType == typeof(VillageHub))
            .Select(method => method.Name)
            .Order();
        Assert.Equal(["Join", "Move"], offered);

        await using var connection = HostTests.Connect(factory);
        await connection.StartAsync();
        foreach (var method in new[] { "Lift", "Reset", "SetSpeed", "Do", "State", "Save", "Try", "Forget", "OnDisconnectedAsync" })
            await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<object>(method, 1, 2, 3));
    }

    [Fact]
    public async Task A_page_of_the_site_moves_a_thing_and_the_next_tick_has_it_where_it_was_put()
    {
        await using var server = await Village();
        var (connection, join) = await Joined(server);
        await using var _ = connection;
        var (thing, row, col) = HostTests.FreeTileFor(Host(server), join);
        var moved = new TaskCompletionSource<Thing>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<TickMessage>("tick", message =>
        {
            if (message.Things?.FirstOrDefault(t => t.Id == thing.Id) is { } sent) moved.TrySetResult(sent);
        });

        Assert.Equal(InterventionResult.Done, await connection.InvokeAsync<InterventionResult>("Move", thing.Id, row, col));

        Assert.Equal(new Thing(thing.Id, thing.Kind, row * join.Map.Cols + col), await moved.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(new Cell(row, col), Where(Host(server), thing.Id));
    }

    [Fact]
    public async Task Into_the_water_is_refused_with_the_name_of_the_refusal()
    {
        await using var server = await Village();
        var (connection, join) = await Joined(server);
        await using var _ = connection;
        var thing = join.Things[0];

        Assert.Equal(new InterventionResult(false, "Water", null), await connection.InvokeAsync<InterventionResult>("Move", thing.Id, 0, 0));
        Assert.Equal(new Cell(thing.Cell / join.Map.Cols, thing.Cell % join.Map.Cols), Where(Host(server), thing.Id));
    }

    [Fact]
    public async Task Right_after_a_try_another_is_too_soon_and_the_page_hears_how_long_to_wait()
    {
        await using var server = await Village(("Village:Limits:MinSecondsBetweenTries", "60"));
        var (connection, join) = await Joined(server);
        await using var _ = connection;
        var (thing, row, col) = HostTests.FreeTileFor(Host(server), join);
        var (_, row2, col2) = HostTests.FreeTileFor(Host(server), join, skip: 1);

        Assert.True((await connection.InvokeAsync<InterventionResult>("Move", thing.Id, row, col)).Ok);
        // as it goes over the line
        var again = await connection.InvokeAsync<JsonElement>("Move", thing.Id, row2, col2);
        Assert.Equal(["ok", "refusal", "wait"], again.EnumerateObject().Select(p => p.Name).Order());
        Assert.False(again.GetProperty("ok").GetBoolean());
        Assert.Equal("TooSoon", again.GetProperty("refusal").GetString());
        Assert.Equal(60, again.GetProperty("wait").GetInt32());
        Assert.Equal(new Cell(row, col), Where(Host(server), thing.Id));
    }

    [Fact]
    public async Task Over_several_connections_the_village_as_a_whole_runs_out_and_is_busy()
    {
        await using var server = await Village(("Village:Limits:Village", "2"), ("Village:Limits:VillageEverySeconds", "3600"));
        var results = new List<InterventionResult>();
        for (var i = 0; i < 3; i++)
        {
            // every connection tries once: on its own none of them would have to wait
            var (connection, join) = await Joined(server);
            await using var _ = connection;
            var (thing, row, col) = HostTests.FreeTileFor(Host(server), join, skip: i);
            results.Add(await connection.InvokeAsync<InterventionResult>("Move", thing.Id, row, col));
        }

        Assert.Equal([true, true, false], results.Select(r => r.Ok));
        Assert.Equal("Busy", results[2].Refusal);
        Assert.InRange(results[2].Wait!.Value, 3590, 3600);
    }

    [Fact]
    public async Task Nonsense_is_refused_and_never_breaks_anything()
    {
        await using var server = await Village(("Village:Limits:MinSecondsBetweenTries", "0"), ("Village:Limits:PerVisitor", "100"), ("Village:Limits:Village", "100"));
        var (connection, join) = await Joined(server);
        await using var _ = connection;
        var thing = join.Things[0];
        var other = join.Things.First(t => t.Id != thing.Id && join.Map.Islands.Single(island => island.Cells.Contains(t.Cell)).Cells.Contains(thing.Cell));
        var lot = join.Map.Ground.Select((ground, cell) => (ground, cell))
            .First(tile => tile.ground == 'h' && join.Map.Islands.Single(island => island.Cells.Contains(thing.Cell)).Cells.Contains(tile.cell)
                && join.Sites.All(site => !site.Cells.Contains(tile.cell))).cell;
        var elsewhere = join.Map.Islands.First(island => !island.Eddie && !island.Cells.Contains(thing.Cell)).Cells[0];
        var cols = join.Map.Cols;

        (object[] Args, string Refusal)[] tries =
        [
            ([-1, 9, 24], "NotFound"),
            ([int.MaxValue, int.MinValue, int.MaxValue], "NotFound"),
            ([thing.Id, -1, 5], "Water"),
            ([thing.Id, int.MaxValue, int.MaxValue], "Water"),
            ([thing.Id, int.MinValue, int.MinValue], "Water"),
            ([thing.Id, other.Cell / cols, other.Cell % cols], "Taken"),
            ([thing.Id, lot / cols, lot % cols], "Taken"),
            ([thing.Id, elsewhere / cols, elsewhere % cols], "OtherIsland"),
        ];
        foreach (var (args, refusal) in tries)
            Assert.Equal(new InterventionResult(false, refusal, null), await connection.InvokeCoreAsync<InterventionResult>("Move", args));

        // what is not a number does not get as far as the hub: SignalR turns it away, and the connection stays
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<InterventionResult>("Move", "bench", 9, 24));
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<InterventionResult>("Move", 8, 19));
        Assert.Equal(HubConnectionState.Connected, connection.State);
        Assert.Equal(new InterventionResult(false, "NotFound", null), await connection.InvokeAsync<InterventionResult>("Move", -1, 9, 24));
        Assert.Equal(new Cell(thing.Cell / cols, thing.Cell % cols), Where(Host(server), thing.Id));
    }

    [Fact]
    public async Task When_a_connection_closes_what_the_server_knew_of_it_is_gone()
    {
        await using var server = await Village();
        var limiter = server.Services.GetRequiredService<InterventionLimiter>();
        var (connection, join) = await Joined(server);
        // only a try makes the server remember anything
        Assert.Equal(0, limiter.Visitors);
        await connection.InvokeAsync<InterventionResult>("Move", join.Things[0].Id, 0, 0);
        Assert.Equal(1, limiter.Visitors);

        await connection.DisposeAsync();
        await HostTests.Until(() => limiter.Visitors == 0, "the closed connection to be forgotten");
    }

    [Fact]
    public async Task While_the_village_is_not_loaded_a_page_hears_it_is_not_ready()
    {
        await using var server = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IVillageStore>(new FakeStore { Away = true })));
        await using var connection = HostTests.Connect(server);
        await connection.StartAsync();

        Assert.Equal(InterventionResult.NotReady, await connection.InvokeAsync<InterventionResult>("Move", 1, 9, 24));
        // not even a try: the limits do not count it
        Assert.Equal(0, server.Services.GetRequiredService<InterventionLimiter>().Visitors);
    }

    [Fact]
    public async Task The_village_has_the_releases_its_settings_name_and_none_without()
    {
        static bool HasCafe(WebApplicationFactory<Program> server) =>
            Host(server).Join().Map.Blueprints.Any(blueprint => blueprint.Id == "cafe");

        await using var plain = await Village();
        Assert.False(HasCafe(plain));

        await using var released = await Village(("Village:Extras:0", "releases/cafe.json"));
        Assert.True(HasCafe(released));
    }

    [Fact]
    public async Task A_release_with_problems_keeps_the_server_from_starting_and_the_log_says_why()
    {
        // the same release twice: every id it brings is named twice
        var log = new Recorder();
        await using var server = factory.WithWebHostBuilder(builder => builder
            .UseSetting("Village:Extras:0", "releases/cafe.json")
            .UseSetting("Village:Extras:1", "releases/cafe.json")
            .ConfigureLogging(logging => logging.AddProvider(log)));

        var refused = Assert.ThrowsAny<Exception>(() => server.Server);

        Assert.Contains("twice", refused.ToString());
        Assert.Contains(log.Lines, line => line.Level == LogLevel.Critical && line.Text.Contains("'cafe' twice"));
    }

    /// <summary>Keeps what the server logs, to read back in a test.</summary>
    internal sealed class Recorder : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Text)> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Writer(this);

        public void Dispose() { }

        private sealed class Writer(Recorder recorder) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                recorder.Lines.Enqueue((logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public async Task The_flat_map_is_served_in_development()
    {
        await using var server = await Village();
        var client = server.CreateClient();

        var page = await client.GetStringAsync("/");
        var state = JsonDocument.Parse(await client.GetStringAsync("/state")).RootElement;

        Assert.Contains("<canvas", page);
        Assert.Equal(1, state.GetProperty("join").GetProperty("protocol").GetInt32());
        Assert.Equal(0, state.GetProperty("speed").GetInt32());
        Assert.Equal(JsonValueKind.Array, state.GetProperty("said").ValueKind);
    }

    [Fact]
    public async Task The_flat_map_and_its_buttons_are_not_served_outside_development()
    {
        var client = factory.WithWebHostBuilder(builder => builder.UseSetting("environment", "Production")).CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/state")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/reset", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/speed", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }
}
