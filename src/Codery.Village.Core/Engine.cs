using System.Text.Json;
using System.Text.Json.Nodes;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;

namespace Codery.Village.Core;

/// <summary>
/// Runs the village: starts the modules, steps them in the order of the list once per tick, carries the
/// events of one tick to the next, keeps the log, and saves and loads it all. A module that throws is
/// stopped and the rest go on; its state stays as it was.
/// </summary>
public sealed class Engine
{
    /// <summary>How far back, in ticks, the log is kept by <see cref="Forget"/>: ten minutes.</summary>
    private const int RecallTicks = 2400;
    /// <summary>Of older lines, at least this many stay.</summary>
    private const int KeepLines = 100;

    private readonly IReadOnlyList<IModule> _modules;
    private readonly ModuleContext[] _contexts;
    private readonly List<IModule> _running;
    private readonly List<(string Module, string Error)> _stopped = [];
    private readonly Clocks _clocks;
    private readonly TimeProvider _time;
    private readonly ulong _seed;
    // the events the modules can publish, by the name of their record, in the order of that name
    private readonly SortedDictionary<string, Type> _events;
    private readonly List<LogEntry> _log = [];
    // parts of a loaded save that no module took up: kept as they were, in every save after
    private readonly List<SavedModule> _dormant = [];
    // modules whose part is dormant: they neither run nor save
    private readonly HashSet<IModule> _asleep = [];
    private IReadOnlyList<VillageEvent> _received = [];
    private List<VillageEvent> _published = [];

    public Engine(IReadOnlyList<IModule> modules, DataSet data, IslandMap map, ulong seed, TimeProvider time)
    {
        var twice = modules.GroupBy(module => module.Name).FirstOrDefault(group => group.Count() > 1);
        if (twice is not null) throw new ArgumentException($"Two modules are called '{twice.Key}'.", nameof(modules));

        _modules = modules;
        _running = [.. modules];
        _time = time;
        _seed = seed;
        _events = EventTypes(modules);
        _clocks = new Clocks(new Calendar(time), new LifeClock(data.Section<LifeSettings>("life").TicksPerLifeYear));
        _contexts = [.. modules.Select(module => new ModuleContext(this, _clocks, map, data, Rng.For(seed, module.Name)))];
    }

    /// <summary>The tick the village is at; 0 before the first step.</summary>
    public long Tick => _clocks.Tick;

    /// <summary>The modules that still run, in the order they step.</summary>
    public IReadOnlyList<IModule> Running => _running.AsReadOnly();

    /// <summary>The modules that threw, with what they threw.</summary>
    public IReadOnlyList<(string Module, string Error)> Stopped => _stopped.AsReadOnly();

    internal IReadOnlyList<VillageEvent> Received => _received;

    /// <summary>Starts a new village: every module gets <see cref="IModule.Start"/>.</summary>
    public void Start() => Run(module => module.Start);

    /// <summary>One tick: the clock moves on, last tick's events arrive, every running module steps.</summary>
    public void Step()
    {
        _clocks.Advance();
        _received = _published.AsReadOnly();
        _published = [];
        Run(module => module.Step);
        if (_time is SimulatedTime simulated) simulated.Advance(TimeSpan.FromMilliseconds(1000 / Clocks.TicksPerSecond));
    }

    private void Run(Func<IModule, Action<ModuleContext>> what)
    {
        for (var i = 0; i < _modules.Count; i++)
        {
            var module = _modules[i];
            if (!_running.Contains(module)) continue;
            try
            {
                what(module)(_contexts[i]);
            }
            catch (Exception e)
            {
                Stop(module, e);
            }
        }
    }

    private void Stop(IModule module, Exception e)
    {
        _running.Remove(module);
        _stopped.Add((module.Name, e.Message));
        Note(new LogEntry(Tick, "module-stopped", module.Name, e.Message));
    }

    internal void Publish(VillageEvent e) => _published.Add(e);

    // the engine stamps the tick, so the log stays in order whatever tick a module wrote
    internal void Note(LogEntry entry) => _log.Add(entry with { Tick = Tick });

    // ── saving and loading ──

    /// <summary>
    /// The village as it stands. A stopped module keeps its part; a running module that throws while saving
    /// is stopped and left out, so it starts afresh when the save is loaded.
    /// </summary>
    public SavedVillage Save()
    {
        var parts = new List<SavedModule>();
        for (var i = 0; i < _modules.Count; i++)
        {
            var module = _modules[i];
            if (_asleep.Contains(module)) continue;
            JsonNode data;
            try
            {
                data = module.Save().DeepClone();
            }
            catch (Exception e)
            {
                if (_running.Contains(module)) Stop(module, e);
                continue;
            }
            parts.Add(new SavedModule(module.Name, module.Version, _contexts[i].Random.State, data));
        }
        parts.AddRange(_dormant.Select(part => part with { Data = part.Data.DeepClone() }));

        return new SavedVillage(
            SavedVillage.CurrentFormat,
            _seed,
            Tick,
            _clocks.Calendar.Now,
            parts,
            [.. _published.Select(e => new SavedEvent(e.GetType().Name, JsonSerializer.SerializeToNode(e, e.GetType(), SaveJson.Options)!))],
            LogStart,
            [.. _log]);
    }

    /// <summary>
    /// Takes up a saved village. The tick and the log come from the save, the calendar from
    /// <paramref name="time"/>. Every module with a part gets <see cref="IModule.Load"/> at the part's version
    /// and its random stream back; a module without a part gets <see cref="IModule.Start"/>. A part that no
    /// module takes up, because there is none or its <c>Load</c> throws, sleeps: it stays as it was in every
    /// save after. An event whose record is not known any more is dropped.
    /// </summary>
    public static Engine Load(SavedVillage saved, IReadOnlyList<IModule> modules, DataSet data, IslandMap map, TimeProvider time)
    {
        if (saved.Format != SavedVillage.CurrentFormat)
            throw new SaveException($"The save has format {saved.Format}; this village reads format {SavedVillage.CurrentFormat}.");

        var engine = new Engine(modules, data, map, saved.Seed, time);
        engine._clocks.SetTick(saved.Tick);
        // the lines as they were, with their own ticks and numbers: not through Note, which stamps the tick
        engine._log.AddRange(saved.Log);
        engine.LogStart = saved.LogStart;
        engine.TakeUp(saved);
        return engine;
    }

    private void TakeUp(SavedVillage saved)
    {
        foreach (var pending in saved.Pending)
        {
            var (e, problem) = Read(pending);
            if (e is null) Note(new LogEntry(Tick, "event-dropped", pending.Type, problem));
            else _published.Add(e);
        }

        var sleeping = new HashSet<string>(saved.Modules.Select(part => part.Name));
        for (var i = 0; i < _modules.Count; i++)
        {
            var module = _modules[i];
            var part = saved.Modules.FirstOrDefault(part => part.Name == module.Name);
            if (part is null)
            {
                // a module that is new to this village
                try
                {
                    module.Start(_contexts[i]);
                }
                catch (Exception e)
                {
                    Stop(module, e);
                }
                continue;
            }

            _contexts[i].Random = new Rng(part.Random);
            try
            {
                module.Load(_contexts[i], part.Data.DeepClone(), part.Version);
                sleeping.Remove(module.Name);
            }
            catch (Exception e)
            {
                _running.Remove(module);
                _asleep.Add(module);
                Note(new LogEntry(Tick, "module-dormant", module.Name, e.Message));
            }
        }

        foreach (var part in saved.Modules.Where(part => sleeping.Contains(part.Name)))
        {
            _dormant.Add(part with { Data = part.Data.DeepClone() });
            if (!_modules.Any(module => module.Name == part.Name)) Note(new LogEntry(Tick, "module-dormant", part.Name));
        }
    }

    private (VillageEvent? Event, string? Problem) Read(SavedEvent saved)
    {
        if (!_events.TryGetValue(saved.Type, out var type)) return (null, null);
        try
        {
            return ((VillageEvent?)saved.Data.Deserialize(type, SaveJson.Options), null);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return (null, e.Message);
        }
    }

    // every event a module can publish lives next to a module: in the assemblies of the modules
    private static SortedDictionary<string, Type> EventTypes(IEnumerable<IModule> modules)
    {
        var types = modules
            .Select(module => module.GetType().Assembly)
            .Distinct()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsSubclassOf(typeof(VillageEvent)) && !type.IsAbstract && !type.ContainsGenericParameters)
            .ToList();
        var twice = types.GroupBy(type => type.Name).OrderBy(group => group.Key, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (twice is not null)
            throw new ArgumentException($"Two events are called '{twice.Key}': {string.Join(", ", twice.Select(type => type.AssemblyQualifiedName))}.", nameof(modules));
        return new SortedDictionary<string, Type>(types.ToDictionary(type => type.Name), StringComparer.Ordinal);
    }

    // ── the log ──

    public IReadOnlyList<LogEntry> Log => _log.AsReadOnly();

    /// <summary>The number of the first line still in <see cref="Log"/>. Every line has a number that keeps counting when old lines are forgotten.</summary>
    public long LogStart { get; private set; }

    /// <summary>The number the next line will get.</summary>
    public long LogEnd => LogStart + _log.Count;

    /// <summary>The lines from a number on, with their numbers, as they are now. Lines that were forgotten are left out.</summary>
    public IReadOnlyList<(long Sequence, LogEntry Entry)> LogFrom(long sequence)
    {
        var from = Math.Max(sequence, LogStart);
        return [.. _log.Skip((int)Math.Min(from - LogStart, _log.Count)).Select((entry, i) => (from + i, entry))];
    }

    /// <summary>Lets go of log lines from before the time it looks back, but always keeps the last hundred.</summary>
    public void Forget()
    {
        var recent = _log.FindIndex(entry => Tick - entry.Tick < RecallTicks);
        var old = Math.Min(recent < 0 ? _log.Count : recent, Math.Max(0, _log.Count - KeepLines));
        _log.RemoveRange(0, old);
        LogStart += old;
    }
}
