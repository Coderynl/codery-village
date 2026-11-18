using Codery.Village.Modules.Building;
using Codery.Village.Server;

namespace Codery.Village.Tests;

/// <summary>A clock that only moves when a test moves it.</summary>
internal sealed class FakeClock : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
}

/// <summary>How often a page may change the village: per connection and for the whole village, and only what works costs anything.</summary>
public class LimiterTests
{
    private readonly FakeClock _clock = new();
    /// <summary>How often the intervention itself ran: never when a limit says no.</summary>
    private int _ran;

    private InterventionLimiter Limiter(InterventionLimits? limits = null) => new(limits ?? new InterventionLimits(), _clock);

    private InterventionResult Works()
    {
        _ran++;
        return InterventionResult.Done;
    }

    private InterventionResult Refused()
    {
        _ran++;
        return InterventionResult.Of(Refusal.Water);
    }

    /// <summary>Moves the clock to a moment, counted from the start of the test.</summary>
    private double _now;
    private void At(double seconds)
    {
        Assert.True(seconds >= _now, "the clock only goes forward");
        _clock.Advance(seconds - _now);
        _now = seconds;
    }

    [Fact]
    public void Unless_set_otherwise_the_limits_are_those_of_the_plan()
    {
        var limits = new InterventionLimits();
        Assert.Equal(1, limits.MinSecondsBetweenTries);
        Assert.Equal(2, limits.PerVisitor);
        Assert.Equal(10, limits.PerVisitorEverySeconds);
        Assert.Equal(10, limits.Village);
        Assert.Equal(4, limits.VillageEverySeconds);
    }

    [Fact]
    public void A_new_connection_begins_with_two_in_stock()
    {
        var limiter = Limiter();
        Assert.Equal(InterventionResult.Done, limiter.Try("a", Works));
        At(1);
        Assert.Equal(InterventionResult.Done, limiter.Try("a", Works));
        At(2);
        // the first one comes back ten seconds after it was spent
        Assert.Equal(InterventionResult.TooSoon(8), limiter.Try("a", Works));
        Assert.Equal(2, _ran);
    }

    [Fact]
    public void Less_than_a_second_after_the_last_try_is_too_soon()
    {
        var limiter = Limiter();
        limiter.Try("a", Works);
        At(0.5);
        Assert.Equal(InterventionResult.TooSoon(1), limiter.Try("a", Works));
        Assert.Equal(1, _ran);
        // another connection does not have to wait for this one
        Assert.Equal(InterventionResult.Done, limiter.Try("b", Works));
    }

    [Fact]
    public void A_second_after_the_last_try_is_soon_enough()
    {
        var limiter = Limiter();
        limiter.Try("a", Works);
        At(1);
        Assert.Equal(InterventionResult.Done, limiter.Try("a", Works));
    }

    [Fact]
    public void Every_try_counts_for_the_time_between_tries_also_one_that_was_turned_away()
    {
        var limiter = Limiter();
        limiter.Try("a", Works);
        At(0.5);
        Assert.False(limiter.Try("a", Works).Ok);
        At(1.2);
        // a second after the first try, but not after the second
        Assert.Equal(InterventionResult.TooSoon(1), limiter.Try("a", Works));
        At(2.2);
        Assert.Equal(InterventionResult.Done, limiter.Try("a", Works));
    }

    [Fact]
    public void What_the_rules_refuse_costs_no_stock_but_counts_for_the_time_between_tries()
    {
        var limiter = Limiter(new InterventionLimits { Village = 2 });
        Assert.Equal(InterventionResult.Of(Refusal.Water), limiter.Try("a", Refused));
        Assert.Equal(new InterventionResult(false, "Water", null), limiter.Try("b", Refused));
        At(0.5);
        Assert.Equal(InterventionResult.TooSoon(1), limiter.Try("a", Works));
        Assert.Equal(2, _ran);

        // both refusals were free: the connection still has two, and the village two as well
        At(1.5);
        Assert.Equal(InterventionResult.Done, limiter.Try("a", Works));
        Assert.Equal(InterventionResult.Done, limiter.Try("b", Works));
        At(2.5);
        Assert.Equal(InterventionResult.Busy(3), limiter.Try("a", Works));
    }

    [Fact]
    public void Every_ten_seconds_one_comes_back_up_to_two()
    {
        var limiter = Limiter();
        limiter.Try("a", Works);
        At(1);
        limiter.Try("a", Works);
        At(10);
        Assert.Equal(InterventionResult.Done, limiter.Try("a", Works));
        At(11);
        Assert.Equal(InterventionResult.TooSoon(9), limiter.Try("a", Works));

        // after a long while there are two again, not more
        At(111);
        Assert.Equal(InterventionResult.Done, limiter.Try("a", Works));
        At(112);
        Assert.Equal(InterventionResult.Done, limiter.Try("a", Works));
        At(113);
        Assert.Equal(InterventionResult.TooSoon(8), limiter.Try("a", Works));
        Assert.Equal(5, _ran);
    }

    [Fact]
    public void When_the_village_has_none_left_it_is_busy_for_every_connection()
    {
        var limiter = Limiter();
        for (var visitor = 0; visitor < 10; visitor++)
            Assert.Equal(InterventionResult.Done, limiter.Try($"v{visitor}", Works));
        // a connection with a full stock waits all the same
        Assert.Equal(InterventionResult.Busy(4), limiter.Try("new", Works));
        Assert.Equal(10, _ran);

        // every four seconds one comes back
        At(4);
        Assert.Equal(InterventionResult.Done, limiter.Try("other", Works));
        Assert.Equal(InterventionResult.Busy(4), limiter.Try("another", Works));

        // after a long while there are ten again, not more
        At(400);
        for (var visitor = 0; visitor < 10; visitor++)
            Assert.Equal(InterventionResult.Done, limiter.Try($"w{visitor}", Works));
        Assert.Equal(InterventionResult.Busy(4), limiter.Try("late", Works));
    }

    [Fact]
    public void The_limits_are_tried_in_order_the_time_between_tries_the_connection_and_then_the_village()
    {
        var limiter = Limiter(new InterventionLimits { PerVisitor = 1, Village = 1 });
        limiter.Try("a", Works);
        // everything is out: the connection hears it is too soon, not that the village is busy
        At(0.5);
        Assert.Equal(InterventionResult.TooSoon(10), limiter.Try("a", Works));
        At(2);
        Assert.Equal(InterventionResult.TooSoon(8), limiter.Try("a", Works));
        Assert.Equal(InterventionResult.Busy(2), limiter.Try("b", Works));
        Assert.Equal(1, _ran);
    }

    [Theory]
    [InlineData(2.5, 8)]
    [InlineData(3, 7)]
    [InlineData(9.6, 1)]
    [InlineData(9.999, 1)]
    public void The_wait_is_rounded_up_to_whole_seconds(double at, int wait)
    {
        var limiter = Limiter(new InterventionLimits { MinSecondsBetweenTries = 0.25 });
        limiter.Try("a", Works);
        At(1);
        limiter.Try("a", Works);
        At(at);
        Assert.Equal(InterventionResult.TooSoon(wait), limiter.Try("a", Works));
    }

    [Theory]
    [InlineData(0.5, 4)]
    [InlineData(1, 3)]
    [InlineData(3.75, 1)]
    public void The_wait_for_the_village_is_rounded_up_too(double at, int wait)
    {
        var limiter = Limiter(new InterventionLimits { MinSecondsBetweenTries = 0.25, Village = 1 });
        limiter.Try("a", Works);
        At(at);
        Assert.Equal(InterventionResult.Busy(wait), limiter.Try("b", Works));
    }

    [Fact]
    public void A_connection_that_is_forgotten_leaves_nothing_behind()
    {
        var limiter = Limiter();
        Assert.Equal(0, limiter.Visitors);
        limiter.Try("a", Works);
        limiter.Try("b", Works);
        Assert.Equal(2, limiter.Visitors);

        limiter.Forget("a");
        limiter.Forget("never seen");
        Assert.Equal(1, limiter.Visitors);
    }
}
