using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Time;

namespace Codery.Village.Life;

/// <summary>The village of the new core: its modules, in the order they step, and a fresh start of them.</summary>
public static class LifeVillage
{
    /// <summary>A new village on <paramref name="world"/>, started, its calendar at <paramref name="start"/> and moving 250 ms a tick.</summary>
    public static Engine Start(ulong seed, DataSet world, IslandMap map, DateTimeOffset start)
    {
        var engine = new Engine(Modules(seed), world, map, seed, new SimulatedTime(start));
        engine.Start();
        return engine;
    }

    /// <summary>
    /// Fresh instances of the modules, in the order they step. Living works what Knowing says a person knows; the chronicle,
    /// last, tells what the others did and keeps <paramref name="seed"/> for its title (a loaded chronicle has it from the save).
    /// </summary>
    public static IReadOnlyList<IModule> Modules(ulong seed = 0)
    {
        var knowing = new Knowing.Knowing();
        return [new Folk.Folk(), new Living.Living(knowing.Knows), knowing, new Inventing.Inventing(), new Feelings.Feelings(), new Chronicle.Chronicle(seed)];
    }
}
