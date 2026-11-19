using System.Text.Json;
using Codery.Village.Core.Map;

namespace Codery.Village.Core.Data;

public sealed partial class DataSet
{
    /// <summary>
    /// What is wrong with the catalog and the wish list, one sentence each; empty when the data is sound.
    /// Doubled ids, references to materials, roles or wishes that do not exist, ticks and amounts that are not
    /// positive, a blueprint without phases or look, a shape without its anchor, a wish with an empty <c>after</c>, a
    /// recipe that takes from a ground or from a yield <c>economy.nature</c> does not name, and a release that adds to a
    /// list the catalog does not have. The server does not start on data with a problem.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>(_releaseProblems);
        var catalog = TryRead<Catalog>(CatalogName, problems);
        var wishes = TryRead<WishLabels>("wishes", problems);
        if (wishes is not null)
        {
            problems.AddRange(Doubles("The wishes have the wish", wishes.Wishes.Select(wish => wish.Id)));
            // an id after names may come with a later release, so only an empty list is wrong
            problems.AddRange(wishes.Wishes.Where(wish => wish.After is { Count: 0 })
                .Select(wish => $"The wish '{wish.Id}' has an empty 'after'; leave it out or name what it waits for."));
        }
        if (catalog is null) return problems;

        problems.AddRange(Doubles("The catalog has the material", catalog.Materials.Select(material => material.Id)));
        problems.AddRange(Doubles("The catalog has the role", catalog.Roles.Select(role => role.Id)));
        problems.AddRange(Doubles("The catalog has the blueprint", catalog.Blueprints.Select(blueprint => blueprint.Id)));
        problems.AddRange(Doubles("The catalog has the thing", catalog.Things.Select(thing => thing.Id)));

        var materials = catalog.Materials.Select(material => material.Id).ToHashSet();
        var roles = catalog.Roles.Select(role => role.Id).ToHashSet();
        var labels = wishes?.Wishes.Select(wish => wish.Label).ToHashSet();
        var yields = (Names.Contains("economy") ? TryRead<NatureIds>("economy", problems)?.Nature?.Keys : null)?.ToHashSet() ?? [];
        foreach (var blueprint in catalog.Blueprints)
            problems.AddRange(ProblemsOf(blueprint, materials, roles, labels, yields));
        return problems;
    }

    private static IEnumerable<string> ProblemsOf(Blueprint blueprint, IReadOnlySet<string> materials, IReadOnlySet<string> roles, IReadOnlySet<string>? labels,
        IReadOnlySet<string> yields)
    {
        var owner = $"Blueprint '{blueprint.Id}'";
        if (blueprint.Phases.Count == 0) yield return $"{owner} has no phases.";
        foreach (var problem in Doubles($"{owner} has the phase", blueprint.Phases.Select(phase => phase.Id))) yield return problem;
        foreach (var phase in blueprint.Phases)
        {
            var step = $"{owner} phase '{phase.Id}'";
            if (phase.Ticks <= 0) yield return $"{step} takes {phase.Ticks} ticks; a phase takes at least 1.";
            foreach (var problem in Amounts($"{step} costs", phase.Cost, materials)) yield return problem;
        }

        if (blueprint.Recipe is { } recipe)
        {
            if (recipe.Ticks <= 0) yield return $"{owner} makes its recipe in {recipe.Ticks} ticks; a recipe takes at least 1.";
            foreach (var problem in Amounts($"{owner} takes", recipe.In, materials)) yield return problem;
            foreach (var problem in Amounts($"{owner} makes", recipe.Out, materials)) yield return problem;
            if (recipe.Takes is { } takes && IsGround(takes))
                yield return $"{owner} takes from '{takes}', which is a ground; a yield of its own has a name of its own.";
            else if (recipe.Takes is { } other && !yields.Contains(other))
                yield return $"{owner} takes from the yield '{other}', which economy.nature does not name.";
        }

        foreach (var job in blueprint.Jobs ?? [])
        {
            if (!roles.Contains(job.Role)) yield return $"{owner} has a job for the unknown role '{job.Role}'.";
            if (job.Count <= 0) yield return $"{owner} has places for {job.Count} {job.Role}; a job has at least 1.";
        }
        if (blueprint.Homes is <= 0) yield return $"{owner} has {blueprint.Homes} homes; a dwelling has at least 1.";
        if (blueprint.Range is <= 0) yield return $"{owner} has range {blueprint.Range}; a range is at least 1.";

        if (blueprint.Shape is { } shape)
        {
            if (shape.Any(point => point.Length != 2)) yield return $"{owner} has a shape point that is not [row,col].";
            else if (!shape.Any(point => point is [0, 0])) yield return $"{owner} has a shape without [0,0], its anchor.";
        }

        if (blueprint.Serves is { } label && labels is not null && !labels.Contains(label))
            yield return $"{owner} serves '{label}', which no wish names.";

        foreach (var problem in blueprint.Look.Problems(owner)) yield return problem;
    }

    private static bool IsGround(string name) => Enum.GetNames<Ground>().Contains(name, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> Amounts(string what, IReadOnlyDictionary<string, int> amounts, IReadOnlySet<string> materials)
    {
        foreach (var (material, amount) in amounts)
        {
            if (!materials.Contains(material)) yield return $"{what} the unknown material '{material}'.";
            if (amount <= 0) yield return $"{what} {amount} {material}; an amount is at least 1.";
        }
    }

    private static IEnumerable<string> Doubles(string what, IEnumerable<string> ids) =>
        ids.GroupBy(id => id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{what} '{group.Key}' twice.");

    private T? TryRead<T>(string name, List<string> problems) where T : class
    {
        try
        {
            return Section<T>(name);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidDataException)
        {
            problems.Add(e.Message);
            return null;
        }
    }

    // only what the problems need from economy.json: the yields under nature
    private sealed record NatureIds(IReadOnlyDictionary<string, JsonElement>? Nature = null);

    // only what the problems need from wishes.json; the wishes module reads the rest
    private sealed record WishLabels(IReadOnlyList<WishLabel> Wishes);

    private sealed record WishLabel(string Id, string Nl, string En, string Label, IReadOnlyList<string>? After = null);
}
