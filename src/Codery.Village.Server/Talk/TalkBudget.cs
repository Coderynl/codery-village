using Codery.Village.Core.Time;

namespace Codery.Village.Server.Talk;

/// <summary>
/// The dollars for conversations per Dutch calendar day; from 00:00 Europe/Amsterdam the day starts empty. A call
/// first reserves what it can cost at most, and runs only when that fits in what is left; afterwards it books what
/// it cost and lets go of the rest. So the day never goes over the budget, as long as no call costs more than its
/// reservation.
/// </summary>
public sealed class TalkBudget(decimal dailyUsd, TimeProvider time)
{
    private readonly Calendar _calendar = new(time);
    private readonly Lock _lock = new();
    private DateOnly _day;
    private decimal _spent;
    private decimal _reserved;

    /// <summary>What today's calls cost so far, in dollars.</summary>
    public decimal Spent
    {
        get
        {
            lock (_lock)
            {
                Roll();
                return _spent;
            }
        }
    }

    /// <summary>Holds <paramref name="usd"/> for a call, or says no when it does not fit in what is left today.</summary>
    public bool TryReserve(decimal usd, out Reservation reservation)
    {
        lock (_lock)
        {
            Roll();
            reservation = new Reservation(_day, usd);
            if (_spent + _reserved + usd > dailyUsd) return false;
            _reserved += usd;
            return true;
        }
    }

    /// <summary>Books what the calls of a reservation cost, and lets go of the reservation.</summary>
    public void Settle(Reservation reservation, decimal spent)
    {
        lock (_lock)
        {
            Roll();
            // a reservation of yesterday was let go at midnight; its cost counts today
            if (reservation.Day == _day) _reserved -= reservation.Usd;
            _spent += spent;
        }
    }

    private void Roll()
    {
        var today = _calendar.Moment.Date;
        if (today == _day) return;
        _day = today;
        _spent = 0;
        _reserved = 0;
    }

    public readonly record struct Reservation(DateOnly Day, decimal Usd);
}
