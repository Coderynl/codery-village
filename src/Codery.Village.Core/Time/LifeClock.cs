namespace Codery.Village.Core.Time;

/// <summary>Converts ticks to life years and back. The rate comes from data/life.json.</summary>
public sealed class LifeClock(long ticksPerLifeYear)
{
    public double YearsAt(long tick) => (double)tick / ticksPerLifeYear;

    public long TickOfAge(double years) => (long)Math.Round(years * ticksPerLifeYear);
}
