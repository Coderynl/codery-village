using Codery.Village.Core.Data;

namespace Codery.Village.Server;

/// <summary>Reads the data of the village from disk; the core only gets the texts.</summary>
public static class DataFolder
{
    /// <summary>
    /// Reads every <c>*.json</c> file in <paramref name="folder"/> as a section named after the file, and adds
    /// each release in <paramref name="extras"/> (paths relative to <paramref name="folder"/>) to the catalog.
    /// The map and subfolders are not sections.
    /// </summary>
    public static DataSet Load(string folder, IEnumerable<string> extras)
    {
        var sections = Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly)
            .ToDictionary(path => Path.GetFileNameWithoutExtension(path), File.ReadAllText);
        return DataSet.FromTexts(sections, extras.Select(extra => File.ReadAllText(Path.Combine(folder, extra))));
    }
}
