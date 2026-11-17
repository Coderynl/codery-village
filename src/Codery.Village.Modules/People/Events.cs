using Codery.Village.Core.Modules;

namespace Codery.Village.Modules.People;

/// <summary>A newcomer came to live in the village; those who were there at the start do not arrive.</summary>
public sealed record ResidentArrived(long Tick, int Resident) : VillageEvent(Tick);

public sealed record ResidentPhaseChanged(long Tick, int Resident, LifePhase Phase) : VillageEvent(Tick);

/// <summary>Someone left the village. <paramref name="Reason"/> is a short key: "coast".</summary>
public sealed record ResidentLeft(long Tick, int Resident, string Reason) : VillageEvent(Tick);
