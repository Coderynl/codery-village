using Codery.Village.Modules.Building;

namespace Codery.Village.Server;

/// <summary>
/// What a page hears back after an intervention. <see cref="Refusal"/> is the name of a refusal of the rules, or
/// one of the server's own: <c>TooSoon</c> (this connection has to wait), <c>Busy</c> (the whole village has to
/// wait) or <c>NotReady</c> (the village is not loaded yet). <see cref="Wait"/> is how many whole seconds until it
/// can again, rounded up; only with <c>TooSoon</c> and <c>Busy</c>.
/// </summary>
public sealed record InterventionResult(bool Ok, string? Refusal, int? Wait)
{
    public static InterventionResult Done { get; } = new(true, null, null);
    public static InterventionResult NotReady { get; } = new(false, nameof(NotReady), null);
    public static InterventionResult TooSoon(int wait) => new(false, nameof(TooSoon), wait);
    public static InterventionResult Busy(int wait) => new(false, nameof(Busy), wait);
    public static InterventionResult Of(Refusal? refusal) => refusal is null ? Done : new(false, refusal.ToString(), null);
}

/// <summary>How often pages may change the village. Set under <c>Village:Limits</c>.</summary>
public sealed record InterventionLimits
{
    /// <summary>The least time between two tries of one connection; every try counts, also one that was turned away.</summary>
    public double MinSecondsBetweenTries { get; init; } = 1;
    /// <summary>How many interventions one connection has in stock; a new connection begins full.</summary>
    public int PerVisitor { get; init; } = 2;
    public double PerVisitorEverySeconds { get; init; } = 10;
    /// <summary>How many interventions the whole village has in stock.</summary>
    public int Village { get; init; } = 10;
    public double VillageEverySeconds { get; init; } = 4;
}

/// <summary>
/// Keeps pages from changing the village too often. It knows a connection only by the id the hub gives it,
/// keeps that in memory, and forgets it when the connection closes. Only an intervention that worked costs
/// stock, of the connection and of the village.
/// </summary>
public sealed class InterventionLimiter(InterventionLimits limits, TimeProvider clock)
{
    private readonly Lock _gate = new();
    private readonly long _start = clock.GetTimestamp();
    private readonly TimeSpan _between = TimeSpan.FromSeconds(limits.MinSecondsBetweenTries);
    private readonly Dictionary<string, Visitor> _visitors = [];
    private readonly Stock _village = new(limits.Village, TimeSpan.FromSeconds(limits.VillageEverySeconds));

    /// <summary>
    /// Tries an intervention for a connection: first the limits, in order, then the intervention itself. It runs
    /// under this lock, so two connections cannot both spend the last one of the village. The village takes its
    /// own lock inside this one; never the other way round.
    /// </summary>
    public InterventionResult Try(string visitor, Func<InterventionResult> intervention)
    {
        lock (_gate)
        {
            var now = clock.GetElapsedTime(_start);
            if (!_visitors.TryGetValue(visitor, out var known))
                _visitors[visitor] = known = new Visitor(new Stock(limits.PerVisitor, TimeSpan.FromSeconds(limits.PerVisitorEverySeconds)));
            var last = known.LastTry;
            known.LastTry = now;

            // this try counts too: the next one has to wait at least the time between tries
            if (last is { } previous && now - previous < _between)
                return InterventionResult.TooSoon(Seconds(Longest(_between, known.Stock.UntilOne(now))));
            if (known.Stock.UntilOne(now) is var own && own > TimeSpan.Zero)
                return InterventionResult.TooSoon(Seconds(Longest(_between, own)));
            if (_village.UntilOne(now) is var shared && shared > TimeSpan.Zero)
                return InterventionResult.Busy(Seconds(Longest(_between, shared)));

            var result = intervention();
            if (result.Ok)
            {
                known.Stock.Spend(now);
                _village.Spend(now);
            }
            return result;
        }
    }

    /// <summary>Forgets a connection that closed.</summary>
    public void Forget(string visitor)
    {
        lock (_gate) _visitors.Remove(visitor);
    }

    /// <summary>For the tests: how many connections it knows of.</summary>
    internal int Visitors
    {
        get
        {
            lock (_gate) return _visitors.Count;
        }
    }

    private static TimeSpan Longest(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>Whole seconds, rounded up. A stock of none never fills: that wait is as long as an int goes.</summary>
    private static int Seconds(TimeSpan wait) => (int)Math.Min(int.MaxValue, Math.Ceiling(wait.TotalSeconds));

    private sealed class Visitor(Stock stock)
    {
        public Stock Stock { get; } = stock;
        public TimeSpan? LastTry { get; set; }
    }

    /// <summary>
    /// A stock that fills up by one every so often, to a most. Kept as the moment it is full again, so the
    /// clock is all it needs: what is spent pushes that moment on.
    /// </summary>
    private sealed class Stock(int most, TimeSpan every)
    {
        private TimeSpan _fullAt = TimeSpan.MinValue;

        /// <summary>How long until there is one to spend; zero when there is one now.</summary>
        public TimeSpan UntilOne(TimeSpan now)
        {
            if (_fullAt <= now) return most > 0 ? TimeSpan.Zero : TimeSpan.MaxValue;
            var wait = _fullAt - now - TimeSpan.FromTicks(every.Ticks * (most - 1));
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        public void Spend(TimeSpan now) => _fullAt = (_fullAt > now ? _fullAt : now) + every;
    }
}
