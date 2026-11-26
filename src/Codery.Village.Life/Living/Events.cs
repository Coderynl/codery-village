using Codery.Village.Core.Modules;

namespace Codery.Village.Life.Living;

/// <summary><paramref name="What"/>, <c>food</c> or <c>fuel</c>, ran out this day; <paramref name="Days"/> is how many days in a row.</summary>
public sealed record Shortage(long Tick, string What, int Days) : VillageEvent(Tick);

/// <summary>Someone made <paramref name="Substance"/>: as their trade's work, for a household's use, or as a trial (both null).</summary>
public sealed record Made(long Tick, int Maker, string Substance, string? ForUse, int? ForHousehold) : VillageEvent(Tick);

public sealed record Built(long Tick, string Structure, string Substance) : VillageEvent(Tick);

/// <summary>Whole units of <paramref name="Substance"/> that rotted in stock this day.</summary>
public sealed record Spoiled(long Tick, string Substance, int Amount) : VillageEvent(Tick);

public sealed record FellIll(long Tick, int Person) : VillageEvent(Tick);

public sealed record Recovered(long Tick, int Person) : VillageEvent(Tick);

public sealed record DroughtBegan(long Tick) : VillageEvent(Tick);

public sealed record DroughtEnded(long Tick) : VillageEvent(Tick);

/// <summary>A household got something for <paramref name="Use"/>: made for it, or given.</summary>
public sealed record Got(long Tick, int Household, string Use, string Substance) : VillageEvent(Tick);

/// <summary>A household's possession for <paramref name="Use"/> broke or rotted, or was taken.</summary>
public sealed record Lost(long Tick, int Household, string Use) : VillageEvent(Tick);
