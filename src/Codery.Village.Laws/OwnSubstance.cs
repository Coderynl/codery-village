using Codery.Village.Core;

namespace Codery.Village.Laws;

/// <summary>Each island's substance found nowhere else: drawn from the seed within the world's bounds, unlike every other.</summary>
public static class OwnSubstance
{
    private const int Tries = 10_000;

    /// <summary>How far apart two profiles are: the sum of the differences, divided by 10.</summary>
    public static double Distance(Profile a, Profile b) => Enumerable.Range(0, Profile.Count).Sum(i => Math.Abs(a.Values[i] - b.Values[i])) / 10.0;

    /// <summary>
    /// A raw substance of kind <c>own-{island}</c>, every property drawn within the bounds, at least
    /// <see cref="OwnBounds.MinDistance"/> from every one of <paramref name="others"/>.
    /// </summary>
    public static Substance Draw(Rng random, OwnBounds bounds, int island, IEnumerable<Substance> others)
    {
        var them = others.Select(s => s.Profile).ToList();
        for (var i = 0; i < Tries; i++)
        {
            var profile = new Profile([.. Enum.GetValues<Property>().Select(p => bounds.Min[p] + random.Next(bounds.Max[p] - bounds.Min[p] + 1))]);
            if (them.All(other => Distance(other, profile) >= bounds.MinDistance))
                return new Substance($"own-{island}", $"own-{island}", profile, Raw: true);
        }
        throw new InvalidOperationException($"No substance of its own fits within the bounds for island {island}.");
    }
}
