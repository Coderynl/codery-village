namespace Codery.Village.Core.Time;

/// <summary>A clock that only moves when told to: the engine advances it 250 ms per tick in tests and on export.</summary>
public sealed class SimulatedTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start.ToUniversalTime();

    public void Advance(TimeSpan by) => _now += by;

    public override DateTimeOffset GetUtcNow() => _now;
}
