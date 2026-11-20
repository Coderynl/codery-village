using System.Text.Json;
using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Save;
using Codery.Village.Modules.Day;
using Codery.Village.Modules.Talk;
using Codery.Village.Modules.Wishes;
using Codery.Village.Server;
using Codery.Village.Server.Talk;
using TalkModule = Codery.Village.Modules.Talk.Talk;
using WishesModule = Codery.Village.Modules.Wishes.Wishes;

namespace Codery.Village.Tests;

public class TalkTests
{
    private const ulong Seed = 7;
    private static DateTimeOffset June(int day, int hour, int minute = 0) => Builders.June(day, hour, minute);

    private static readonly DataSet Data = DataFolder.Load(Builders.Folder, []);
    private static readonly TalkSettings Settings = Data.Section<TalkSettings>("talk");

    /// <summary>The real village with wishes and talk, on a Saturday afternoon: many are free.</summary>
    private static Builders.Village Start(DateTimeOffset? at = null) =>
        Builders.Start(Seed, at ?? June(6, 14), Data, more: [new WishesModule(), new TalkModule()]);

    private static Builders.Village Load(SavedVillage saved) =>
        Builders.Load(saved, Data, more: [new WishesModule(), new TalkModule()]);

    private static TalkModule Talk(Builders.Village village) => village.Modules.OfType<TalkModule>().Single();

    private static IEnumerable<int> Islands() =>
        Builders.RealMap.Islands.Where(island => !island.Eddie).Select(island => island.Id);

    /// <summary>The pairs on the island who are at leisure or on a visit, arrived, on the same or a neighbouring tile.</summary>
    private static List<(int, int)> Pairs(Builders.Village village, int island)
    {
        var free = village.People.On(island).Where(resident => !resident.IsEddie)
            .Select(resident => (resident.Id, Doing: village.Day.Of(resident.Id)))
            .Where(item => item.Doing is { Activity: Activity.Leisure or Activity.Visit, Walk: null })
            .ToList();
        var pairs = new List<(int, int)>();
        for (var i = 0; i < free.Count; i++)
            for (var j = i + 1; j < free.Count; j++)
                if (free[i].Doing.At.DistanceTo(free[j].Doing.At) <= 1)
                    pairs.Add((Math.Min(free[i].Id, free[j].Id), Math.Max(free[i].Id, free[j].Id)));
        return pairs;
    }

    private static (int, int) PairOf(TalkRequest request) => (Math.Min(request.A, request.B), Math.Max(request.A, request.B));

    /// <summary>Runs until the module asks for a conversation; takes and returns it.</summary>
    private static TalkRequest RunUntilRequest(Builders.Village village, int most = 4 * 3600)
    {
        var talk = Talk(village);
        for (var i = 0; i < most; i++)
        {
            village.Run(1);
            if (talk.TakeRequests() is [var request, ..]) return request;
        }
        throw new InvalidOperationException("No moment came.");
    }

    private static TalkLines Written(TalkRequest request) =>
        new([$"{request.Facts.A.Name}: Hallo.", $"{request.Facts.B.Name}: Dag!"], [$"{request.Facts.A.Name}: Hello.", $"{request.Facts.B.Name}: Hi!"], FromTemplate: false);

    [Fact]
    public void Two_residents_at_leisure_make_a_moment()
    {
        var village = Start();
        var talk = Talk(village);

        for (var i = 0; i < 4 * 3600; i++)
        {
            village.Run(1);
            var requests = talk.TakeRequests();
            var withPairs = Islands().Where(island => Pairs(village, island).Count > 0).ToList();
            if (withPairs.Count == 0)
            {
                Assert.Empty(requests);
                continue;
            }

            // the first time two may talk, they do: one moment per island that has a pair
            Assert.Equal(withPairs.Order(), requests.Select(request => request.Island).Order());
            foreach (var request in requests)
            {
                Assert.Contains(PairOf(request), Pairs(village, request.Island));
                Assert.Equal(village.People.Get(request.A).Name, request.Facts.A.Name);
                Assert.Equal(village.People.Get(request.B).Name, request.Facts.B.Name);
                Assert.Equal(village.People.Get(request.A).Phase, request.Facts.A.Phase);
                Assert.Equal(request.Topic.Split(':')[0], request.Facts.Topic.Kind);
            }
            // nothing is shown yet
            Assert.Empty(talk.Recent);
            Assert.Empty(village.Heard<Talked>());
            return;
        }
        Assert.Fail("Nobody was free on the same tile all afternoon.");
    }

    [Fact]
    public void At_most_one_moment_per_half_hour_per_island()
    {
        var village = Start();
        var talk = Talk(village);
        var moments = new List<(int Island, DateTimeOffset At)>();

        for (var i = 0; i < 4 * 3600 * 2; i++)
        {
            var at = village.Time.GetUtcNow();
            village.Run(1);
            moments.AddRange(talk.TakeRequests().Select(request => (request.Island, at)));
        }

        Assert.True(moments.Count >= 3, $"only {moments.Count} moments in two hours");
        foreach (var island in moments.GroupBy(moment => moment.Island))
        {
            var times = island.Select(moment => moment.At).ToList();
            for (var i = 1; i < times.Count; i++)
                Assert.True(times[i] - times[i - 1] >= TimeSpan.FromMinutes(Settings.MinMinutesBetween), $"{times[i - 1]} and {times[i]}");
        }
    }

    [Fact]
    public void The_topic_is_their_heaviest_shared_wish_or_else_work()
    {
        var village = Start(June(6, 9));
        var talk = Talk(village);
        var wishes = village.Modules.OfType<WishesModule>().Single();
        var weights = Data.Section<WishSettings>("wishes").Wishes.Select((wish, index) => (wish.Id, wish.Weight, index)).ToDictionary(wish => wish.Id);
        var roles = Data.Section<Catalog>("catalog").Roles.ToDictionary(role => role.Id);
        var kinds = new HashSet<string>();
        var seen = 0;

        for (var at = June(6, 9); at < June(6, 21); at = at.AddMinutes(31))
        {
            village.JumpTo(at);
            foreach (var request in talk.TakeRequests())
            {
                seen++;
                var a = village.People.Get(request.A);
                var b = village.People.Get(request.B);
                var shared = wishes.Of(a).Intersect(wishes.Of(b)).ToList();
                var kind = request.Facts.Topic.Kind;
                kinds.Add(kind);
                if (shared.Count > 0)
                {
                    var heaviest = shared.OrderByDescending(wish => weights[wish].Weight).ThenBy(wish => weights[wish].index).First();
                    Assert.Equal("wish:" + heaviest, request.Topic);
                    var wish = Data.Section<WishSettings>("wishes").Wishes.Single(item => item.Id == heaviest);
                    Assert.Equal(new TalkName(wish.Nl, wish.En), request.Facts.Topic.Name);
                }
                else if (village.Work.JobOf(a.Id) is { } job)
                {
                    Assert.Equal("work:" + job.Role, request.Topic);
                    Assert.Equal(new TalkName(roles[job.Role].Nl, roles[job.Role].En), request.Facts.Topic.Name);
                    Assert.Equal(request.Facts.Topic.Name, request.Facts.A.Work);
                }
                else
                {
                    // the one who works is A
                    Assert.Null(village.Work.JobOf(b.Id));
                    Assert.Equal("interest", kind);
                    Assert.Equal("interest:" + a.Interests[0], request.Topic);
                }
            }
        }

        Assert.True(seen >= 5, $"only {seen} moments in a day");
        Assert.Contains("wish", kinds);
    }

    [Fact]
    public void An_answer_shows_in_the_next_tick_and_in_the_log()
    {
        var village = Start();
        var talk = Talk(village);
        var request = RunUntilRequest(village);
        var lines = Written(request);

        talk.Answer(request.Id, lines);
        Assert.Empty(talk.Recent);
        village.Run(1);

        var held = Assert.Single(talk.Recent);
        Assert.Equal((request.Id, request.Island, request.A, request.B, request.Topic), (held.Id, held.Island, held.A, held.B, held.Topic));
        Assert.Equal(lines.Nl, held.Lines.Nl);
        Assert.Equal(lines.En, held.Lines.En);
        Assert.False(held.Lines.FromTemplate);
        var entry = Assert.Single(village.Engine.Log, entry => entry.Kind == "talked");
        Assert.Contains(request.Facts.A.Name, entry.Subject);
        Assert.Contains("Hallo.", entry.Detail);
        Assert.Contains("Hello.", entry.Detail);
        Assert.Contains("talked", Settings.Log.Keys);

        village.Run(1);
        var talked = Assert.Single(village.Heard<Talked>());
        Assert.Equal((request.A, request.B), (talked.A, talked.B));
        Assert.Equal(lines.Nl, talked.Lines.Nl);

        // an answer comes once; an unknown one is let go
        talk.Answer(request.Id, lines);
        talk.Answer(9999, lines);
        village.Run(1);
        Assert.Single(talk.Recent);
    }

    [Fact]
    public void A_request_without_an_answer_becomes_a_template()
    {
        var village = Start();
        var talk = Talk(village);
        var request = RunUntilRequest(village);

        village.Run(Settings.AnswerWithinMinutes * 60 * 4 + 1);

        var held = Assert.Single(talk.Recent, conversation => conversation.Id == request.Id);
        Assert.True(held.Lines.FromTemplate);
        Assert.InRange(held.Lines.Nl.Count, 2, 4);
        Assert.InRange(held.Lines.En.Count, 2, 4);
        Assert.DoesNotContain(held.Lines.Nl.Concat(held.Lines.En), line => line.Contains('{'));
        Assert.Contains(held.Lines.Nl, line => line.Contains(request.Facts.A.Name));
        Assert.Contains(held.Lines.En, line => line.Contains(request.Facts.Topic.Name.En));
    }

    [Fact]
    public void A_load_keeps_the_conversations_and_asks_nothing_again()
    {
        var village = Start();
        var talk = Talk(village);
        var first = RunUntilRequest(village);
        talk.Answer(first.Id, Written(first));
        village.Run(1);
        // a second moment, taken but not answered when the village stops
        var second = RunUntilRequest(village, most: 4 * 3600 * 2);

        var loaded = Load(village.Engine.Save());
        Assert.DoesNotContain(loaded.Engine.Log, entry => entry.Kind.StartsWith("module"));
        var again = Talk(loaded);

        Assert.Equal(Json(talk.Recent), Json(again.Recent));
        Assert.Empty(again.TakeRequests());
        loaded.Run(Settings.AnswerWithinMinutes * 60 * 4 + 1);
        Assert.DoesNotContain(again.TakeRequests(), request => request.Id <= second.Id);
        Assert.True(Assert.Single(again.Recent, conversation => conversation.Id == second.Id).Lines.FromTemplate);
    }

    [Fact]
    public void The_save_keeps_only_the_last_conversations()
    {
        var village = Start(June(6, 9));
        var talk = Talk(village);
        var answered = 0;

        for (var at = June(6, 9); at < June(7, 21) && answered <= Settings.Keep; at = at.AddMinutes(31))
        {
            village.JumpTo(at);
            foreach (var request in talk.TakeRequests())
            {
                talk.Answer(request.Id, Written(request));
                answered++;
            }
        }
        village.Run(1);

        Assert.True(answered > Settings.Keep, $"only {answered} conversations");
        Assert.Equal(Settings.Keep, talk.Recent.Count);
    }

    [Fact]
    public void Talk_changes_nothing_else()
    {
        // one village is answered by a model at once; the other is never answered and falls back to templates
        // minutes later: other words, at another time
        var model = Start(June(1, 12));
        var templates = Start(June(1, 12));
        var said = 0;

        for (var i = 0; i < 20_000; i++)
        {
            model.Run(1);
            templates.Run(1);
            foreach (var request in Talk(model).TakeRequests())
            {
                Talk(model).Answer(request.Id, Written(request));
                said++;
            }
            Talk(templates).TakeRequests();
        }

        Assert.True(said > 0, "nobody talked");
        var late = Talk(templates).Recent.ToDictionary(conversation => conversation.Id);
        var shared = Talk(model).Recent.Where(conversation => late.ContainsKey(conversation.Id)).ToList();
        Assert.NotEmpty(shared);
        Assert.All(shared, conversation =>
        {
            Assert.False(conversation.Lines.FromTemplate);
            Assert.True(late[conversation.Id].Lines.FromTemplate);
            Assert.True(late[conversation.Id].Tick - conversation.Tick >= Settings.AnswerWithinMinutes * 60 * 4 - 1,
                $"answered at {conversation.Tick} and {late[conversation.Id].Tick}");
        });
        var a = model.Engine.Save();
        var b = templates.Engine.Save();
        Assert.Equal(
            a.Modules.Where(part => part.Name != "talk").Select(part => JsonSerializer.Serialize(part, SaveJson.Options)),
            b.Modules.Where(part => part.Name != "talk").Select(part => JsonSerializer.Serialize(part, SaveJson.Options)));
    }

    // risk: a word list that refuses plain talk of hunting or a death makes the village mute about life, or a list
    // that lets sexual content, religion or discrimination through breaks what Michael forbids
    private static readonly Decency Decent = new(Data.Section<TalkServerSettings>("talk"));

    [Fact]
    public void A_line_about_hunting_or_death_passes_the_word_list()
    {
        Assert.Null(Decent.Forbidden(new TalkLines(["Jan: Morgen ga ik jagen, het wild is dun gezaaid."], ["Jan: Tomorrow I go hunting, game is scarce."], FromTemplate: false)));
        Assert.Null(Decent.Forbidden(new TalkLines(["Els: Sinds opa dood is, is het stil in huis."], ["Els: Since grandpa died the house is quiet."], FromTemplate: false)));
    }

    // risk: wine from their own grapes is what the village wishes for; a list that still refuses it blocks that wish
    [Fact]
    public void A_glass_of_wine_passes_the_word_list()
    {
        Assert.Null(Decent.Forbidden(new TalkLines(["Piet: Vanavond drinken we wijn van onze eigen druiven."], ["Piet: Tonight we drink wine from our own grapes."], FromTemplate: false)));
    }

    [Theory]
    [InlineData("Jan: Seks is niet voor hier.", "Jan: Sex is not for here.")]
    [InlineData("Jan: We gaan naar de kerk.", "Jan: We go to church.")]
    [InlineData("Jan: Ik haat die lelijke buurman.", "Jan: I hate that ugly neighbour.")]
    [InlineData("Jan: Piet is weer dronken.", "Jan: Piet is drunk again.")]
    public void A_line_with_sexual_content_religion_or_discrimination_is_still_refused(string nl, string en)
    {
        Assert.NotNull(Decent.Forbidden(new TalkLines([nl], [en], FromTemplate: false)));
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, SaveJson.Options);
}
