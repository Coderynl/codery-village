using System.Text.Json;
using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Core.Time;
using Codery.Village.Server;

namespace Codery.Village.Tests;

public class DataTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "data");
    private const string Cafe = "releases/cafe.json";

    private static DataSet Base() => DataFolder.Load(Folder, []);

    private static Catalog CatalogOf(DataSet data) => data.Section<Catalog>("catalog");

    [Fact]
    public void The_data_has_no_problems()
    {
        Assert.Empty(Base().Problems());
        Assert.Empty(DataFolder.Load(Folder, Ladder.InOrder()).Problems());
    }

    [Fact]
    public void Every_json_file_in_the_folder_is_a_section()
    {
        var data = Base();

        Assert.Equal(new[] { "building", "catalog", "day", "economy", "life", "people", "wishes" }, data.Names);
        Assert.Equal(new LifeSettings(2_419_200), data.Section<LifeSettings>("life"));
    }

    [Fact]
    public void A_house_takes_about_ten_minutes()
    {
        var huts = CatalogOf(Base()).Blueprints.Where(blueprint => blueprint.Id is "hut" or "stone-hut").ToList();

        Assert.Equal(2, huts.Count);
        foreach (var hut in huts)
        {
            Assert.Equal(new[] { "foundation", "frame", "roof", "walls", "garden" }, hut.Phases.Select(phase => phase.Id));
            Assert.Equal(2400, hut.Phases.Sum(phase => phase.Ticks));
            Assert.True(hut.Homes > 0);
        }
    }

    [Fact]
    public void Eddie_has_a_house_and_a_workshop_of_one_tile_on_land()
    {
        var catalog = CatalogOf(Base());

        foreach (var id in new[] { "eddie-house", "eddie-workshop" })
        {
            var blueprint = catalog.Blueprints.Single(b => b.Id == id);
            Assert.Equal(new[] { new Cell(0, 0) }, blueprint.Footprint.Offsets);
            Assert.Contains(Ground.Land, blueprint.On);
        }
    }

    [Fact]
    public void Someone_starts_before_dawn_or_works_at_night()
    {
        var shifts = CatalogOf(Base()).Blueprints.SelectMany(b => b.Jobs ?? []).Select(job => job.Shift).ToHashSet();

        Assert.Contains(Shift.Day, shifts);
        Assert.True(shifts.Contains(Shift.Early) || shifts.Contains(Shift.Night));
    }

    [Fact]
    public void The_base_is_prehistoric()
    {
        var catalog = CatalogOf(Base());

        // nothing in the base fulfils a wish: every step forward is a release
        Assert.All(catalog.Blueprints, blueprint => Assert.Null(blueprint.Serves));
        Assert.NotEmpty(catalog.Things);
        // what came later is not there yet
        Assert.DoesNotContain(catalog.Materials, material => material.Id is "wood" or "planks" or "grain" or "bread" or "bricks");
        Assert.DoesNotContain(catalog.Blueprints, blueprint => blueprint.Id is "house" or "stone-house" or "sawmill" or "bakery" or "farm");
        // a fire, where someone sits at night
        var fire = catalog.Blueprints.Single(blueprint => blueprint.Id == "campfire");
        Assert.Contains(fire.Jobs!, job => job.Shift == Shift.Night);
        // gathering and fishing: no hunting
        Assert.DoesNotContain(catalog.Roles, role => role.Id.Contains("hunt", StringComparison.Ordinal));
    }

    [Fact]
    public void Money_is_counted_in_shells()
    {
        var currency = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "economy.json"))).RootElement.GetProperty("currency");

        Assert.Equal("schelpen", currency.GetProperty("nl").GetString());
        Assert.Equal("shells", currency.GetProperty("en").GetString());
    }

    [Fact]
    public void Both_islands_can_live_on_their_own_ground()
    {
        var map = MapText.Parse(File.ReadAllText(Path.Combine(Folder, "map.txt")));
        var catalog = CatalogOf(Base());
        var satisfies = catalog.Materials.ToDictionary(material => material.Id, material => material.Satisfies ?? []);

        foreach (var island in map.Islands.Where(island => !island.Eddie))
        {
            // the grounds of the island, and the fishing ground off its shore
            var grounds = island.Cells.Select(map.At)
                .Concat(island.Cells.SelectMany(cell => cell.Neighbours()).Where(cell => map.At(cell) == Ground.Fishing).Select(map.At))
                .ToHashSet();
            var fits = catalog.Blueprints.Where(blueprint => blueprint.On.Any(grounds.Contains)).ToList();

            // what it can make from nothing: a place that fits and that it can pay for, making what its recipe gives
            var made = new HashSet<string>();
            bool Affords(Blueprint blueprint) => blueprint.Phases.All(phase => phase.Cost.Keys.All(made.Contains));
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var blueprint in fits.Where(blueprint => blueprint.Recipe is { } recipe && Affords(blueprint) && recipe.In.Keys.All(made.Contains)))
                    foreach (var material in blueprint.Recipe!.Out.Keys)
                        grew |= made.Add(material);
            }

            Assert.True(made.Any(material => satisfies[material].Contains("food")), $"{island.Name} makes no food from {string.Join(", ", made)}");
            Assert.True(made.Any(material => satisfies[material].Contains("heat")), $"{island.Name} makes no warmth from {string.Join(", ", made)}");
            Assert.True(fits.Any(blueprint => blueprint.Homes > 0 && Affords(blueprint)), $"{island.Name} builds no hut from {string.Join(", ", made)}");
            Assert.Contains(fits, blueprint => blueprint.Id == "campfire");
        }
    }

    [Fact]
    public void Every_release_serves_a_wish_and_loads()
    {
        var labels = WishLabels();
        var files = Ladder.Files();
        Assert.NotEmpty(files);
        // the README names each release once, in an order that loads
        Assert.Equal(files, Ladder.InOrder().Order(StringComparer.Ordinal));

        foreach (var release in files)
        {
            var with = Ladder.With(release);
            var order = Ladder.InOrder().ToList();
            Assert.All(with.SkipLast(1), before => Assert.True(order.IndexOf(before) < order.IndexOf(release), $"{before} comes after {release} in the README"));
            var data = DataFolder.Load(Folder, with);
            Assert.True(data.Problems().Count == 0, $"{release}: {string.Join(" ", data.Problems())}");

            var added = JsonDocument.Parse(Ladder.Text(release)).RootElement.GetProperty("blueprints").EnumerateArray()
                .Select(blueprint => blueprint.TryGetProperty("serves", out var serves) ? serves.GetString() : null).ToList();
            Assert.Contains(added, label => label is not null && labels.Contains(label));
        }
        // the wishes for progress are each served by a release
        var served = Ladder.Files().SelectMany(release => CatalogOf(DataFolder.Load(Folder, Ladder.With(release))).Blueprints)
            .Select(blueprint => blueprint.Serves).OfType<string>().ToHashSet();
        foreach (var label in new[] { "tools", "planks", "fields", "bread", "stone", "bricks", "house", "jetty", "cafe" })
            Assert.Contains(label, served);
    }

    private static HashSet<string> WishLabels() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "wishes.json"))).RootElement.GetProperty("wishes").EnumerateArray()
            .Select(wish => wish.GetProperty("label").GetString()!).ToHashSet();

    [Fact]
    public void A_release_adds_without_changing()
    {
        var before = CatalogOf(Base());
        var after = CatalogOf(DataFolder.Load(Folder, [Cafe]));

        var cafe = Assert.Single(after.Blueprints, blueprint => blueprint.Serves == "cafe");
        Assert.DoesNotContain(before.Blueprints, blueprint => blueprint.Id == cafe.Id);
        Assert.Equal(Json(before.Materials), Json(after.Materials.Take(before.Materials.Count)));
        Assert.Equal(Json(before.Roles), Json(after.Roles.Take(before.Roles.Count)));
        Assert.Equal(Json(before.Blueprints), Json(after.Blueprints.Take(before.Blueprints.Count)));
        Assert.Equal(Json(before.Things), Json(after.Things.Take(before.Things.Count)));
    }

    private static string Json<T>(IEnumerable<T> items) => JsonSerializer.Serialize(items.ToList(), DataSet.JsonOptions);

    // a small catalog without a problem; each case below breaks it in one place
    private const string Materials = """[{ "id": "wood", "nl": "hout", "en": "wood" }]""";
    private const string Wishes = """{ "wishes": [{ "id": "cafe", "nl": "een café", "en": "a café", "label": "cafe" }] }""";
    private const string Phase = """{ "id": "frame", "ticks": 10, "cost": { "wood": 1 } }""";
    private const string Look = """{ "form": "building" }""";

    private static string Hut(string phases = $"[{Phase}]", string look = Look, string more = "") =>
        $$"""{ "id": "hut", "nl": "hut", "en": "hut", "on": ["land"], "phases": {{phases}}, "look": {{look}}{{more}} }""";

    private const string Roles = """[{ "id": "carpenter", "nl": "timmerman", "en": "carpenter" }]""";

    private static string Catalog(string? blueprints = null, string materials = Materials, string things = "[]", string roles = Roles) =>
        $$"""{ "materials": {{materials}}, "roles": {{roles}}, "blueprints": {{blueprints ?? $"[{Hut()}]"}}, "things": {{things}} }""";

    private static DataSet Small(string catalog, string wishes = Wishes, params string[] releases) =>
        DataSet.FromTexts(new Dictionary<string, string> { ["catalog"] = catalog, ["wishes"] = wishes }, releases);

    [Fact]
    public void The_small_catalog_has_no_problems()
    {
        Assert.Empty(Small(Catalog()).Problems());
        Assert.Empty(Small(Catalog($"[{Hut(more: """, "serves": "cafe", "range": 4""")}]")).Problems());
        Assert.Empty(Small(Catalog($"[{Hut(more: """, "jobs": [{ "role": "carpenter", "count": 1, "shift": "night", "outdoor": false }]""")}]")).Problems());
    }

    public static TheoryData<string, string, string> Broken => new()
    {
        { Catalog(materials: """[{ "id": "wood", "nl": "hout", "en": "wood" }, { "id": "wood", "nl": "hout", "en": "wood" }]"""), Wishes, "material 'wood' twice" },
        { Catalog($"[{Hut()}, {Hut()}]"), Wishes, "blueprint 'hut' twice" },
        { Catalog(things: """[{ "id": "bench", "nl": "bank", "en": "bench" }, { "id": "bench", "nl": "bank", "en": "bench" }]"""), Wishes, "thing 'bench' twice" },
        { Catalog(), """{ "wishes": [{ "id": "cafe", "nl": "café", "en": "café", "label": "cafe" }, { "id": "cafe", "nl": "café", "en": "café", "label": "cafe" }] }""", "wish 'cafe' twice" },
        { Catalog(roles: $"[{Roles[1..^1]}, {Roles[1..^1]}]"), Wishes, "role 'carpenter' twice" },
        { Catalog($"[{Hut($"[{Phase}, {Phase}]")}]"), Wishes, "phase 'frame' twice" },
        { Catalog($"[{Hut(more: """, "jobs": [{ "role": "juggler", "count": 1, "shift": "day", "outdoor": false }]""")}]"), Wishes, "unknown role 'juggler'" },
        { Catalog($"[{Hut("""[{ "id": "frame", "ticks": 10, "cost": { "gold": 1 } }]""")}]"), Wishes, "unknown material 'gold'" },
        { Catalog($"[{Hut(more: """, "recipe": { "in": { "gold": 1 }, "out": { "wood": 1 }, "ticks": 5 }""")}]"), Wishes, "unknown material 'gold'" },
        { Catalog($"[{Hut(more: """, "recipe": { "in": {}, "out": { "gold": 1 }, "ticks": 5 }""")}]"), Wishes, "unknown material 'gold'" },
        { Catalog($"[{Hut("""[{ "id": "frame", "ticks": 0, "cost": { "wood": 1 } }]""")}]"), Wishes, "0 ticks" },
        { Catalog($"[{Hut("""[{ "id": "frame", "ticks": 10, "cost": { "wood": -2 } }]""")}]"), Wishes, "-2 wood" },
        { Catalog($"[{Hut(more: """, "recipe": { "in": {}, "out": { "wood": 1 }, "ticks": 0 }""")}]"), Wishes, "0 ticks" },
        { Catalog($"[{Hut(more: """, "recipe": { "in": {}, "out": { "wood": 0 }, "ticks": 5 }""")}]"), Wishes, "0 wood" },
        { Catalog($"[{Hut(more: """, "jobs": [{ "role": "carpenter", "count": 0, "shift": "day", "outdoor": false }]""")}]"), Wishes, "0 carpenter" },
        { Catalog($"[{Hut(more: """, "homes": 0""")}]"), Wishes, "0 homes" },
        { Catalog($"[{Hut(more: """, "serves": "cafe", "range": 0""")}]"), Wishes, "range 0" },
        { Catalog($"[{Hut("[]")}]"), Wishes, "no phases" },
        { Catalog("""[{ "id": "hut", "nl": "hut", "en": "hut", "on": ["land"], "phases": [{ "id": "frame", "ticks": 10, "cost": {} }] }]"""), Wishes, "'look'" },
        { Catalog($"[{Hut(look: """{ "form": "building", "roof": "straw" }""")}]"), Wishes, "unknown roof 'straw'" },
        { Catalog($"[{Hut(more: """, "shape": [[0, 1], [1, 0]]""")}]"), Wishes, "without [0,0]" },
        { Catalog($"[{Hut(more: """, "shape": [[0, 0], [1]]""")}]"), Wishes, "[row,col]" },
        { Catalog($"[{Hut(more: """, "serves": "pub" """)}]"), Wishes, "'pub', which no wish names" },
        { Catalog("""[{ "id": "hut", "en": "hut", "on": ["land"], "phases": [], "look": { "form": "building" } }]"""), Wishes, "'nl'" },
        { Catalog($"[{Hut(more: """, "jobs": [{ "role": "carpenter", "count": 1, "shift": "evening", "outdoor": false }]""")}]"), Wishes, "catalog" },
        { "{}", Wishes, "catalog" },
        { Catalog(), """{ "wishes": [{ "id": "cafe", "nl": "café", "en": "café" }] }""", "'label'" },
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public void Problems_are_found(string catalog, string wishes, string expected)
    {
        var problem = Assert.Single(Small(catalog, wishes).Problems());

        Assert.Contains(expected, problem);
    }

    [Fact]
    public void Missing_sections_are_problems()
    {
        var problems = DataSet.FromTexts(new Dictionary<string, string>(), []).Problems();

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, problem => problem.Contains("'catalog'"));
        Assert.Contains(problems, problem => problem.Contains("'wishes'"));
    }

    [Fact]
    public void A_release_may_not_reuse_an_id()
    {
        var release = $$"""{ "blueprints": [{{Hut()}}] }""";

        var problem = Assert.Single(Small(Catalog(), Wishes, release).Problems());

        Assert.Contains("blueprint 'hut' twice", problem);
    }

    [Fact]
    public void A_release_only_adds_to_lists_the_catalog_has()
    {
        var problem = Assert.Single(Small(Catalog(), Wishes, """{ "recipes": [] }""").Problems());

        Assert.Contains("'recipes'", problem);
    }

    [Fact]
    public void A_section_with_a_missing_field_is_refused()
    {
        var data = new DataSet(new Dictionary<string, string> { ["life"] = "{}" });

        var error = Assert.Throws<InvalidDataException>(() => data.Section<LifeSettings>("life"));
        Assert.Contains("'life'", error.Message);
    }

    [Fact]
    public void A_section_is_read_once()
    {
        var data = Base();

        Assert.Same(data.Section<Catalog>("catalog"), data.Section<Catalog>("catalog"));
    }

    [Fact]
    public void Names_are_in_ordinal_order()
    {
        var data = new DataSet(new Dictionary<string, string> { ["b"] = "{}", ["a"] = "{}", ["B"] = "{}" });

        Assert.Equal(new[] { "B", "a", "b" }, data.Names);
    }
}
