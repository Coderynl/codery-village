using Codery.Village.Core.Modules;

namespace Codery.Village.Life.Folk;

public sealed record Born(long Tick, int Person, int Mother, int Father) : VillageEvent(Tick);

public sealed record Paired(long Tick, int A, int B) : VillageEvent(Tick);

/// <summary><paramref name="Cause"/>: <c>age</c>, or what <see cref="Folk.Harm"/> was given: <c>sickness</c>, <c>cold</c>, <c>hunger</c>, <c>accident</c>.</summary>
public sealed record Died(long Tick, int Person, string Cause) : VillageEvent(Tick);

/// <summary>Someone turned 18 and chose a trade (<see cref="Person.Trade"/>).</summary>
public sealed record CameOfAge(long Tick, int Person) : VillageEvent(Tick);
