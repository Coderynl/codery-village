using Codery.Village.Modules.People;

namespace Codery.Village.Modules.Day;

/// <summary>The section "day" of the data: the rhythm of the day for each kind of resident.</summary>
public sealed record DaySettings(
    // how fast they walk
    int TicksPerCell,
    // how much later someone with conscientiousness 0 does everything; from 50 up, nobody is late
    int LateMinutes,
    OutsideRule Outside,
    EveningRule Evening,
    // what someone does in a "free" block, by weight
    IReadOnlyList<FreeChoice> Free,
    // by name: toddler, pupil, student, day, early, night (the shifts), idle (no job), retired, eddie
    IReadOnlyDictionary<string, Rhythm> Rhythms,
    // where free time and wandering go, by weight: a place where there is something
    IReadOnlyList<OutingChoice>? Outings = null,
    // from this agreeableness up, free time is spent helping at a building site of the island, by who is in one of HelpPhases
    int HelpFrom = 101,
    // a tile with more than pathThreshold steps in the last pathWindowDays days is a path while it keeps more than pathKeep
    int PathWindowDays = 3,
    int PathThreshold = int.MaxValue,
    int PathKeep = 0,
    // a walk over a path goes this many times as fast
    double PathSpeed = 1,
    // the phases of life that help build; children do not
    IReadOnlyList<LifePhase>? HelpPhases = null,
    // where free time and wandering go between dusk and dawn, by weight; without it, the same as by day
    IReadOnlyList<OutingChoice>? OutingsAfterDark = null,
    // per month from January, how much more or less outdoor free time and going out there is; without it, 1
    IReadOnlyList<double>? OutdoorByMonth = null,
    // per month from January, "HH:mm": when it gets dark and when light again (the fire reads them too); without
    // them day and night are not told apart: a visit stays inside and the outings are those by day
    IReadOnlyList<string>? Dusk = null,
    IReadOnlyList<string>? Dawn = null);

/// <summary>A kind of place to go in free time, and how often it is chosen.</summary>
public sealed record OutingChoice(Outing To, int Weight);

/// <summary>
/// A place where there is something: the island's fire, the water's edge, under a tree at the edge of a wood, or by
/// the home of a neighbour.
/// </summary>
public enum Outing { Fire, Water, Tree, Neighbour }

/// <summary>How many residents of an island are at least outside: always, and by day ("HH:mm", from up to).</summary>
public sealed record OutsideRule(int Always, int ByDay, string DayFrom, string DayTo);

/// <summary>
/// An "out" block: he goes out with chance <paramref name="Chance"/> plus <paramref name="PerExtraversion"/>
/// times (extraversion − 50) / 50, times the month's <c>outdoorByMonth</c>; out, he visits someone with chance
/// <paramref name="Visit"/>, else he goes where there is something. Otherwise he stays home.
/// </summary>
public sealed record EveningRule(double Chance, double PerExtraversion, double Visit);

public sealed record FreeChoice(Activity Do, int Weight);

/// <summary>The blocks of a weekday and, when it differs, of a weekend day.</summary>
public sealed record Rhythm(IReadOnlyList<Block> Weekday, IReadOnlyList<Block>? Weekend = null);

/// <summary>From "HH:mm" until the next block: an activity, "free" or "out".</summary>
public sealed record Block(string From, string Do);
