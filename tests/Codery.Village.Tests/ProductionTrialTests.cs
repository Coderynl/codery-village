using System.Net;
using System.Text.Json.Nodes;
using Codery.Village.Checks;
using Codery.Village.Core.Data;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;
using Codery.Village.Modules.Wishes;
using Codery.Village.Server;
using Codery.Village.Server.Feed;

namespace Codery.Village.Tests;

/// <summary>
/// The check that tries a pull request's data on the production state before it merges. It measures the time per
/// tick on the real clock, so it runs with the other real-time tests, not next to the weeks of village.
/// </summary>
[Collection(RealTimeCollection.Name)]
public class ProductionTrialTests
{
    // a release Eddie could write for the wine task; not in data/releases
    private const string Winery = """
        {
          "blueprints": [
            {
              "id": "winery", "nl": "wijnmakerij", "en": "winery", "on": ["lot", "land"], "serves": "wine", "range": 10,
              "phases": [{ "id": "foundation", "nl": "fundering", "en": "foundation", "ticks": 400, "cost": {} }],
              "look": { "form": "building", "size": "medium", "walls": "plaster", "roof": "gable" }
            }
          ]
        }
        """;

    private static readonly string Grapes = File.ReadAllText(Path.Combine(VillageSetup.Folder, "releases", "grapes.json"));

    /// <summary>The base data with grapes, where everyone wishes for wine, heavily and over a low threshold, so the village asks for wine soon.</summary>
    private static DataSet Data(Action<JsonObject>? wishes = null, params string[] extras)
    {
        var sections = Directory.EnumerateFiles(VillageSetup.Folder, "*.json").ToDictionary(path => Path.GetFileNameWithoutExtension(path), File.ReadAllText);
        var section = JsonNode.Parse(sections["wishes"])!.AsObject();
        var wine = section["wishes"]!.AsArray().Single(wish => (string)wish!["id"]! == "wine")!.AsObject();
        wine["who"] = new JsonObject();
        wine["weight"] = 10;
        section["taskThreshold"] = 1;
        wishes?.Invoke(section);
        sections["wishes"] = section.ToJsonString();
        return DataSet.FromTexts(sections, [Grapes, .. extras]);
    }

    // production as it stands: the village asks Eddie for wine
    private static readonly Lazy<SavedVillage> AskingForWine = new(() =>
    {
        var engine = VillageSetup.Begin(Data(), VillageSetup.Map(), 1, new SimulatedTime(VillageSetup.ExportStart));
        for (var i = 0; i < 240; i++) engine.Step();
        Assert.Equal("wine", engine.Running.OfType<Wishes>().Single().Task?.Wish);
        return engine.Save();
    });

    // Risk: Eddie's release for the open task merges while it does not answer it, or CI calls a good release a problem.
    [Fact]
    public void A_release_for_the_open_task_answers_it()
    {
        var result = ProductionTrial.Run(AskingForWine.Value, Data(extras: Winery), VillageSetup.Map(), 240);

        Assert.Empty(result.Problems);
        Assert.Equal("wine", result.OpenWish);
        Assert.True(result.OpenWishAnswered);
        Assert.InRange(result.MeanTickMs, 0, ProductionTrial.TickLimitMs);
    }

    // Risk: a release whose data a module cannot load merges, and that module sleeps in production.
    [Fact]
    public void A_release_that_puts_a_module_to_sleep_is_a_problem()
    {
        var result = ProductionTrial.Run(AskingForWine.Value, Data(wishes => wishes["everyTicks"] = 0, Winery), VillageSetup.Map(), 240);

        Assert.Contains(result.Problems, problem => problem.Contains("module-dormant") && problem.Contains("wishes"));
    }

    // Risk: a release the server refuses to start on merges, and production stays down after the deploy.
    [Fact]
    public void A_release_the_server_would_not_start_on_is_a_problem()
    {
        // a wish with an empty after: every module takes it, the server's check of the data does not
        var result = ProductionTrial.Run(AskingForWine.Value, Data(wishes => wishes["wishes"]!.AsArray().First(wish => (string)wish!["id"]! == "wine")!["after"] = new JsonArray(), Winery), VillageSetup.Map(), 240);

        Assert.Contains(result.Problems, problem => problem.Contains("empty 'after'"));
        Assert.DoesNotContain(result.Problems, problem => problem.StartsWith("module-"));
    }

    // Risk: Eddie's release merges untried while production cannot be read, or every other pull request fails until /save is deployed.
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task Production_that_cannot_be_read_fails_only_Eddies_pull_request(bool eddie, int exit)
    {
        var output = new StringWriter();
        using var http = new HttpClient(new Production(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var code = await ProductionTrial.Check(http, "https://village.test", 10, eddie, output);

        Assert.True(code == exit, output.ToString());
        Assert.Contains("https://village.test/save", output.ToString());
    }

    // Risk: Eddie's release that does not answer the open wish merges, or his revert (which adds no release) cannot.
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task Eddies_pull_request_that_adds_a_release_must_answer_the_open_wish(bool productionRunsEveryRelease, int exit)
    {
        // the checked-out data has no release that serves wine
        var releases = productionRunsEveryRelease ? VillageSetup.Releases() : [];
        var output = new StringWriter();
        using var http = new HttpClient(new Production(request => request.RequestUri!.AbsolutePath switch
        {
            "/save" => Json(AskingForWine.Value),
            "/version" => Json(new { commit = "abc", releases }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }));

        var code = await ProductionTrial.Check(http, "https://village.test", 240, eddie: true, output);

        Assert.True(code == exit, output.ToString());
        Assert.Contains(productionRunsEveryRelease ? "passes on the production state" : "does not answer the open wish: wine", output.ToString());
    }

    // Risk: a pull request that takes away what production built on merges, and production forgets it after the deploy.
    [Fact]
    public async Task A_building_whose_blueprint_the_data_lacks_fails_a_pull_request()
    {
        var output = new StringWriter();
        using var http = new HttpClient(Serving(WithWinery(AskingForWine.Value), VillageSetup.Releases()));

        var code = await ProductionTrial.Check(http, "https://village.test", 240, eddie: false, output);

        Assert.True(code == 1, output.ToString());
        Assert.Contains("forgotten: winery", output.ToString());
    }

    // Risk: a pull request that drops a release production runs merges, or Eddie's revert of his own release cannot.
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task A_release_production_runs_that_the_data_lacks_passes_only_as_eddies_revert(bool eddie, int exit)
    {
        var output = new StringWriter();
        using var http = new HttpClient(Serving(WithWinery(AskingForWine.Value), [.. VillageSetup.Releases(), "winery"]));

        var code = await ProductionTrial.Check(http, "https://village.test", 240, eddie, output);

        Assert.True(code == exit, output.ToString());
        Assert.Contains("winery", output.ToString());
    }

    /// <summary>Production with a winery built: a structure of a blueprint no release in data/releases has.</summary>
    private static SavedVillage WithWinery(SavedVillage save) => save with
    {
        Modules = [.. save.Modules.Select(module => module.Name != "building" ? module : module with { Data = WineryBuilt(module.Data) })],
    };

    private static JsonNode WineryBuilt(JsonNode building)
    {
        var copy = building.DeepClone();
        var structures = copy["structures"]!.AsArray();
        var winery = structures[0]!.DeepClone();
        winery["id"] = 9999;
        winery["blueprint"] = "winery";
        winery["households"] = new JsonArray();
        structures.Add(winery);
        return copy;
    }

    private static Production Serving(SavedVillage save, IReadOnlyList<string> releases) => new(request => request.RequestUri!.AbsolutePath switch
    {
        "/save" => Json(save),
        "/version" => Json(new { commit = "abc", releases }),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    });

    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(value, Protocol.Json)) };

    private sealed class Production(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
