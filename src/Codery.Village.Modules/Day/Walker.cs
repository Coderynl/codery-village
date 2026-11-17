using Codery.Village.Core.Map;

namespace Codery.Village.Modules.Day;

/// <summary>How a resident gets to his place: over land, a tile at a time.</summary>
internal static class Walker
{
    /// <summary>
    /// The doing of someone who sets off from <paramref name="from"/> now: on his way when there is a route,
    /// at the place at once when <paramref name="walk"/> is false or he is already there.
    /// </summary>
    public static Doing Go(IslandMap map, Activity activity, Cell from, Cell place, bool inside, bool walk, long tick, int ticksPerCell)
    {
        if (walk && from != place && map.FindRoute(from, place) is { Cells.Count: > 0 } route)
            return new Doing(activity, place, from, new Walk(route.Cells, tick, ticksPerCell), Inside: false);
        return new Doing(activity, place, place, null, inside);
    }

    /// <summary>The tile he stands on at <paramref name="tick"/>.</summary>
    public static Cell Position(Doing doing, long tick)
    {
        if (doing.Walk is not { } walk) return doing.At;
        var steps = (tick - walk.StartTick) / walk.TicksPerCell;
        return steps <= 0 ? doing.At : walk.Cells[(int)Math.Min(steps, walk.Cells.Count) - 1];
    }

    /// <summary>He has done the last step.</summary>
    public static bool Arrived(Walk walk, long tick) => tick - walk.StartTick >= (long)walk.Cells.Count * walk.TicksPerCell;
}
