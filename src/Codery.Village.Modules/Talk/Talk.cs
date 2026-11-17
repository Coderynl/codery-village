using System.Text.Json;
using System.Text.Json.Nodes;
using Codery.Village.Core;
using Codery.Village.Core.Data;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Modules.Contracts;
using Codery.Village.Modules.Day;
using Codery.Village.Modules.People;
using Codery.Village.Modules.Wishes;
using DayModule = Codery.Village.Modules.Day.Day;
using PeopleModule = Codery.Village.Modules.People.People;
using WishesModule = Codery.Village.Modules.Wishes.Wishes;

namespace Codery.Village.Modules.Talk;

/// <summary>
/// Conversations. When two residents of an island are both at leisure or on a visit, arrived, on the same or a
/// neighbouring tile, that is a moment: at most one per island per <c>minMinutesBetween</c> calendar minutes. The
/// module then puts a <see cref="TalkRequest"/> in its outbox and shows nothing yet. The host takes the requests
/// (<see cref="TakeRequests"/>), has them written outside the village and hands each answer back
/// (<see cref="Answer"/>); the next tick the module publishes <see cref="Talked"/>, logs it and keeps it. A request
/// without an answer after <c>answerWithinMinutes</c> gets a template. What is said changes nothing but the log
/// and what the hero shows, so it does not matter when an answer comes. A load asks nothing again.
/// </summary>
public sealed class Talk : IModule
{
    private const string TalkedKind = "talked";
    private const int MostTraits = 3;

    private TalkSettings _settings = null!;
    private Dictionary<string, Wish> _wishes = [];
    private Dictionary<string, Role> _roles = [];
    private Dictionary<string, Interest> _interests = [];

    private long _next = 1;
    // per island, the calendar moment of the last moment
    private SortedDictionary<int, DateTimeOffset> _last = [];
    // asked and not yet answered, in the order they were asked
    private List<Open> _open = [];
    // answered by the host, to show in the next tick
    private List<Answered> _answered = [];
    private List<Conversation> _recent = [];

    public string Name => "talk";

    public int Version => 1;

    /// <summary>The last conversations, the oldest first.</summary>
    public IReadOnlyList<Conversation> Recent => _recent.AsReadOnly();

    /// <summary>The requests that were not taken yet. Each is given once; after a load, none is given again.</summary>
    public IReadOnlyList<TalkRequest> TakeRequests()
    {
        var taken = new List<TalkRequest>();
        for (var i = 0; i < _open.Count; i++)
        {
            if (_open[i].Taken) continue;
            _open[i] = _open[i] with { Taken = true };
            taken.Add(_open[i].Request);
        }
        return taken;
    }

    /// <summary>The written conversation for a request; shown in the next tick. An unknown or late answer is let go.</summary>
    public void Answer(long id, TalkLines lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_open.Any(open => open.Request.Id == id) && _answered.All(answered => answered.Id != id))
            _answered.Add(new Answered(id, lines));
    }

    public void Start(ModuleContext village) => Read(village);

    public void Step(ModuleContext village)
    {
        var now = village.Clocks.Calendar.Now;
        foreach (var answered in _answered)
            if (_open.FirstOrDefault(open => open.Request.Id == answered.Id) is { } open)
                Hold(village, open.Request, answered.Lines);
        _answered.Clear();

        var late = TimeSpan.FromMinutes(_settings.AnswerWithinMinutes);
        foreach (var open in _open.Where(open => now - open.Asked >= late).ToList())
            Hold(village, open.Request, _settings.Templates.Fill(open.Request));

        var between = TimeSpan.FromMinutes(_settings.MinMinutesBetween);
        foreach (var island in village.Map.Islands)
        {
            if (island.Eddie || (_last.TryGetValue(island.Id, out var last) && now - last < between)) continue;
            if (Moment(village, island.Id) is not { } request) continue;
            _open.Add(new Open(request, now, Taken: false));
            _last[island.Id] = now;
        }
    }

    private void Hold(ModuleContext village, TalkRequest request, TalkLines lines)
    {
        var tick = village.Clocks.Tick;
        _open.RemoveAll(open => open.Request.Id == request.Id);
        _recent.Add(new Conversation(request.Id, tick, request.Island, request.A, request.B, request.Topic, lines));
        if (_recent.Count > _settings.Keep) _recent.RemoveRange(0, _recent.Count - _settings.Keep);
        village.Publish(new Talked(tick, request.A, request.B, lines));
        var said = JsonSerializer.Serialize(new { nl = lines.Nl, en = lines.En }, SaveJson.Options);
        village.Log(new LogEntry(tick, TalkedKind, $"{request.Facts.A.Name} & {request.Facts.B.Name}", said));
    }

    // ── the moment ──

    private TalkRequest? Moment(ModuleContext village, int island)
    {
        if (village.Find<PeopleModule>() is not { } people || village.Find<DayModule>() is not { } day) return null;
        var free = new List<(Resident Resident, Doing Doing)>();
        foreach (var resident in people.On(island))
        {
            if (resident.IsEddie) continue;
            Doing doing;
            try
            {
                doing = day.Of(resident.Id);
            }
            catch (KeyNotFoundException)
            {
                continue;
            }
            if (doing is { Activity: Activity.Leisure or Activity.Visit, Walk: null }) free.Add((resident, doing));
        }

        var pairs = new List<(Resident A, Resident B)>();
        for (var i = 0; i < free.Count; i++)
            for (var j = i + 1; j < free.Count; j++)
                if (free[i].Doing.At.DistanceTo(free[j].Doing.At) <= 1) pairs.Add((free[i].Resident, free[j].Resident));
        if (pairs.Count == 0) return null;

        var (a, b) = pairs[village.Random.Next(pairs.Count)];
        return Request(village, island, a, b);
    }

    // their heaviest shared wish, or else the work of one of them, or else an interest of the first
    private TalkRequest Request(ModuleContext village, int island, Resident a, Resident b)
    {
        var jobs = village.Find<IJobs>();
        string kind;
        string id;
        TalkName name;
        if (SharedWish(village, a, b) is { } wish)
            (kind, id, name) = ("wish", wish.Id, new TalkName(wish.Nl, wish.En));
        else if ((RoleOf(jobs, a) ?? RoleOf(jobs, b)) is { } role)
        {
            // the one who does the work is A
            if (RoleOf(jobs, a) is null) (a, b) = (b, a);
            (kind, id, name) = ("work", role.Id, new TalkName(role.Nl, role.En));
        }
        else
        {
            var interest = _interests.TryGetValue(a.Interests[0], out var known) ? known : new Interest(a.Interests[0], a.Interests[0], a.Interests[0]);
            (kind, id, name) = ("interest", interest.Id, new TalkName(interest.Nl, interest.En));
        }

        var facts = new TalkFacts(Person(jobs, a), Person(jobs, b), new TalkTopic(kind, name));
        return new TalkRequest(_next++, island, a.Id, b.Id, $"{kind}:{id}", facts);
    }

    private Wish? SharedWish(ModuleContext village, Resident a, Resident b)
    {
        if (village.Find<WishesModule>() is not { } wishes) return null;
        var theirs = wishes.Of(b).ToHashSet();
        Wish? heaviest = null;
        // in the order of the data, so the first of equal weight wins
        foreach (var id in wishes.Of(a))
            if (theirs.Contains(id) && _wishes.TryGetValue(id, out var wish) && (heaviest is null || wish.Weight > heaviest.Weight))
                heaviest = wish;
        return heaviest;
    }

    private Role? RoleOf(IJobs? jobs, Resident resident) =>
        jobs?.JobOf(resident.Id) is { } job && _roles.TryGetValue(job.Role, out var role) ? role : null;

    private TalkPerson Person(IJobs? jobs, Resident resident) => new(
        resident.Name,
        resident.Phase,
        [.. resident.Traits.Notable().Take(MostTraits).Select(trait => $"{(trait.High ? "high" : "low")} {trait.Trait}")],
        RoleOf(jobs, resident) is { } role ? new TalkName(role.Nl, role.En) : null);

    // ── the data ──

    private void Read(ModuleContext village)
    {
        _settings = village.Data.Section<TalkSettings>("talk");
        _wishes = village.Data.Names.Contains("wishes")
            ? village.Data.Section<WishSettings>("wishes").Wishes.ToDictionary(wish => wish.Id)
            : [];
        _roles = village.Data.Section<Catalog>("catalog").Roles.ToDictionary(role => role.Id);
        _interests = village.Data.Section<PeopleSettings>("people").Interests.ToDictionary(interest => interest.Id);

        if (_settings.MinMinutesBetween <= 0 || _settings.AnswerWithinMinutes <= 0 || _settings.Keep <= 0)
            throw new InvalidDataException("The data section 'talk' needs minMinutesBetween, answerWithinMinutes and keep above 0.");
        if (!_settings.Log.ContainsKey(TalkedKind))
            throw new InvalidDataException($"The data section 'talk' has no log sentence for {TalkedKind}.");
        foreach (var kind in new[] { "wish", "work", "interest" })
        {
            var templates = _settings.Templates.Of(kind);
            if (templates.Count == 0) throw new InvalidDataException($"The data section 'talk' has no template for a {kind}.");
            var sample = new TalkRequest(0, 0, 0, 0, kind, new TalkFacts(
                new TalkPerson("A", LifePhase.Working, []), new TalkPerson("B", LifePhase.Working, []), new TalkTopic(kind, new TalkName("x", "x"))));
            for (var i = 0; i < templates.Count; i++)
            {
                var template = templates[i];
                if (template.Nl.Count is < 2 or > 4 || template.En.Count is < 2 or > 4)
                    throw new InvalidDataException($"A {kind} template in 'talk' does not have two to four lines in both languages.");
                var lines = _settings.Templates.Fill(sample with { Id = i });
                if (lines.Nl.Concat(lines.En).Any(line => line.Contains('{') || line.Contains('}')))
                    throw new InvalidDataException($"A {kind} template in 'talk' has a placeholder other than {{a}}, {{b}} and {{topic}}.");
            }
        }
    }

    // ── saving ──

    public JsonNode Save() =>
        JsonSerializer.SerializeToNode(new SavedTalk(_next, _last, _open, _answered, _recent), SaveJson.Options)!;

    public void Load(ModuleContext village, JsonNode data, int version)
    {
        Read(village);
        var saved = data.Deserialize<SavedTalk>(SaveJson.Options) ?? throw new InvalidDataException("The talk part of the save is empty.");
        _next = saved.Next;
        _last = new SortedDictionary<int, DateTimeOffset>(saved.Last.ToDictionary());
        // what was taken before the stop is not given again: it waits for its template
        _open = [.. saved.Open];
        _answered = [.. saved.Answered];
        _recent = [.. saved.Recent];
    }

    private sealed record Open(TalkRequest Request, DateTimeOffset Asked, bool Taken);

    private sealed record Answered(long Id, TalkLines Lines);

    private sealed record SavedTalk(
        long Next,
        IReadOnlyDictionary<int, DateTimeOffset> Last,
        IReadOnlyList<Open> Open,
        IReadOnlyList<Answered> Answered,
        IReadOnlyList<Conversation> Recent);
}
