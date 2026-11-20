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

    /// <summary>The base data with grapes and only the wine wish, for everyone and over a low threshold, so the village asks for wine soon.</summary>
    private static DataSet Data(Action<JsonObject>? wishes = null, params string[] extras)
    {
        var sections = Directory.EnumerateFiles(VillageSetup.Folder, "*.json").ToDictionary(path => Path.GetFileNameWithoutExtension(path), File.ReadAllText);
        var section = JsonNode.Parse(sections["wishes"])!.AsObject();
        var wine = section["wishes"]!.AsArray().Single(wish => (string)wish!["id"]! == "wine")!.DeepClone().AsObject();
        wine["who"] = new JsonObject();
        wine["weight"] = 2;
        section["wishes"] = new JsonArray(wine);
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

    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(value, Protocol.Json)) };

    private sealed class Production(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
