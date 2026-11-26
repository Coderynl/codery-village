namespace Codery.Village.Laws;

/// <summary>
/// One property a use asks for: between <see cref="Min"/> and <see cref="Max"/>, and how much it weighs in the score. A
/// <see cref="Low"/> need scores the lower value better (a hull wants density below water).
/// </summary>
public sealed record Need(Property Property, int Min, double Weight, int Max = 100, bool Low = false);

/// <summary>
/// A purpose something can serve (to cut, to hold water) and what it asks of a substance. A <see cref="Household"/> use
/// is one every household wants something for; a use that names a <see cref="Structure"/> is how that structure gets
/// built, from a substance scoring at least <see cref="Threshold"/>.
/// </summary>
public sealed record Use(string Id, IReadOnlyList<Need> Needs, bool Household = false, string? Structure = null, double Threshold = 0,
    string Nl = "", string En = "")
{
    /// <summary>How well a profile serves, from 0 to 1: 0 when any need is outside its bounds, else the weighted mean of the values.</summary>
    public double Score(Profile profile)
    {
        if (Needs.Any(need => profile[need.Property] < need.Min || profile[need.Property] > need.Max)) return 0;
        var weight = Needs.Sum(need => need.Weight);
        return weight <= 0 ? 0 : Needs.Sum(need => need.Weight * (need.Low ? 100 - profile[need.Property] : profile[need.Property]) / 100.0) / weight;
    }
}
