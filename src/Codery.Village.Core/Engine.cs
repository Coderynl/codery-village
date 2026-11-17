using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Time;

namespace Codery.Village.Core;

/// <summary>
/// Runs the village: starts the modules, steps them in the order of the list once per tick, carries the
/// events of one tick to the next, and keeps the log. A module that throws is stopped and the rest go on;
/// its state stays as it was.
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
    private readonly List<LogEntry> _log = [];
    private List<VillageEvent> _received = [];
    private List<VillageEvent> _published = [];

    public Engine(IReadOnlyList<IModule> modules, DataSet data, IslandMap map, ulong seed, TimeProvider time)
    {
        var twice = modules.GroupBy(module => module.Name).FirstOrDefault(group => group.Count() > 1);
        if (twice is not null) throw new ArgumentException($"Two modules are called '{twice.Key}'.", nameof(modules));

        _modules = modules;
        _running = [.. modules];
        _time = time;
        _clocks = new Clocks(new Calendar(time), new LifeClock(data.Section<LifeSettings>("life").TicksPerLifeYear));
        _contexts = [.. modules.Select(module => new ModuleContext(this, _clocks, map, data, Rng.For(seed, module.Name)))];
    }

    /// <summary>The tick the village is at; 0 before the first step.</summary>
    public long Tick => _clocks.Tick;

    /// <summary>The modules that still run, in the order they step.</summary>
    public IReadOnlyList<IModule> Running => _running;

    /// <summary>The modules that threw, with what they threw.</summary>
    public IReadOnlyList<(string Module, string Error)> Stopped => _stopped;

    internal IReadOnlyList<VillageEvent> Received => _received;

    /// <summary>Starts a new village: every module gets <see cref="IModule.Start"/>.</summary>
    public void Start() => Run(module => module.Start);

    /// <summary>One tick: the clock moves on, last tick's events arrive, every running module steps.</summary>
    public void Step()
    {
        _clocks.Advance();
        _received = _published;
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
                _running.Remove(module);
                _stopped.Add((module.Name, e.Message));
                Note(new LogEntry(Tick, "module-stopped", module.Name, e.Message));
            }
        }
    }

    internal void Publish(VillageEvent e) => _published.Add(e);

    internal void Note(LogEntry entry) => _log.Add(entry);

    // ── the log ──

    public IReadOnlyList<LogEntry> Log => _log;

    /// <summary>The number of the first line still in <see cref="Log"/>. Every line has a number that keeps counting when old lines are forgotten.</summary>
    public long LogStart { get; private set; }

    /// <summary>The number the next line will get.</summary>
    public long LogEnd => LogStart + _log.Count;

    /// <summary>The lines from a number on, with their numbers. Lines that were forgotten are left out.</summary>
    public IEnumerable<(long Sequence, LogEntry Entry)> LogFrom(long sequence)
    {
        for (var at = Math.Max(sequence, LogStart); at < LogEnd; at++) yield return (at, _log[(int)(at - LogStart)]);
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
