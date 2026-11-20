using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Codery.Village.Tests;

/// <summary>
/// The releases in <c>data/releases</c>: the steps from the prehistoric base to the future. The live village loads
/// them all; a release may use what another adds, so on its own it loads on top of the base and the releases it
/// depends on. The README lists them.
/// </summary>
public static partial class Ladder
{
    public static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "data", "releases");

    /// <summary>The release files the numbered list in the README names, as paths relative to the data folder (<c>releases/cafe.json</c>).</summary>
    public static IReadOnlyList<string> InOrder() =>
        [.. FileName().Matches(File.ReadAllText(Path.Combine(Folder, "README.md"))).Select(match => "releases/" + match.Groups[1].Value)];

    /// <summary>Every release file in the folder, as a path relative to the data folder.</summary>
    public static IReadOnlyList<string> Files() =>
        [.. Directory.EnumerateFiles(Folder, "*.json").Select(path => "releases/" + Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    /// <summary>The texts of the whole ladder.</summary>
    public static IReadOnlyList<string> All() => [.. Files().Select(Text)];

    public static string Text(string release) => File.ReadAllText(Path.Combine(Folder, "..", release));

    /// <summary>
    /// The release and what it needs first: the releases that add the materials and roles it uses and the base
    /// does not have, and theirs, in an order that loads.
    /// </summary>
    public static IReadOnlyList<string> With(string release)
    {
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, "..", "catalog.json")))!;
        var known = Ids(catalog, "materials").Concat(Ids(catalog, "roles")).ToHashSet();
        var adds = Files().ToDictionary(file => file, file => Ids(JsonNode.Parse(Text(file))!, "materials").Concat(Ids(JsonNode.Parse(Text(file))!, "roles")).ToHashSet());
        var order = new List<string>();
        void Visit(string file, HashSet<string> path)
        {
            if (order.Contains(file)) return;
            if (!path.Add(file)) throw new InvalidOperationException($"The releases depend on each other in a circle at {file}.");
            foreach (var used in Uses(JsonNode.Parse(Text(file))!).Where(id => !known.Contains(id) && !adds[file].Contains(id)))
            {
                var by = adds.Where(item => item.Value.Contains(used)).Select(item => item.Key).ToList();
                Assert.True(by.Count == 1, $"{file} uses '{used}', which {by.Count} releases add.");
                Visit(by[0], path);
            }
            order.Add(file);
        }
        Visit(release, []);
        return order;
    }

    private static IEnumerable<string> Ids(JsonNode list, string name) =>
        list[name]?.AsArray().Select(item => (string)item!["id"]!) ?? [];

    // the materials and roles a release refers to
    private static IEnumerable<string> Uses(JsonNode release)
    {
        foreach (var blueprint in release["blueprints"]?.AsArray() ?? [])
        {
            foreach (var phase in blueprint!["phases"]!.AsArray())
                foreach (var (material, _) in phase!["cost"]!.AsObject()) yield return material;
            if (blueprint["recipe"] is { } recipe)
                foreach (var side in new[] { "in", "out" })
                    foreach (var (material, _) in recipe[side]!.AsObject()) yield return material;
            foreach (var job in blueprint["jobs"]?.AsArray() ?? []) yield return (string)job!["role"]!;
        }
    }

    // a numbered line of the README that starts with a release file
    [GeneratedRegex(@"^\d+\.\s+`([a-z0-9-]+\.json)`", RegexOptions.Multiline)]
    private static partial Regex FileName();
}
