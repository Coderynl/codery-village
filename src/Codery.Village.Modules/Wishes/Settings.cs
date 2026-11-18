using Codery.Village.Modules.People;

namespace Codery.Village.Modules.Wishes;

/// <summary>The section "wishes" of the data.</summary>
public sealed record WishSettings(
    // how often, in ticks, the village looks whether to write a task
    int EveryTicks,
    // an island has to weigh a wish more than this before the village writes a task for it
    double TaskThreshold,
    // what a wish does to the happiness of who has it
    WishPoints Points,
    TaskTemplate Task,
    IReadOnlyDictionary<string, Sentence> Log,
    IReadOnlyList<Wish> Wishes);

/// <summary>
/// Something residents wish for. <paramref name="Label"/> is what a blueprint <c>serves</c> to fulfil it. It counts
/// for who fits <paramref name="Who"/>, from <paramref name="AfterDays"/> calendar days after the village began, and
/// only once the data knows every material or blueprint id in <paramref name="After"/>: wine after grapes.
/// </summary>
public sealed record Wish(string Id, string Nl, string En, string Label, double Weight, Who? Who = null, double AfterDays = 0,
    IReadOnlyList<string>? After = null);

/// <summary>
/// Who has a wish: someone with any of the <paramref name="Interests"/>, in any of the <paramref name="Phases"/>,
/// and with every trait in <paramref name="Traits"/> at least its value. What is left out does not matter.
/// </summary>
public sealed record Who(
    IReadOnlyList<string>? Interests = null,
    IReadOnlyList<LifePhase>? Phases = null,
    IReadOnlyDictionary<string, int>? Traits = null);

/// <summary>The points of happiness per wish that counts for a resident: fulfilled or not.</summary>
public sealed record WishPoints(int Fulfilled, int Unfulfilled);

/// <summary>
/// The text of a task. The placeholders are <c>{wish}</c>, <c>{count}</c> (residents of the island who wish it),
/// <c>{island}</c>, <c>{elsewhere}</c> (residents of the other islands who wish it), <c>{since}</c> (the date),
/// <c>{weight}</c> and <c>{threshold}</c>.
/// </summary>
public sealed record TaskTemplate(string TitleNl, string TitleEn, string BodyNl);
