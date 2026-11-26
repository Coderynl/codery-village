using Codery.Village.Laws;

namespace Codery.Village.Life.Inventing;

/// <summary>
/// The section "inventing" of the world.
/// <list type="bullet">
/// <item>An adult with openness at least TryOpenness has <paramref name="TriesPerDay"/> goes a day; each time, an adult with openness at least <paramref name="TryOpenness"/> tries something on a village day with chance
/// <paramref name="TryBase"/> × openness/100 × (1 + the pressing weight near their trade / <paramref name="PressingScale"/>).</item>
/// <item>Substances weigh <paramref name="TradeWeight"/> when their kind belongs to the trade, <paramref name="PressingWeight"/> when
/// it <paramref name="Helps"/> a pressing use; a try made before is kept with chance <paramref name="RetryWeight"/>; one in
/// <paramref name="WildChance"/> is wholly random. Two substances go in at one of the <paramref name="Ratios"/>.</item>
/// <item>Everyday work goes astray with <paramref name="AccidentChance"/> per batch.</item>
/// <item>A result is a discovery when it beats the island's best for a felt use by <paramref name="NoticeMargin"/>; a witness
/// notices with <paramref name="WitnessShare"/> of the maker's chance. The finder knows it at <paramref name="FoundLevel"/>.</item>
/// <item>A use pressing at least <paramref name="WallWeight"/> for <paramref name="WallYears"/> with nothing within one step that
/// serves it hits a wall.</item>
/// </list>
/// </summary>
public sealed record InventingRules(
    double TryOpenness,
    double TryBase,
    double PressingScale,
    double TradeWeight,
    double PressingWeight,
    double RetryWeight,
    double WildChance,
    double AccidentChance,
    double NoticeMargin,
    double WitnessShare,
    int FoundLevel,
    double WallWeight,
    double WallYears,
    IReadOnlyList<int[]> Ratios,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Helps,
    int TriesPerDay = 1,
    double UsefulWeight = 1);

/// <summary>The section "names": how a found substance is named, an adjective from its most telling property and its kind.</summary>
public sealed record NameRules(IReadOnlyList<Adjective> Adjectives, Name Own, Name Numbered, IReadOnlyList<string>? Neuter = null);

/// <summary>An adjective for a property, high or low; <see cref="NlNeuter"/> is its Dutch form before a het-word ("dof hout").</summary>
public sealed record Adjective(Property Property, bool High, string Nl, string En, string? NlNeuter = null);
