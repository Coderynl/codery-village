namespace Codery.Village.Laws;

/// <summary>What does not fit in a world's data, one line per problem. An empty list is a world the village can live in.</summary>
public static class Problems
{
    public static IReadOnlyList<string> Of(WorldData w)
    {
        var problems = new List<string>();

        var properties = Enum.GetValues<Property>().Select(p => p.ToString()).ToList();
        foreach (var p in properties.Where(p => w.PropertyIds.Count(id => string.Equals(id, p, StringComparison.OrdinalIgnoreCase)) != 1))
            problems.Add($"properties: '{p}' must be listed once.");
        foreach (var id in w.PropertyIds.Where(id => !properties.Contains(id, StringComparer.OrdinalIgnoreCase)))
            problems.Add($"properties: '{id}' is no property of the laws.");

        bool KnownKind(string kind) => w.Kinds.ContainsKey(kind);
        foreach (var s in w.Raw.Where(s => !KnownKind(s.Kind))) problems.Add($"substance '{s.Id}': unknown kind '{s.Kind}'.");
        foreach (var twice in w.Raw.GroupBy(s => s.Id).Where(g => g.Count() > 1)) problems.Add($"substance '{twice.Key}' is listed twice.");

        var processes = w.Processes.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var structures = w.Uses.Where(u => u.Structure is not null).Select(u => u.Structure!).ToHashSet(StringComparer.Ordinal);
        foreach (var p in w.Processes)
        {
            if (p.Needs is { } needs && !structures.Contains(needs)) problems.Add($"process '{p.Id}' needs '{needs}', which no use builds.");
            if (p.MaxInputs < 1) problems.Add($"process '{p.Id}' takes no input.");
        }

        foreach (var law in w.Laws)
        {
            if (!processes.Contains(law.Process)) problems.Add($"law '{law.Id}' names unknown process '{law.Process}'.");
            foreach (var kind in law.When.Select(c => c.Kind).Concat(law.Then.Select(e => e.Of)).Append(law.Becomes).OfType<string>().Where(k => !KnownKind(k)))
                problems.Add($"law '{law.Id}' names unknown kind '{kind}'.");
            if (law.When.Count == 0) problems.Add($"law '{law.Id}' has no condition.");
            if (law.Then.Count == 0 && law.Becomes is null) problems.Add($"law '{law.Id}' changes nothing.");
        }
        foreach (var twice in w.Laws.GroupBy(l => l.Id).Where(g => g.Count() > 1)) problems.Add($"law '{twice.Key}' is listed twice.");

        var raw = w.Raw.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var s in w.Raw.Where(s => w.Deposits.All(d => d.Substance != s.Id))) problems.Add($"substance '{s.Id}' lies nowhere: it has no deposit.");
        foreach (var d in w.Deposits)
        {
            if (!raw.Contains(d.Substance)) problems.Add($"a deposit names '{d.Substance}', which is no raw substance.");
            if (d.Chance is <= 0 or > 1) problems.Add($"the deposit of '{d.Substance}' on {d.Ground} has chance {d.Chance}, outside (0, 1].");
        }

        foreach (var u in w.Uses)
        {
            if (u.Needs.Count == 0) problems.Add($"use '{u.Id}' asks nothing.");
            foreach (var n in u.Needs.Where(n => n.Min > n.Max || n.Weight <= 0 || !Enum.IsDefined(n.Property)))
                problems.Add($"use '{u.Id}': the need for {n.Property} is out of bounds.");
            if (u.Threshold is < 0 or > 1) problems.Add($"use '{u.Id}' has threshold {u.Threshold}, outside 0 to 1.");
        }
        foreach (var twice in w.Uses.GroupBy(u => u.Id).Where(g => g.Count() > 1)) problems.Add($"use '{twice.Key}' is listed twice.");

        foreach (var p in Enum.GetValues<Property>().Where(p => w.Own.Min[p] > w.Own.Max[p])) problems.Add($"own: the bounds of {p} are reversed.");

        var start = w.StartData;
        foreach (var p in start.Processes.Where(p => !processes.Contains(p))) problems.Add($"start knows unknown process '{p}'.");
        var uses = w.Uses.Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var u in start.Uses.Where(u => !uses.Contains(u))) problems.Add($"start knows unknown use '{u}'.");
        foreach (var r in start.Recipes.Where(r => !start.Processes.Contains(r.Process))) problems.Add($"start recipe '{r.Label}' uses '{r.Process}', which the start does not know.");
        foreach (var t in start.Trades)
            foreach (var g in t.Gathers.Where(g => !raw.Contains(g))) problems.Add($"trade '{t.Id}' gathers '{g}', which is no raw substance.");

        // the recipes last: they need the laws to be sound
        if (problems.Count == 0)
        {
            try
            {
                _ = w.Start;
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException)
            {
                problems.Add(e.Message);
            }
        }
        return problems;
    }
}
