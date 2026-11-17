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
    IReadOnlyDictionary<string, Rhythm> Rhythms);

/// <summary>How many residents of an island are at least outside: always, and by day ("HH:mm", from up to).</summary>
public sealed record OutsideRule(int Always, int ByDay, string DayFrom, string DayTo);

/// <summary>
/// An "out" block: he goes out with chance <paramref name="Chance"/> plus <paramref name="PerExtraversion"/>
/// times (extraversion − 50) / 50; out, he visits someone with chance <paramref name="Visit"/>, else he goes
/// to the water. Otherwise he stays home.
/// </summary>
public sealed record EveningRule(double Chance, double PerExtraversion, double Visit);

public sealed record FreeChoice(Activity Do, int Weight);

/// <summary>The blocks of a weekday and, when it differs, of a weekend day.</summary>
public sealed record Rhythm(IReadOnlyList<Block> Weekday, IReadOnlyList<Block>? Weekend = null);

/// <summary>From "HH:mm" until the next block: an activity, "free" or "out".</summary>
public sealed record Block(string From, string Do);
