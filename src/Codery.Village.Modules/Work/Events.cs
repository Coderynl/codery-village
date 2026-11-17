using Codery.Village.Core.Modules;

namespace Codery.Village.Modules.Work;

/// <summary>The resident got a job on his island.</summary>
public sealed record Hired(long Tick, int Resident, int Island, string Role) : VillageEvent(Tick);

/// <summary>A workplace of the island made something.</summary>
public sealed record Produced(long Tick, int Island, string Material, int Amount) : VillageEvent(Tick);

/// <summary>What the treasury of the island paid out since the previous one: told every calendar day at 00:00.</summary>
public sealed record Paid(long Tick, int Island, long Wages, long Allowances, long Pensions) : VillageEvent(Tick);
