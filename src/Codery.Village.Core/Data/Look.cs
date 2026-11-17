using System.Text.RegularExpressions;

namespace Codery.Village.Core.Data;

/// <summary>
/// How something looks, described in parts from fixed lists. The page builds the model from this; there is
/// no free shape, no text and no symbol to put on it. A look that names a model takes it from the kit.
/// </summary>
public sealed record Look(
    string Form,
    int Floors = 1,
    string? Size = null,
    string? Walls = null,
    string? Roof = null,
    string? Surface = null,
    bool Rails = false,
    int Length = 1,
    string? Cargo = null,
    bool Cabin = false,
    bool Sail = false,
    IReadOnlyList<string>? Models = null,
    IReadOnlyList<string>? Extras = null)
{
    public static readonly IReadOnlySet<string> Forms = new HashSet<string> { "none", "road", "deck", "building", "kit", "boat" };
    public static readonly IReadOnlySet<string> Sizes = new HashSet<string> { "small", "medium", "large" };
    public static readonly IReadOnlySet<string> WallKinds = new HashSet<string> { "wood", "planks", "plaster", "bricks", "stone", "glass", "open" };
    public static readonly IReadOnlySet<string> RoofKinds = new HashSet<string> { "thatch", "gable", "hip", "flat", "dome" };
    public static readonly IReadOnlySet<string> Surfaces = new HashSet<string> { "logs", "planks", "stone" };
    public static readonly IReadOnlySet<string> CargoKinds = new HashSet<string> { "none", "crates", "containers" };
    public static readonly IReadOnlySet<string> ExtraKinds = new HashSet<string> { "door", "chimney", "crates", "flag", "antenna", "awning" };
    public const int MaxFloors = 40;
    public const int MaxLength = 3;

    private static readonly Regex PlainName = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    /// <summary>What is wrong with this look, in sentences that start with the owner's name. Empty when it is sound.</summary>
    public IEnumerable<string> Problems(string owner)
    {
        if (!Forms.Contains(Form)) yield return $"{owner} has the unknown look form '{Form}'.";
        if (Size is not null && !Sizes.Contains(Size)) yield return $"{owner} has the unknown look size '{Size}'.";
        if (Walls is not null && !WallKinds.Contains(Walls)) yield return $"{owner} has unknown walls '{Walls}'.";
        if (Roof is not null && !RoofKinds.Contains(Roof)) yield return $"{owner} has the unknown roof '{Roof}'.";
        if (Surface is not null && !Surfaces.Contains(Surface)) yield return $"{owner} has the unknown surface '{Surface}'.";
        if (Cargo is not null && !CargoKinds.Contains(Cargo)) yield return $"{owner} has unknown cargo '{Cargo}'.";
        if (Floors < 1 || Floors > MaxFloors) yield return $"{owner} has {Floors} floors; a look has 1 to {MaxFloors} floors.";
        if (Length < 1 || Length > MaxLength) yield return $"{owner} has length {Length}; a look is 1 to {MaxLength} tiles long.";
        foreach (var extra in Extras ?? [])
            if (!ExtraKinds.Contains(extra)) yield return $"{owner} has the unknown extra '{extra}'.";
        if (Form == "kit" && (Models is null || Models.Count == 0)) yield return $"{owner} takes its look from the kit but names no model.";
        foreach (var model in Models ?? [])
            if (!PlainName.IsMatch(model)) yield return $"{owner} names the model '{model}'; a model name is letters, digits and underscores.";
    }
}
