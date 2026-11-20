using System.Globalization;
using Codery.Village.Core.Data;
using Codery.Village.Server;
using Codery.Village.Server.Feed;
using Codery.Village.Server.Store;
using Codery.Village.Server.Talk;
using Microsoft.AspNetCore.Http.Connections;

// "export <folder> [<duration>]" writes the village as it begins (start.json) and after the duration (grown.json,
// a week when none is given: 7d, 12h, 30m or 7.00:00:00), for the still village in the hero, and stops.
// It runs on the data of the server, with every release. --Village:Seed=2 works as on the server. --feed=2400 also
// records the join, 2400 ticks and the join after them (feed.json), for the hero's tests.
if (args is ["export", _, ..])
{
    string[] words = [.. args.Skip(1).Where(arg => !arg.StartsWith("--", StringComparison.Ordinal))];
    var settings = new ConfigurationBuilder().AddCommandLine([.. args.Where(arg => arg.StartsWith("--", StringComparison.Ordinal))]).Build();
    var duration = words.Length > 1 ? VillageSetup.Duration(words[1]) : TimeSpan.FromDays(7);
    var feedTicks = settings.GetValue<int?>("feed") ?? 0;
    VillageSetup.Export(words[0], duration, settings.GetValue<ulong?>("Village:Seed") ?? 1, feedTicks);
    Console.WriteLine($"Wrote start.json and grown.json ({duration}){(feedTicks > 0 ? $" and feed.json ({feedTicks} ticks)" : "")} to {Path.GetFullPath(words[0])}");
    return;
}

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))) builder.WebHost.UseUrls("http://localhost:5210");
// requests are not logged: no addresses, no paths
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

builder.Services.AddSignalR(options =>
{
    // a page has nothing to say but "join" and "move"
    options.MaximumReceiveMessageSize = 512;
    options.EnableDetailedErrors = false;
})
    // what is not there is left out, not sent as null: a quiet tick is only its number
    .AddJsonProtocol(json => Protocol.Configure(json.PayloadSerializerOptions));

// the world as data, with every release in the folder, and the map
builder.Services.AddSingleton(_ => VillageSetup.Live());
builder.Services.AddSingleton(_ => VillageSetup.Map());
// the real time; on a developer's machine Village:ClockStart starts it at another moment, to see the village at night by day
builder.Services.AddSingleton(services =>
{
    var start = services.GetRequiredService<IConfiguration>()["Village:ClockStart"];
    return services.GetRequiredService<IHostEnvironment>().IsDevelopment()
        && DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
        ? new ShiftedTime(at)
        : TimeProvider.System;
});

// conversations: written by the model when there is an API key, from a template when not
builder.Services.AddSingleton(services => TalkOptions.From(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(services => new TalkBudget(services.GetRequiredService<TalkOptions>().DailyUsd, services.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<ITalker>(services => new ClaudeTalker(
    services.GetRequiredService<DataSet>(), services.GetRequiredService<TalkOptions>(), services.GetRequiredService<TalkBudget>(),
    log: services.GetRequiredService<ILogger<ClaudeTalker>>()));
builder.Services.AddSingleton(services => new TalkWorker(
    services.GetRequiredService<ITalker>(), services.GetRequiredService<DataSet>(), services.GetRequiredService<TalkOptions>(),
    services.GetRequiredService<ILogger<TalkWorker>>()));

// with a connection string the village is kept in Postgres and goes on after a restart; without one it lives in memory
var database = builder.Configuration.GetConnectionString("Village");
if (!string.IsNullOrWhiteSpace(database)) builder.Services.AddSingleton<IVillageStore>(_ => new PostgresVillageStore(database));
builder.Services.AddSingleton<VillageHost>();
// how often pages may change the village
builder.Services.AddSingleton(services => new InterventionLimiter(
    services.GetRequiredService<IConfiguration>().GetSection("Village:Limits").Get<InterventionLimits>() ?? new InterventionLimits(),
    services.GetRequiredService<TimeProvider>()));
builder.Services.AddHostedService(services => services.GetRequiredService<VillageHost>());
builder.Services.AddHostedService(services => services.GetRequiredService<TalkWorker>());

var app = builder.Build();

// only pages of the site itself may connect; a browser always says where a page comes from
var allowed = app.Configuration.GetSection("Village:AllowedOrigins").Get<string[]>() ?? ["http://localhost:3100", "http://localhost:3000"];
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/hub"))
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (!allowed.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }
    await next();
});
app.UseWebSockets();

// unhealthy while the saved village cannot be loaded: it waits for a fix, and is never replaced by a new one
app.MapGet("/health", (VillageHost host) => host.Unloadable
    ? Results.Text("cannot load the saved village", statusCode: StatusCodes.Status503ServiceUnavailable)
    : Results.Text("ok"));
// what runs here, and the village as it stands, for the tools that work on the village; no visitors in it
var commit = app.Configuration["VILLAGE_COMMIT"] is { Length: > 0 } built ? built : "dev";
var releases = VillageSetup.Releases();
app.MapGet("/version", () => Results.Json(new { commit, releases }, Protocol.Json));
app.MapGet("/save", (VillageHost host) =>
    host.LastSave() is { } save ? Results.Json(save, Protocol.Json) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
app.MapHub<VillageHub>("/hub", options => options.Transports = HttpTransportType.WebSockets);

// the flat map, to watch along: on a developer's machine only
if (app.Environment.IsDevelopment())
{
    var village = app.Services.GetRequiredService<VillageHost>();
    app.MapGet("/", () =>
    {
        using var page = typeof(VillageHost).Assembly.GetManifestResourceStream("viewer.html")!;
        using var reader = new StreamReader(page);
        return Results.Content(reader.ReadToEnd(), "text/html; charset=utf-8");
    });
    // while the village is being loaded there is nothing to show or to touch
    var notThere = Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    app.MapGet("/state", () => village.Ready.IsCompleted ? Results.Json(village.State(), Protocol.Json) : notThere);
    app.MapPost("/speed", (SpeedRequest request) => village.SetSpeed(request.Value));
    app.MapPost("/reset", async (ResetRequest request) => village.Ready.IsCompleted ? Results.Ok(await village.Reset(request.Seed)) : notThere);
}

app.Run();

internal sealed record SpeedRequest(int Value);

internal sealed record ResetRequest(ulong Seed);

/// <summary>So the tests can start the server in memory.</summary>
public partial class Program;
