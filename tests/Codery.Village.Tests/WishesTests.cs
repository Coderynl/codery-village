using System.Text.Json.Nodes;
using Codery.Village.Core.Data;
using Codery.Village.Modules.People;
using Codery.Village.Modules.Wishes;
using WishesModule = Codery.Village.Modules.Wishes.Wishes;

namespace Codery.Village.Tests;

public class WishesTests
{
    private static readonly int West = Builders.West;
    private static readonly int Oost = Builders.Oost;
    private static DateTimeOffset June(int day, int hour, int minute = 0) => Builders.June(day, hour, minute);

    private static readonly JsonObject Real = JsonNode.Parse(File.ReadAllText(Path.Combine(Builders.Folder, "wishes.json")))!.AsObject();
    private static readonly int Every = (int)Real["everyTicks"]!;

    /// <summary>The real wishes.json with only the café left, which everyone wishes for with the given weight.</summary>
    private static Action<JsonObject> OnlyCafe(double weight, double threshold) => wishes =>
    {
        var cafe = wishes["wishes"]!.AsArray().Single(wish => (string)wish!["id"]! == "cafe")!.DeepClone().AsObject();
        cafe["who"] = new JsonObject();
        cafe["weight"] = weight;
        cafe.Remove("afterDays");
        wishes["wishes"] = new JsonArray(cafe);
        wishes["taskThreshold"] = threshold;
    };

    private static double WeightOf(IReadOnlyList<(string Wish, double Weight)> wishes, string wish) =>
        wishes.Where(item => item.Wish == wish).Sum(item => item.Weight);

    private static WishesModule Wishes(Builders.Village village) => village.Modules.OfType<WishesModule>().Single();

    [Fact]
    public void A_wish_counts_for_whom_it_fits_and_weighs_after_sensitivity()
    {
        var village = Builders.Start(2, June(1, 9), more: [new WishesModule()]);
        village.Run(1);
        var wishes = Wishes(village);
        var list = Real["wishes"]!.AsArray().Select(wish => wish!.AsObject()).ToList();

        // who: any of the interests, any of the phases, every trait at least its minimum; some only after days
        foreach (var resident in village.People.All)
        {
            var expected = resident.IsEddie ? [] : list.Where(wish => Fits(wish, resident) && wish["afterDays"] is null).Select(wish => (string)wish["id"]!).ToList();
            Assert.Equal(expected, wishes.Of(resident));
        }

        // an island weighs weight × (1 + sensitivity / 100) for each who wishes it and does not have it
        foreach (var island in new[] { West, Oost })
        {
            foreach (var wish in list.Where(wish => wish["afterDays"] is null))
            {
                var id = (string)wish["id"]!;
                var weight = village.People.On(island).Where(resident => wishes.Of(resident).Contains(id) && !wishes.Fulfilled(resident, id))
                    .Sum(resident => (double)wish["weight"]! * (1 + resident.Traits.Sensitivity / 100.0));
                Assert.Equal(weight, WeightOf(wishes.Of(island), id), 6);
            }
            var weights = wishes.Of(island).Select(item => item.Weight).ToList();
            Assert.Equal(weights.OrderDescending(), weights);
        }

        // nothing in the base data serves a wish, so Community is offered none
        Assert.Empty(wishes.Wanted(West));
        Assert.Empty(wishes.Wanted(Oost));

        // a wish that comes after some days is not there on the first day, and is there after them
        var later = list.Where(wish => wish["afterDays"] is not null).ToList();
        Assert.NotEmpty(later);
        var days = later.Max(wish => (double)wish["afterDays"]!);
        village.JumpTo(June(1, 9).AddDays(days).AddMinutes(1));
        foreach (var resident in village.People.All.Where(resident => !resident.IsEddie))
            Assert.Equal(list.Where(wish => Fits(wish, resident)).Select(wish => (string)wish["id"]!), wishes.Of(resident));
    }

    // in the base data, which knows nothing a wish waits for with after
    private static bool Fits(JsonObject wish, Resident resident)
    {
        if (wish["after"] is not null) return false;
        var who = wish["who"]?.AsObject() ?? [];
        if (who["interests"] is JsonArray interests && !interests.Any(interest => resident.Interests.Contains((string)interest!))) return false;
        if (who["phases"] is JsonArray phases && !phases.Any(phase => string.Equals((string)phase!, resident.Phase.ToString(), StringComparison.OrdinalIgnoreCase))) return false;
        if (who["traits"] is JsonObject traits)
            foreach (var (trait, min) in traits)
                if (resident.Traits.Values[Personality.Traits.ToList().IndexOf(trait)] < (int)min!) return false;
        return true;
    }

    [Fact]
    public void A_wish_without_a_solution_becomes_one_task()
    {
        // below the threshold on both islands: no task
        var village = Builders.Start(1, June(1, 9), Builders.Data(wishes: OnlyCafe(10, 1e9)), more: [new WishesModule()]);
        village.Run(2 * Every);
        Assert.Null(Wishes(village).Task);
        Assert.Empty(village.Heard<TaskOpened>());
        var west = WeightOf(Wishes(village).Of(West), "cafe");
        var oost = WeightOf(Wishes(village).Of(Oost), "cafe");
        Assert.True(west > 0 && oost > 0 && west != oost);

        // over the threshold on one island, and over it on both: one task for the whole land, with the weight of both
        foreach (var threshold in new[] { (west + oost) / 2, Math.Min(west, oost) / 2 })
        {
            village = Builders.Start(1, June(1, 9), Builders.Data(wishes: OnlyCafe(10, threshold)), more: [new WishesModule()]);
            village.Run(2 * Every);
            var task = Assert.IsType<OpenTask>(Wishes(village).Task);
            Assert.Equal("cafe", task.Wish);
            Assert.Equal(west > oost ? West : Oost, task.Island);
            Assert.Equal(west + oost, task.Weight, 6);
            Assert.Single(village.Heard<TaskOpened>(), e => e.Task == task);
            Assert.Single(village.Engine.Log, line => line.Kind == "task-opened" && line.Subject == "cafe");

            // the text is the template with the numbers: how many wish it, where, and since when
            var island = Builders.RealMap.Islands.Single(i => i.Id == task.Island).Name;
            var count = village.People.On(task.Island).Count(resident => !resident.IsEddie);
            Assert.Equal("Het dorp wenst een café", task.TitleNl);
            Assert.Equal("The village wishes for a café", task.TitleEn);
            Assert.Contains($"{count} bewoners van {island}", task.BodyNl);
            Assert.Contains("1-6-2026", task.BodyNl);
            Assert.DoesNotContain("{", task.BodyNl);

            // it stays the one task
            village.Run(10 * Every);
            Assert.Equal(task, Wishes(village).Task);
            Assert.Single(village.Heard<TaskOpened>());
        }
        Assert.Empty(village.Engine.Stopped);
    }

    [Fact]
    public void A_release_closes_the_task_and_the_island_builds()
    {
        var setting = OnlyCafe(10, 1);
        var village = Builders.Start(1, June(1, 9), Builders.Data(wishes: setting), community: true, more: [new WishesModule()]);
        village.Run(Every);
        Assert.Equal("cafe", Wishes(village).Task?.Wish);
        var most = new[] { West, Oost }.MaxBy(island => WeightOf(Wishes(village).Of(island), "cafe"));

        // a restart without the release: the task stays
        var saved = village.Engine.Save();
        village = Builders.Load(saved, Builders.Data(wishes: setting), community: true, more: [new WishesModule()]);
        village.Run(2 * Every);
        Assert.Equal("cafe", Wishes(village).Task?.Wish);

        // a restart with the release: the task is gone, and Community is offered the café
        var extras = new[] { File.ReadAllText(Path.Combine(Builders.Folder, "releases", "cafe.json")) };
        village = Builders.Load(saved, Builders.Data(wishes: setting, extras: extras), community: true, more: [new WishesModule()]);
        village.Run(2);
        Assert.Null(Wishes(village).Task);
        Assert.Single(village.Heard<TaskClosed>(), e => e.Wish == "cafe");
        Assert.Single(village.Engine.Log, line => line.Kind == "task-closed" && line.Subject == "cafe");
        Assert.Equal("cafe", Wishes(village).Wanted(most)[0].Label);

        // within a few days a café stands on the island that wishes it most
        for (var day = 2; day <= 5 && !Cafe(village, most); day++)
        {
            village.Run(4 * 3600 * 4);
            if (!Cafe(village, most)) village.JumpTo(June(day, 9));
        }
        Assert.True(Cafe(village, most));

        // it fulfils the wish of those who live within its range, and nobody opens a new task for it
        var cafe = village.Building.All.First(structure => structure.Island == most && structure.Blueprint == "cafe" && structure.Done);
        var range = (int)JsonNode.Parse(extras[0])!["blueprints"]![0]!["range"]!;
        foreach (var resident in village.People.On(most).Where(resident => !resident.IsEddie))
        {
            var home = village.Building.HomeOf(resident.Id);
            Assert.Equal(home is { } at && at.DistanceTo(cafe.Anchor) <= range, Wishes(village).Fulfilled(resident, "cafe"));
        }
        Assert.Null(Wishes(village).Task);
        Assert.Empty(village.Engine.Stopped);
    }

    [Fact]
    public void A_wish_after_what_the_village_knows_comes_with_its_release()
    {
        // everyone wishes for something sweet, and for wine, which waits for grapes; nothing serves either
        Action<JsonObject> setting = wishes =>
        {
            var list = wishes["wishes"]!.AsArray();
            var fruit = list.Single(wish => (string)wish!["id"]! == "fruit")!.DeepClone().AsObject();
            var wine = list.Single(wish => (string)wish!["id"]! == "wine")!.DeepClone().AsObject();
            foreach (var wish in new[] { fruit, wine })
            {
                wish["who"] = new JsonObject();
                wish.Remove("afterDays");
            }
            fruit["weight"] = 1;
            wine["weight"] = 2;
            wishes["wishes"] = new JsonArray(fruit, wine);
            wishes["taskThreshold"] = 1;
        };
        Assert.Equal(["grapes"], Real["wishes"]!.AsArray().Single(wish => (string)wish!["id"]! == "wine")!["after"]!.AsArray().Select(id => (string)id!));

        // without the grapes the wine does not count: not for a resident, not for an island, not for the task
        var village = Builders.Start(1, June(1, 9), Builders.Data(wishes: setting), community: true, more: [new WishesModule()]);
        village.Run(Every);
        Assert.All(village.People.All.Where(resident => !resident.IsEddie), resident => Assert.Equal(["fruit"], Wishes(village).Of(resident)));
        Assert.DoesNotContain(Wishes(village).Of(West), item => item.Wish == "wine");
        Assert.Equal("fruit", Wishes(village).Task?.Wish);

        // with the grapes the fruit is known, Community is offered it, and the wine counts and becomes the task
        var extras = new[] { File.ReadAllText(Path.Combine(Builders.Folder, "releases", "grapes.json")) };
        village = Builders.Load(village.Engine.Save(), Builders.Data(wishes: setting, extras: extras), community: true, more: [new WishesModule()]);
        village.Run(Every);
        Assert.Single(village.Heard<TaskClosed>(), e => e.Wish == "fruit");
        Assert.All(village.People.All.Where(resident => !resident.IsEddie), resident => Assert.Equal(["fruit", "wine"], Wishes(village).Of(resident)));
        Assert.Contains(Wishes(village).Of(West), item => item.Wish == "wine");
        Assert.Equal([("fruit", WeightOf(Wishes(village).Of(West), "fruit"))], Wishes(village).Wanted(West));
        Assert.Equal("wine", Wishes(village).Task?.Wish);
        Assert.Empty(village.Engine.Stopped);
    }

    private static bool Cafe(Builders.Village village, int island) =>
        village.Building.All.Any(structure => structure.Island == island && structure.Blueprint == "cafe" && structure.Done);
}
