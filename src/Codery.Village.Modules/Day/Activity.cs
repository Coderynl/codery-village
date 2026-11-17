using Codery.Village.Core.Map;
using Codery.Village.Core.Modules;

namespace Codery.Village.Modules.Day;

/// <summary>What a resident is doing this quarter of an hour.</summary>
public enum Activity { Sleep, School, Study, Work, Shop, Leisure, Visit, Wander, Home }

/// <summary>
/// What a resident does and where: the <paramref name="Place"/> of the activity, the tile <paramref name="At"/>
/// where he stands (where his walk began, while he walks), the <paramref name="Walk"/> there if he is on his
/// way, and whether he is <paramref name="Inside"/> a building, which he is not while he walks. The defaults
/// let a save leave out a walk that is not there.
/// </summary>
public sealed record Doing(Activity Activity, Cell Place, Cell At, Walk? Walk = null, bool Inside = false);

/// <summary>
/// A walk over <paramref name="Cells"/> (the first step up to and including the place), begun on
/// <paramref name="StartTick"/>, one tile per <paramref name="TicksPerCell"/> ticks. The hero draws it itself.
/// </summary>
public sealed record Walk(IReadOnlyList<Cell> Cells, long StartTick, int TicksPerCell);

/// <summary>A resident does something else, or goes somewhere else; not sent when he arrives.</summary>
public sealed record DoingChanged(long Tick, int Resident, Doing Doing) : VillageEvent(Tick);
