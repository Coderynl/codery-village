using Codery.Village.Core.Modules;

namespace Codery.Village.Life.Inventing;

/// <summary>
/// Something found and noticed: <see cref="By"/> saw that <see cref="Substance"/>, made by <see cref="Recipe"/>, serves
/// <see cref="Use"/> better than anything the island had. <see cref="How"/> is <c>trying</c> or <c>accident</c>;
/// <see cref="Witnesses"/> were there.
/// </summary>
public sealed record Discovery(long Tick, int By, string Substance, string Recipe, string Use, string How, IReadOnlyList<int> Witnesses);

public sealed record Discovered(long Tick, Discovery D) : VillageEvent(Tick);

/// <summary>Someone tried something; <paramref name="Result"/> is null when nothing happened. Counted, not told.</summary>
public sealed record Tried(long Tick, int By, string Recipe, string? Result) : VillageEvent(Tick);

/// <summary>A need pressed for years and nothing within one step of what the island has can meet it.</summary>
public sealed record HitAWall(long Tick, string Use, double Weight) : VillageEvent(Tick);
