using Codery.Village.Core.Map;

namespace Codery.Village.Modules.Contracts;

/// <summary>
/// The places of the village, as Day asks for them. Building gives them; until it runs, Day finds a home on
/// the resident's island itself and holds school and gatherings in the middle of the island.
/// </summary>
public interface IPlaces
{
    /// <summary>The tile of the resident's home, or null when he has none.</summary>
    Cell? HomeOf(int resident);

    /// <summary>The tile of the school on the island, or null when there is none.</summary>
    Cell? SchoolOn(int island);

    /// <summary>The tile where the island gathers, or null when there is none.</summary>
    Cell? GatheringOn(int island);
}
