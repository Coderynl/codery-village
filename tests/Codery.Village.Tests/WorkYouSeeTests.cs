using System.Text.Json;
using System.Text.Json.Nodes;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Save;
using Codery.Village.Modules.Building;
using Codery.Village.Modules.Contracts;
using Codery.Village.Modules.Day;
using Codery.Village.Modules.People;
using Codery.Village.Modules.Work;
using FireModule = Codery.Village.Modules.Fire.Fire;

namespace Codery.Village.Tests;

/// <summary>
/// Work you can see: builders fetch what a phase costs, gatherers bring their batch to the store, much-walked tiles
/// wear into paths, the fire keeper tends the fire, friendly residents help build, and free time goes to a place.
/// </summary>
public class WorkYouSeeTests
{
    private static readonly int West = Builders.West;
    private static readonly int Oost = Builders.Oost;

    private static DateTimeOffset June(int day, int hour, int minute = 0) => Builders.June(day, hour, minute);

    private static Cell Store(Builders.Village village, int island) => ((IPlaces)village.Building).GatheringOn(island)!.Value;

    [Fact]
    public void Builders_fetch_before_they_build()
    {
        // West has no branches but those of its brushwood, which makes a batch in 30 ticks
        var village = Builders.Start(1, June(1, 9), Builders.Data(
            economy: economy => economy["start"]!["stock"]!["West"]!.AsObject().Remove("branches"),
            catalog: catalog => Blueprint(catalog, "brushwood")["recipe"]!["ticks"] = 30, startWorkplaces: false));
        village.Run(1);
        var brushwood = village.PlaceNearHeart("brushwood", West);
        village.JumpTo(June(1, 10));
        village.Run(400);
        Assert.True(village.Get(brushwood).Done);
        village.Run(1);
        village.JumpTo(June(1, 10, 45));
        var ticks = 0;
        while (village.Work.StockAt(brushwood).GetValueOrDefault("branches") < 2 && ticks++ < 5000) village.Run(1);

        // in the evening a hut is placed; the branches are where they were made
        village.JumpTo(June(1, 20));
        var id = village.PlaceNearHeart("hut", West);
        var site = village.Get(id);
        var store = ((IStock)village.Work).SourceOf(West, "branches", site.Anchor);
        Assert.Equal(village.Get(brushwood).Anchor, store);
        var branches = village.Work.Count(West, "branches");

        // the morning after: the builders come to the site, the foundation is paid, and they go to the brushwood for it
        village.JumpTo(June(2, 10));
        var builders = village.People.All.Where(resident => village.Work.JobOf(resident.Id) is { Role: "builder" } && resident.Island == West).ToList();
        Assert.Equal(3, builders.Count);
        Assert.Equal(branches - 2, village.Work.Count(West, "branches"));
        Assert.All(builders, builder =>
        {
            var doing = village.Day.Of(builder.Id);
            Assert.Equal(Activity.Fetch, doing.Activity);
            Assert.Equal(store, doing.Place);
            Assert.NotNull(doing.Walk);
            // on the way out the hands are empty
            Assert.Null(doing.Carry);
        });
        Assert.Equal((0, 0), (site.Phase, site.Progress));

        // back with the branches on their backs; the phase grows only once they are back
        var carried = false;
        ticks = 0;
        while (site.Progress == 0 && ticks++ < 2000)
        {
            Assert.Equal(0, site.Phase);
            village.Run(1);
            foreach (var builder in builders)
                if (village.Day.Of(builder.Id) is { Activity: Activity.Fetch, Walk: not null } doing && doing.Place == site.Anchor)
                {
                    Assert.Equal("branches", doing.Carry);
                    carried = true;
                }
        }
        Assert.True(carried, "nobody carried the branches back");
        Assert.Equal(3, village.BuildersAt(site));
        // the tick they are back, the phase grows: each fetched once, and the site never waited for builders
        village.Run(1);
        Assert.All(builders, builder => Assert.Single(village.Heard<Reached>(), e => e.Resident == builder.Id && e.Errand == Activity.Fetch && e.At == store));
        Assert.DoesNotContain(village.Heard<ConstructionWaiting>(), e => e.Structure == id);
        Assert.DoesNotContain(village.Engine.Log, line => line.Kind == "waiting-builders" && line.Detail == id.ToString());
        village.Run(10);
        Assert.Equal(0, site.Phase);
        Assert.True(site.Progress > 0);
        Assert.Null(site.Waiting);
    }

    private static JsonNode Blueprint(JsonObject catalog, string id) =>
        catalog["blueprints"]!.AsArray().Single(blueprint => (string)blueprint!["id"]! == id)!;

    [Fact]
    public void Gatherers_bring_their_batch_to_their_workplace()
    {
        var data = Builders.Data(catalog: catalog => Blueprint(catalog, "berry-patch")["recipe"]!["ticks"] = 30, startWorkplaces: false);
        var village = Builders.Start(1, June(1, 9), data);
        village.Run(1);
        var id = village.PlaceNearHeart("berry-patch", West);
        village.JumpTo(June(1, 10));
        village.Run(400);
        var patch = village.Get(id);
        Assert.True(patch.Done);

        // after a stop the gatherers are at their place at once, each on his own tile
        village.JumpTo(June(1, 10, 45));
        var gatherers = village.People.All.Where(resident => village.Work.JobOf(resident.Id) is { } job && job.Building == id).ToList();
        Assert.NotEmpty(gatherers);
        var spots = gatherers.ToDictionary(gatherer => gatherer.Id, gatherer => village.Work.JobOf(gatherer.Id)!.Place);
        Assert.All(gatherers, gatherer => Assert.Equal(new Doing(Activity.Work, spots[gatherer.Id], spots[gatherer.Id]), village.Day.Of(gatherer.Id)));

        var before = village.Work.StockAt(id).ToDictionary();
        var ticks = 0;
        while (!village.Heard<Produced>().Any(e => e.Material == "berries") && ticks++ < 100) village.Run(1);
        // the batch is in the stock of the patch, at the patch
        var stock = village.Work.StockAt(id);
        Assert.Equal(before.GetValueOrDefault("berries") + 2, stock["berries"]);
        Assert.Equal(before.GetValueOrDefault("roots") + 1, stock["roots"]);
        // the batch is done in the tick before the ear hears it: who is not at the patch is on his way there with it
        var store = patch.Anchor;
        var bringing = gatherers.Where(gatherer => spots[gatherer.Id] != store).ToList();
        Assert.NotEmpty(bringing);
        Assert.All(bringing, gatherer =>
        {
            var doing = village.Day.Of(gatherer.Id);
            Assert.Equal(Activity.Bring, doing.Activity);
            Assert.Equal(store, doing.Place);
            Assert.Equal("berries", doing.Carry);
        });

        // each of them brings it there and goes back to his own tile (who is back first makes the next batch, and
        // brings that too)
        ticks = 0;
        while (bringing.Any(gatherer => !village.Heard<Returned>().Any(e => e.Resident == gatherer.Id && e.Errand == Activity.Bring)) && ticks++ < 2000) village.Run(1);
        Assert.All(bringing, gatherer =>
        {
            Assert.Equal(new Reached(0, gatherer.Id, Activity.Bring, store, "berries"),
                village.Heard<Reached>().First(e => e.Resident == gatherer.Id && e.Errand == Activity.Bring) with { Tick = 0 });
            Assert.Equal(spots[gatherer.Id], village.Heard<Returned>().First(e => e.Resident == gatherer.Id && e.Errand == Activity.Bring).At);
        });
    }

    [Fact]
    public void A_workplace_fetches_its_input_at_the_source()
    {
        // a berry patch that makes its batch from a bundle of reed, as a bakery from grain
        var data = Builders.Data(catalog: catalog =>
        {
            var recipe = Blueprint(catalog, "berry-patch")["recipe"]!;
            recipe["ticks"] = 30;
            recipe["in"] = new JsonObject { ["reed"] = 1 };
        }, startWorkplaces: false);
        var (village, id) = PatchFrom(June(1, 9), data);
        // the reed West starts with is where the island gathers: no workplace makes it
        var source = ((IStock)village.Work).SourceOf(West, "reed", village.Get(id).Anchor);
        Assert.Equal(Store(village, West), source);
        var reed = village.Work.Count(West, "reed");
        var gatherers = WorkersOf(village, id);

        // before the batch one of them fetches it there, and the batch waits for him
        var fetchers = new HashSet<int>();
        var ticks = 0;
        while (!village.Heard<Produced>().Any(e => e.Material == "berries") && ticks++ < 5000)
        {
            village.Run(1);
            var away = gatherers.Where(gatherer => village.Day.Of(gatherer).Activity == Activity.Fetch).ToList();
            fetchers.UnionWith(away);
            // taken in the tick he is back, not before
            if (fetchers.Count == 0 || away.Count > 0) Assert.Equal(reed, village.Work.Count(West, "reed"));
        }
        var fetcher = Assert.Single(fetchers);
        Assert.Single(village.Heard<Reached>(), e => e.Resident == fetcher && e.Errand == Activity.Fetch && e.At == source);
        var back = Assert.Single(village.Heard<Returned>(), e => e.Resident == fetcher && e.Errand == Activity.Fetch);
        Assert.Equal("reed", back.Carry);
        Assert.True(village.Heard<Produced>().First(e => e.Material == "berries").Tick > back.Tick);
        // the batch took it from the stock
        Assert.Equal(reed - 1, village.Work.Count(West, "reed"));
    }

    private static int HouseholdOf(Resident resident) => resident.Household ?? -1 - resident.Id;

    private static bool Grown(Resident resident) => resident.Phase is LifePhase.Working or LifePhase.Retired;

    [Fact]
    public void A_household_fetches_its_food_at_the_source()
    {
        var village = Builders.Start(1, June(1, 9));
        village.Run(1);
        // everyone can pay for his food
        foreach (var resident in village.People.All) resident.Money = 100_000;

        // Tuesday: the purchase is booked at midnight; the day shift comes home at 17:30
        village.JumpTo(June(2, 17, 15));
        Assert.Equal(0, village.Work.LastShopping(West).Short["food"]);
        while (village.Time.GetUtcNow() < June(2, 17, 30)) village.Run(1);
        village.Run(1);

        // who comes home goes for the food his household bought, to where it lies nearest home: one of a household
        var trips = new Dictionary<int, Trip>();
        foreach (var household in village.People.On(West).GroupBy(HouseholdOf))
        {
            var fetching = household.Where(resident => village.Day.TripOf(resident.Id) is { Errand.Kind: Activity.Fetch }).ToList();
            if (fetching.Count == 0) continue;
            var fetcher = Assert.Single(fetching);
            Assert.True(Grown(fetcher), $"{fetcher.Name} is a {fetcher.Phase}");
            var trip = village.Day.TripOf(fetcher.Id)!;
            Assert.Equal(Activity.Home, trip.Was);
            Assert.Equal(new Errand(Activity.Fetch, ((IStock)village.Work).SourceOf(West, "berries", trip.Home)!.Value, null, "berries"), trip.Errand);
            trips[fetcher.Id] = trip;
        }
        Assert.NotEmpty(trips);

        // there, and home with it on his back
        var ticks = 0;
        while (trips.Keys.Any(fetcher => village.Day.TripOf(fetcher) is not null) && ticks++ < 5000) village.Run(1);
        village.Run(1);
        Assert.True(village.Time.GetUtcNow() < June(2, 19), "the walks lasted past the home block");
        foreach (var (fetcher, trip) in trips)
        {
            Assert.Single(village.Heard<Reached>(), e => e.Resident == fetcher && e.Errand == Activity.Fetch && e.At == trip.Errand.Target);
            Assert.Equal(new Returned(0, fetcher, Activity.Fetch, trip.Home, "berries"),
                village.Heard<Returned>().Single(e => e.Resident == fetcher && e.Errand == Activity.Fetch) with { Tick = 0 });
        }
        // once a day: no household of West goes a second time (one whose grown came home later goes then)
        village.Run(90 * 60 * 4);
        var households = village.People.On(West).ToDictionary(resident => resident.Id, HouseholdOf);
        var went = village.Heard<Reached>().Where(e => e.Errand == Activity.Fetch && households.ContainsKey(e.Resident))
            .GroupBy(e => households[e.Resident]).ToDictionary(group => group.Key, group => group.Count());
        Assert.All(went, item => Assert.Equal(1, item.Value));
        Assert.All(trips.Keys, fetcher => Assert.Contains(households[fetcher], went.Keys));
    }

    private static string Local(DateTimeOffset at) => at.ToOffset(TimeSpan.FromHours(2)).ToString("ddd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Households_fetch_on_the_way_home_not_at_night()
    {
        // a fishing spot on Oost, whose fishers work the early shift
        var village = Builders.Start(1, June(1, 9));
        village.Run(1);
        var spot = village.PlaceNearHeart("fishing-spot", Oost);
        village.JumpTo(June(1, 10));
        for (var tick = 0; tick < 5000 && !village.Get(spot).Done; tick++) village.Run(1);
        Assert.True(village.Get(spot).Done);
        village.Run(1);
        foreach (var resident in village.People.All) resident.Money = 100_000;

        // from Monday 22:00 to Tuesday 18:30, tick by tick: when does a household set off for its food?
        village.JumpTo(June(1, 22));
        var started = new Dictionary<int, DateTimeOffset>();
        while (village.Time.GetUtcNow() < June(2, 18, 30))
        {
            village.Run(1);
            foreach (var resident in village.People.All)
                if (village.Day.TripOf(resident.Id) is { Errand.Kind: Activity.Fetch, Was: Activity.Home } && !started.ContainsKey(resident.Id))
                    started[resident.Id] = village.Time.GetUtcNow();
        }
        Assert.NotEmpty(started);
        var household = village.People.All.ToDictionary(resident => resident.Id, HouseholdOf);
        var members = village.People.All.ToLookup(HouseholdOf);
        var fetched = started.GroupBy(item => household[item.Key]).ToDictionary(group => group.Key, group => group.Min(item => item.Value));

        // nobody goes at night
        Assert.All(fetched.Values, at => Assert.False(at >= June(1, 22) && at < June(2, 6), $"a fetch at {Local(at)}"));

        // a household of the day shift goes as it comes home at 17:30 (who is careless comes home later)
        Shift? ShiftOf(Resident resident) => village.Work.JobOf(resident.Id)?.Shift;
        var day = members.Where(group => group.Where(Grown).All(member => ShiftOf(member) == Shift.Day)
            && group.Any(member => ShiftOf(member) == Shift.Day && member.Traits.Conscientiousness >= 50)).Select(group => group.Key).ToList();
        Assert.NotEmpty(day);
        Assert.All(day, key => Assert.True(fetched.TryGetValue(key, out var at) && at >= June(2, 17, 30) && at < June(2, 17, 45),
            $"household {key} went at {(fetched.TryGetValue(key, out var when) ? Local(when) : "no time")}"));

        // a household of the early shift goes from noon on, as its fisher comes home
        var early = members.Where(group => group.Any(member => ShiftOf(member) == Shift.Early)).Select(group => group.Key).ToList();
        Assert.NotEmpty(early);
        Assert.Contains(early, fetched.ContainsKey);
        Assert.All(early.Where(fetched.ContainsKey), key => Assert.True(fetched[key] >= June(2, 12), $"household {key} went at {Local(fetched[key])}"));
    }

    [Fact]
    public void A_household_with_no_one_to_send_still_eats()
    {
        var start = Builders.Start(1, June(1, 9));
        start.Run(1);
        // one household of West becomes a household of children, who are home at 17:30 too
        var saved = start.Engine.Save();
        var residents = saved.Modules.Single(part => part.Name == "people").Data["residents"]!.AsArray();
        var year = (long)JsonNode.Parse(File.ReadAllText(Path.Combine(Builders.Folder, "life.json")))!["ticksPerLifeYear"]!;
        var children = start.People.On(West).First(resident => Grown(resident) && resident.Household is not null).Household!.Value;
        foreach (var resident in residents.Where(resident => (int?)resident!["household"] == children)) resident!["bornTick"] = saved.Tick - 10 * year;
        var village = Builders.Load(saved, Builders.Data());
        var kids = village.People.On(West).Where(resident => HouseholdOf(resident) == children).ToList();
        Assert.NotEmpty(kids);
        foreach (var resident in village.People.All) resident.Money = 100_000;
        var berries = village.Work.Count(West, "berries");

        village.JumpTo(June(2, 17, 30));
        Assert.All(kids, kid => Assert.Equal(LifePhase.Child, kid.Phase));
        // the purchase is booked as before: every household of West has all its food, the children too
        var shopping = village.Work.LastShopping(West);
        Assert.Equal((village.People.On(West).Select(HouseholdOf).Distinct().Count(), 0), (shopping.Households, shopping.Short["food"]));
        Assert.Equal(berries - village.People.On(West).Count(), village.Work.Count(West, "berries"));

        // the other households go for theirs; the children stay home, all evening
        Assert.Contains(village.People.On(West), resident => village.Day.TripOf(resident.Id) is { Errand.Kind: Activity.Fetch });
        Assert.All(kids, kid => Assert.Equal(Activity.Home, village.Day.Of(kid.Id).Activity));
        village.Run(90 * 60 * 4);
        Assert.DoesNotContain(village.Heard<Reached>(), e => kids.Any(kid => kid.Id == e.Resident));
    }

    // a berry patch near the heart of West, standing, on Monday the first of June just after ten
    private static (Builders.Village Village, int Patch) BerryPatch(DataSet? data = null)
    {
        var village = Builders.Start(1, June(1, 9), data ?? Builders.Data(startWorkplaces: false));
        village.Run(1);
        var id = village.PlaceNearHeart("berry-patch", West);
        village.JumpTo(June(1, 10));
        village.Run(400);
        Assert.True(village.Get(id).Done);
        return (village, id);
    }

    private static List<int> WorkersOf(Builders.Village village, int structure) =>
        [.. village.People.All.Where(resident => village.Work.JobOf(resident.Id) is { } job && job.Building == structure).Select(resident => resident.Id)];

    private static JsonNode Economy(string key) => JsonNode.Parse(File.ReadAllText(Path.Combine(Builders.Folder, "economy.json")))![key]!;

    [Fact]
    public void Gatherers_work_out_on_the_ground_of_their_area()
    {
        var (village, id) = BerryPatch();
        village.JumpTo(June(1, 10, 45));
        var anchor = village.Get(id).Anchor;
        var area = village.Work.WorkAreaOf(id);
        var gatherers = WorkersOf(village, id);
        Assert.NotEmpty(gatherers);
        Assert.True(area.Count >= gatherers.Count, $"an area of {area.Count} tiles");
        Assert.All(area, cell => Assert.Equal((Ground.Fertile, (int?)West), (Builders.RealMap.At(cell), Builders.RealMap.IslandOf(cell))));

        // each at work on a tile of the area, standing, where his job says
        Assert.All(gatherers, gatherer =>
        {
            var doing = village.Day.Of(gatherer);
            Assert.Equal((Activity.Work, (Walk?)null), (doing.Activity, doing.Walk));
            Assert.Contains(doing.Place, area);
            Assert.Equal(village.Work.JobOf(gatherer)!.Place, doing.Place);
        });
        // spread out over the area, not all on the patch's own tile
        Assert.Equal(gatherers.Count, gatherers.Select(gatherer => village.Day.Of(gatherer).Place).Distinct().Count());
        Assert.Contains(gatherers, gatherer => village.Day.Of(gatherer).Place != anchor);
    }

    [Fact]
    public void A_fisher_works_on_the_shore()
    {
        var map = Builders.RealMap;
        var village = Builders.Start(1, June(1, 9), Builders.Data(startWorkplaces: false));
        village.Run(1);
        var id = village.PlaceNearHeart("fishing-spot", Oost);
        village.JumpTo(June(1, 10));
        for (var tick = 0; tick < 5000 && !village.Get(id).Done; tick++) village.Run(1);
        Assert.True(village.Get(id).Done);
        Assert.Equal(Ground.Fishing, map.At(village.Get(id).Anchor));
        // the fishers are hired in the tick after it stands
        village.Run(1);

        // the early shift, the next morning
        village.JumpTo(June(2, 6));
        var area = village.Work.WorkAreaOf(id);
        var fishers = WorkersOf(village, id);
        Assert.Equal(3, fishers.Count);
        Assert.All(fishers, fisher =>
        {
            var doing = village.Day.Of(fisher);
            Assert.Equal((Activity.Work, (Walk?)null), (doing.Activity, doing.Walk));
            Assert.Contains(doing.Place, area);
            Assert.True(map.IsWalkable(doing.Place));
            Assert.Equal(Oost, map.IslandOf(doing.Place));
            Assert.Contains(doing.Place.Neighbours(), cell => map.At(cell) == Ground.Fishing);
        });
        Assert.Equal(fishers.Count, fishers.Select(fisher => village.Day.Of(fisher).Place).Distinct().Count());
    }

    [Fact]
    public void A_worker_keeps_his_tile_for_a_while_and_is_paid_while_he_moves()
    {
        // slow walkers, so a walk from one tile to the next takes long enough to show in the wage
        var (village, id) = BerryPatch(Builders.Data(day: day => day["ticksPerCell"] = 200, startWorkplaces: false));
        var stay = (int)Economy("stayQuarters");
        var wage = (long)Economy("wagePerHour");
        const int Quarter = 15 * 60 * 4;

        // after a stop they are at work at once: from here on the walk to work is behind them
        village.JumpTo(June(1, 10, 45));
        var gatherers = WorkersOf(village, id);
        Assert.NotEmpty(gatherers);
        var money = gatherers.ToDictionary(gatherer => gatherer, gatherer => village.People.Get(gatherer).Money);

        // three hours, looked at in the middle of every quarter
        var places = gatherers.ToDictionary(gatherer => gatherer, _ => new List<Cell>());
        village.Run(Quarter / 2);
        for (var quarter = 0; quarter < 12; quarter++)
        {
            foreach (var gatherer in gatherers) places[gatherer].Add(village.Work.JobOf(gatherer)!.Place);
            village.Run(quarter < 11 ? Quarter : Quarter / 2);
        }

        // he keeps his tile for stayQuarters quarters; after that it may change
        foreach (var gatherer in gatherers)
        {
            var changes = Enumerable.Range(1, 11).Where(quarter => places[gatherer][quarter] != places[gatherer][quarter - 1]).ToList();
            for (var i = 1; i < changes.Count; i++)
                Assert.True(changes[i] - changes[i - 1] >= stay, $"moved after {changes[i] - changes[i - 1]} quarters");
        }
        Assert.Contains(gatherers, gatherer => places[gatherer].Distinct().Count() > 1);
        // paid every tick of the three hours, walking from tile to tile too: as much as a worker who never moved
        Assert.All(gatherers, gatherer => Assert.Equal(3 * wage, village.People.Get(gatherer).Money - money[gatherer]));
    }

    [Fact]
    public void The_workplace_does_not_move_during_an_errand()
    {
        // a new tile every quarter, so the errand crosses a moment his workplace would move
        var (village, id) = BerryPatch(Builders.Data(economy: economy => economy["stayQuarters"] = 1, startWorkplaces: false));
        var boundary = June(1, 11);
        village.JumpTo(boundary.AddSeconds(-10));
        var gatherers = WorkersOf(village, id);
        Assert.NotEmpty(gatherers);
        var left = gatherers.ToDictionary(gatherer => gatherer, gatherer => village.Day.Of(gatherer).Place);
        var anchor = village.Get(id).Anchor;
        var far = Builders.RealMap.Islands.Single(island => island.Id == West).Cells
            .Where(Builders.RealMap.IsWalkable).OrderByDescending(cell => cell.DistanceTo(anchor)).ThenBy(cell => cell.Row).ThenBy(cell => cell.Col).First();
        foreach (var gatherer in gatherers) Assert.True(village.Day.Send(gatherer, new Errand(Activity.Bring, far, "berries")));

        var ticks = 0;
        while (gatherers.Any(gatherer => village.Day.TripOf(gatherer) is not null) && ticks++ < 5000)
        {
            village.Run(1);
            foreach (var gatherer in gatherers.Where(gatherer => village.Day.TripOf(gatherer) is not null))
                Assert.Equal(left[gatherer], village.Work.JobOf(gatherer)!.Place);
        }
        Assert.True(village.Time.GetUtcNow() > boundary, "the errands ended before the quarter turned");
        // back on the tile he left, within the quarter; only now does his workplace move on
        Assert.True(village.Time.GetUtcNow() < boundary.AddMinutes(15));
        Assert.All(gatherers, gatherer => Assert.Equal(new Doing(Activity.Work, left[gatherer], left[gatherer]), village.Day.Of(gatherer)));
        Assert.Contains(gatherers, gatherer => village.Work.JobOf(gatherer)!.Place != left[gatherer]);
    }

    [Fact]
    public void An_errand_of_one_worker_does_not_move_the_others()
    {
        // a small area, so the workers' tiles crowd; each in turn is out on an errand as the stretch turns at 11:00
        var data = Builders.Data(economy: economy => economy["workRange"] = 1);
        var boundary = June(1, 11);
        var (first, id) = BerryPatch(data);
        foreach (var traveller in WorkersOf(first, id))
        {
            var (village, _) = BerryPatch(data);
            village.JumpTo(boundary.AddSeconds(-10));
            var others = WorkersOf(village, id).Where(worker => worker != traveller).ToList();
            var anchor = village.Get(id).Anchor;
            var far = Builders.RealMap.Islands.Single(island => island.Id == West).Cells
                .Where(Builders.RealMap.IsWalkable).OrderByDescending(cell => cell.DistanceTo(anchor)).ThenBy(cell => cell.Row).ThenBy(cell => cell.Col).First();
            Assert.True(village.Day.Send(traveller, new Errand(Activity.Bring, far, "berries")));
            village.Run(40);
            Assert.True(village.Time.GetUtcNow() > boundary);
            Assert.NotNull(village.Day.TripOf(traveller));

            // the others' tiles of the new stretch hold until it ends, also once he is back
            var tiles = others.ToDictionary(worker => worker, worker => village.Work.JobOf(worker)!.Place);
            for (var ticks = 0; village.Day.TripOf(traveller) is not null && ticks < 5000; ticks++) village.Run(1);
            Assert.Null(village.Day.TripOf(traveller));
            village.Run(15 * 60 * 4);
            Assert.All(others, worker => Assert.Equal(tiles[worker], village.Work.JobOf(worker)!.Place));
        }
    }

    [Fact]
    public void A_worker_turned_round_on_ground_outside_the_area_is_still_paid()
    {
        // fertile ground in strips with land between them, so the area is not of one piece
        var map = MapText.Parse("""
            ~~~~~~~~~~~~~~
            ~hhhhhhhhhhhh~
            ~hhhhhhhhhhhh~
            ~F..F..F.....~
            ~F..F..F.....~
            ~............~
            ~~~~~~~~~~~~~~
            island 1 1 Proef
            """);
        // a new tile every quarter, and a quarter for every step: a walk over the land between the strips is still
        // going when the quarter turns, and Day turns him round towards his new tile from where he is; no batch is
        // done in these hours, so no errand comes in between
        var village = Builders.Start(1, June(1, 9), Builders.Data(
            economy: economy => economy["stayQuarters"] = 1,
            day: day => day["ticksPerCell"] = 15 * 60 * 4,
            catalog: catalog => catalog["blueprints"]!.AsArray().Single(blueprint => (string)blueprint!["id"]! == "berry-patch")!["recipe"]!["ticks"] = 1_000_000), map: map);
        village.Run(1);
        var id = village.Building.Place("berry-patch", new Cell(3, 4), 0);
        village.JumpTo(June(1, 10));
        village.Run(400);
        Assert.True(village.Get(id).Done);
        village.Run(1);
        village.JumpTo(June(1, 10, 45));
        var gatherers = WorkersOf(village, id);
        Assert.NotEmpty(gatherers);
        var area = village.Work.WorkAreaOf(id);
        var wage = (long)Economy("wagePerHour");
        var money = gatherers.ToDictionary(gatherer => gatherer, gatherer => village.People.Get(gatherer).Money);

        var outside = false;
        for (var tick = 0; tick < 3 * 14400; tick++)
        {
            village.Run(1);
            outside |= gatherers.Any(gatherer => village.Day.Of(gatherer) is { Activity: Activity.Work, Walk: not null } doing && !area.Contains(doing.At));
        }
        Assert.True(outside, "nobody was turned round on ground outside the area");
        // paid every tick of the three hours
        Assert.All(gatherers, gatherer => Assert.Equal(3 * wage, village.People.Get(gatherer).Money - money[gatherer]));
    }

    // ── nature runs out ──

    private const int Low = 20;

    /// <summary>The real data with a berry patch that makes a batch in 30 ticks, and the economy changed as the test needs.</summary>
    internal static DataSet QuickBerries(Action<JsonObject>? economy = null) => Builders.Data(
        economy: economy,
        startWorkplaces: false,
        catalog: catalog => catalog["blueprints"]!.AsArray().Single(blueprint => (string)blueprint!["id"]! == "berry-patch")!["recipe"]!["ticks"] = 30);

    /// <summary>A berry patch near the heart of West, placed at nine on <paramref name="nine"/> and standing an hour later.</summary>
    internal static (Builders.Village Village, int Patch) PatchFrom(DateTimeOffset nine, DataSet data)
    {
        var village = Builders.Start(1, nine, data);
        village.Run(1);
        var id = village.PlaceNearHeart("berry-patch", West);
        village.JumpTo(nine.AddHours(1));
        village.Run(400);
        Assert.True(village.Get(id).Done);
        // the gatherers are hired in the tick after it stands; after a stop they are at work at once
        village.Run(1);
        village.JumpTo(nine.AddHours(1).AddMinutes(45));
        return (village, id);
    }

    /// <summary>Works the patch until a tile of its area runs low, and gives that tile.</summary>
    internal static Cell WorkUntilBare(Builders.Village village, int patch)
    {
        var area = village.Work.WorkAreaOf(patch);
        for (var tick = 0; tick < 20_000; tick++)
        {
            foreach (var cell in area)
                if (village.Work.YieldOf(cell) < Low) return cell;
            village.Run(1);
        }
        throw new Xunit.Sdk.XunitException("no tile of the area ran low");
    }

    private static long QuarterOf(Builders.Village village) => village.Time.GetUtcNow().ToUnixTimeSeconds() / (15 * 60);

    [Fact]
    public void A_worked_tile_runs_out_and_the_worker_moves_on()
    {
        var (village, id) = PatchFrom(June(1, 9), QuickBerries());
        var tile = WorkUntilBare(village, id);
        Assert.Contains(tile, village.Work.Bare);

        // the quarter it ran out in, who works it keeps it
        var holders = WorkersOf(village, id).Where(worker => village.Work.JobOf(worker)!.Place == tile).ToList();
        Assert.NotEmpty(holders);
        var quarter = QuarterOf(village);
        while (QuarterOf(village) == quarter)
        {
            Assert.All(holders, holder => Assert.Equal(tile, village.Work.JobOf(holder)!.Place));
            village.Run(1);
        }

        // the next quarter nobody is placed on it (but who is still out on an errand from it)
        var workers = WorkersOf(village, id);
        Assert.Contains(workers, worker => village.Day.TripOf(worker) is null);
        Assert.All(workers.Where(worker => village.Day.TripOf(worker) is null), worker => Assert.NotEqual(tile, village.Work.JobOf(worker)!.Place));
        Assert.True(village.Work.YieldOf(tile) < Low);
    }

    [Fact]
    public void A_bare_area_stands_still_until_it_grows_back()
    {
        // a tiny area, so the gatherers soon work all of it bare
        var (village, id) = PatchFrom(June(1, 9), QuickBerries(economy => economy["workRange"] = 1));
        var area = village.Work.WorkAreaOf(id);
        var anchor = village.Get(id).Anchor;
        for (var tick = 0; tick < 50_000 && area.Any(cell => village.Work.YieldOf(cell) >= Low); tick++) village.Run(1);
        Assert.All(area, cell => Assert.Contains(cell, village.Work.Bare));

        // from the next quarter on, and once back from their errands, they stand at the patch and make nothing
        var quarter = QuarterOf(village);
        while (QuarterOf(village) == quarter) village.Run(1);
        var gatherers = WorkersOf(village, id);
        for (var tick = 0; tick < 5000 && gatherers.Any(gatherer => village.Day.TripOf(gatherer) is not null); tick++) village.Run(1);
        // what the patch made: the berries and roots of West
        int Made() => village.Heard<Produced>().Count(e => e.Island == West && e.Material is "berries" or "roots");
        var made = Made();
        village.Run(15 * 60 * 4);
        Assert.All(gatherers, gatherer => Assert.Equal(anchor, village.Work.JobOf(gatherer)!.Place));
        Assert.Equal(made, Made());

        // after regrowDays it grows back, and the patch makes berries again
        var regrow = (int)Economy("nature")!["fertile"]!["regrowDays"]!;
        village.JumpTo(June(1 + regrow, 10, 45));
        Assert.Contains(area, cell => village.Work.YieldOf(cell) >= Low);
        for (var tick = 0; tick < 5000 && Made() == made; tick++) village.Run(1);
        Assert.True(Made() > made, "the patch made nothing after it grew back");
    }

    [Fact]
    public void One_tile_gone_bare_moves_only_its_worker()
    {
        // a stretch of a whole day, so the quarters that follow do not move anyone by themselves
        var data = Builders.Data(economy: economy => economy["stayQuarters"] = 96, startWorkplaces: false);
        var (village, id) = BerryPatch(data);
        village.JumpTo(June(1, 10, 45));
        var gatherers = WorkersOf(village, id);
        Assert.True(gatherers.Count > 1);
        var before = gatherers.ToDictionary(gatherer => gatherer, gatherer => village.Work.JobOf(gatherer)!.Place);
        var worker = gatherers[0];

        // the tile of one of them has gone bare (as a save holds it)
        var saved = SaveJson.FromJson(SaveJson.ToJson(village.Engine.Save()));
        var work = saved.Modules.Single(part => part.Name == "work").Data;
        work["nature"]!["tiles"]!.AsArray().Add(JsonSerializer.SerializeToNode(new NatureTile(before[worker], 0, Ground.Fertile, 0), SaveJson.Options));
        var bare = Builders.Load(saved, data);
        Assert.Contains(before[worker], bare.Work.Bare);

        // he moves to a tile that yields; the others keep theirs, this quarter and the next
        for (var quarter = 0; quarter < 2; quarter++)
        {
            Assert.NotEqual(before[worker], bare.Work.JobOf(worker)!.Place);
            Assert.True(bare.Work.YieldOf(bare.Work.JobOf(worker)!.Place) >= Low);
            Assert.All(gatherers.Where(gatherer => gatherer != worker), gatherer => Assert.Equal(before[gatherer], bare.Work.JobOf(gatherer)!.Place));
            bare.Run(15 * 60 * 4);
        }
    }

    [Fact]
    public void Ground_does_not_grow_back_outside_its_months()
    {
        // fertile ground that grows from April to October only
        var data = QuickBerries(economy => economy["nature"]!["fertile"]!["growsInMonths"] = new JsonArray(4, 5, 6, 7, 8, 9, 10));

        // worked bare on the first of December, it is as bare a fortnight later
        var winter = new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.FromHours(1));
        var (cold, coldPatch) = PatchFrom(winter, data);
        var frozen = WorkUntilBare(cold, coldPatch);
        var left = cold.Work.YieldOf(frozen);
        cold.JumpTo(new DateTimeOffset(2026, 12, 15, 1, 0, 0, TimeSpan.FromHours(1)));
        Assert.Equal(left, cold.Work.YieldOf(frozen));
        Assert.Contains(frozen, cold.Work.Bare);

        // worked bare on the first of June, it is back by the third
        var (summer, summerPatch) = PatchFrom(June(1, 9), data);
        var grown = WorkUntilBare(summer, summerPatch);
        summer.JumpTo(June(3, 1));
        Assert.True(summer.Work.YieldOf(grown) >= Low, $"a yield of {summer.Work.YieldOf(grown)}");
        Assert.DoesNotContain(grown, summer.Work.Bare);
    }

    // Risk: gathering branches strips the forest on the page, as if the gatherers felled it.
    [Fact]
    public void A_gathered_forest_is_not_bare()
    {
        // the brushwood West starts with, making a batch in 30 ticks
        var data = Builders.Data(catalog: catalog => Blueprint(catalog, "brushwood")["recipe"]!["ticks"] = 30);
        var village = Builders.Start(1, June(1, 10, 45), data);
        village.Run(1);
        var brushwood = village.Building.All.Single(structure => structure.Island == West && structure.Blueprint == "brushwood").Id;

        var gathered = WorkUntilBare(village, brushwood);
        Assert.Equal(Ground.Forest, Builders.RealMap.At(gathered));
        Assert.DoesNotContain(gathered, village.Work.Bare);

        // a felled tree is bare
        var tree = village.Work.WorkAreaOf(brushwood).First(cell => cell != gathered);
        var saved = SaveJson.FromJson(SaveJson.ToJson(village.Engine.Save()));
        saved.Modules.Single(part => part.Name == "work").Data["felled"]!.AsArray()
            .Add(new JsonObject { ["cell"] = JsonSerializer.SerializeToNode(tree, SaveJson.Options), ["until"] = saved.Tick + 100_000 });
        var felled = Builders.Load(saved, data);
        Assert.Contains(tree, felled.Work.Bare);
        Assert.DoesNotContain(gathered, felled.Work.Bare);
    }

    // ── builders without a site ──

    private static List<int> BuildersOn(Builders.Village village, int island) =>
        [.. village.People.All.Where(resident => resident.Island == island && village.Work.JobOf(resident.Id) is { Role: "builder" }).Select(resident => resident.Id)];

    private static List<int> StartWorkplaces(Builders.Village village, int island) =>
        [.. village.Building.All.Where(structure => structure.Island == island && structure.Done
            && structure.Blueprint is "berry-patch" or "brushwood" or "fishing-spot" or "driftwood-beach").Select(structure => structure.Id)];

    // Risk: with nothing to build, the builders stand at the fire all day instead of working on the land.
    [Fact]
    public void A_builder_without_a_site_helps_on_the_land()
    {
        var village = Builders.Start(1, June(1, 9));
        village.Run(1);
        Assert.DoesNotContain(village.Building.All, structure => structure.Island == West && !structure.Done);
        village.JumpTo(June(1, 10, 45));

        var workplaces = StartWorkplaces(village, West);
        var area = workplaces.SelectMany(village.Work.WorkAreaOf).ToHashSet();
        var held = workplaces.SelectMany(id => WorkersOf(village, id)).Select(worker => village.Work.JobOf(worker)!.Place).ToHashSet();
        var builders = BuildersOn(village, West);
        Assert.Equal(3, builders.Count);
        var money = builders.ToDictionary(builder => builder, builder => village.People.Get(builder).Money);

        // each at work, standing on a tile of a work area of his own: not the fire, not a gatherer's tile
        Assert.All(builders, builder =>
        {
            var doing = village.Day.Of(builder);
            Assert.Equal((Activity.Work, (Walk?)null), (doing.Activity, doing.Walk));
            Assert.Contains(doing.Place, area);
            Assert.NotEqual(Store(village, West), doing.Place);
            Assert.DoesNotContain(doing.Place, held);
        });
        Assert.Equal(builders.Count, builders.Select(builder => village.Day.Of(builder).Place).Distinct().Count());

        // and paid for it, as at a site
        village.Run(15 * 60 * 4);
        Assert.All(builders, builder => Assert.True(village.People.Get(builder).Money > money[builder], "a helper was not paid"));
    }

    // Risk: helping builders speed up a workplace or wear out its ground, so the economy shifts with every idle builder.
    [Fact]
    public void Helping_adds_nothing_to_production()
    {
        // berries in batches of 30 ticks, so a morning wears the patch's ground; without paths, so the helpers' walks
        // do not speed up the gatherers'
        (int Made, List<int> Yields, int Helped) Morning(string builderShift)
        {
            var village = Builders.Start(1, June(1, 9), Builders.Data(
                economy: economy => economy["builderShift"] = builderShift,
                day: day => day["pathThreshold"] = 1_000_000,
                catalog: catalog => Blueprint(catalog, "berry-patch")["recipe"]!["ticks"] = 30));
            village.Run(1);
            village.JumpTo(June(1, 10, 45));
            var patch = village.Building.All.Single(structure => structure.Island == West && structure.Blueprint == "berry-patch").Id;
            var area = village.Work.WorkAreaOf(patch);
            var builders = BuildersOn(village, West);
            var made = village.Heard<Produced>().Count(e => e.Island == West && e.Material == "berries");
            var helped = 0;
            for (var tick = 0; tick < 2 * 60 * 60 * 4; tick++)
            {
                village.Run(1);
                helped += builders.Count(builder => village.Day.Of(builder) is { Activity: Activity.Work, Walk: null } doing && area.Contains(doing.Place));
            }
            return (village.Heard<Produced>().Count(e => e.Island == West && e.Material == "berries") - made, [.. area.Select(village.Work.YieldOf)], helped);
        }

        // the builders help by day; on the night shift they sleep while the patch is worked
        var helped = Morning("day");
        var alone = Morning("night");
        Assert.True(helped.Helped > 0, "nobody helped at the patch");
        Assert.Equal(0, alone.Helped);
        Assert.True(alone.Made > 0);
        Assert.Equal(alone.Made, helped.Made);
        Assert.Equal(alone.Yields, helped.Yields);
    }

    // Risk: a builder with nothing to do waits at the fire, paid, or loses his place for good.
    [Fact]
    public void A_builder_with_nothing_to_help_has_free_time()
    {
        // free time by day never goes to the fire or the shop here, so who stands there was sent there
        var data = Builders.Data(day: day =>
        {
            day["outings"] = new JsonArray(
                new JsonObject { ["to"] = "water", ["weight"] = 3 },
                new JsonObject { ["to"] = "tree", ["weight"] = 2 },
                new JsonObject { ["to"] = "neighbour", ["weight"] = 2 });
            day["free"]!.AsArray().Remove(day["free"]!.AsArray().Single(choice => (string)choice!["do"]! == "shop"));
        });
        var start = Builders.Start(1, June(1, 9), data);
        start.Run(1);
        start.JumpTo(June(1, 10, 30));
        var builders = BuildersOn(start, West);
        Assert.Equal(3, builders.Count);

        // every tile of West's workplaces worked bare, as a save holds it
        var saved = SaveJson.FromJson(SaveJson.ToJson(start.Engine.Save()));
        var tiles = saved.Modules.Single(part => part.Name == "work").Data["nature"]!["tiles"]!.AsArray();
        var area = StartWorkplaces(start, West).SelectMany(start.Work.WorkAreaOf).Distinct().ToList();
        foreach (var cell in area) tiles.Add(JsonSerializer.SerializeToNode(new NatureTile(cell, 0, Builders.RealMap.At(cell), 0), SaveJson.Options));
        var village = Builders.Load(saved, data);
        Assert.All(area, cell => Assert.True(village.Work.YieldOf(cell) < Low));

        // the next quarter they have free time: not at work, not at the fire, and not paid
        village.JumpTo(June(1, 10, 45));
        var money = builders.ToDictionary(builder => builder, builder => village.People.Get(builder).Money);
        for (var minute = 0; minute < 15; minute++)
        {
            Assert.All(builders, builder =>
            {
                var doing = village.Day.Of(builder);
                Assert.NotEqual(Activity.Work, doing.Activity);
                Assert.NotEqual(Store(village, West), doing.Place);
            });
            village.Run(60 * 4);
        }
        Assert.All(builders, builder => Assert.Equal(money[builder], village.People.Get(builder).Money));

        // once the land has grown back they are builders at work again, from the quarter after it grew
        village.JumpTo(June(2, 10, 30));
        village.JumpTo(June(2, 10, 45));
        Assert.All(builders, builder =>
        {
            Assert.Equal("builder", village.Work.JobOf(builder)?.Role);
            Assert.Equal(Activity.Work, village.Day.Of(builder).Activity);
        });
    }

    // Risk: hiring fills the first workplace of an island and leaves the next without anyone for days.
    [Fact]
    public void Hiring_spreads_over_the_workplaces()
    {
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(Builders.Folder, "catalog.json")))!["blueprints"]!.AsArray();
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var village = Builders.Start(seed, June(1, 9));
            village.Run(1);
            foreach (var island in new[] { West, Oost })
            {
                var workplaces = StartWorkplaces(village, island).Select(id =>
                {
                    var blueprint = village.Get(id).Blueprint;
                    var seats = catalog.Single(plan => (string)plan!["id"]! == blueprint)!["jobs"]!.AsArray().Sum(job => (int)job!["count"]!);
                    return (Blueprint: blueprint, Held: WorkersOf(village, id).Count, Seats: seats);
                }).ToList();
                Assert.Equal(2, workplaces.Count);
                var jobless = village.People.All.Count(resident => resident is { Phase: LifePhase.Working, IsEddie: false } && resident.Island == island
                    && village.Work.JobOf(resident.Id) is null);
                // every one has a worker while the island has hands left, and none has two more than another that
                // still has a free seat
                Assert.All(workplaces, workplace => Assert.True(workplace.Held > 0 || jobless == 0, $"seed {seed}: {workplace.Blueprint} has nobody"));
                foreach (var full in workplaces)
                    foreach (var open in workplaces.Where(other => other.Held < other.Seats))
                        Assert.True(full.Held <= open.Held + 1, $"seed {seed}: {full.Blueprint} has {full.Held}, {open.Blueprint} {open.Held} of {open.Seats}");
            }
        }
    }

    private static Action<JsonObject> Paths(int window, int threshold, int keep, double speed) => day =>
    {
        day["pathWindowDays"] = window;
        day["pathThreshold"] = threshold;
        day["pathKeep"] = keep;
        day["pathSpeed"] = speed;
    };

    /// <summary>Sends the resident to <paramref name="target"/> and back, as often as asked, and waits until he is back.</summary>
    private static void GoBackAndForth(Builders.Village village, int resident, Cell target, int times)
    {
        for (var trip = 0; trip < times; trip++)
        {
            Assert.True(village.Day.Send(resident, new Errand(Activity.Bring, target, "stones")));
            var ticks = 0;
            while (village.Day.TripOf(resident) is not null && ticks++ < 5000) village.Run(1);
            Assert.Null(village.Day.TripOf(resident));
        }
    }

    // a resident of West who works outside, and a tile of West far from his place
    private static (int Resident, Cell From, Cell Far) Wanderer(Builders.Village village)
    {
        var resident = village.People.All.First(who => who.Island == West && village.Day.Of(who.Id) is { Inside: false, Walk: null });
        var from = village.Day.Of(resident.Id).At;
        var far = Builders.RealMap.Islands.Single(island => island.Id == West).Cells
            .Where(Builders.RealMap.IsWalkable).OrderByDescending(cell => cell.DistanceTo(from)).ThenBy(cell => cell.Row).ThenBy(cell => cell.Col).First();
        return (resident.Id, from, far);
    }

    [Fact]
    public void A_well_used_tile_becomes_a_path_and_an_unused_path_grows_over()
    {
        var change = Paths(window: 2, threshold: 3, keep: 1, speed: 1.6);
        var village = Builders.Start(1, June(1, 10), Builders.Data(day: change));
        village.Run(1);
        var (resident, from, far) = Wanderer(village);
        // the middle of the walk there; the walk back goes another way, as the shortest way back may
        var route = Builders.RealMap.FindRoute(from, far)!.Cells;
        var middle = route[route.Count / 2];

        GoBackAndForth(village, resident, far, 3);
        // three steps on it: not yet a path
        Assert.DoesNotContain(middle, village.Day.Paths);
        GoBackAndForth(village, resident, far, 1);
        Assert.Contains(middle, village.Day.Paths);

        // paths are part of the saved village
        var loaded = Builders.Load(village.Engine.Save(), Builders.Data(day: change));
        Assert.Equal(village.Day.Paths.OrderBy(cell => (cell.Row, cell.Col)), loaded.Day.Paths.OrderBy(cell => (cell.Row, cell.Col)));

        // the next day it is still in the window
        village.JumpTo(June(2, 3));
        Assert.Contains(middle, village.Day.Paths);
        // once its steps are out of the window, it grows over
        village.JumpTo(June(4, 3));
        Assert.DoesNotContain(middle, village.Day.Paths);
    }

    [Fact]
    public void Paths_are_faster_and_preferred()
    {
        // on the map: of two ways round a lake, the one that costs less is taken, though it is longer
        var map = MapText.Parse("""
            ~~~~~~~~~
            ~.......~
            ~.~~~~~.~
            ~.~~~~~.~
            ~.......~
            ~~~~~~~~~
            island 1 1 Proef
            """);
        var (from, to) = (new Cell(1, 2), new Cell(1, 6));
        Assert.All(map.FindRoute(from, to)!.Cells, cell => Assert.Equal(1, cell.Row));
        var below = map.FindRoute(from, to, cell => cell.Row == 1 ? 100 : 10)!.Cells;
        Assert.Contains(below, cell => cell.Row == 4);
        Assert.Equal(map.FindRoute(from, to)!.Cells, map.FindRoute(from, to, _ => 100)!.Cells);

        // in the village: a walk over a path is faster, and Day prefers the path
        var change = Paths(window: 3, threshold: 3, keep: 1, speed: 2);
        var village = Builders.Start(1, June(1, 10), Builders.Data(day: change));
        village.Run(1);
        var (resident, start, far) = Wanderer(village);
        var before = village.Day.Paths.ToHashSet();
        Assert.True(village.Day.Send(resident, new Errand(Activity.Bring, far, "stones")));
        var walk = village.Day.Of(resident).Walk!;
        Assert.Equal(8, walk.TicksPerCell);
        while (village.Day.TripOf(resident) is not null) village.Run(1);
        GoBackAndForth(village, resident, far, 3);
        var worn = village.Day.Paths.Except(before).ToHashSet();
        Assert.NotEmpty(worn);

        Assert.True(village.Day.Send(resident, new Errand(Activity.Bring, far, "stones")));
        var over = village.Day.Of(resident).Walk!;
        var paths = village.Day.Paths.ToHashSet();
        Assert.True(over.TicksPerCell < 8, $"{over.TicksPerCell} ticks a tile over the path");
        Assert.Equal(Builders.RealMap.FindRoute(start, far, cell => paths.Contains(cell) ? 50 : 100)!.Cells, over.Cells);
    }

    [Fact]
    public void The_fire_keeper_lights_the_fire_at_dusk_and_feeds_it()
    {
        var data = Builders.Data(day: day =>
        {
            day["dusk"] = new JsonArray([.. Enumerable.Range(0, 12).Select(_ => JsonValue.Create("21:00"))]);
            day["dawn"] = new JsonArray([.. Enumerable.Range(0, 12).Select(_ => JsonValue.Create("06:00"))]);
            day["fireFeedMinutes"] = 15;
            day["fireReserveDays"] = 1;
            day["fireSmoulderMinutes"] = 60;
        });
        var village = Builders.Start(1, June(1, 20, 45), data, more: new FireModule());
        var fire = village.Modules.OfType<FireModule>().Single();
        village.Run(1);
        var campfire = village.Building.All.Single(structure => structure.Island == West && structure.Blueprint == "campfire");
        var keeper = village.People.All.Single(resident => village.Work.JobOf(resident.Id) is { Role: "fire-keeper" } job && job.Building == campfire.Id).Id;
        Assert.Equal(0, fire.Strength(campfire.Id));

        // at dusk he goes to the fire and lights it, with a piece of firewood
        village.Run(14 * 60 * 4);
        Assert.Equal(0, fire.Strength(campfire.Id));
        var firewood = village.Work.Count(West, "firewood");
        var tended = false;
        var ticks = 0;
        while (fire.Strength(campfire.Id) == 0 && ticks++ < 20 * 60 * 4)
        {
            village.Run(1);
            tended |= village.Day.Of(keeper).Activity == Activity.Tend;
        }
        Assert.True(tended, "the fire keeper did not go to the fire");
        Assert.Equal(3, fire.Strength(campfire.Id));
        village.Run(1);
        Assert.Contains(village.Heard<Reached>(), e => e.Resident == keeper && e.Errand == Activity.Tend && e.At == campfire.Anchor);
        Assert.Equal(firewood - 1, village.Work.Count(West, "firewood"));

        // through the night he feeds it
        village.Run(40 * 60 * 4);
        Assert.Equal(3, fire.Strength(campfire.Id));
        Assert.True(village.Work.Count(West, "firewood") <= firewood - 3);

        // by day it smoulders, and goes out
        village.JumpTo(June(2, 6, 15));
        Assert.Equal(1, fire.Strength(campfire.Id));
        village.JumpTo(June(2, 7, 30));
        Assert.Equal(0, fire.Strength(campfire.Id));

        // the next night it is lit again; without firewood to spare it sinks, a step for every feed that cannot be
        // done: the keeper leaves the homes what they need of it for a day, a piece per household
        village.JumpTo(June(2, 20, 59));
        village.Run(20 * 60 * 4);
        Assert.Equal(3, fire.Strength(campfire.Id));
        var households = village.People.On(West).Select(resident => resident.Household ?? -1 - resident.Id).Distinct().Count();
        Assert.True(village.Work.TryTake(West, new Dictionary<string, int> { ["firewood"] = village.Work.Count(West, "firewood") - households }, campfire.Anchor));
        var seen = new List<int>();
        for (var step = 0; step < 6 * 15 * 60 * 4; step++)
        {
            village.Run(1);
            if (seen.Count == 0 || seen[^1] != fire.Strength(campfire.Id)) seen.Add(fire.Strength(campfire.Id));
        }
        Assert.Equal([3, 2, 1, 0], seen);
        Assert.Equal(households, village.Work.Count(West, "firewood"));

        // the fire is part of the saved village
        var loaded = Builders.Load(village.Engine.Save(), data, more: new FireModule());
        Assert.Equal(fire.Save().ToJsonString(), loaded.Modules.OfType<FireModule>().Single().Save().ToJsonString());
        Assert.Empty(village.Engine.Stopped);
    }

    [Fact]
    public void Friendly_residents_help_build_in_their_free_time()
    {
        // one builder a island; everyone without a job has free time from nine and takes it at the water, or helps
        Builders.Village Village(int helpFrom) => Builders.Start(2, June(1, 9), Builders.Data(
            economy: economy => economy["builders"] = 1,
            day: day =>
            {
                day["helpFrom"] = helpFrom;
                day["free"] = new JsonArray(new JsonObject { ["do"] = "leisure", ["weight"] = 1 });
            },
            startWorkplaces: false));

        int Finish(Builders.Village village, out List<int> helpers, out int fullCrewTicks)
        {
            village.Run(1);
            var id = village.PlaceNearHeart("hut", West);
            var site = village.Get(id);
            village.JumpTo(June(1, 10));
            helpers = [];
            fullCrewTicks = 0;
            var money = village.People.All.ToDictionary(resident => resident.Id, resident => resident.Money);
            var ticks = 0;
            while (!site.Done && ticks++ < 9000)
            {
                var progress = (site.Phase, site.Progress);
                village.Run(1);
                foreach (var resident in village.People.All.Where(who => who.Island == West))
                    if (village.Day.Of(resident.Id) is { Activity: Activity.Help } && !helpers.Contains(resident.Id)) helpers.Add(resident.Id);
                if ((site.Phase, site.Progress) != progress) fullCrewTicks++;
            }
            // helpers are not paid
            foreach (var helper in helpers) Assert.Equal(money[helper], village.People.Get(helper).Money);
            return ticks;
        }

        var alone = Finish(Village(101), out var none, out _);
        var helped = Finish(Village(0), out var helpers, out var crewTicks);

        Assert.Empty(none);
        Assert.NotEmpty(helpers);
        Assert.True(helped < alone, $"with help {helped} ticks, alone {alone}");
        // never faster than a full crew: a hut is 2400 ticks of a full crew
        Assert.True(helped >= 2400, $"with help {helped} ticks");
        Assert.True(crewTicks >= 2400 - 5, $"{crewTicks} ticks of progress");
    }

    [Fact]
    public void Children_do_not_help_build()
    {
        // everyone is friendly enough and spends free time helping; on a weekday at 10:00 toddlers have free time too
        var village = Builders.Start(2, June(1, 9), Builders.Data(day: day =>
        {
            day["helpFrom"] = 0;
            day["free"] = new JsonArray(new JsonObject { ["do"] = "leisure", ["weight"] = 1 });
        }));
        village.Run(1);
        village.PlaceNearHeart("hut", West);
        village.JumpTo(June(1, 10, 30));
        var free = village.People.All.Where(resident => village.Day.Of(resident.Id).Activity is Activity.Help or Activity.Leisure or Activity.Fetch).ToList();
        Assert.Contains(free, resident => resident.Phase == LifePhase.Child);
        // the grown-ups help, and fetch what the phase costs; the children have their free time elsewhere
        Assert.Contains(free, resident => resident.Phase != LifePhase.Child && village.Day.Of(resident.Id).Activity is Activity.Help or Activity.Fetch);
        Assert.DoesNotContain(free, resident => resident.Phase == LifePhase.Child && village.Day.Of(resident.Id).Activity is Activity.Help or Activity.Fetch);
        village.Run(600);
        Assert.DoesNotContain(village.People.All, resident => resident.Phase == LifePhase.Child && village.Day.Of(resident.Id).Activity is Activity.Help or Activity.Fetch);
    }

    [Fact]
    public void A_site_saved_before_builders_counted_in_hundredths_keeps_its_progress()
    {
        var village = Builders.Start(4, June(1, 9));
        village.Run(1);
        var id = village.PlaceNearHeart("hut", West);
        village.JumpTo(June(1, 10));
        var site = village.Get(id);
        for (var tick = 0; tick < 3000 && site.Progress < 100; tick++) village.Run(1);
        Assert.True(site.Progress >= 100);
        var (phase, progress) = (site.Phase, site.Progress);

        // as version 1 wrote it: the work in builder ticks, not in hundredths of them
        var saved = village.Engine.Save();
        var part = saved.Modules.Single(module => module.Name == "building");
        Assert.Equal(2, part.Version);
        var data = part.Data.DeepClone();
        foreach (var structure in data["structures"]!.AsArray()) structure!["units"] = (int)structure["units"]! / 100;
        var old = saved with { Modules = [.. saved.Modules.Select(module => module.Name == "building" ? module with { Version = 1, Data = data } : module)] };

        var loaded = Builders.Load(old, Builders.Data());
        Assert.Equal((phase, progress), (loaded.Get(id).Phase, loaded.Get(id).Progress));
    }

    [Fact]
    public void Free_time_goes_to_a_place_with_something_there()
    {
        var village = Builders.Start(3, June(6, 9));
        village.Run(1);
        var map = Builders.RealMap;
        bool Water(Cell cell) => cell.Neighbours().Any(next => !map.IsWalkable(next));
        bool Tree(Cell cell) => map.At(cell) == Ground.Forest;
        var places = (IPlaces)village.Building;
        var counted = 0;
        for (var hour = 9; hour < 22; hour++)
        {
            village.JumpTo(June(6, hour, 15));
            var homes = village.People.All.Select(resident => places.HomeOf(resident.Id)).OfType<Cell>().ToHashSet();
            bool ByAHome(Cell cell) => homes.Contains(cell) || cell.Neighbours().Any(homes.Contains);
            foreach (var resident in village.People.All.Where(who => !who.IsEddie))
            {
                var doing = village.Day.Of(resident.Id);
                if (doing.Activity is not (Activity.Leisure or Activity.Wander)) continue;
                counted++;
                var place = doing.Place;
                Assert.True(place == places.GatheringOn(resident.Island) || Water(place) || Tree(place) || ByAHome(place),
                    $"{resident.Name} spends his free time at {place}, where there is nothing");
            }
        }
        Assert.True(counted > 20, $"only {counted} at leisure");
    }
}
