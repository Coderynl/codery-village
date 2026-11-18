namespace Codery.Village.Core.Time;

/// <summary>The three clocks: the tick (simulation), the calendar (the real Dutch date) and the life clock.</summary>
public sealed class Clocks(Calendar calendar, LifeClock life)
{
    public const int TicksPerSecond = 4;

    public long Tick { get; private set; }

    public Calendar Calendar { get; } = calendar;

    public LifeClock Life { get; } = life;

    /// <summary>Moves the simulation one tick on. Only the engine calls this.</summary>
    public void Advance() => Tick++;

    /// <summary>Sets the tick when a save is loaded.</summary>
    public void SetTick(long tick) => Tick = tick;
}
