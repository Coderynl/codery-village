using System.Text.Json.Nodes;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
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
        var village = Builders.Start(1, June(1, 9));
        village.Run(1);
        var branches = village.Work.Count(West, "branches");
        var id = village.PlaceNearHeart("hut", West);
        var site = village.Get(id);
        var store = Store(village, West);

        // the morning after: the builders come to the site, the foundation is paid, and they go to the store for it
        village.JumpTo(June(1, 10));
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
        var ticks = 0;
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

    [Fact]
    public void Gatherers_bring_their_batch_to_the_store()
    {
        var data = Builders.Data(catalog: catalog =>
        {
            var patch = catalog["blueprints"]!.AsArray().Single(blueprint => (string)blueprint!["id"]! == "berry-patch")!;
            patch["recipe"]!["ticks"] = 30;
        });
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

        var ticks = 0;
        while (!village.Heard<Produced>().Any(e => e.Material == "berries") && ticks++ < 100) village.Run(1);
        // the batch is done in the tick before the ear hears it: they are on their way with it
        var store = Store(village, West);
        Assert.All(gatherers, gatherer =>
        {
            var doing = village.Day.Of(gatherer.Id);
            Assert.Equal(Activity.Bring, doing.Activity);
            Assert.Equal(store, doing.Place);
            Assert.Equal("berries", doing.Carry);
        });

        ticks = 0;
        while (gatherers.Any(gatherer => village.Day.Of(gatherer.Id).Activity == Activity.Bring) && ticks++ < 2000) village.Run(1);
        village.Run(1);
        Assert.All(gatherers, gatherer => Assert.Equal(new Doing(Activity.Work, spots[gatherer.Id], spots[gatherer.Id]), village.Day.Of(gatherer.Id)));
        Assert.Equal(gatherers.Count, village.Heard<Reached>().Count(e => e.Errand == Activity.Bring && e.At == store && e.Carry == "berries"));
        Assert.All(gatherers, gatherer =>
            Assert.Single(village.Heard<Returned>(), e => e.Resident == gatherer.Id && e.Errand == Activity.Bring && e.At == spots[gatherer.Id]));
    }

    // a berry patch near the heart of West, standing, on Monday the first of June just after ten
    private static (Builders.Village Village, int Patch) BerryPatch(DataSet? data = null)
    {
        var village = Builders.Start(1, June(1, 9), data);
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
        var village = Builders.Start(1, June(1, 9));
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
        var (village, id) = BerryPatch(Builders.Data(day: day => day["ticksPerCell"] = 200));
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
        var (village, id) = BerryPatch(Builders.Data(economy: economy => economy["stayQuarters"] = 1));
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
        Assert.True(village.Work.TryTake(West, new Dictionary<string, int> { ["firewood"] = village.Work.Count(West, "firewood") - households }));
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
            }));

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
