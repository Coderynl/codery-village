namespace Codery.Village.Life.Feelings;

/// <summary>
/// The section "feelings" of the world. Times are in life years. Happiness follows a target:
/// <c>base + temper + needs + joy − pain − unmet wishes + memory</c>, by at most <see cref="MaxStepPerHour"/> an hour.
/// </summary>
/// <param name="Base">Where a content person with nothing going on sits.</param>
/// <param name="Temper">Points that a cheerful (extravert, not sensitive) or gloomy temper adds or takes, at the extremes.</param>
/// <param name="JoyScale">Joy of a fulfilment per life year waited, at full wish weight.</param>
/// <param name="WaitFloor">The shortest wait joy counts, so that even a quick fulfilment pleases a little.</param>
/// <param name="WaitCap">Waits longer than this give no more joy.</param>
/// <param name="JoyHalfLife">Joy halves in this many life years.</param>
/// <param name="ExpectedAfter">After this, a fulfilment is expected: it gives no joy, and losing it hurts.</param>
/// <param name="UnwishedStrength">The weight of a fulfilment nobody wished for, against a full wish (1).</param>
/// <param name="LossAversion">Losing something costs this many times the joy it gave at the start.</param>
/// <param name="PainHalfLife">Pain halves in this many life years.</param>
/// <param name="WishPain">Points an unmet wish of full weight takes, times 0.5 + sensitivity/100.</param>
public sealed record FeelingsRules(
    double Base,
    double Temper,
    double MaxStepPerHour,
    int MemorySize,
    double MemoryHalfLife,
    double JoyScale,
    double WaitFloor,
    double WaitCap,
    double JoyHalfLife,
    double ExpectedAfter,
    double UnwishedStrength,
    double LossAversion,
    double PainHalfLife,
    double WishPain,
    NeedRules Needs,
    WishRules Wishes,
    IReadOnlyDictionary<string, int> Moments);

/// <summary>What the body and the home add: hungry or cold today, ill, a partner, and health below 100 (points at health 0).</summary>
public sealed record NeedRules(double Hungry, double Cold, double Ill, double Partner, double Health);

/// <summary>
/// How wishes arise, once a village day. A new wish starts at <paramref name="StartWeight"/> (full is <paramref name="MaxWeight"/>)
/// and grows by <paramref name="GrowthPerYear"/> while unmet; a person holds at most <paramref name="MaxWishes"/>.
/// <list type="bullet">
/// <item><paramref name="LackDaily"/>: the chance a day that a household use known on the island, which the household lacks,
/// becomes a wish (shortage).</item>
/// <item><paramref name="ComparisonDaily"/>: the chance a day, times ((sensitivity + status) / 100) to the power
/// <paramref name="ComparisonPower"/>, that a possession an acquaintance's household has, and one's own lacks or has worse by
/// <paramref name="Better"/>, becomes a wish (comparison). Acquaintances: households within <paramref name="Neighbours"/> by
/// number, kin and workmates.</item>
/// <item><paramref name="HabituationDaily"/>: the chance a day, times openness / 100, that someone whose wishes are all met and
/// whose fulfilments are all expected wants a household use few on the island have (habituation).</item>
/// <item><paramref name="PromiseDaily"/>: the chance a day, times openness / 100, that a newly found substance serving a use
/// nobody has becomes a wish (promise).</item>
/// </list>
/// </summary>
public sealed record WishRules(
    double StartWeight,
    double GrowthPerYear,
    double MaxWeight,
    int MaxWishes,
    double LackDaily,
    double ComparisonDaily,
    double ComparisonPower,
    double Better,
    int Neighbours,
    double HabituationDaily,
    double PromiseDaily);
