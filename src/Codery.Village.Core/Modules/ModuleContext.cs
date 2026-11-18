using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Time;

namespace Codery.Village.Core.Modules;

/// <summary>
/// What a module sees of the village: the clocks, the map, the data, its own random stream, the other
/// running modules, the events of the previous tick and the log. The engine makes one per module.
/// </summary>
public sealed class ModuleContext
{
    private readonly Engine _engine;

    internal ModuleContext(Engine engine, Clocks clocks, IslandMap map, DataSet data, Rng random)
    {
        _engine = engine;
        Clocks = clocks;
        Map = map;
        Data = data;
        Random = random;
    }

    public Clocks Clocks { get; }

    public IslandMap Map { get; }

    public DataSet Data { get; }

    /// <summary>This module's own stream, <c>Rng.For(seed, Name)</c>; it continues from tick to tick, and after a load.</summary>
    public Rng Random { get; internal set; }

    /// <summary>What was published in the previous tick, in the order it was published.</summary>
    public IReadOnlyList<VillageEvent> Received => _engine.Received;

    /// <summary>The first running module that is a <typeparamref name="T"/>; throws when there is none.</summary>
    public T Module<T>() where T : class =>
        Find<T>() ?? throw new InvalidOperationException($"No running module is a {typeof(T).Name}.");

    /// <summary>The first running module that is a <typeparamref name="T"/>, or null.</summary>
    public T? Find<T>() where T : class => _engine.Running.OfType<T>().FirstOrDefault();

    /// <summary>Reaches every module in the next tick.</summary>
    public void Publish(VillageEvent e) => _engine.Publish(e);

    /// <summary>Adds a line to the log; the engine sets its tick to the current one.</summary>
    public void Log(LogEntry entry) => _engine.Note(entry);
}
