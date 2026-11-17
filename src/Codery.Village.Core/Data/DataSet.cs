using System.Text.Json;

namespace Codery.Village.Core.Data;

/// <summary>
/// The world as data: named sections of JSON, read once at start and not changed after. Each module reads
/// its own section with <see cref="Section{T}"/>; the core does not look inside.
/// </summary>
public sealed class DataSet
{
    private readonly Dictionary<string, JsonElement> _sections;

    /// <summary>A data set from sections given as JSON text, by name.</summary>
    public DataSet(IReadOnlyDictionary<string, string> sections)
    {
        _sections = sections.ToDictionary(
            section => section.Key,
            section =>
            {
                using var document = JsonDocument.Parse(section.Value);
                return document.RootElement.Clone();
            });
    }

    public IEnumerable<string> Names => _sections.Keys;

    /// <summary>The section called <paramref name="name"/>, read as <typeparamref name="T"/> with the web defaults (camelCase).</summary>
    public T Section<T>(string name)
    {
        if (!_sections.TryGetValue(name, out var section))
            throw new KeyNotFoundException($"The data has no section '{name}'.");
        return section.Deserialize<T>(JsonSerializerOptions.Web)
            ?? throw new InvalidDataException($"The data section '{name}' is empty.");
    }
}
