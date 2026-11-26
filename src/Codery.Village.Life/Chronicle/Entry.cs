namespace Codery.Village.Life.Chronicle;

/// <summary>
/// One line of the chronicle: what happened at <see cref="Tick"/>, in life year <see cref="LifeYear"/> (from 1) on
/// <see cref="Date"/>, of a <see cref="Kind"/> such as <c>born</c> or <c>discovered</c>, about <see cref="People"/>, told in
/// both languages.
/// </summary>
public sealed record Entry(long Tick, int LifeYear, DateOnly Date, string Kind, IReadOnlyList<int> People, string Nl, string En);

/// <summary>A life year that is over: its first and last calendar day, and its numbers.</summary>
public sealed record LifeYear(int Year, DateOnly First, DateOnly Last, YearNumbers Numbers);

/// <summary>
/// A life year in numbers: the people living at its end, those born and died in it, what was tried, made, learned and
/// discovered in it, the items known on the island at its end, and the median happiness of the living at its end with the
/// spread between the first and third quarter.
/// </summary>
public sealed record YearNumbers(int People, int Born, int Died, int Tried, int Made, int Learned, int Discoveries, int Known,
    int HappinessMedian, int HappinessSpread);
