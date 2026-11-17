namespace Codery.Village.Modules.Contracts;

/// <summary>The stock of an island, as Building asks for it. Work keeps it; until it runs, nothing can be built.</summary>
public interface IStock
{
    /// <summary>How much of the material the island has; 0 for a material it has none of.</summary>
    int Count(int island, string material);

    /// <summary>Takes the whole cost from the island's stock, or nothing when it does not hold all of it.</summary>
    bool TryTake(int island, IReadOnlyDictionary<string, int> cost);
}
