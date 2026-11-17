using Codery.Village.Core.Time;

namespace Codery.Village.Tests;

public class ClockTests
{
    private static DateTimeOffset Utc(int y, int mo, int d, int h = 0, int mi = 0) =>
        new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    [Fact]
    public void A_life_year_lasts_a_real_week()
    {
        var life = new LifeClock(2_419_200);

        Assert.Equal(1.0, life.YearsAt(2_419_200));
        Assert.Equal(2_419_200, life.TickOfAge(1.0));
        Assert.Equal(4 * 60 * 60 * 24 * 7, 2_419_200);
    }

    [Fact]
    public void The_calendar_is_Dutch_time()
    {
        var summer = new Calendar(new SimulatedTime(Utc(2026, 7, 1, 10)));
        var winter = new Calendar(new SimulatedTime(Utc(2026, 12, 1, 10)));

        Assert.Equal(new TimeOnly(12, 0), summer.Moment.Time);
        Assert.Equal(new DateOnly(2026, 7, 1), summer.Moment.Date);
        Assert.Equal(new TimeOnly(11, 0), winter.Moment.Time);
        Assert.Equal(DayOfWeek.Wednesday, summer.Moment.Day);
        Assert.False(summer.Moment.Weekend);
    }

    [Fact]
    public void The_weekend_is_saturday_and_sunday()
    {
        var saturday = new Calendar(new SimulatedTime(Utc(2026, 6, 6, 10)));
        var friday = new Calendar(new SimulatedTime(Utc(2026, 6, 5, 10)));

        Assert.True(saturday.Moment.Weekend);
        Assert.False(friday.Moment.Weekend);
    }

    [Theory]
    [InlineData(2026, 10, 25, 25)]
    [InlineData(2026, 3, 29, 23)]
    public void Daylight_saving_does_not_repeat_or_skip_an_hour(int y, int mo, int d, int hoursInDay)
    {
        // The local day starts 1 or 2 hours before UTC midnight; start early, then keep only this local date.
        var time = new SimulatedTime(Utc(y, mo, d).AddHours(-3));
        var calendar = new Calendar(time);
        var date = new DateOnly(y, mo, d);
        var minutesPerHour = new int[24];
        var previous = calendar.Now.UtcDateTime;

        for (var i = 0; i < 24 * 60 + 6 * 60; i++)
        {
            var now = calendar.Now.UtcDateTime;
            Assert.True(now >= previous);
            previous = now;
            var moment = calendar.Moment;
            if (moment.Date == date) minutesPerHour[moment.Time.Hour]++;
            time.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(hoursInDay * 60, minutesPerHour.Sum());
        for (var hour = 0; hour < 24; hour++)
        {
            var expected = (mo, hour) switch
            {
                (10, 2) => 120, // 02:xx happens twice
                (3, 2) => 0,    // 02:xx does not happen
                _ => 60,
            };
            Assert.Equal(expected, minutesPerHour[hour]);
        }
    }

    [Fact]
    public void Downtime_moves_the_calendar_not_the_ticks()
    {
        // Monday 2026-06-01 12:00 Dutch time = 10:00 UTC.
        var time = new SimulatedTime(Utc(2026, 6, 1, 10));
        var clocks = new Clocks(new Calendar(time), new LifeClock(2_419_200));
        clocks.SetTick(1000);
        Assert.Equal(DayOfWeek.Monday, clocks.Calendar.Moment.Day);

        time.Advance(TimeSpan.FromDays(1));

        Assert.Equal(1000, clocks.Tick);
        Assert.Equal(DayOfWeek.Tuesday, clocks.Calendar.Moment.Day);
        Assert.Equal(new TimeOnly(12, 0), clocks.Calendar.Moment.Time);
    }

    [Fact]
    public void Advance_moves_the_tick_by_one()
    {
        var clocks = new Clocks(new Calendar(new SimulatedTime(Utc(2026, 6, 1))), new LifeClock(2_419_200));

        clocks.Advance();
        clocks.Advance();

        Assert.Equal(2, clocks.Tick);
        Assert.Equal(4, Clocks.TicksPerSecond);
    }
}
