using Codery.Village.Core.Map;

namespace Codery.Village.Modules.Contracts;

/// <summary>
/// The stock of an island, as Building asks for it. Work keeps it, in stores: one at every workplace, of what it
/// makes, and one where the island gathers, of what no workplace makes. Until Work runs, nothing can be built.
/// </summary>
public interface IStock
{
    /// <summary>How much of the material the island has in all its stores; 0 for a material it has none of.</summary>
    int Count(int island, string material);

    /// <summary>
    /// Takes the whole cost from the island's stores, from those nearest <paramref name="near"/> first, or nothing
    /// when the island does not hold all of it.
    /// </summary>
    bool TryTake(int island, IReadOnlyDictionary<string, int> cost, Cell near);

    /// <summary>The tile of the store of the island nearest <paramref name="near"/> that has any of the material; null when none has.</summary>
    Cell? SourceOf(int island, string material, Cell near);
}
