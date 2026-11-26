using Codery.Village.Laws;

namespace Codery.Village.Life.Tests;

/// <summary>
/// The laws on a small hand-made nature, not the world's data. Risk: a law that does not do what its data says, or the same
/// result reached two ways counted as two substances, makes every discovery in the village wrong.
/// </summary>
public class LawsTests
{
    private static Profile P(params (Property Property, int Value)[] values) =>
        values.Aggregate(Profile.Zero, (profile, v) => profile.With(v.Property, v.Value));

    private static readonly Substance Clay = new("clay", "clay",
        P((Property.Hardness, 20), (Property.Flexibility, 70), (Property.Watertightness, 10), (Property.MeltingPoint, 60)), Raw: true);

    private static readonly Substance OtherClay = new("clay-b", "clay",
        P((Property.Hardness, 24), (Property.Flexibility, 70), (Property.Watertightness, 10), (Property.MeltingPoint, 60)), Raw: true);

    private static readonly Substance Copper = new("copper", "metal", P((Property.Hardness, 40), (Property.MeltingPoint, 54)), Raw: true);
    private static readonly Substance Tin = new("tin", "metal", P((Property.Hardness, 25), (Property.MeltingPoint, 12)), Raw: true);

    private static Nature Nature() => new(
        [new Process("fire", 800, null, 2), new Process("embers", 300, null, 2), new Process("smelt", 1200, null, 2), new Process("soak", 0, null, 2)],
        [
            new Law("soak-clay", "soak", [new Condition("clay", null, null, null, null)], [new Effect(Property.Flexibility, 10, null, null)], null),
            new Law("fire-clay", "fire",
                [new Condition("clay", null, null, null, null)],
                [new Effect(Property.Hardness, 40, null, null), new Effect(Property.Watertightness, 30, null, null), new Effect(Property.Flexibility, null, 0, null)],
                "ceramic"),
            // two metals, the main one between 75 and 95 parts in a hundred, both molten
            new Law("alloy", "smelt",
                [new Condition("metal", null, 100, null, null), new Condition(null, null, 75, 95, null), new Condition(null, null, null, null, true)],
                [new Effect(Property.Hardness, null, null, 15)],
                "alloy"),
        ]);

    [Fact]
    public void Clay_hardens_in_a_hot_fire()
    {
        var result = Nature().Apply("fire", [new Portion(Clay, 1)]);

        Assert.NotNull(result);
        Assert.Equal("ceramic", result.Kind);
        Assert.Equal(60, result.Profile[Property.Hardness]);
        Assert.Equal(40, result.Profile[Property.Watertightness]);
        Assert.Equal(0, result.Profile[Property.Flexibility]);
        Assert.False(result.Raw);
    }

    [Fact]
    public void A_fire_too_cold_changes_nothing() => Assert.Null(Nature().Apply("embers", [new Portion(Clay, 1)]));

    [Fact]
    public void The_same_result_by_two_routes_is_one_substance()
    {
        var alone = Nature().Apply("fire", [new Portion(Clay, 1)]);
        var mixed = Nature().Apply("fire", [new Portion(Clay, 3), new Portion(OtherClay, 1)]);

        Assert.NotNull(alone);
        Assert.NotNull(mixed);
        Assert.Equal(alone.Id, mixed.Id);
    }

    [Fact]
    public void Two_metals_melted_together_are_harder_than_both()
    {
        var result = Nature().Apply("smelt", [new Portion(Copper, 9), new Portion(Tin, 1)]);

        Assert.NotNull(result);
        Assert.Equal("alloy", result.Kind);
        Assert.Equal(55, result.Profile[Property.Hardness]);
        // outside the band the law does not fire, and nothing happens
        Assert.Null(Nature().Apply("smelt", [new Portion(Copper, 1), new Portion(Tin, 1)]));
    }

    [Fact]
    public void A_law_keeps_the_kind_it_works_on_whatever_else_went_in()
    {
        var result = Nature().Apply("soak", [new Portion(Copper, 1), new Portion(Clay, 1)]);

        Assert.NotNull(result);
        Assert.Equal("clay", result.Kind);
    }

    [Fact]
    public void A_use_scores_zero_below_any_minimum()
    {
        var knife = new Use("knife", [new Need(Property.Hardness, 50, 1), new Need(Property.Toughness, 0, 1)]);
        var wood = P((Property.Hardness, 30), (Property.Toughness, 50));
        var flint = P((Property.Hardness, 70), (Property.Toughness, 40));

        Assert.Equal(0, knife.Score(wood));
        Assert.True(knife.Score(flint) > 0);
    }
}
