using Codery.Village.Core.Modules;

namespace Codery.Village.Life.Knowing;

/// <summary>
/// <paramref name="Person"/> came to know <paramref name="Item"/>: its level passed 20. <paramref name="How"/>: <c>parent</c>,
/// <c>master</c>, <c>school</c>, <c>watching</c> or <c>found</c>.
/// </summary>
public sealed record Learned(long Tick, int Person, string Item, string How) : VillageEvent(Tick);

/// <summary>Nobody living knows <paramref name="Item"/> any more: <paramref name="LastBearer"/> died with it.</summary>
public sealed record Forgotten(long Tick, string Item, int LastBearer) : VillageEvent(Tick);
