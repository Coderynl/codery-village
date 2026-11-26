using Codery.Village.Core.Modules;
using Codery.Village.Core.Time;

namespace Codery.Village.Life;

/// <summary>
/// The gate of the life modules: they act once a village hour, and some once a day at 06:00. Every other tick a
/// module returns at once, because a run of 26 life years has some 63 million ticks. The engine moves the calendar after
/// the modules stepped, so during a tick the calendar shows the moment of the tick before.
/// </summary>
public static class Hourly
{
    /// <summary>A village hour: an hour of the calendar at four ticks a second.</summary>
    public const long TicksPerHour = 14_400;

    /// <summary>This tick starts a village hour.</summary>
    public static bool Now(ModuleContext v) => v.Clocks.Tick % TicksPerHour == 0;

    /// <summary>This tick starts the hour from 06:00 local time: the village's day begins.</summary>
    public static bool DayStarts(ModuleContext v) => Now(v) && v.Clocks.Calendar.Now.Hour == 6;

    /// <summary>
    /// The tick after <see cref="DayStarts"/>: the events published at the day's start (work, births, deaths) arrive now.
    /// </summary>
    public static bool DayBegan(ModuleContext v) =>
        v.Clocks.Tick % TicksPerHour == 1 && v.Clocks.Calendar.Now.AddMilliseconds(-1000.0 / Clocks.TicksPerSecond).Hour == 6;
}
