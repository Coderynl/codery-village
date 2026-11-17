using Codery.Village.Core.Map;
using Codery.Village.Server.Feed;
using Microsoft.AspNetCore.SignalR;

namespace Codery.Village.Server;

/// <summary>
/// What a page can ask of the village: to be told everything once, and to put a loose thing somewhere else. A move
/// goes through the limits first and then through Building; an unknown id or a tile off the map is refused, never an
/// error. Of a page the hub knows only the id of its connection, and only while that is open.
/// </summary>
public sealed class VillageHub(VillageHost village, InterventionLimiter limiter) : Hub
{
    public JoinMessage Join() => village.Join();

    /// <summary>Puts a bench, a basket or a sapling down on another free land tile of its island.</summary>
    public InterventionResult Move(int thing, int row, int col) =>
        // until the village is loaded there is nothing to change, and that is no try
        village.Ready.IsCompleted
            ? limiter.Try(Context.ConnectionId, () => village.Move(thing, new Cell(row, col)))
            : InterventionResult.NotReady;

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        limiter.Forget(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
