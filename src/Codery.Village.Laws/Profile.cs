using System.Collections.Immutable;

namespace Codery.Village.Laws;

/// <summary>
/// What a substance is like, each on a scale of 0 to 100. <see cref="Density"/> 50 is water: below it floats.
/// <see cref="MeltingPoint"/> is degrees Celsius divided by 20.
/// </summary>
public enum Property { Hardness, Toughness, Flexibility, Density, Flammability, MeltingPoint, Watertightness, Nutrition, Durability, Conductivity }

/// <summary>A value for every <see cref="Property"/>, in the order of the enum. Two profiles are equal when their values are.</summary>
public sealed record Profile
{
    /// <summary>The number of properties.</summary>
    public static readonly int Count = Enum.GetValues<Property>().Length;

    /// <summary>Every property at 0.</summary>
    public static Profile Zero { get; } = new([.. new int[Count]]);

    public Profile(ImmutableArray<int> values)
    {
        if (values.Length != Count) throw new ArgumentException($"A profile has {Count} values, not {values.Length}.", nameof(values));
        Values = values;
    }

    public ImmutableArray<int> Values { get; }

    public int this[Property property] => Values[(int)property];

    /// <summary>The same profile with one property changed, kept within 0 and 100.</summary>
    public Profile With(Property property, int value) => new(Values.SetItem((int)property, Math.Clamp(value, 0, 100)));

    /// <summary>Every value to the nearest step of 5: what tells one substance from another.</summary>
    public Profile Rounded() => new([.. Values.Select(value => (int)Math.Round(value / 5.0, MidpointRounding.AwayFromZero) * 5)]);

    /// <summary>The mean of the profiles, each weighing its parts, rounded half away from zero.</summary>
    public static Profile Mix(IReadOnlyList<(Profile Profile, int Parts)> parts)
    {
        if (parts.Count == 0) throw new ArgumentException("Nothing to mix.", nameof(parts));
        if (parts.Any(part => part.Parts <= 0)) throw new ArgumentException("Every part counts at least once.", nameof(parts));
        var total = parts.Sum(part => part.Parts);
        return new([.. Enumerable.Range(0, Count).Select(i =>
            (int)Math.Round(parts.Sum(part => (decimal)part.Profile.Values[i] * part.Parts) / total, MidpointRounding.AwayFromZero))]);
    }

    public bool Equals(Profile? other) => other is not null && Values.SequenceEqual(other.Values);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in Values) hash.Add(value);
        return hash.ToHashCode();
    }

    public override string ToString() => string.Join(" ", Enum.GetValues<Property>().Select(p => $"{p}={this[p]}"));
}
