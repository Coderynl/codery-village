using Codery.Village.Core.Map;

namespace Codery.Village.Modules.Work;

/// <summary>
/// The stores of one island: one per workplace (by the id of its structure) and one where the island gathers
/// (<see cref="Gathering"/>), each with what it holds. The island has the sum of them; what is taken is taken from
/// the stores nearest the place it is taken for first. Where a store lies, <c>placeOf</c> says.
/// </summary>
public sealed class Stores(Func<int, Cell> placeOf)
{
    /// <summary>The store where the island gathers.</summary>
    public const int Gathering = -1;

    private readonly SortedDictionary<int, SortedDictionary<string, int>> _stores = [];
    private readonly SortedDictionary<string, int> _total = new(StringComparer.Ordinal);

    /// <summary>What the island holds, by material, in all its stores.</summary>
    public IReadOnlyDictionary<string, int> Total => _total;

    /// <summary>The stores that hold something, by id, with what they hold.</summary>
    public IEnumerable<(int Store, IReadOnlyDictionary<string, int> Stock)> All =>
        _stores.Select(store => (store.Key, (IReadOnlyDictionary<string, int>)store.Value));

    /// <summary>What the store holds; nothing for a store that holds nothing.</summary>
    public IReadOnlyDictionary<string, int> At(int store) =>
        _stores.TryGetValue(store, out var stock) ? stock : new SortedDictionary<string, int>(StringComparer.Ordinal);

    public int Count(string material) => _total.GetValueOrDefault(material);

    /// <summary>Puts <paramref name="amount"/> of the material in the store; a negative amount takes it out.</summary>
    public void Add(int store, string material, int amount)
    {
        if (amount == 0) return;
        if (!_stores.TryGetValue(store, out var stock)) _stores[store] = stock = new SortedDictionary<string, int>(StringComparer.Ordinal);
        Change(stock, material, amount);
        Change(_total, material, amount);
        if (stock.Count == 0) _stores.Remove(store);
    }

    /// <summary>Takes the whole cost, from the stores nearest <paramref name="near"/> first; nothing when the island does not hold all of it.</summary>
    public bool Take(IReadOnlyDictionary<string, int> cost, Cell near)
    {
        if (cost.Any(item => item.Value > Count(item.Key))) return false;
        foreach (var (material, amount) in cost.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var left = amount;
            foreach (var store in Nearest(material, near))
            {
                if (left == 0) break;
                var taken = Math.Min(left, _stores[store][material]);
                Add(store, material, -taken);
                left -= taken;
            }
        }
        return true;
    }

    /// <summary>The tile of the store nearest <paramref name="near"/> that has any of the material; null when none has.</summary>
    public Cell? SourceOf(string material, Cell near) =>
        Nearest(material, near).Select(store => (Cell?)placeOf(store)).FirstOrDefault();

    // the stores that have any of the material, the nearest first; of equal distance the lowest id
    private List<int> Nearest(string material, Cell near) =>
        [.. _stores.Where(store => store.Value.ContainsKey(material))
            .Select(store => (Store: store.Key, Distance: placeOf(store.Key).DistanceTo(near)))
            .OrderBy(store => store.Distance).ThenBy(store => store.Store)
            .Select(store => store.Store)];

    private static void Change(SortedDictionary<string, int> stock, string material, int amount)
    {
        var count = stock.GetValueOrDefault(material) + amount;
        if (count == 0) stock.Remove(material);
        else stock[material] = count;
    }
}
