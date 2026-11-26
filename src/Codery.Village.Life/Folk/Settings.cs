using Codery.Village.Modules.People;

namespace Codery.Village.Life.Folk;

/// <summary>The section "people" of the world: who can be born, and who is there at the start.</summary>
public sealed record FolkPeople(NamesBySex Names, IReadOnlyList<FolkInterest> Interests, PersonalitySpread Personality, FolkStart Start);

public sealed record FolkInterest(string Id, string Nl, string En);

/// <summary>Between <paramref name="Min"/> and <paramref name="Max"/> people on West, in pairs with children.</summary>
public sealed record FolkStart(int Min, int Max, IReadOnlyList<ChildCount> Children, IReadOnlyList<AgeBand> PairAges, int PartnerGap);

public sealed record ChildCount(int Count, int Weight);

/// <summary>
/// The section "life" of the world: the rules of a life, in life years and chances per life year. <paramref name="Accident"/> is
/// the chance a life year that an adult with a trade dies in an accident.
/// </summary>
public sealed record LifeRules(Stages Stages, PairingRules Pairing, BirthRules Births, IReadOnlyList<AgeChance> Dying, TradeRules Trades, double Accident = 0);

/// <summary>The ages a stage begins at.</summary>
public sealed record Stages(int Pupil, int Adult, int Old);

/// <summary>
/// A single adult pairs with <paramref name="Chance"/> a life year, times 0.5 + extraversion/100, times the share of
/// <paramref name="Crowd"/> that the singles they could pair with make up, at most <paramref name="Max"/> (below 1, so
/// extraversion keeps mattering). Partners differ at most <paramref name="AgeGap"/> years, and each is drawn to the other's sex:
/// <paramref name="OwnSex"/> is the share of people drawn to their own sex.
/// </summary>
public sealed record PairingRules(double Chance, int Crowd, int AgeGap, double Max, double OwnSex);

/// <summary>
/// A pair has a child with <paramref name="Chance"/> a life year while the mother is <paramref name="MotherFrom"/> up to and
/// including <paramref name="MotherTo"/>, and her last child is <paramref name="GapYears"/> old. Each trait of the child is the
/// parents' mean within <paramref name="Spread"/>.
/// </summary>
public sealed record BirthRules(double Chance, int MotherFrom, int MotherTo, int GapYears, int Spread);

/// <summary>From age <paramref name="From"/> on, the chance a life year to die of age.</summary>
public sealed record AgeChance(int From, double Chance);

/// <summary>
/// The weights of a trade choice, each trade from 1: times <paramref name="Parent"/> for a parent's trade, times
/// <paramref name="Interest"/> when it matches an interest (by <paramref name="Interests"/>), times <paramref name="Missing"/> when
/// nobody living has it. The <paramref name="Agreeable"/> trades are open only from agreeableness <paramref name="AgreeableFrom"/>.
/// </summary>
public sealed record TradeRules(double Parent, double Interest, double Missing, int AgreeableFrom, IReadOnlyList<string> Agreeable,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Interests, IReadOnlyList<string>? Essential = null, Need? Need = null);

/// <summary>
/// When <c>food</c> or <c>fuel</c> (the keys of <paramref name="Trades"/>) has run short for <paramref name="Days"/> days in a
/// row, an adult up to <paramref name="UpTo"/> years old takes up the trade that answers it, with <paramref name="Chance"/> a
/// day for the whole village; a choice at coming of age weighs that trade <paramref name="Weight"/> times.
/// </summary>
public sealed record Need(IReadOnlyDictionary<string, string> Trades, int Days, double Chance, int UpTo, double Weight);
