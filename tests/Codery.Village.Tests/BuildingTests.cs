using System.Text.Json.Nodes;
using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;
using Codery.Village.Modules.Building;
using Codery.Village.Modules.Contracts;
using Codery.Village.Modules.Day;
using Codery.Village.Modules.People;
using Codery.Village.Modules.Work;
using BuildingModule = Codery.Village.Modules.Building.Building;
using CommunityModule = Codery.Village.Modules.Community.Community;

namespace Codery.Village.Tests;

/// <summary>A village of the real modules up to Building (and Community, when asked), for the tests of building.</summary>
internal static class Builders
{
    public static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "data");
    public static readonly IslandMap RealMap = MapText.Parse(File.ReadAllText(Path.Combine(Folder, "map.txt")));
    public static readonly int West = RealMap.Islands.Single(island => island.Name == "West").Id;
    public static readonly int Oost = RealMap.Islands.Single(island => island.Name == "Oost").Id;
    public static readonly int Islet = RealMap.Islands.Single(island => island.Eddie).Id;

    /// <summary>A Dutch summer time on a day of June 2026; the first of June is a Monday.</summary>
    public static DateTimeOffset June(int day, int hour, int minute = 0) => new(2026, 6, day, hour, minute, 0, TimeSpan.FromHours(2));

    /// <summary>The real data, changed as the test needs.</summary>
    public static DataSet Data(Action<JsonObject>? building = null, Action<JsonObject>? economy = null,
        Action<JsonObject>? people = null, Action<JsonObject>? catalog = null, IEnumerable<string>? extras = null,
        Action<JsonObject>? wishes = null, Action<JsonObject>? life = null, Action<JsonObject>? day = null)
    {
        JsonObject Read(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, name + ".json")))!.AsObject();
        var sections = new Dictionary<string, JsonObject>
        {
            ["life"] = Read("life"),
            ["people"] = Read("people"),
            ["day"] = Read("day"),
            ["catalog"] = Read("catalog"),
            ["economy"] = Read("economy"),
            ["building"] = Read("building"),
            ["wishes"] = Read("wishes"),
        };
        building?.Invoke(sections["building"]);
        day?.Invoke(sections["day"]);
        wishes?.Invoke(sections["wishes"]);
        life?.Invoke(sections["life"]);
        economy?.Invoke(sections["economy"]);
        people?.Invoke(sections["people"]);
        catalog?.Invoke(sections["catalog"]);
        return DataSet.FromTexts(sections.ToDictionary(section => section.Key, section => section.Value.ToJsonString()), extras ?? []);
    }

    public static JsonArray Band(int from, int to) => [new JsonObject { ["from"] = from, ["to"] = to, ["weight"] = 1 }];

    public static JsonArray Kind(int adults, int children) => [new JsonObject { ["adults"] = adults, ["children"] = children, ["weight"] = 1 }];

    public static IModule[] Modules(bool community, params IModule[] more) =>
        [new People(), new Day(), new Work(), new BuildingModule(), .. community ? new IModule[] { new CommunityModule() } : [], .. more, new Ear()];

    public static Village Start(ulong seed, DateTimeOffset at, DataSet? data = null, bool community = false, IslandMap? map = null, params IModule[] more)
    {
        var time = new SimulatedTime(at);
        var modules = Modules(community, more);
        var engine = new Engine(modules, data ?? Data(), map ?? RealMap, seed, time);
        engine.Start();
        return new Village(engine, time, modules);
    }

    public static Village Load(SavedVillage saved, DataSet data, bool community = false, params IModule[] more)
    {
        var time = new SimulatedTime(saved.Calendar);
        var modules = Modules(community, more);
        var engine = Engine.Load(SaveJson.FromJson(SaveJson.ToJson(saved)), modules, data, RealMap, time);
        return new Village(engine, time, modules);
    }

    public sealed record Village(Engine Engine, SimulatedTime Time, IReadOnlyList<IModule> Modules)
    {
        public People People => Modules.OfType<People>().Single();
        public Day Day => Modules.OfType<Day>().Single();
        public Work Work => Modules.OfType<Work>().Single();
        public BuildingModule Building => Modules.OfType<BuildingModule>().Single();
        public Ear Ear => Modules.OfType<Ear>().Single();

        /// <summary>Moves the calendar to <paramref name="at"/> and runs one tick there; Day settles, as after a stop.</summary>
        public void JumpTo(DateTimeOffset at)
        {
            Time.Advance(at - Time.GetUtcNow());
            Engine.Step();
        }

        public void Run(int ticks)
        {
            for (var i = 0; i < ticks; i++) Engine.Step();
        }

        public Structure Get(int id) => Building.All.Single(structure => structure.Id == id);

        /// <summary>Places the blueprint where the island would put it: as near its heart as there is room.</summary>
        public int PlaceNearHeart(string blueprint, int island)
        {
            var (anchor, rotation) = Building.FindPlace(blueprint, island) ?? throw new InvalidOperationException($"No room for {blueprint}.");
            return Building.Place(blueprint, anchor, rotation);
        }

        /// <summary>The builders at work on the tile of the site, not on the way.</summary>
        public int BuildersAt(Structure site) => People.All.Count(resident =>
            Work.JobOf(resident.Id) is { Role: "builder" } && Day.Of(resident.Id) is { Activity: Activity.Work, Walk: null } doing && doing.Place == site.Anchor);

        public IEnumerable<T> Heard<T>() => Ear.Heard.OfType<T>();
    }
}

public class BuildingTests
{
    private static readonly int West = Builders.West;
    private static readonly int Oost = Builders.Oost;
    private static readonly int Islet = Builders.Islet;
    private static DateTimeOffset June(int day, int hour, int minute = 0) => Builders.June(day, hour, minute);

    [Fact]
    public void A_hut_goes_through_five_phases_in_order()
    {
        var village = Builders.Start(1, June(1, 9));
        village.Run(1);
        var (branches, reed) = (village.Work.Count(West, "branches"), village.Work.Count(West, "reed"));
        var things = village.Building.Things.Count;
        var id = village.PlaceNearHeart("hut", West);
        var site = village.Get(id);
        Assert.Equal((0, 0, false), (site.Phase, site.Progress, site.Done));
        Assert.Contains(((IStructures)village.Building).Sites(), view => view.Id == id && view.Island == West && view.Place == site.Anchor);
        Assert.DoesNotContain(((IStructures)village.Building).Standing(), view => view.Id == id);

        // the morning after: Day settles and the island's three builders come to the site, a full crew; the
        // foundation is paid in one go when it starts, and they go to fetch it
        village.JumpTo(June(1, 10));
        Assert.Equal((0, 0), (site.Phase, site.Progress));
        Assert.Equal((branches - 2, reed), (village.Work.Count(West, "branches"), village.Work.Count(West, "reed")));

        // 2400 ticks of work with a full crew, a step every tick, besides the walks to fetch what the phases cost
        var working = 0;
        for (var tick = 0; tick < 5000 && !site.Done; tick++)
        {
            var before = (site.Phase, site.Progress);
            village.Run(1);
            if ((site.Phase, site.Progress) != before) working++;
        }

        Assert.Equal(2400, working);
        Assert.Equal(5, site.Phase);
        village.Run(1);
        Assert.Equal(["foundation", "frame", "roof", "walls", "garden"],
            village.Heard<PhaseStarted>().Where(e => e.Structure == id).Select(e => e.Phase));
        Assert.Single(village.Heard<StructureDone>(), e => e.Structure == id);
        Assert.Equal((branches - 10, reed - 3), (village.Work.Count(West, "branches"), village.Work.Count(West, "reed")));
        Assert.Contains(((IStructures)village.Building).Standing(), view => view.Id == id);
        Assert.DoesNotContain(((IStructures)village.Building).Sites(), view => view.Id == id);
        Assert.Contains(village.Engine.Log, line => line.Kind == "built" && line.Subject == "hut");
        // a finished hut is a home to move into, and puts something loose by the door
        Assert.True(village.Building.Things.Count > things);
        Assert.Empty(village.Engine.Stopped);
    }

    [Fact]
    public void No_builders_no_progress_and_nothing_stuck()
    {
        // newcomers are couples of working age and come every day there is a free home; nobody helps in his free time,
        // so without builders nobody builds, whatever the free time is spent on
        var data = Builders.Data(
            people: people =>
            {
                people["arrivalChance"] = 1;
                people["arrivalDamping"] = 1e12;
                people["households"] = Builders.Kind(2, 0);
                people["adultAges"] = Builders.Band(30, 40);
            },
            day: day => day["helpFrom"] = 101);
        var village = Builders.Start(3, June(1, 9), data);
        village.Run(1);
        var west = village.PlaceNearHeart("hut", West);
        var oost = village.PlaceNearHeart("stone-hut", Oost);

        // Oost becomes an island of a child and the retired, who move to the coast in a moment
        var saved = village.Engine.Save();
        var people = saved.Modules.Single(part => part.Name == "people").Data["residents"]!.AsArray();
        var year = (long)JsonNode.Parse(File.ReadAllText(Path.Combine(Builders.Folder, "life.json")))!["ticksPerLifeYear"]!;
        var onOost = people.Where(resident => (int)resident!["island"]! == Oost).ToList();
        var child = (int)onOost[0]!["household"]!;
        foreach (var resident in onOost)
            resident!["bornTick"] = saved.Tick - ((int)resident["household"]! == child ? 10 * year : 70 * year - 3000);
        village = Builders.Load(saved, data);

        village.JumpTo(June(1, 10));
        village.Run(2000);

        var waiting = village.Get(oost);
        Assert.Equal((0, 0), (waiting.Phase, waiting.Progress));
        Assert.Equal("builders", waiting.Waiting);
        Assert.Single(village.Heard<ConstructionWaiting>(), e => e.Structure == oost);
        Assert.Equal("builders", village.Heard<ConstructionWaiting>().Single(e => e.Structure == oost).Reason);
        Assert.Single(village.Engine.Log, line => line.Kind == "waiting-builders" && line.Detail == oost.ToString());
        // the other island builds on
        var going = village.Get(west);
        Assert.True(going.Phase > 0);
        Assert.Null(going.Waiting);

        // the retired leave and free their homes; the next day newcomers move in, and work
        village.Run(1500);
        Assert.Contains(village.Heard<ResidentLeft>(), e => e.Island == Oost);
        Assert.NotNull(village.Building.FreeHome(Oost));
        village.JumpTo(June(2, 0, 30));
        village.JumpTo(June(2, 10));
        Assert.Contains(village.Heard<ResidentArrived>(), e => village.People.Get(e.Resident).Island == Oost);
        village.Run(100);

        Assert.True(waiting.Progress > 0);
        Assert.Null(waiting.Waiting);
        Assert.True(village.Get(west).Done);
        Assert.Empty(village.Engine.Stopped);
    }

    [Fact]
    public void A_builder_waits_for_a_material_no_one_has()
    {
        // a hut whose foundation is of stones, which nothing on West has or makes
        var village = Builders.Start(1, June(1, 9), Builders.Data(catalog: catalog =>
            catalog["blueprints"]!.AsArray().Single(blueprint => (string)blueprint!["id"]! == "hut")!["phases"]![0]!["cost"] = new JsonObject { ["stones"] = 2 }));
        village.Run(1);
        Assert.Null(((IStock)village.Work).SourceOf(West, "stones", new Cell(5, 8)));
        var id = village.PlaceNearHeart("hut", West);

        // the builders come to the site and stay there: nobody walks off to fetch what no one has
        village.JumpTo(June(1, 10));
        for (var tick = 0; tick < 2000; tick++)
        {
            village.Run(1);
            Assert.DoesNotContain(village.People.All, resident => village.Day.Of(resident.Id).Activity == Activity.Fetch);
        }
        var site = village.Get(id);
        Assert.Equal((0, 0), (site.Phase, site.Progress));
        Assert.Equal("materials", site.Waiting);
        Assert.Equal("materials", Assert.Single(village.Heard<ConstructionWaiting>(), e => e.Structure == id).Reason);
        Assert.DoesNotContain(village.Heard<Reached>(), e => e.Errand == Activity.Fetch);
    }

    [Fact]
    public void A_two_tile_shape_needs_two_free_tiles()
    {
        var map = MapText.Parse("""
            ~~~~~~~~~
            ~.......~
            ~.......~
            ~.......~
            ~.......~
            ~~~~~~~~~
            ~~~~~~..~
            ~~~~~~~~~
            island 1 1 Proef
            island 6 6 Klein
            """);
        var data = Builders.Data(building: building =>
        {
            building["thingsAtStart"] = 0;
            building["fireplace"] = null;
        }, catalog: catalog =>
        {
            var pair = catalog["blueprints"]!.AsArray().Single(blueprint => (string)blueprint!["id"]! == "campfire")!.DeepClone().AsObject();
            pair["id"] = "pair";
            pair["shape"] = new JsonArray(new JsonArray(0, 0), new JsonArray(0, 1));
            catalog["blueprints"]!.AsArray().Add(pair);
        });
        var pair = new Shape([new Cell(0, 0), new Cell(0, 1)]);
        var anchor = new Cell(2, 2);
        for (var rotation = 0; rotation < 6; rotation++)
        {
            var cells = pair.Cover(anchor, rotation).ToList();

            // both free: one site on both tiles, after which neither is free
            var village = Builders.Start(1, June(1, 9), data, map: map);
            Assert.True(village.Building.CanPlace("pair", anchor, rotation), $"rotation {rotation}");
            var id = village.Building.Place("pair", anchor, rotation);
            Assert.Equal(cells, village.Get(id).Cells);
            Assert.False(village.Building.CanPlace("campfire", cells[1], 0));
            Assert.Throws<InvalidOperationException>(() => village.Building.Place("pair", anchor, rotation));

            // one of the two taken
            village = Builders.Start(1, June(1, 9), data, map: map);
            village.Building.Place("campfire", cells[1], 0);
            Assert.False(village.Building.CanPlace("pair", anchor, rotation), $"rotation {rotation}");

            // one of the two on the water, or on the other island
            foreach (var cell in map.Islands.SelectMany(island => island.Cells))
            {
                var cover = pair.Cover(cell, rotation).ToList();
                if (map.IslandOf(cover[1]) == map.IslandOf(cell)) continue;
                Assert.False(village.Building.CanPlace("pair", cell, rotation), $"{cell} rotation {rotation}");
            }
        }
    }

    [Fact]
    public void Every_island_starts_with_homes_for_its_households_and_Eddie_with_his_own()
    {
        var village = Builders.Start(5, June(1, 9));
        village.Run(1);

        foreach (var resident in village.People.All)
        {
            Assert.True(village.Building.HasHome(resident.Household!.Value), resident.Name);
            var home = ((IPlaces)village.Building).HomeOf(resident.Id);
            Assert.NotNull(home);
            Assert.Equal(resident.Island, Builders.RealMap.IslandOf(home!.Value));
        }
        // finished from the start, not sites; the dwelling the island can pay for, and a campfire
        Assert.All(village.Building.All, structure => Assert.True(structure.Done));
        var huts = village.Building.All.Where(structure => structure.Island != Islet && structure.Blueprint != "campfire").ToList();
        Assert.All(huts.Where(structure => structure.Island == West), hut => Assert.Equal("hut", hut.Blueprint));
        Assert.All(huts.Where(structure => structure.Island == Oost), hut => Assert.Equal("stone-hut", hut.Blueprint));
        Assert.All(huts, hut => Assert.Equal(Ground.Lot, Builders.RealMap.At(hut.Anchor)));

        var eddie = village.People.All.Single(resident => resident.IsEddie);
        var own = village.Building.All.Where(structure => structure.Island == Islet).Select(structure => structure.Blueprint).Order();
        Assert.Equal(["eddie-house", "eddie-workshop"], own);
        Assert.Equal(village.Building.All.Single(structure => structure.Blueprint == "eddie-house").Anchor, ((IPlaces)village.Building).HomeOf(eddie.Id));
        Assert.Equal("eddie-workshop", village.Building.All.Single(structure => structure.Id == village.Work.JobOf(eddie.Id)!.Building).Blueprint);
        // the islands gather at their campfire, Eddie at a fixed tile of his own; there is no school yet
        foreach (var island in Builders.RealMap.Islands)
        {
            var gathering = ((IPlaces)village.Building).GatheringOn(island.Id);
            Assert.Equal(island.Id, Builders.RealMap.IslandOf(gathering!.Value));
            Assert.Null(((IPlaces)village.Building).SchoolOn(island.Id));
            var fires = village.Building.All.Where(structure => structure.Island == island.Id && structure.Blueprint == "campfire").ToList();
            if (island.Eddie) Assert.Empty(fires);
            else Assert.Equal(Assert.Single(fires).Anchor, gathering);
        }
        // someone keeps the fire at night
        Assert.Contains(village.People.All, resident => village.Work.JobOf(resident.Id) is { Role: "fire-keeper", Shift: Shift.Night });
    }

    [Fact]
    public void A_home_comes_free_when_its_household_has_left()
    {
        var data = Builders.Data(people: people =>
        {
            people["households"] = Builders.Kind(1, 0);
            people["adultAges"] = Builders.Band(30, 40);
            people["arrivalChance"] = 0;
        });
        var village = Builders.Start(2, June(1, 9), data);
        village.Run(1);
        Assert.Null(village.Building.FreeHome(West));
        var leaving = village.People.On(West).First();
        var home = village.Building.All.Single(structure => structure.Household == leaving.Household);

        // through a save, he is old enough to move to the coast in a moment
        var saved = village.Engine.Save();
        var year = (long)JsonNode.Parse(File.ReadAllText(Path.Combine(Builders.Folder, "life.json")))!["ticksPerLifeYear"]!;
        var him = saved.Modules.Single(part => part.Name == "people").Data["residents"]!.AsArray().Single(resident => (int)resident!["id"]! == leaving.Id)!;
        him["bornTick"] = saved.Tick - (70 * year - 10);
        village = Builders.Load(saved, data);
        village.Run(20);

        Assert.Contains(village.Heard<ResidentLeft>(), e => e.Resident == leaving.Id);
        Assert.Equal(home.Id, village.Building.FreeHome(West));
        Assert.Null(village.Get(home.Id).Household);
        Assert.False(village.Building.HasHome(leaving.Household!.Value));
    }

    [Fact]
    public void Loose_things_move_only_to_free_land_on_their_island()
    {
        var village = Builders.Start(1, June(1, 9));
        var map = Builders.RealMap;
        var kinds = new[] { "log", "basket", "sapling", "boulder" };
        Assert.NotEmpty(village.Building.Things);
        Assert.All(village.Building.Things, thing =>
        {
            Assert.Contains(thing.Kind, kinds);
            Assert.Equal(thing.At, thing.Home);
            Assert.True(map.IsWalkable(thing.At));
        });
        foreach (var island in map.Islands.Where(island => !island.Eddie))
            Assert.Contains(village.Building.Things, thing => map.IslandOf(thing.At) == island.Id);

        var thing = village.Building.Things.First(thing => map.IslandOf(thing.At) == West);
        var other = village.Building.Things.First(other => other.Id != thing.Id && map.IslandOf(other.At) == West);
        var hut = village.Building.All.First(structure => structure.Island == West);
        var water = map.Islands.Single(island => island.Id == West).Cells.SelectMany(cell => cell.Neighbours()).First(cell => !map.IsWalkable(cell));
        var free = map.Islands.Single(island => island.Id == West).Cells
            .First(cell => map.At(cell) == Ground.Land && village.Building.CanPlace("campfire", cell, 0));
        var oost = map.Islands.Single(island => island.Id == Oost).Cells.First(cell => map.At(cell) == Ground.Land);

        Assert.Equal(Refusal.NotFound, village.Building.Move(9999, free));
        Assert.Equal(Refusal.Water, village.Building.Move(thing.Id, water));
        Assert.Equal(Refusal.OtherIsland, village.Building.Move(thing.Id, oost));
        Assert.Equal(Refusal.Taken, village.Building.Move(thing.Id, hut.Anchor));
        Assert.Equal(Refusal.Taken, village.Building.Move(thing.Id, other.At));
        // put down where it stands: nothing moves, so it is no move
        Assert.Equal(Refusal.Taken, village.Building.Move(thing.Id, thing.At));
        Assert.Equal(thing, village.Building.Things.Single(t => t.Id == thing.Id));

        Assert.Null(village.Building.Move(thing.Id, free));
        var moved = village.Building.Things.Single(t => t.Id == thing.Id);
        Assert.Equal((free, thing.Home), (moved.At, moved.Home));
        // a thing takes its tile: nothing is built on it
        Assert.False(village.Building.CanPlace("campfire", free, 0));
    }

    [Fact]
    public void A_thing_is_not_put_on_a_lot_or_where_the_island_gathers()
    {
        // without a fireplace, the island gathers on a fixed tile of its own
        var village = Builders.Start(1, June(1, 9), Builders.Data(building: b => b.Remove("fireplace")));
        var map = Builders.RealMap;
        var thing = village.Building.Things.First(thing => map.IslandOf(thing.At) == West);
        var square = village.Building.GatheringOn(West)!.Value;
        Assert.DoesNotContain(village.Building.All, structure => structure.Cells.Contains(square));
        var lot = map.Islands.Single(island => island.Id == West).Cells
            .First(cell => map.At(cell) == Ground.Lot && village.Building.All.All(structure => !structure.Cells.Contains(cell)));

        Assert.Equal(Refusal.Taken, village.Building.Move(thing.Id, lot));
        Assert.Equal(Refusal.Taken, village.Building.Move(thing.Id, square));
        Assert.Equal(thing, village.Building.Things.Single(t => t.Id == thing.Id));
    }

    [Fact]
    public void Hand_things_come_back()
    {
        var village = Builders.Start(1, June(1, 7, 50));
        var map = Builders.RealMap;
        var minutes = (int)JsonNode.Parse(File.ReadAllText(Path.Combine(Builders.Folder, "building.json")))!["putBackMinutes"]!;
        var ticks = minutes * 60 * Clocks.TicksPerSecond;

        // someone sets off on West: the thing goes on a free tile halfway his walk, in his way
        var thing = village.Building.Things.First(thing => map.IslandOf(thing.At) == West);
        var way = IntoTheWay(village, thing);
        // and one more, on a tile of its island that may or may not be in anybody's way
        var other = village.Building.Things.First(other => other.Id != thing.Id && map.IslandOf(other.At) == West);
        var aside = map.Islands.Single(island => island.Id == West).Cells
            .Where(cell => cell != way && cell != thing.Home && map.At(cell) is Ground.Land or Ground.Forest or Ground.Fertile)
            .First(cell => village.Building.Move(other.Id, cell) is null);
        var lines = village.Engine.LogEnd;

        var crossed = false;
        for (var i = 0; i <= ticks + 1; i++)
        {
            village.Run(1);
            crossed |= village.People.All.Any(resident => village.Day.Of(resident.Id) is var doing
                && (doing.Place == aside || doing.Walk?.Cells.Contains(aside) == true));
        }

        var back = village.Building.Things.Single(t => t.Id == thing.Id);
        Assert.Equal(thing.Home, back.At);
        var put = village.Engine.LogFrom(lines).Select(line => line.Entry).Where(entry => entry.Kind == "put-back" && entry.Detail == thing.Kind).ToList();
        Assert.NotEmpty(put);
        Assert.All(put, entry => Assert.Contains(village.People.All, resident => resident.Name == entry.Subject));
        // what is in nobody's way stays where it was put
        if (!crossed) Assert.Equal(aside, village.Building.Things.Single(t => t.Id == other.Id).At);
    }

    [Fact]
    public void The_nearest_awake_resident_is_on_the_island_and_not_asleep()
    {
        // late in the evening: the little ones sleep, the grown-ups are up; deep in the night all of West may sleep
        var village = Builders.Start(1, June(1, 22));
        village.Run(1);
        var map = Builders.RealMap;
        var thing = village.Building.Things.First(thing => map.IslandOf(thing.At) == West);

        var who = village.Building.NearestAwake(thing.At);

        Assert.NotNull(who);
        var resident = village.People.Get(who!.Value);
        Assert.Equal(West, resident.Island);
        Assert.NotEqual(Activity.Sleep, village.Day.Of(resident.Id).Activity);
    }

    [Fact]
    public void A_thing_on_its_way_back_is_kept_in_the_save()
    {
        var original = Builders.Start(1, June(1, 7, 50));
        var thing = original.Building.Things.First(thing => Builders.RealMap.IslandOf(thing.At) == West);
        IntoTheWay(original, thing);
        original.Run(10);

        var loaded = Builders.Load(original.Engine.Save(), Builders.Data());
        original.Run(4000);
        loaded.Run(4000);

        Assert.Equal(SaveJson.ToJson(original.Engine.Save()), SaveJson.ToJson(loaded.Engine.Save()));
    }

    /// <summary>Runs until someone on the thing's island sets off on a longer walk, and moves the thing onto it, halfway.</summary>
    private static Cell IntoTheWay(Builders.Village village, LooseThing thing)
    {
        var island = Builders.RealMap.IslandOf(thing.At);
        for (var i = 0; i < 4 * 3600; i++)
        {
            village.Run(1);
            var cells = village.People.All.Where(resident => resident.Island == island)
                .Select(resident => village.Day.Of(resident.Id).Walk)
                .Where(walk => walk is { Cells.Count: >= 4 })
                .SelectMany(walk => walk!.Cells.Skip(2).Take(walk.Cells.Count - 3));
            foreach (var cell in cells)
                if (cell != thing.At && village.Building.Move(thing.Id, cell) is null) return cell;
        }
        throw new InvalidOperationException("Nobody set off.");
    }

    [Fact]
    public void A_saved_site_goes_on_as_if_it_never_stopped()
    {
        var original = Builders.Start(4, June(1, 9));
        original.Run(1);
        original.PlaceNearHeart("hut", West);
        original.PlaceNearHeart("stone-hut", Oost);
        original.JumpTo(June(1, 10));
        original.Run(700);
        Assert.Contains(original.Building.All, structure => !structure.Done && structure.Phase > 0);

        var loaded = Builders.Load(original.Engine.Save(), Builders.Data());
        original.Run(2000);
        loaded.Run(2000);

        Assert.Equal(SaveJson.ToJson(original.Engine.Save()), SaveJson.ToJson(loaded.Engine.Save()));
        Assert.Empty(loaded.Engine.Stopped);
    }
}
