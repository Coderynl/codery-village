using System.Text.RegularExpressions;

namespace Codery.Village.Checks;

/// <summary>One changed path of a pull request: Kind is A (added), M (modified), D (deleted) or R (renamed).</summary>
public record Change(char Kind, string Path);

/// <summary>What Eddie's pull request may change: new releases, the line about them in the README, and his own releases removed.</summary>
public static partial class ReleasePaths
{
    private const string Readme = "data/releases/README.md";

    [GeneratedRegex(@"^data/releases/[a-z0-9-]+\.json$")]
    private static partial Regex Release();

    public static IReadOnlyList<string> Problems(IReadOnlyList<Change> changes, Func<string, string?> addedBy, string bot)
    {
        var problems = new List<string>();
        foreach (var (kind, path) in changes)
        {
            var allowed = kind switch
            {
                'A' => Release().IsMatch(path),
                'M' => path == Readme,
                'D' => Release().IsMatch(path) && addedBy(path) == bot,
                _ => false,
            };
            if (!allowed)
                problems.Add($"{kind} {path}: Eddie may only add a release (data/releases/<id>.json), change data/releases/README.md, or delete a release his own bot added.");
        }
        return problems;
    }
}
