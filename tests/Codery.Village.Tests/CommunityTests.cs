using System.Text.Json.Nodes;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Modules;
using Codery.Village.Modules.Building;
using Codery.Village.Modules.Contracts;

namespace Codery.Village.Tests;

public class CommunityTests
{
    private static readonly int West = Builders.West;
    private static readonly int Oost = Builders.Oost;
    private static DateTimeOffset June(int day, int hour, int minute = 0) => Builders.June(day, hour, minute);

    /// <summary>The decisions of the first <paramref name="ticks"/> ticks: the sites per island, in the order they were placed.</summary>
    private static Dictionary<int, string[]> Sites(Builders.Village village) =>
        new[] { West, Oost }.ToDictionary(island => island, island =>
            village.Building.All.Where(structure => structure.Island == island && !structure.Done).OrderBy(structure => structure.Id)
                .Select(structure => structure.Blueprint).ToArray());

    private static void Stock(JsonObject economy, JsonObject west, JsonObject oost)
    {
        economy["start"]!["stock"]!["West"] = west;
        economy["start"]!["stock"]!["Oost"] = oost;
    }

    [Fact]
    public void A_homeless_household_gets_a_house_first()
    {
        // nobody has a home, and there is no food: still the house comes first
        var data = Builders.Data(
            building: building =>
            {
                building["maxStartHuts"] = 0;
                building["community"]!["maxSites"] = 1;
            },
            economy: economy => Stock(economy,
                new JsonObject { ["wood"] = 40, ["planks"] = 40 },
                new JsonObject { ["stone"] = 45, ["bricks"] = 20 }));
        var village = Builders.Start(1, June(1, 9), data, community: true);
        Assert.DoesNotContain(village.Building.All, structure => structure.Island == West || structure.Island == Oost);
        var decide = (int)JsonNode.Parse(File.ReadAllText(Path.Combine(Builders.Folder, "building.json")))!["community"]!["decideTicks"]!;

        village.Run(decide - 1);
        Assert.DoesNotContain(village.Building.All, structure => !structure.Done);
        village.Run(1);

        // each island the dwelling it can pay for, as near the heart of the island as there is a free lot
        var sites = Sites(village);
        Assert.Equal(["house"], sites[West]);
        Assert.Equal(["stone-house"], sites[Oost]);
        foreach (var island in new[] { West, Oost })
        {
            var site = village.Building.All.Single(structure => structure.Island == island && !structure.Done);
            var heart = village.Building.Heart(island);
            Assert.Equal(Ground.Lot, Builders.RealMap.At(site.Anchor));
            var closer = Builders.RealMap.Islands.Single(i => i.Id == island).Cells
                .Where(cell => cell.DistanceTo(heart) < site.Anchor.DistanceTo(heart));
            Assert.DoesNotContain(closer, cell => village.Building.CanPlace(site.Blueprint, cell, 0));
        }

        // at most maxSites at a time
        village.Run(3 * decide);
        Assert.Single(Sites(village)[West]);

        // the house goes up and a household moves in
        var house = village.Building.All.Single(structure => structure.Island == West && !structure.Done);
        village.JumpTo(June(2, 9));
        village.Run(2400);
        Assert.True(house.Done);
        Assert.NotNull(house.Household);
        Assert.True(village.Building.HasHome(house.Household!.Value));
        Assert.Contains(village.People.On(West), resident => resident.Household == house.Household);
        Assert.Empty(village.Engine.Stopped);
    }

    [Fact]
    public void Low_stock_brings_a_workshop_and_what_it_needs_first()
    {
        // housed, but without food or heat in stock
        var data = Builders.Data(
            economy: economy => Stock(economy,
                new JsonObject { ["wood"] = 40, ["planks"] = 40 },
                new JsonObject { ["stone"] = 45, ["bricks"] = 20 }));
        var village = Builders.Start(1, June(1, 9), data, community: true);
        var decide = (int)JsonNode.Parse(File.ReadAllText(Path.Combine(Builders.Folder, "building.json")))!["community"]!["decideTicks"]!;

        village.Run(2 * decide);

        // first a house for the newcomers; then food: on West bread wants grain, so the farm comes before the bakery
        var sites = Sites(village);
        Assert.Equal(["house", "farm"], sites[West]);
        Assert.Equal(["stone-house", "fisher"], sites[Oost]);
        var jetty = village.Building.All.Single(structure => structure.Blueprint == "fisher");
        Assert.Equal(Ground.Fishing, Builders.RealMap.At(jetty.Anchor));
        Assert.Equal(Oost, jetty.Island);

        // two at a time
        village.Run(3 * decide);
        Assert.Equal(sites[West], Sites(village)[West]);
        Assert.Equal(sites[Oost], Sites(village)[Oost]);
        Assert.Empty(village.Engine.Stopped);
    }

    [Fact]
    public void The_heaviest_wish_the_data_can_fulfil_is_built()
    {
        var extras = new[] { File.ReadAllText(Path.Combine(Builders.Folder, "demo", "cafe.json")) };
        var data = Builders.Data(building: building => building["community"]!["maxSites"] = 3, extras: extras);
        // a playground weighs more, but nothing in the data makes one
        var wishes = new FakeWishes(("playground", 9), ("cafe", 5), ("library", 2));
        var village = Builders.Start(1, June(1, 9), data, community: true, more: [wishes]);
        var decide = (int)JsonNode.Parse(File.ReadAllText(Path.Combine(Builders.Folder, "building.json")))!["community"]!["decideTicks"]!;

        village.Run(3 * decide);

        Assert.Equal(["house", "cafe"], Sites(village)[West]);
        Assert.Equal(["stone-house", "cafe"], Sites(village)[Oost]);
        Assert.Empty(village.Engine.Stopped);
    }
}

/// <summary>The same wishes on every island, as Wishes (task 11) will give them.</summary>
public sealed class FakeWishes(params (string Label, double Weight)[] wanted) : IModule, IWishSource
{
    public string Name => "wishes";
    public int Version => 1;
    public IReadOnlyList<(string Label, double Weight)> Wanted(int island) => wanted;
    public void Start(ModuleContext village) { }
    public void Step(ModuleContext village) { }
    public JsonNode Save() => new JsonObject();
    public void Load(ModuleContext village, JsonNode data, int version) { }
}
