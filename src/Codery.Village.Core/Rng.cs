using System.Text;

namespace Codery.Village.Core;

/// <summary>
/// The only source of chance in the village: xorshift64 with shifts 13, 7 and 17, the same as
/// <c>World.Random</c> in the site. Every part of the world draws from its own named stream, so drawing
/// more in one place never changes what another place gets.
/// </summary>
public sealed class Rng
{
    private const ulong Golden = 0x9E3779B97F4A7C15;

    /// <summary>Starts at <paramref name="state"/>; 0 would stay 0 forever and becomes the golden ratio instead.</summary>
    public Rng(ulong state) => State = state == 0 ? Golden : state;

    /// <summary>Where the stream stands; <c>new Rng(State)</c> continues it.</summary>
    public ulong State { get; private set; }

    /// <summary>The stream called <paramref name="stream"/> for this seed.</summary>
    public static Rng For(ulong seed, string stream) => new(Mix(seed ^ Fnv1a(stream)));

    /// <summary>A whole number from 0 up to, not including, <paramref name="below"/>.</summary>
    public int Next(int below)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(below);
        return (int)(NextUlong() % (ulong)below);
    }

    /// <summary>A fraction from 0 up to, not including, 1.</summary>
    public double NextDouble() => (NextUlong() >> 11) * (1.0 / (1UL << 53));

    private ulong NextUlong()
    {
        var x = State;
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        State = x;
        return x;
    }

    private static ulong Fnv1a(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    // the finaliser of splitmix64: seeds that differ by one bit start far apart
    private static ulong Mix(ulong x)
    {
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}
