using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codery.Village.Core.Data;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;
using Codery.Village.Modules.People;
using Codery.Village.Modules.Talk;
using Codery.Village.Server;
using Codery.Village.Server.Talk;
using Microsoft.Extensions.Configuration;
using TalkModule = Codery.Village.Modules.Talk.Talk;

namespace Codery.Village.Tests;

public class TalkWorkerTests
{
    private static readonly DataSet Data = DataFolder.Load(Builders.Folder, []);
    private static readonly JsonObject Real = JsonNode.Parse(File.ReadAllText(Path.Combine(Builders.Folder, "talk.json")))!.AsObject();
    private static readonly int CheckTokens = (int)Real["maxTokens"]!["check"]!;
    private static readonly DateTimeOffset Evening = new(2026, 6, 1, 23, 0, 0, TimeSpan.FromHours(2));

    private static readonly TalkRequest Request = new(1, 1, 10, 11, "work:fisher", new TalkFacts(
        new TalkPerson("Anna", LifePhase.Working, ["high openness"], new TalkName("visser", "fisher")),
        new TalkPerson("Bram", LifePhase.Retired, ["low thrift"]),
        new TalkTopic("work", new TalkName("visser", "fisher"))));

    private static readonly string[] GoodNl = ["Bram: Hoe was het op het water?", "Anna: Rustig, maar ik ving genoeg."];
    private static readonly string[] GoodEn = ["Bram: How was it on the water?", "Anna: Quiet, but I caught enough."];

    /// <summary>Stands in for the Messages API: records each body and answers as told.</summary>
    private sealed class FakeApi(Func<JsonObject, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        private readonly List<JsonObject> _bodies = [];

        public List<string?> Keys { get; } = [];

        public IReadOnlyList<JsonObject> Bodies
        {
            get { lock (_bodies) return [.. _bodies]; }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancel))!.AsObject();
            lock (_bodies)
            {
                _bodies.Add(body);
                Keys.Add(request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : null);
            }
            return await reply(body, cancel);
        }
    }

    private static bool IsCheck(JsonObject body) => (int)body["max_tokens"]! == CheckTokens;

    private static Task<HttpResponseMessage> Reply(string text, int input = 600, int output = 80, string stop = "end_turn")
    {
        var message = new JsonObject
        {
            ["id"] = "msg_test",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = "claude-haiku-5-5",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["stop_reason"] = stop,
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject { ["input_tokens"] = input, ["output_tokens"] = output },
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        });
    }

    private static string Lines(IEnumerable<string> nl, IEnumerable<string> en) =>
        new JsonObject { ["nl"] = new JsonArray([.. nl.Select(line => JsonValue.Create(line))]), ["en"] = new JsonArray([.. en.Select(line => JsonValue.Create(line))]) }.ToJsonString();

    private static string Verdict(string verdict) => new JsonObject { ["verdict"] = verdict, ["reason"] = "test" }.ToJsonString();

    private static Task<HttpResponseMessage> Good(JsonObject body) => IsCheck(body) ? Reply(Verdict("ok")) : Reply(Lines(GoodNl, GoodEn));

    private static async Task<HttpResponseMessage> Never(CancellationToken cancel)
    {
        await Task.Delay(Timeout.Infinite, cancel);
        throw new InvalidOperationException("unreachable");
    }

    private static (TalkWorker Worker, FakeApi Api, TalkBudget Budget) Worker(
        Func<JsonObject, CancellationToken, Task<HttpResponseMessage>> reply, DataSet? data = null, TalkOptions? options = null,
        TimeProvider? time = null, decimal daily = 5m)
    {
        options ??= new TalkOptions("test-key");
        var api = new FakeApi(reply);
        var budget = new TalkBudget(daily, time ?? new SimulatedTime(Evening));
        var talker = new ClaudeTalker(data ?? Data, options, budget, new HttpClient(api));
        return (new TalkWorker(talker, data ?? Data, options), api, budget);
    }

    private static void AssertTemplate(TalkLines lines)
    {
        Assert.True(lines.FromTemplate);
        Assert.InRange(lines.Nl.Count, 2, 4);
        Assert.InRange(lines.En.Count, 2, 4);
        Assert.DoesNotContain(lines.Nl.Concat(lines.En), line => line.Contains('{') || line.Contains('}'));
        Assert.Contains(lines.Nl, line => line.Contains("Anna"));
        Assert.Contains(lines.En, line => line.Contains("Bram"));
        Assert.Contains(lines.En, line => line.Contains("fisher"));
    }

    [Fact]
    public async Task A_clean_conversation_comes_from_the_model()
    {
        var (worker, api, budget) = Worker((body, _) => Good(body));
        await worker.StartAsync(CancellationToken.None);

        worker.Offer(Request);
        var answers = await Answers(worker, 1);
        await worker.StopAsync(CancellationToken.None);

        var (id, lines) = Assert.Single(answers);
        Assert.Equal(Request.Id, id);
        Assert.False(lines.FromTemplate);
        Assert.Equal(GoodNl, lines.Nl);
        Assert.Equal(GoodEn, lines.En);

        // the conversation, then the check, both on the small model, with the key
        Assert.Equal(2, api.Bodies.Count);
        Assert.All(api.Bodies, body => Assert.Equal("claude-haiku-5-5", (string)body["model"]!));
        Assert.Equal(400, (int)api.Bodies[0]["max_tokens"]!);
        Assert.True(IsCheck(api.Bodies[1]));
        Assert.All(api.Keys, key => Assert.Equal("test-key", key));
        Assert.Contains("fictional", api.Bodies[0]["system"]!.ToJsonString());
        // structured JSON, without thinking eating the 400 tokens
        Assert.All(api.Bodies, body =>
        {
            Assert.Equal("disabled", (string)body["thinking"]!["type"]!);
            Assert.Equal("json_schema", (string)body["output_config"]!["format"]!["type"]!);
        });

        // the prompt holds the facts and nothing else
        var user = Assert.Single(api.Bodies[0]["messages"]!.AsArray())!;
        Assert.Equal("user", (string)user["role"]!);
        var said = user["content"] is JsonValue text ? (string)text! : (string)user["content"]![0]!["text"]!;
        Assert.Equal(JsonSerializer.Serialize(Request.Facts, SaveJson.Options), said);
        Assert.True(budget.Spent > 0);
    }

    public static TheoryData<string> Failures => ["timeout", "unreadable", "too-long", "refusal", "words", "judgement", "budget", "no-key"];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Failures_become_templates(string failure)
    {
        var forbidden = Real["words"]!["nl"]!.AsArray().Select(word => (string)word!).First(word => !word.EndsWith('*'));
        var (worker, api, _) = Worker(
            (body, cancel) => failure switch
            {
                "timeout" => Never(cancel),
                "unreadable" => IsCheck(body) ? Reply(Verdict("ok")) : Reply("Anna: hallo {"),
                "too-long" => IsCheck(body) ? Reply(Verdict("ok")) : Reply(Lines([.. GoodNl, .. GoodNl, "Anna: Tot morgen."], GoodEn)),
                "refusal" => IsCheck(body) ? Reply(Verdict("ok")) : Reply("", stop: "refusal"),
                "words" => IsCheck(body) ? Reply(Verdict("ok")) : Reply(Lines(["Bram: Hoe was het?", $"Anna: Echt {forbidden}."], GoodEn)),
                "judgement" => IsCheck(body) ? Reply(Verdict("refuse")) : Reply(Lines(GoodNl, GoodEn)),
                _ => Good(body),
            },
            options: new TalkOptions(failure == "no-key" ? null : "test-key", TimeoutSeconds: failure == "timeout" ? 0.2 : 20),
            daily: failure == "budget" ? 0m : 5m);

        var lines = await worker.Answer(Request, CancellationToken.None);

        AssertTemplate(lines);
        var calls = failure switch
        {
            "budget" or "no-key" => 0,
            "judgement" => 2,
            _ => 1,
        };
        Assert.Equal(calls, api.Bodies.Count);
    }

    [Fact]
    public async Task The_budget_counts_both_calls_and_resets_at_midnight()
    {
        // a cent per hundred tokens, so the five dollars go in a few conversations
        var talk = Real.DeepClone().AsObject();
        talk["prices"]!["claude-haiku-5-5"] = new JsonObject { ["inputPerMillion"] = 100, ["outputPerMillion"] = 100 };
        var data = new DataSet(new Dictionary<string, string> { ["talk"] = talk.ToJsonString() });
        var time = new SimulatedTime(Evening);
        // what the fake model says it used: within what was asked for
        const decimal PerConversation = (1000 + 300 + 1000 + 100) * 100m / 1_000_000m;
        var (worker, api, budget) = Worker(
            (body, _) => IsCheck(body) ? Reply(Verdict("ok"), 1000, 100) : Reply(Lines(GoodNl, GoodEn), 1000, 300),
            data, time: time);

        var written = 0;
        for (var i = 0; i < 100; i++)
        {
            if ((await worker.Answer(Request with { Id = i }, CancellationToken.None)).FromTemplate) break;
            written++;
        }

        Assert.InRange(written, 2, 99);
        Assert.Equal(2 * written, api.Bodies.Count);
        Assert.Equal(written * PerConversation, budget.Spent);
        Assert.True(budget.Spent <= 5m);
        Assert.True(5m - budget.Spent < 1m, $"stopped at {budget.Spent}");

        // just before midnight the day is still spent
        time.Advance(new DateTimeOffset(2026, 6, 1, 23, 59, 59, TimeSpan.FromHours(2)) - time.GetUtcNow());
        Assert.True((await worker.Answer(Request, CancellationToken.None)).FromTemplate);
        Assert.Equal(2 * written, api.Bodies.Count);

        // from midnight, Dutch time, it starts again
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False((await worker.Answer(Request, CancellationToken.None)).FromTemplate);
        Assert.Equal(2 * written + 2, api.Bodies.Count);
        Assert.Equal(PerConversation, budget.Spent);
    }

    [Fact]
    public async Task The_tick_never_waits()
    {
        var village = Builders.Start(7, Builders.June(6, 14), Data, more: [new TalkModule()]);
        var talk = village.Modules.OfType<TalkModule>().Single();
        var worker = new TalkWorker(new SilentTalker(), Data, new TalkOptions("test-key"));
        await worker.StartAsync(CancellationToken.None);
        var offered = 0;

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 10_000; i++)
        {
            village.Run(1);
            foreach (var request in talk.TakeRequests())
            {
                worker.Offer(request);
                offered++;
            }
            foreach (var (id, lines) in worker.TakeAnswers()) talk.Answer(id, lines);
        }
        clock.Stop();
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(10_000, village.Engine.Tick);
        Assert.Empty(village.Engine.Stopped);
        Assert.True(offered > 0, "no moment to wait for");
        // ten thousand ticks are forty minutes of village: they run in far less
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
        // the village went on: what was not answered became a template
        Assert.NotEmpty(talk.Recent);
        Assert.All(talk.Recent, conversation => Assert.True(conversation.Lines.FromTemplate));
    }

    [Fact]
    public void The_options_come_from_Village_Talk_with_five_dollars_and_twenty_seconds()
    {
        var none = new ConfigurationBuilder().Build();
        Assert.Equal(new TalkOptions(null, 5m, 20), TalkOptions.From(none));

        var set = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Village:Talk:ApiKey"] = "secret",
            ["Village:Talk:DailyUsd"] = "2.5",
            ["Village:Talk:TimeoutSeconds"] = "8",
        }).Build();
        Assert.Equal(new TalkOptions("secret", 2.5m, 8), TalkOptions.From(set));
    }

    /// <summary>Never answers, and does not listen to being cancelled either.</summary>
    private sealed class SilentTalker : ITalker
    {
        public Task<TalkLines?> Write(TalkRequest request, CancellationToken cancel) => new TaskCompletionSource<TalkLines?>().Task;
    }

    private static async Task<IReadOnlyList<(long Id, TalkLines Lines)>> Answers(TalkWorker worker, int count)
    {
        var answers = new List<(long Id, TalkLines Lines)>();
        var clock = Stopwatch.StartNew();
        while (answers.Count < count && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            answers.AddRange(worker.TakeAnswers());
            if (answers.Count < count) await Task.Delay(10);
        }
        return answers;
    }
}
