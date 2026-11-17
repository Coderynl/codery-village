using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Codery.Village.Core.Data;

/// <summary>
/// The world as data: named sections of JSON, read once at start and not changed after. Each module reads
/// its own section with <see cref="Section{T}"/>; the core does not look inside. A release adds to the lists
/// of the section <c>catalog</c> and changes nothing that is there.
/// </summary>
public sealed partial class DataSet
{
    private const string CatalogName = "catalog";

    private readonly Dictionary<string, JsonElement> _sections;
    private readonly ConcurrentDictionary<(string Name, Type Type), object> _read = new();
    private readonly List<string> _releaseProblems = [];

    /// <summary>
    /// How the data is read: camelCase, enums as camelCase names, and a missing or null field that the record
    /// requires is refused rather than filled with a default.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>A data set from sections given as JSON text, by name.</summary>
    public DataSet(IReadOnlyDictionary<string, string> sections) : this(sections, []) { }

    private DataSet(IReadOnlyDictionary<string, string> sections, IEnumerable<string> extras)
    {
        _sections = sections.ToDictionary(section => section.Key, section => Parse(section.Value));
        var added = extras.Select(release => JsonNode.Parse(release)).ToList();
        if (added.Count > 0) AddReleases(added);
        Names = [.. _sections.Keys.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// A data set from sections given as JSON text, by name, with each release in <paramref name="extras"/>
    /// (JSON text) added to the catalog. The server reads the texts from disk (<c>DataFolder.Load</c>).
    /// </summary>
    public static DataSet FromTexts(IReadOnlyDictionary<string, string> sections, IEnumerable<string> extras) =>
        new(sections, extras);

    /// <summary>The names of the sections, in ordinal order.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>
    /// The section called <paramref name="name"/>, read as <typeparamref name="T"/> with <see cref="JsonOptions"/>.
    /// Read once per type and kept. Throws <see cref="KeyNotFoundException"/> for an unknown section and
    /// <see cref="InvalidDataException"/> for one that does not fit <typeparamref name="T"/>.
    /// </summary>
    public T Section<T>(string name)
    {
        if (!_sections.TryGetValue(name, out var section))
            throw new KeyNotFoundException($"The data has no section '{name}'.");
        return (T)_read.GetOrAdd((name, typeof(T)), _ => Read<T>(name, section)!);
    }

    private static T Read<T>(string name, JsonElement section)
    {
        try
        {
            return section.Deserialize<T>(JsonOptions)
                ?? throw new InvalidDataException($"The data section '{name}' is empty.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"The data section '{name}' cannot be read as {typeof(T).Name}: {e.Message}", e);
        }
    }

    // appends the items of each list in each release to the same list in the catalog; problems wait for Problems()
    private void AddReleases(IReadOnlyList<JsonNode?> releases)
    {
        if (!_sections.TryGetValue(CatalogName, out var section) || JsonNode.Parse(section.GetRawText()) is not JsonObject catalog)
        {
            _releaseProblems.Add("A release adds to the catalog, but there is no catalog to add to.");
            return;
        }
        foreach (var release in releases)
        {
            if (release is not JsonObject lists)
            {
                _releaseProblems.Add("A release is not an object of lists.");
                continue;
            }
            foreach (var (list, items) in lists)
            {
                if (catalog[list] is not JsonArray into || items is not JsonArray adding)
                {
                    _releaseProblems.Add($"A release adds to '{list}', which is not a list in the catalog.");
                    continue;
                }
                foreach (var item in adding) into.Add(item?.DeepClone());
            }
        }
        _sections[CatalogName] = JsonSerializer.SerializeToElement(catalog);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
