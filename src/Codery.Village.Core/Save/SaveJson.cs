using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codery.Village.Core.Save;

/// <summary>How a save is written as JSON, and read back.</summary>
public static class SaveJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // jsonb sorts the keys of an object its own way
        AllowOutOfOrderMetadataProperties = true,
        // a save with a hole in it is refused, not loaded with nulls
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static string ToJson(SavedVillage saved) => JsonSerializer.Serialize(saved, Options);

    /// <summary>Reads a save. Throws <see cref="SaveException"/> when it is not one.</summary>
    public static SavedVillage FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SavedVillage>(json, Options) ?? throw new SaveException("The save is empty.");
        }
        catch (JsonException exception)
        {
            throw new SaveException($"The save cannot be read: {exception.Message}");
        }
    }
}

/// <summary>A save that cannot be loaded, with the reason in plain words.</summary>
public sealed class SaveException(string message) : Exception(message);
