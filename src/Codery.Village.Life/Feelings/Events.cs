using Codery.Village.Core.Modules;

namespace Codery.Village.Life.Feelings;

/// <summary>Someone came to want something for <paramref name="Use"/>, for <paramref name="Cause"/> (one of <see cref="Wish.Causes"/>).</summary>
public sealed record Wished(long Tick, int Person, string Use, string Cause, string? Because) : VillageEvent(Tick);

/// <summary>A wish came true after <paramref name="WaitedTicks"/>.</summary>
public sealed record Fulfilled(long Tick, int Person, string Use, long WaitedTicks) : VillageEvent(Tick);

/// <summary>Someone gave up wanting <paramref name="Use"/>: something weighed more, and a heart holds only so many wishes.</summary>
public sealed record GaveUp(long Tick, int Person, string Use) : VillageEvent(Tick);
