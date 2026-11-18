using Codery.Village.Core.Map;

namespace Codery.Village.Modules.Day;

/// <summary>How a resident gets to his place: over land, a tile at a time, and faster over a path.</summary>
internal static class Walker
{
    /// <summary>What a step onto an ordinary tile costs; a step onto a path costs this divided by the path speed.</summary>
    public const int Step = 100;

    /// <summary>
    /// The doing of someone who sets off from <paramref name="from"/> now: on his way when there is a route,
    /// at the place at once when <paramref name="walk"/> is false or he is already there. The route costs least over
    /// the tiles' <paramref name="cost"/>, and its pace is the base pace times the mean cost of its tiles.
    /// </summary>
    public static Doing Go(IslandMap map, Activity activity, Cell from, Cell place, bool inside, bool walk, long tick, int ticksPerCell,
        Func<Cell, int> cost, string? carry = null)
    {
        if (walk && from != place && map.FindRoute(from, place, cost) is { Cells.Count: > 0 } route)
        {
            var pace = Math.Max(1, (int)Math.Round((double)ticksPerCell * route.Cells.Sum(cost) / (route.Cells.Count * Step), MidpointRounding.AwayFromZero));
            return new Doing(activity, place, from, new Walk(route.Cells, tick, pace), Inside: false, carry);
        }
        return new Doing(activity, place, place, null, inside);
    }

    /// <summary>The tile he stands on at <paramref name="tick"/>.</summary>
    public static Cell Position(Doing doing, long tick)
    {
        if (doing.Walk is not { } walk) return doing.At;
        var steps = (tick - walk.StartTick) / walk.TicksPerCell;
        return steps <= 0 ? doing.At : walk.Cells[(int)Math.Min(steps, walk.Cells.Count) - 1];
    }

    /// <summary>The tile he steps onto at <paramref name="tick"/>, or null when he sets no foot on a new tile then.</summary>
    public static Cell? SteppedOn(Walk walk, long tick)
    {
        var elapsed = tick - walk.StartTick;
        if (elapsed <= 0 || elapsed % walk.TicksPerCell != 0) return null;
        var steps = elapsed / walk.TicksPerCell;
        return steps <= walk.Cells.Count ? walk.Cells[(int)steps - 1] : null;
    }

    /// <summary>He has done the last step.</summary>
    public static bool Arrived(Walk walk, long tick) => tick - walk.StartTick >= (long)walk.Cells.Count * walk.TicksPerCell;
}
