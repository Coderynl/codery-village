using Codery.Village.Core;

namespace Codery.Village.Tests;

public class RngTests
{
    [Fact]
    public void Streams_do_not_disturb_each_other()
    {
        var alone = Rng.For(1, "people");
        var expected = Enumerable.Range(0, 100).Select(_ => alone.Next(1000)).ToList();

        var day = Rng.For(1, "day");
        var people = Rng.For(1, "people");
        for (var i = 0; i < 50; i++) day.Next(1000);
        var actual = Enumerable.Range(0, 100).Select(_ => people.Next(1000)).ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Each_name_and_each_seed_gives_its_own_stream()
    {
        static List<int> First(ulong seed, string stream)
        {
            var rng = Rng.For(seed, stream);
            return Enumerable.Range(0, 20).Select(_ => rng.Next(1_000_000)).ToList();
        }

        Assert.Equal(First(1, "people"), First(1, "people"));
        Assert.NotEqual(First(1, "people"), First(1, "day"));
        Assert.NotEqual(First(1, "people"), First(2, "people"));
    }

    [Fact]
    public void It_is_the_xorshift_of_the_site()
    {
        // World.Random in the site: shifts 13, 7 and 17; seed 0 becomes the golden ratio
        var rng = new Rng(0);
        Assert.Equal(0x9E3779B97F4A7C15UL, rng.State);

        var state = 0x9E3779B97F4A7C15UL;
        for (var i = 0; i < 10; i++)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            Assert.Equal((int)(state % 97UL), rng.Next(97));
            Assert.Equal(state, rng.State);
        }
    }

    [Fact]
    public void The_state_continues_the_stream()
    {
        var rng = Rng.For(7, "people");
        rng.Next(10);
        var copy = new Rng(rng.State);

        Assert.Equal(rng.Next(1000), copy.Next(1000));
        Assert.Equal(rng.NextDouble(), copy.NextDouble());
    }

    [Fact]
    public void Numbers_stay_in_their_range()
    {
        var rng = Rng.For(3, "range");
        for (var i = 0; i < 1000; i++)
        {
            Assert.InRange(rng.Next(6), 0, 5);
            var fraction = rng.NextDouble();
            Assert.True(fraction is >= 0 and < 1);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.Next(0));
    }
}
