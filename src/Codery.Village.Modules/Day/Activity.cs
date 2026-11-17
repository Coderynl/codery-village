using Codery.Village.Core.Map;
using Codery.Village.Core.Modules;

namespace Codery.Village.Modules.Day;

/// <summary>
/// What a resident is doing this quarter of an hour. <see cref="Help"/> is free time spent at a building site;
/// <see cref="Fetch"/>, <see cref="Bring"/> and <see cref="Tend"/> are errands another module sends him on.
/// </summary>
public enum Activity { Sleep, School, Study, Work, Shop, Leisure, Visit, Wander, Home, Fetch, Bring, Help, Tend }

/// <summary>
/// What a resident does and where: the <paramref name="Place"/> of the activity, the tile <paramref name="At"/>
/// where he stands (where his walk began, while he walks), the <paramref name="Walk"/> there if he is on his
/// way, whether he is <paramref name="Inside"/> a building, which he is not while he walks, and the material he
/// <paramref name="Carry"/>s on his back, by id. The defaults let a save leave out what is not there.
/// </summary>
public sealed record Doing(Activity Activity, Cell Place, Cell At, Walk? Walk = null, bool Inside = false, string? Carry = null);

/// <summary>
/// A walk over <paramref name="Cells"/> (the first step up to and including the place), begun on
/// <paramref name="StartTick"/>, one tile per <paramref name="TicksPerCell"/> ticks. The hero draws it itself.
/// </summary>
public sealed record Walk(IReadOnlyList<Cell> Cells, long StartTick, int TicksPerCell);

/// <summary>
/// An errand: to <paramref name="Target"/>, carrying <paramref name="There"/> on the way out and
/// <paramref name="Back"/> on the way back, and then back to what he was doing. <paramref name="Kind"/> is
/// <see cref="Activity.Fetch"/>, <see cref="Activity.Bring"/> or <see cref="Activity.Tend"/>.
/// </summary>
public sealed record Errand(Activity Kind, Cell Target, string? There = null, string? Back = null);

/// <summary>An errand on its way: what he left (<paramref name="Was"/> at <paramref name="Home"/>), and whether he is on his way back.</summary>
public sealed record Trip(Errand Errand, Activity Was, Cell Home, bool Back);

/// <summary>A resident does something else, or goes somewhere else; not sent when he arrives.</summary>
public sealed record DoingChanged(long Tick, int Resident, Doing Doing) : VillageEvent(Tick);

/// <summary>A resident on an errand has come to its target, with what he carried there.</summary>
public sealed record Reached(long Tick, int Resident, Activity Errand, Cell At, string? Carry) : VillageEvent(Tick);

/// <summary>A resident is back from an errand at the place he left, with what he carried back.</summary>
public sealed record Returned(long Tick, int Resident, Activity Errand, Cell At, string? Carry) : VillageEvent(Tick);
