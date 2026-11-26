namespace Codery.Village.Laws;

/// <summary>One property a use asks for: at least <see cref="Min"/>, and how much it weighs in the score.</summary>
public sealed record Need(Property Property, int Min, double Weight);

/// <summary>A purpose something can serve (to cut, to hold water) and what it asks of a substance.</summary>
public sealed record Use(string Id, IReadOnlyList<Need> Needs)
{
    /// <summary>How well a profile serves, from 0 to 1: 0 when any need is below its minimum, else the weighted mean of the values.</summary>
    public double Score(Profile profile)
    {
        if (Needs.Any(need => profile[need.Property] < need.Min)) return 0;
        var weight = Needs.Sum(need => need.Weight);
        return weight <= 0 ? 0 : Needs.Sum(need => need.Weight * profile[need.Property] / 100.0) / weight;
    }
}
