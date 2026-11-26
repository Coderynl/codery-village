using System.Text;

namespace Codery.Village.Laws;

/// <summary>
/// Something in the world with a <see cref="Kind"/> (clay, metal, ceramic) and a profile. A raw one lies in the world
/// and keeps its data id; a made one is known by its kind and rounded profile, so the same result by two routes is one
/// substance.
/// </summary>
public sealed record Substance(string Id, string Kind, Profile Profile, bool Raw)
{
    /// <summary>The id of a made substance: its kind and eight hex digits of FNV-1a over the kind and the rounded profile.</summary>
    public static string IdFor(string kind, Profile profile)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes($"{kind}|{string.Join(',', profile.Rounded().Values)}"))
        {
            hash ^= b;
            hash *= 16777619u;
        }
        return $"{kind}-{hash:x8}";
    }
}

/// <summary>An amount of a substance in a mix, in parts.</summary>
public sealed record Portion(Substance Substance, int Parts);
