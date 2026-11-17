using System.Text.Json.Serialization;

namespace Codery.Village.Modules.People;

/// <summary>Who someone is, in eight values from 0 up to and including 100. Drawn once and never changed.</summary>
public readonly record struct Personality(
    int Openness,
    int Conscientiousness,
    int Extraversion,
    int Agreeableness,
    int Sensitivity,
    int Status,
    int Comfort,
    int Thrift)
{
    public const int Low = 30;
    public const int High = 70;

    /// <summary>The names of the traits, in the order of the record, as the data names them.</summary>
    public static IReadOnlyList<string> Traits { get; } =
        ["openness", "conscientiousness", "extraversion", "agreeableness", "sensitivity", "status", "comfort", "thrift"];

    /// <summary>The values in the order of <see cref="Traits"/>.</summary>
    [JsonIgnore]
    public IReadOnlyList<int> Values => [Openness, Conscientiousness, Extraversion, Agreeableness, Sensitivity, Status, Comfort, Thrift];

    /// <summary>The traits that stand out: below 30 (<c>High</c> false) or above 70 (<c>High</c> true), in the order of the record.</summary>
    public IEnumerable<(string Trait, bool High)> Notable()
    {
        var values = Values;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] < Low) yield return (Traits[i], false);
            else if (values[i] > High) yield return (Traits[i], true);
        }
    }

    /// <summary>A personality from eight values in the order of <see cref="Traits"/>.</summary>
    public static Personality From(IReadOnlyList<int> values) =>
        new(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7]);
}
