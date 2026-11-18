namespace Codery.Village.Core.Modules;

/// <summary>
/// Something that happened, for the other modules. Published in tick N, it reaches every module in tick N+1.
/// Each module defines its own events as records derived from this one.
/// </summary>
public abstract record VillageEvent(long Tick);
