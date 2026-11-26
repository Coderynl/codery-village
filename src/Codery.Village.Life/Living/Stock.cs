namespace Codery.Village.Life.Living;

/// <summary>What the island holds in common, in units by substance id. Living adds and takes; everyone may look.</summary>
public sealed class Stock
{
    private readonly SortedDictionary<string, double> _amounts = new(StringComparer.Ordinal);

    /// <summary>The units of a substance in stock; 0 for one there is none of.</summary>
    public double this[string substance] => _amounts.GetValueOrDefault(substance);

    /// <summary>Every substance in stock with its units, by id.</summary>
    public IReadOnlyDictionary<string, double> Amounts => _amounts;

    internal void Add(string substance, double units)
    {
        if (units <= 0) return;
        _amounts[substance] = this[substance] + units;
    }

    /// <summary>Takes at most <paramref name="units"/> and returns what it took.</summary>
    internal double Take(string substance, double units)
    {
        var have = this[substance];
        var taken = Math.Min(have, Math.Max(0, units));
        if (have - taken <= 1e-9) _amounts.Remove(substance);
        else _amounts[substance] = have - taken;
        return taken;
    }

    internal void Clear() => _amounts.Clear();

    internal Dictionary<string, double> Copy() => new(_amounts, StringComparer.Ordinal);

    internal void Restore(IReadOnlyDictionary<string, double> amounts)
    {
        _amounts.Clear();
        foreach (var (id, units) in amounts) _amounts[id] = units;
    }
}
