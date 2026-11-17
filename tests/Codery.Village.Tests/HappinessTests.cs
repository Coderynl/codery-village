using System.Text.Json.Nodes;
using Codery.Village.Modules.Day;
using Codery.Village.Modules.People;
using HappinessModule = Codery.Village.Modules.Happiness.Happiness;
using WishesModule = Codery.Village.Modules.Wishes.Wishes;

namespace Codery.Village.Tests;

public class HappinessTests
{
    private static readonly int West = Builders.West;
    private static readonly int Islet = Builders.Islet;
    private static DateTimeOffset June(int day, int hour, int minute = 0) => Builders.June(day, hour, minute);
    private const int Hour = 3600 * 4;

    private static readonly JsonObject Settings = JsonNode.Parse(File.ReadAllText(Path.Combine(Builders.Folder, "life.json")))!["happiness"]!.AsObject();
    private static readonly int MaxStep = (int)Settings["maxStepPerHour"]!;

    private static HappinessModule Happiness(Builders.Village village) => village.Modules.OfType<HappinessModule>().Single();

    private static int Weighed(int points, Resident resident) =>
        points >= 0 ? points : (int)Math.Round(points * (1 + resident.Traits.Sensitivity / 100.0), MidpointRounding.AwayFromZero);

    [Fact]
    public void Happiness_is_base_and_its_reasons_and_the_minuses_weigh_after_sensitivity()
    {
        var village = Builders.Start(4, June(1, 9), more: [new WishesModule(), new HappinessModule()]);
        village.Run(1);
        var happiness = Happiness(village);
        var wishes = village.Modules.OfType<WishesModule>().Single();
        var reasons = Settings["reasons"]!.AsObject();
        var wishPoints = JsonNode.Parse(File.ReadAllText(Path.Combine(Builders.Folder, "wishes.json")))!["points"]!;

        foreach (var resident in village.People.All)
        {
            var given = happiness.Reasons(resident.Id).ToDictionary(reason => reason.Reason, reason => reason.Points);
            int Expect(string reason, bool had) => Weighed((int)reasons[reason]![had ? "plus" : "minus"]!, resident);

            // a home: everyone has moved into a hut on the first tick
            Assert.Equal(Expect("home", true), given["home"]);
            // food and heat: the island has them in stock; Eddie's islet is outside the economy
            if (resident.Island == Islet) Assert.False(given.ContainsKey("food") || given.ContainsKey("heat"));
            else Assert.Equal((Expect("food", true), Expect("heat", true)), (given["food"], given["heat"]));
            // work, for who is of working age
            if (resident.Phase == LifePhase.Working) Assert.Equal(Expect("work", village.Work.JobOf(resident.Id) is not null), given["work"]);
            else Assert.False(given.ContainsKey("work"));
            // free time had today: so far only who is at it now
            Assert.Equal(Expect("leisure", village.Day.Of(resident.Id).Activity is Activity.Leisure or Activity.Visit), given["leisure"]);
            // per wish that counts for him, fulfilled or not
            foreach (var wish in wishes.Of(resident))
            {
                var fulfilled = wishes.Fulfilled(resident, wish);
                Assert.Equal(Weighed((int)wishPoints[fulfilled ? "fulfilled" : "unfulfilled"]!, resident), given["wish:" + wish]);
            }
            Assert.Equal(given.Count, happiness.Reasons(resident.Id).Count);

            // a new village starts at its target, on the first tick
            Assert.Equal(Math.Clamp((int)Settings["base"]! + given.Values.Sum(), 0, 100), happiness.Of(resident.Id));
        }

        // free time had today comes from Day
        var free = village.People.All.FirstOrDefault(resident => village.Day.Of(resident.Id).Activity is Activity.Leisure or Activity.Visit);
        for (var quarter = 0; quarter < 40 && free is null; quarter++)
        {
            village.Run(900 * 4);
            free = village.People.All.FirstOrDefault(resident => village.Day.Of(resident.Id).Activity is Activity.Leisure or Activity.Visit);
        }
        Assert.NotNull(free);
        village.Run(2);
        Assert.Contains(("leisure", Weighed((int)reasons["leisure"]!["plus"]!, free)), happiness.Reasons(free.Id));
        Assert.Empty(village.Engine.Stopped);
    }

    [Fact]
    public void Happiness_moves_slowly()
    {
        // half past ten: the next calendar hour falls within what is watched
        var village = Builders.Start(1, June(1, 9), more: [new WishesModule(), new HappinessModule()]);
        village.Run(Hour * 3 / 2);
        var resident = village.People.On(West).First(resident => resident.Phase == LifePhase.Working);
        var before = Happiness(village).Of(resident.Id);

        // his hut is gone, and there is no other
        var saved = village.Engine.Save();
        var structures = saved.Modules.Single(part => part.Name == "building").Data["structures"]!.AsArray();
        structures.Remove(structures.Single(structure => structure!["households"]!.AsArray().Any(household => (int)household! == resident.Household)));
        village = Builders.Load(saved, Builders.Data(), more: [new WishesModule(), new HappinessModule()]);
        village.Run(1);
        Assert.Empty(village.Engine.Log.Where(line => line.Kind.StartsWith("module-")).Select(line => line.Subject + ": " + line.Detail));
        Assert.False(village.Building.HasHome(resident.Household!.Value));
        Assert.Contains(Happiness(village).Reasons(resident.Id), reason => reason.Reason == "home" && reason.Points < 0);

        // no calendar hour moves it more than maxStepPerHour, and it goes down
        var seen = new List<int> { before };
        for (var tick = 0; tick < 8 * Hour; tick++)
        {
            seen.Add(Happiness(village).Of(resident.Id));
            village.Run(1);
        }
        for (var at = 0; at + Hour < seen.Count; at++)
            Assert.True(Math.Abs(seen[at + Hour] - seen[at]) <= MaxStep, $"{seen[at]} to {seen[at + Hour]} in an hour from tick {at}");
        Assert.True(seen[^1] < before, $"{before} to {seen[^1]}");
        Assert.Empty(village.Engine.Stopped);
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(2, null)]
    [InlineData(3, 0)]
    [InlineData(5, 100)]
    public void Happiness_stays_within_0_and_100(ulong seed, int? start)
    {
        // the real numbers, or a base at an edge
        var data = Builders.Data(life: life =>
        {
            if (start is { } value) life["happiness"]!["base"] = value;
        });
        var village = Builders.Start(seed, June(1, 9), data, community: true, more: [new WishesModule(), new HappinessModule()]);
        for (var hour = 1; hour <= 72; hour++)
        {
            village.JumpTo(June(1, 9).AddHours(hour));
            village.Run(60);
            foreach (var resident in village.People.All)
                Assert.InRange(Happiness(village).Of(resident.Id), 0, 100);
        }
        Assert.Empty(village.Engine.Stopped);
    }
}
