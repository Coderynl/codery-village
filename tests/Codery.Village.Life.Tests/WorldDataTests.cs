using Codery.Village.Core;
using Codery.Village.Laws;

namespace Codery.Village.Life.Tests;

/// <summary>
/// The real world's data. Risk: a law that names nothing, a recipe the laws do not make, a law that can never fire, or a
/// world in which bronze cannot be found, leaves the village with a history we wrote instead of one it finds.
/// </summary>
public class WorldDataTests
{
    [Fact]
    public void The_world_has_no_problems() => Assert.Empty(Problems.Of(World.Laws));

    [Fact]
    public void Bronze_lies_within_reach_but_is_not_known()
    {
        var world = World.Laws;
        var reached = Search(world, 6, found => found.Values.Any(s => s.Kind == "alloy"));

        var bronze = reached.Values.Where(s => s.Kind == "alloy").ToList();
        Assert.NotEmpty(bronze);
        // harder than copper and tin, the metals that melt in a kiln
        var hardestSoftMetal = reached.Values.Where(s => s.Kind == "metal" && s.Profile[Property.MeltingPoint] <= 55).Max(s => s.Profile[Property.Hardness]);
        Assert.Contains(bronze, alloy => alloy.Profile[Property.Hardness] > hardestSoftMetal);
        Assert.DoesNotContain(world.Start.Recipes, recipe => world.Substances[recipe.Out].Kind == "alloy");
    }

    [Fact]
    public void Every_law_can_fire()
    {
        var world = World.Laws;
        var fired = new HashSet<string>();
        Search(world, 6, _ => fired.Count == world.Laws.Count, fired);

        var never = world.Laws.Select(law => law.Id).Where(id => !fired.Contains(id)).ToList();
        Assert.True(never.Count == 0, $"Never fired: {string.Join(", ", never)}");
    }

    [Fact]
    public void Each_island_draws_its_own_substance()
    {
        var world = World.Laws;
        var west = OwnSubstance.Draw(Rng.For(1, "own-0"), world.Own, 0, world.Raw);
        var east = OwnSubstance.Draw(Rng.For(1, "own-2"), world.Own, 2, world.Raw);

        Assert.NotEqual(west.Profile, east.Profile);
        foreach (var own in new[] { west, east })
        {
            Assert.True(own.Raw);
            foreach (var p in Enum.GetValues<Property>())
                Assert.InRange(own.Profile[p], world.Own.Min[p], world.Own.Max[p]);
            Assert.All(world.Raw, raw => Assert.True(OwnSubstance.Distance(raw.Profile, own.Profile) >= world.Own.MinDistance));
        }
    }

    // breadth first from the raw substances over every process, alone or in pairs at a few ratios; stops when done says so.
    // Pairs take every raw substance and at most a few made ones per kind, or the search grows past what a test can wait for.
    private static Dictionary<string, Substance> Search(WorldData world, int steps, Func<Dictionary<string, Substance>, bool> done, HashSet<string>? fired = null)
    {
        (int, int)[] ratios = [(1, 1), (3, 1), (1, 3), (9, 1), (1, 9)];
        var known = world.Raw.ToDictionary(s => s.Id);
        var fresh = known.Values.ToList();
        for (var step = 0; step < steps && fresh.Count > 0 && !done(known); step++)
        {
            var all = Few(known.Values);
            var pairing = Few(fresh);
            var made = new List<Substance>();
            foreach (var process in world.Processes)
            {
                void Try(IReadOnlyList<Portion> inputs)
                {
                    var (result, laws) = world.Nature.Fire(process.Id, inputs);
                    if (fired is not null) foreach (var law in laws) fired.Add(law.Id);
                    if (result is not null && !known.ContainsKey(result.Id) && made.All(m => m.Id != result.Id)) made.Add(result);
                }

                foreach (var a in fresh) Try([new Portion(a, 1)]);
                if (process.MaxInputs < 2) continue;
                foreach (var a in pairing)
                foreach (var b in all)
                {
                    if (a.Id == b.Id) continue;
                    foreach (var (x, y) in ratios) Try([new Portion(a, x), new Portion(b, y)]);
                }
            }
            foreach (var m in made) known[m.Id] = m;
            fresh = made;
        }
        return known;
    }

    private static List<Substance> Few(IEnumerable<Substance> substances) =>
        [.. substances.GroupBy(s => s.Raw ? s.Id : s.Kind).SelectMany(group => group.Take(4))];
}
