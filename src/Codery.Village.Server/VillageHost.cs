using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Save;
using Codery.Village.Server.Feed;
using Codery.Village.Server.Store;
using Codery.Village.Server.Talk;
using Microsoft.AspNetCore.SignalR;
using BuildingModule = Codery.Village.Modules.Building.Building;
using TalkModule = Codery.Village.Modules.Talk.Talk;
using WorkModule = Codery.Village.Modules.Work.Work;

namespace Codery.Village.Server;

/// <summary>
/// The one village. It runs whether anybody watches or not, four ticks a second, and after every tick tells every
/// page that is connected what changed. A lock keeps the clock and the requests apart; under it, after each step,
/// the conversations the village asks for go to the talk worker and the written ones come back.
///
/// With a store the village is loaded when the server starts, saved every so often and when it stops, and after a
/// restart it goes on where it was, with the calendar of the clock. The lock is never held while the store is busy:
/// the village is copied under the lock and written outside it. What the conversations of today cost is kept with it.
/// </summary>
public sealed class VillageHost(
    IHubContext<VillageHub> hub,
    IConfiguration configuration,
    ILogger<VillageHost> logger,
    DataSet data,
    IslandMap map,
    TimeProvider time,
    TalkWorker talk,
    TalkBudget budget,
    IHostEnvironment environment,
    IVillageStore? store = null) : BackgroundService
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan LastRetry = TimeSpan.FromSeconds(60);
    private const int MaxSpeed = 64;
    // how often a village without a store lets go of its old log lines: once a simulated minute
    private const int ForgetEvery = 60 * VillageFeed.TicksPerSecond;
    // what the flat map shows of what was said
    private const int RecentSaid = 8;

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _saving = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ulong _seed = configuration.GetValue<ulong?>("Village:Seed") ?? 1;
    private readonly TimeSpan _saveEvery = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue<double?>("Village:SaveSeconds") ?? 60));
    private readonly List<Said> _said = [];
    private int _speed = Math.Clamp(configuration.GetValue<int?>("Village:Speed") ?? 1, 0, MaxSpeed);
    // how old a new village begins: on a developer's machine only
    private TimeSpan? _advance;
    private Engine? _engine;
    private VillageFeed? _feed;
    private Hand? _hand;

    // the village in the store, and how it stood at the last save
    private long? _village;
    private (long? Village, long Tick, long LogEnd)? _lastSaved;

    /// <summary>Done once there is a village: at once without a store, once it is loaded with one.</summary>
    internal Task Ready => _ready.Task;

    /// <summary>What a tick becomes for the pages: the feed's next message, with what was said about a visitor's hand. A test can make it fail.</summary>
    internal Func<VillageFeed, Engine, IReadOnlyList<Said>, TickMessage> PassOn { get; set; } = (feed, engine, said) => feed.Next(engine, said);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // a release that could make the village stall does not get in: with a problem in the data the server does not start
        var problems = data.Problems();
        if (problems.Count > 0)
        {
            foreach (var problem in problems)
                logger.LogCritical("The data with the releases of Village:Extras has a problem: {Problem}", problem);
            throw new InvalidOperationException(
                $"The village does not start: the data with the releases of Village:Extras has {problems.Count} problem(s). {string.Join(" ", problems)}");
        }
        _advance = Advance();
        _hand = new Hand(data);
        if (store is null) Use(First(), village: null);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (store is not null) await Resume(store, stoppingToken);
        await Task.WhenAll(RunClock(stoppingToken), KeepSaving(stoppingToken));
    }

    /// <summary>Stops the clock, and saves the village once more as it stands, so a restart loses nothing.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await Save(cancellationToken);
    }

    private async Task RunClock(CancellationToken stoppingToken)
    {
        using var clock = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / VillageFeed.TicksPerSecond));
        while (await clock.WaitForNextTickAsync(stoppingToken))
        {
            TickMessage? message = null;
            lock (_gate)
            {
                if (_engine is null || _feed is null) continue;
                // what goes wrong in one tick is written down; the clock runs on, and the next tick tries again
                try
                {
                    for (var i = 0; i < _speed; i++)
                    {
                        _engine.Step();
                        Converse(_engine);
                        // without a store nothing keeps the old log lines; with one, a save lets go of them
                        if (store is null && _engine.Tick % ForgetEvery == 0) _engine.Forget();
                    }
                    message = PassOn(_feed, _engine, _hand!.Due(_engine));
                    if (message.Said is { } said) Remember(said);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Tick {Tick} went wrong ({Reason}); the village runs on.", _engine.Tick, exception.Message);
                }
            }
            if (message is not null) await hub.Clients.All.SendAsync("tick", message, stoppingToken);
        }
    }

    // the requests of this tick go to the worker; what it wrote goes to the module, which shows it in the next tick
    private void Converse(Engine engine)
    {
        if (engine.Running.OfType<TalkModule>().FirstOrDefault() is not { } conversations) return;
        foreach (var request in conversations.TakeRequests()) talk.Offer(request);
        foreach (var (id, lines) in talk.TakeAnswers()) conversations.Answer(id, lines);
    }

    private void Remember(IEnumerable<Said> said)
    {
        _said.AddRange(said);
        if (_said.Count > RecentSaid) _said.RemoveRange(0, _said.Count - RecentSaid);
    }

    private async Task KeepSaving(CancellationToken stoppingToken)
    {
        if (store is null) return;
        using var timer = new PeriodicTimer(_saveEvery);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await Save(stoppingToken);
    }

    // ── keeping the village ──

    private Engine Begin(ulong seed) => VillageSetup.Begin(data, map, seed, time);

    /// <summary>
    /// On a developer's machine Village:Advance (3d, 12h, 3.00:00:00) begins a new village that much older, to see
    /// it lived in. A value that is not a duration keeps the server from starting; elsewhere the setting is ignored.
    /// </summary>
    private TimeSpan? Advance()
    {
        if (!environment.IsDevelopment() || configuration["Village:Advance"] is not { Length: > 0 } text) return null;
        try
        {
            return VillageSetup.Duration(text);
        }
        catch (FormatException exception)
        {
            logger.LogCritical("Village:Advance is not a duration: {Reason}", exception.Message);
            throw new InvalidOperationException($"The village does not start: Village:Advance {exception.Message}", exception);
        }
    }

    /// <summary>The village a server begins with: new, or as old as Village:Advance says, its days run before it serves and ending now.</summary>
    private Engine First()
    {
        if (_advance is not { } age) return Begin(_seed);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var engine = VillageSetup.Aged(data, map, _seed, time, age);
        logger.LogInformation("Village:Advance: the village begins {Age} old, at tick {Tick}; that took {Seconds:0.0} s.", age, engine.Tick, watch.Elapsed.TotalSeconds);
        return engine;
    }

    /// <summary>Makes a village the one that runs.</summary>
    private void Use(Engine engine, long? village)
    {
        lock (_gate)
        {
            _engine = engine;
            _feed = new VillageFeed(data, map, time);
            _hand!.Clear();
            _said.Clear();
            if (engine.Running.OfType<TalkModule>().FirstOrDefault() is { } conversations)
                Remember(conversations.Recent.Select(conversation => new Said(conversation.A, conversation.B, conversation.Lines.Nl, conversation.Lines.En)));
            _village = village;
            // a village that was just loaded or begun stands as it was saved
            _lastSaved = (village, engine.Tick, engine.LogEnd);
        }
        _ready.TrySetResult();
    }

    /// <summary>Loads the last village, or begins a new one. While the store cannot be reached it tries again, a little later each time.</summary>
    private async Task Resume(IVillageStore store, CancellationToken stoppingToken)
    {
        for (var wait = FirstRetry; ; wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, LastRetry.Ticks)))
        {
            try
            {
                await store.Prepare(stoppingToken);
                var (village, engine) = await LoadOrBegin(store, stoppingToken);
                var (day, _) = budget.Today;
                budget.Restore(day, await store.TalkSpent(day, stoppingToken));
                Use(engine, village);
                return;
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("The village store cannot be reached ({Reason}); trying again in {Seconds} s.", exception.Message, wait.TotalSeconds);
            }
            await Task.Delay(wait, stoppingToken);
        }
    }

    /// <summary>
    /// The last village goes on, unless its seed is not the one in the settings or its save cannot be read or loaded.
    /// Then a new one begins, next to it: the old one is left as it is.
    /// </summary>
    private async Task<(long Village, Engine Engine)> LoadOrBegin(IVillageStore store, CancellationToken cancellation)
    {
        StoredVillage? latest = null;
        try
        {
            latest = await store.Latest(cancellation);
        }
        catch (SaveException exception)
        {
            logger.LogWarning("The last village cannot be read: {Reason} A new village begins; the old one stays as it is.", exception.Message);
        }

        if (latest is not null && latest.Seed != _seed)
            logger.LogInformation("Village {Village} has seed {Seed}; the settings say {Wanted}, so a new village begins.", latest.Id, latest.Seed, _seed);
        else if (latest is { Save: { } save } && Restore(latest.Id, save) is { } restored)
        {
            logger.LogInformation("Village {Village} goes on at tick {Tick}.", latest.Id, restored.Tick);
            if (_advance is not null)
                logger.LogInformation("Village:Advance is ignored: village {Village} is saved, and a saved village is never replaced.", latest.Id);
            return (latest.Id, restored);
        }

        var engine = First();
        var village = await store.Begin(_seed, engine.Save(), cancellation);
        logger.LogInformation("Village {Village} begins, with seed {Seed}.", village, _seed);
        return (village, engine);
    }

    private Engine? Restore(long village, SavedVillage save)
    {
        try
        {
            return Engine.Load(save, VillageSetup.Modules(), data, map, time);
        }
        catch (SaveException exception)
        {
            logger.LogWarning("Village {Village} cannot be loaded: {Reason} A new village begins; the old one stays as it is.", village, exception.Message);
            return null;
        }
    }

    /// <summary>
    /// Writes the village to the store, one save at a time: copied under the lock, written outside it, with what the
    /// conversations of today cost. False when that did not work; the log lines then go along the next time.
    /// </summary>
    internal async Task<bool> Save(CancellationToken cancellation)
    {
        if (store is null) return false;
        await _saving.WaitAsync(cancellation);
        try
        {
            long village;
            SavedVillage? saved = null;
            lock (_gate)
            {
                if (_engine is null || _village is null) return false;
                village = _village.Value;
                // nothing happened since the last save: no need for another
                if (_lastSaved != (_village, _engine.Tick, _engine.LogEnd)) saved = _engine.Save();
            }

            // the save is a copy: it goes to the store while the village runs on
            if (saved is not null) await store.Save(village, saved, cancellation);
            var (day, spent) = budget.Today;
            await store.SaveTalkSpent(day, spent, cancellation);
            if (saved is null) return true;
            lock (_gate)
            {
                _lastSaved = (village, saved.Tick, saved.LogStart + saved.Log.Count);
                // the lines are in the store now; the village only keeps the recent ones
                if (_village == village) _engine!.Forget();
            }
            return true;
        }
        catch (Exception exception) when (!cancellation.IsCancellationRequested)
        {
            logger.LogWarning("The village could not be saved ({Reason}); its log goes along the next time.", exception.Message);
            return false;
        }
        finally
        {
            _saving.Release();
        }
    }

    /// <summary>For the tests: a look at the village, under the lock.</summary>
    internal T Inspect<T>(Func<Engine, T> look)
    {
        lock (_gate) return look(_engine ?? throw new InvalidOperationException("The village is not there yet."));
    }

    /// <summary>The whole village, for a page that has just connected.</summary>
    public JoinMessage Join()
    {
        lock (_gate)
        {
            // until the village is loaded, a page shows the still village it has of its own
            if (_engine is null || _feed is null) throw new HubException("The village is not there yet.");
            return _feed.Join(_engine);
        }
    }

    /// <summary>
    /// Puts a loose thing on another tile, under the lock, through Building. The hub calls this after its limits.
    /// When it is moved, the nearest resident who is awake will say something about it.
    /// </summary>
    public InterventionResult Move(int thing, Cell to)
    {
        lock (_gate)
        {
            var engine = _engine ?? throw new InvalidOperationException("The village is not there yet.");
            if (engine.Running.OfType<BuildingModule>().FirstOrDefault() is not { } building)
                return InterventionResult.Of(Codery.Village.Modules.Building.Refusal.NotFound);
            var result = InterventionResult.Of(building.Move(thing, to));
            if (result.Ok) _hand!.Moved(engine, thing);
            return result;
        }
    }

    // ── for the flat map, on a developer's machine only ──

    public object SetSpeed(int value)
    {
        lock (_gate)
        {
            _speed = Math.Clamp(value, 0, MaxSpeed);
            return new { speed = _speed };
        }
    }

    /// <summary>Begins again. With a store that is a new village there, next to the old one.</summary>
    public async Task<object> Reset(ulong seed)
    {
        seed = seed == 0 ? 1 : seed;
        var engine = Begin(seed);
        if (store is null)
        {
            Use(engine, village: null);
            return new { ok = true };
        }

        // not while a save of the old village is under way
        await _saving.WaitAsync();
        try
        {
            Use(engine, await store.Begin(seed, engine.Save(), CancellationToken.None));
            return new { ok = true };
        }
        catch (Exception exception)
        {
            logger.LogWarning("No new village could be begun ({Reason}); the old one runs on.", exception.Message);
            return new { ok = false, refusal = "the store cannot be reached" };
        }
        finally
        {
            _saving.Release();
        }
    }

    /// <summary>The village as the flat map draws it: the join, the speed, the last things said and the stock of each island.</summary>
    public object State()
    {
        lock (_gate)
        {
            var engine = _engine ?? throw new InvalidOperationException("The village is not there yet.");
            var work = engine.Running.OfType<WorkModule>().FirstOrDefault();
            return new
            {
                speed = _speed,
                join = _feed!.Join(engine),
                said = _said.AsEnumerable().Reverse().Select(said => new { a = said.A, b = said.B, nl = said.Nl }),
                felled = work is null ? [] : work.Felled.Select(cell => cell.Row * map.Cols + cell.Col).Order().ToArray(),
                stock = map.Islands.Where(island => !island.Eddie).Select(island => new
                {
                    island = island.Id,
                    materials = work?.Stock(island.Id) ?? new Dictionary<string, int>(),
                }),
            };
        }
    }
}
