namespace Codery.Village.Life.Living;

/// <summary>The weather of the day: a <paramref name="Drought"/> or not, and how <paramref name="Cold"/> it is, 0 to 100, by the month.</summary>
public sealed record Weather(bool Drought, int Cold);
