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

    /// <summary>
    /// The data the live village runs on: the sections in <paramref name="folder"/> and every release in its
    /// <c>releases</c> folder, in ordinal order of the file name. A release only adds to the catalog, so the order
    /// does not change what the village knows.
    /// </summary>
    public static DataSet LoadLive(string folder) =>
        Load(folder, Directory.EnumerateFiles(Path.Combine(folder, "releases"), "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => Path.Combine("releases", Path.GetFileName(path)))
            .Order(StringComparer.Ordinal));
}
