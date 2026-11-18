using Codery.Village.Core.Modules;

namespace Codery.Village.Modules.Building;

/// <summary>A phase of a site began: its cost has left the island's stock.</summary>
public sealed record PhaseStarted(long Tick, int Structure, string Phase) : VillageEvent(Tick);

/// <summary>The last phase of a site is done: the building stands.</summary>
public sealed record StructureDone(long Tick, int Structure) : VillageEvent(Tick);

/// <summary>A site stands still. <paramref name="Reason"/> is <c>materials</c> or <c>builders</c>; told once per reason.</summary>
public sealed record ConstructionWaiting(long Tick, int Structure, string Reason) : VillageEvent(Tick);
