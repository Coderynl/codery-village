using System.Text.Json.Nodes;
using Codery.Village.Core.Data;
using Codery.Village.Server;
using Codery.Village.Server.Desk;
using Codery.Village.Server.Store;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codery.Village.Tests;

/// <summary>
/// The desk on a running village, with GitHub in memory. Test plan, one risk per test:
/// - the village's task never reaches Eddie, or a live release is not told on its issue;
/// - with the switch off, or switched on without the App's settings, the desk still speaks to GitHub or crashes;
/// - while another server (the old one, during a deploy) has the round, this one acts too and posts twice;
/// - a stop, a refusal or a lost answer between opening and commanding opens a second issue or posts the command twice;
/// - an issue the App opened already is opened again, or an old command or result on it is taken for the new one;
/// - a live wish's errand is forgotten while the desk asks for another wish, so Michael's reject never holds it.
/// </summary>
[Collection(RealTimeCollection.Name)]
public class DeskTests
{
    private const string Eddie = "eddie-codery[bot]";
    private const string App = "codery-village[bot]";

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

    private static readonly DeskOptions On = new()
    {
        Enabled = true,
        AppId = "12345",
        PrivateKey = "a key",
        AppLogin = App,
        EddieLogin = Eddie,
    };

    private readonly FakeGitHub _github = new(App);
    private readonly MemoryErrandStore _errands = new();
    private readonly Clock _clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));

    /// <summary>The base data with grapes, where everyone wishes for wine over a low threshold, so the village asks for wine soon; with the winery it serves wine.</summary>
    private static DataSet Data(bool winery = false)
    {
        var sections = Directory.EnumerateFiles(VillageSetup.Folder, "*.json").ToDictionary(path => Path.GetFileNameWithoutExtension(path), File.ReadAllText);
        var section = JsonNode.Parse(sections["wishes"])!.AsObject();
        var wine = section["wishes"]!.AsArray().Single(wish => (string)wish!["id"]! == "wine")!.AsObject();
        wine["who"] = new JsonObject();
        wine["weight"] = 10;
        section["taskThreshold"] = 1;
        sections["wishes"] = section.ToJsonString();
        return DataSet.FromTexts(sections, winery ? [Grapes, Winery] : [Grapes]);
    }

    private static WebApplicationFactory<Program> Server(FakeStore store, DataSet data) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.ClearProviders())
                .UseSetting("Village:SaveSeconds", "3600")
                .UseSetting("Village:Speed", "64");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IVillageStore>(store);
                services.AddSingleton(data);
            });
        });

    /// <summary>A village that asks for wine.</summary>
    private static async Task<VillageHost> Asking(WebApplicationFactory<Program> server)
    {
        var host = await HostTests.Started(server);
        await HostTests.Until(() => host.OpenTask() is { Wish: "wine" }, "the task for wine");
        return host;
    }

    private EddieDesk Desk(VillageHost host, DataSet data, DeskOptions? options = null, IErrandStore? store = null) =>
        new(host, _github, store ?? _errands, options ?? On, data, ["grapes"], _clock, NullLogger<EddieDesk>.Instance);

    private static string Result(long command, string outcome) =>
        $"Klaar.\n\n<!-- eddie:result cmd={command} run=run-1 outcome={outcome} sig=0123456789abcdef -->";

    private static async Task<ErrandRecord> Record(MemoryErrandStore errands, string wish) =>
        Assert.Single(await errands.All(default), errand => errand.Wish == wish);

    [Fact]
    public async Task A_task_goes_round_to_a_live_release()
    {
        var store = new FakeStore();
        var asking = Data();
        await using (var server = Server(store, asking))
        {
            var host = await Asking(server);
            var desk = Desk(host, asking);

            // the server begins to stop while the issue is opened: the desk still does what it decided, and keeps it
            using var stopping = new CancellationTokenSource();
            _github.Once("issue", after: true, stopping.Cancel);
            await desk.Round(stopping.Token);
            await desk.Round(default);

            var issue = Assert.Single(_github.Issues);
            Assert.Equal(host.OpenTask()!.TitleNl, issue.Title);
            Assert.Equal(["wish"], issue.Labels);
            var command = Assert.Single(issue.Comments);
            Assert.StartsWith("/eddie ", command.Body);
            Assert.Equal((issue.Number, command.Id, ErrandState.Asking), Of(await Record(_errands, "wine")));

            _github.Add(issue.Number, Eddie, Result(command.Id, "done"));
            await desk.Round(default);
            Assert.Equal(ErrandState.Delivered, (await Record(_errands, "wine")).State);
        }

        // the release is deployed: the server starts again on the data that serves wine, with the same village and errands
        var serving = Data(winery: true);
        await using (var server = Server(store, serving))
        {
            var desk = Desk(await HostTests.Started(server), serving);
            await desk.Round(default);
            await desk.Round(default);
        }

        var told = Assert.Single(_github.Issues);
        Assert.Equal(3, told.Comments.Count);
        Assert.Equal(DeskTexts.From(serving).Live.Nl, told.Comments[^1].Body);
        Assert.Equal(ErrandState.Live, (await Record(_errands, "wine")).State);
    }

    [Fact]
    public async Task The_switch_off_touches_nothing()
    {
        var data = Data();
        await using var server = Server(new FakeStore(), data);
        var host = await Asking(server);

        foreach (var options in new[] { On with { Enabled = false }, On with { PrivateKey = "" }, On with { AppLogin = "" } })
        {
            var desk = Desk(host, data, options);
            await desk.StartAsync(default);
            // it ends at once, without a fault, and a round on demand does nothing either
            await desk.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
            await desk.Round(default);
            await desk.StopAsync(default);
        }

        Assert.Equal(0, _github.Calls);
        Assert.Empty(await _errands.All(default));
        Assert.Empty(host.Held());
    }

    [Fact]
    public async Task A_round_another_server_holds_does_nothing()
    {
        var data = Data();
        await using var server = Server(new FakeStore(), data);
        var host = await Asking(server);

        await Desk(host, data, store: new Taken(_errands)).Round(default);

        Assert.Equal(0, _github.Calls);
        Assert.Empty(await _errands.All(default));
    }

    /// <summary>The errands, while another server has the round.</summary>
    private sealed class Taken(IErrandStore errands) : IErrandStore
    {
        public Task<IReadOnlyList<ErrandRecord>> All(CancellationToken cancellation) => errands.All(cancellation);

        public Task Save(ErrandRecord errand, CancellationToken cancellation) => errands.Save(errand, cancellation);

        public Task<IAsyncDisposable?> TryLockRound(CancellationToken cancellation) => Task.FromResult<IAsyncDisposable?>(null);
    }

    [Fact]
    public async Task A_restart_between_opening_and_commanding_posts_the_command_once()
    {
        var data = Data();
        await using var server = Server(new FakeStore(), data);
        var host = await Asking(server);

        // GitHub refuses the command right after the issue is opened: the number is kept, the command waits for the next start
        _github.Once("comment", after: false, () => throw new GitHubException(502, "/repos/x/issues/1/comments"));
        await Desk(host, data).Round(default);
        var issue = Assert.Single(_github.Issues);
        Assert.Empty(issue.Comments);
        Assert.Equal((issue.Number, 0L, ErrandState.Asking), Of(await Record(_errands, "wine")));

        // after the restart the command is posted, but GitHub's answer gets lost
        _github.Once("comment", after: true, () => throw new HttpRequestException("The answer got lost."));
        await Desk(host, data).Round(default);

        // and after the next restart it is adopted, not posted again
        await Desk(host, data).Round(default);
        await Desk(host, data).Round(default);

        Assert.Single(_github.Issues);
        var command = Assert.Single(issue.Comments);
        Assert.Equal((issue.Number, command.Id, ErrandState.Asking), Of(await Record(_errands, "wine")));
    }

    [Fact]
    public async Task An_issue_the_app_opened_already_is_used_again_without_its_old_command()
    {
        var data = Data();
        await using var server = Server(new FakeStore(), data);
        var host = await Asking(server);
        // an earlier errand for wine on an issue that is still open, answered long ago; and a stranger's issue with the same title
        var title = host.OpenTask()!.TitleNl;
        var earlier = _github.Opened(title, App);
        var old = _github.Add(earlier, App, "/eddie build wine, attempt 1");
        _github.Add(earlier, Eddie, Result(old, "done"));
        _github.Opened(title, "stranger");

        // the new command is posted there, but GitHub's answer gets lost: the next round takes it, and the old done stays old
        var desk = Desk(host, data);
        _github.Once("comment", after: true, () => throw new HttpRequestException("The answer got lost."));
        await desk.Round(default);
        await desk.Round(default);
        await desk.Round(default);

        Assert.Equal(2, _github.Issues.Count);
        Assert.Equal(3, _github.Issues.Single(issue => issue.Number == earlier).Comments.Count);
        var command = _github.Issues.Single(issue => issue.Number == earlier).Comments[^1];
        Assert.True(command.Id > old);
        Assert.StartsWith("/eddie ", command.Body);
        Assert.Equal((earlier, command.Id, ErrandState.Asking), Of(await Record(_errands, "wine")));
    }

    [Fact]
    public async Task A_live_record_is_followed_while_another_wish_is_asked()
    {
        var data = Data();
        await using var server = Server(new FakeStore(), data);
        var host = await Asking(server);
        // the café went live earlier; now Michael rejects it, while the village asks for wine
        var cafe = _github.Opened("Het dorp wenst een café", App);
        var asked = _github.Add(cafe, App, "/eddie build cafe, attempt 1");
        await _errands.Save(new ErrandRecord("cafe", cafe, asked, ErrandState.Live, _clock.Now, 0, [], false), default);
        _github.Add(cafe, Eddie, Result(asked, "reverted"));

        await Desk(host, data).Round(default);

        Assert.Contains("cafe", host.Held());
        Assert.Equal(ErrandState.Held, (await Record(_errands, "cafe")).State);
        var held = _github.Issues.Single(issue => issue.Number == cafe);
        Assert.Equal(DeskTexts.From(data).Rejected.Nl, held.Comments[^1].Body);
        Assert.Contains("on-hold", held.Labels);

        var wine = await Record(_errands, "wine");
        Assert.NotEqual(cafe, wine.Issue);
        Assert.Equal(ErrandState.Asking, wine.State);
        Assert.StartsWith("/eddie ", Assert.Single(_github.Issues.Single(issue => issue.Number == wine.Issue).Comments).Body);
    }

    private static (int Issue, long Command, ErrandState State) Of(ErrandRecord errand) => (errand.Issue, errand.Command, errand.State);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>GitHub's issues in memory, as the App sees them; one test at a time.</summary>
    private sealed class FakeGitHub(string app) : IVillageGitHub
    {
        public sealed record Issue(int Number, string Title, string Author, List<string> Labels, List<IssueComment> Comments);

        private long _ids = 1000;

        public List<Issue> Issues { get; } = [];

        public int Calls { get; private set; }

        private (string Call, bool After, Action Act)? _once;

        /// <summary>
        /// Runs <paramref name="act"/> once, at the next <paramref name="call"/> (<c>issue</c>, <c>comment</c> or <c>label</c>),
        /// before the change or after it: to refuse it, lose its answer or stop the server.
        /// </summary>
        public void Once(string call, bool after, Action act) => _once = (call, after, act);

        public int Opened(string title, string author, string label = "wish")
        {
            Issues.Add(new Issue(Issues.Count + 1, title, author, [label], []));
            return Issues.Count;
        }

        public long Add(int issue, string author, string body)
        {
            var id = ++_ids;
            Issues.Single(item => item.Number == issue).Comments.Add(new IssueComment(id, author, body, DateTimeOffset.UnixEpoch.AddSeconds(id)));
            return id;
        }

        public Task<int> OpenIssue(string title, string body, IReadOnlyList<string> labels, CancellationToken cancellation)
        {
            Calls++;
            At("issue", after: false);
            Issues.Add(new Issue(Issues.Count + 1, title, app, [.. labels], []));
            At("issue", after: true);
            return Task.FromResult(Issues.Count);
        }

        public Task<long> Comment(int issue, string body, CancellationToken cancellation)
        {
            Calls++;
            At("comment", after: false);
            var id = Add(issue, app, body);
            At("comment", after: true);
            return Task.FromResult(id);
        }

        public Task<IReadOnlyList<IssueComment>> Comments(int issue, CancellationToken cancellation)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<IssueComment>>([.. Issues.Single(item => item.Number == issue).Comments]);
        }

        public Task Label(int issue, string label, CancellationToken cancellation)
        {
            Calls++;
            At("label", after: false);
            Issues.Single(item => item.Number == issue).Labels.Add(label);
            At("label", after: true);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<IssueSummary>> OpenIssues(string label, CancellationToken cancellation)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<IssueSummary>>(
                [.. Issues.Where(issue => issue.Labels.Contains(label)).Select(issue => new IssueSummary(issue.Number, issue.Title, issue.Author))]);
        }

        private void At(string call, bool after)
        {
            if (_once is not { } once || once.Call != call || once.After != after) return;
            _once = null;
            once.Act();
        }
    }
}
