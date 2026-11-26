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
        var engine = new Engine(Modules(), world, map, seed, new SimulatedTime(start));
        engine.Start();
        return engine;
    }

    /// <summary>
    /// Fresh instances of the modules, in the order they step. <paramref name="knower"/> says whether a person knows a recipe
    /// or process; null until Knowing comes: everyone knows their trade's start recipes and every start process.
    /// </summary>
    public static IReadOnlyList<IModule> Modules(Func<int, string, bool>? knower = null) => [new Folk.Folk(), new Living.Living(knower)];
}
