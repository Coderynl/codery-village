using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Laws;

namespace Codery.Village.Life.Tests;

/// <summary>The real world of the new core, as the test output holds it: every <c>world/*.json</c> as a section, and the map.</summary>
internal static class World
{
    private static readonly Lazy<DataSet> Sections = new(() => Read(Path.Combine(AppContext.BaseDirectory, "world")));

    public static DataSet Data => Sections.Value;

    public static WorldData Laws => WorldData.From(Data);

    public static IslandMap Map => MapText.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "map.txt")));

    /// <summary>The world with some sections replaced, by name, as JSON text.</summary>
    public static DataSet With(IReadOnlyDictionary<string, string> replaced)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "world");
        var sections = Directory.EnumerateFiles(folder, "*.json").ToDictionary(path => Path.GetFileNameWithoutExtension(path), File.ReadAllText);
        foreach (var (name, text) in replaced) sections[name] = text;
        return new DataSet(sections);
    }

    private static DataSet Read(string folder) =>
        new(Directory.EnumerateFiles(folder, "*.json").ToDictionary(path => Path.GetFileNameWithoutExtension(path), File.ReadAllText));
}
