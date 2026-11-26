using System.Text.Json;
using System.Text.Json.Nodes;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Laws;
using Codery.Village.Life.Folk;

namespace Codery.Village.Life.Knowing;

/// <summary>
/// What the people of West know, each at a level from 0 to 100: processes, recipes (<c>{process}:{out}</c>) and uses. Someone
/// knows an item once its level passes 20. At the start every adult knows the start processes, the start uses and their
/// trade's items. Once a day, the tick after 06:00 (so the day's work and deaths have reached it): what was used at work
/// grows by practice, what the dead alone knew is forgotten, and everyone learns: a child or pupil from each parent, an adult
/// from the best master of their trade, a pupil from the teachers once a school stands, and on a working day anyone from the
/// workers of their household. Each item a day is learnt from the one source that teaches it most.
/// </summary>
public sealed class Knowing : IModule
{
    private ModuleContext _village = null!;
    private KnowingRules _rules = null!;
    private Folk.Folk _folk = null!;
    private Living.Living _living = null!;
    private IReadOnlyList<string> _startItems = [];
    // a trade's items: its recipes, their processes, and every use its outputs (made or gathered) serve at all
    private Dictionary<string, SortedSet<string>> _tradeItems = [];
    private readonly List<Joined> _joined = [];

    // the state that is saved: levels by person and item; the dead leave once Knowing has seen them die
    private readonly SortedDictionary<int, SortedDictionary<string, double>> _levels = [];

    public string Name => "knowing";

    public int Version => 1;

    /// <summary>Whether <paramref name="person"/> knows <paramref name="item"/>: its level passed 20.</summary>
    public bool Knows(int person, string item) => _rules is not null && LevelOf(person, item) > _rules.Knows;

    /// <summary>The level of <paramref name="person"/> in <paramref name="item"/>, 0 to 100.</summary>
    public int Level(int person, string item) => (int)Math.Floor(LevelOf(person, item));

    /// <summary>The items at least one living person knows, in ordinal order.</summary>
    public IReadOnlyCollection<string> KnownOnIsland()
    {
        if (_rules is null) return [];
        var known = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (person, levels) in _levels)
        {
            if (_folk.Get(person) is not { DiedTick: null }) continue;
            foreach (var (item, level) in levels)
                if (level > _rules.Knows) known.Add(item);
        }
        return known;
    }

    /// <summary>
    /// Raises the level of <paramref name="person"/> in <paramref name="item"/> to at least <paramref name="level"/>;
    /// <c>Learned</c> with <paramref name="how"/> when it passes 20. Inventing calls it on a discovery, with <c>found</c>.
    /// </summary>
    public void Learn(int person, string item, int level, string how) => Raise(person, item, level, how, _village.Clocks.Tick);

    public void Start(ModuleContext village)
    {
        Read(village);
        var tick = village.Clocks.Tick;
        // in person-id order, and per person in item order, one draw per item
        foreach (var p in _folk.Living)
        {
            if (_folk.StageOf(p, tick) < LifeStage.Adult) continue;
            var items = new SortedSet<string>(_startItems, StringComparer.Ordinal);
            items.UnionWith(ItemsOf(p.Trade));
            var levels = LevelsOf(p.Id);
            foreach (var item in items) levels[item] = _rules.StartMin + village.Random.Next(_rules.StartMax - _rules.StartMin + 1);
        }
    }

    public void Step(ModuleContext village)
    {
        // the tick after the day began: then the events of Folk's and Living's 06:00 arrive
        if (!Hourly.DayBegan(village)) return;

        var tick = village.Clocks.Tick;
        Practise(village.Received);
        Raised(village.Received, tick);
        ForgetTheDead(tick);
        Teach(tick, !village.Clocks.Calendar.Moment.Weekend);
    }

    private void Read(ModuleContext village)
    {
        _village = village;
        _rules = village.Data.Section<KnowingRules>("knowing");
        _folk = village.Module<Folk.Folk>();
        _living = village.Module<Living.Living>();
        var world = WorldData.From(village.Data);
        _startItems = [.. world.Start.Processes, .. world.Start.Uses];
        var recipes = world.Start.Recipes.ToDictionary(r => r.Id, StringComparer.Ordinal);
        _tradeItems = world.Start.Trades.ToDictionary(t => t.Id, t =>
        {
            var items = new SortedSet<string>(t.Recipes, StringComparer.Ordinal);
            items.UnionWith(t.Recipes.Select(id => recipes[id].Process));
            var outputs = t.Recipes.Select(id => recipes[id].Out).Concat(t.Gathers)
                .Where(world.Substances.ContainsKey).Select(id => world.Substances[id].Profile).ToList();
            items.UnionWith(world.Uses.Where(u => outputs.Any(o => u.Score(o) > 0)).Select(u => u.Id));
            return items;
        }, StringComparer.Ordinal);
    }

    /// <summary>
    /// Makes <paramref name="item"/> part of <paramref name="trade"/>: masters teach it to the trade's apprentices and
    /// household members see it at work. A find joins the finder's trade this way. Kept in the save.
    /// </summary>
    public void JoinTrade(string trade, string item)
    {
        if (!_tradeItems.TryGetValue(trade, out var items) || !items.Add(item)) return;
        _joined.Add(new Joined(trade, item));
    }

    private IReadOnlyCollection<string> ItemsOf(string? trade) =>
        trade is not null && _tradeItems.TryGetValue(trade, out var items) ? items : [];

    // ── a day ──

    // each item a worker used at work, and already knew, grows by practice: once a day, however often it was used
    private void Practise(IReadOnlyList<VillageEvent> received)
    {
        var used = new SortedDictionary<int, SortedSet<string>>();
        foreach (var made in received.OfType<Living.Made>())
        {
            if (!used.TryGetValue(made.Maker, out var items)) used[made.Maker] = items = new(StringComparer.Ordinal);
            items.Add(made.Recipe);
            items.Add(made.Recipe.Split(':')[0]);
            if (made.ForUse is { } use) items.Add(use);
        }
        foreach (var (maker, items) in used)
        {
            if (!_levels.TryGetValue(maker, out var levels)) continue;
            foreach (var item in items)
                if (levels.TryGetValue(item, out var level) && level > _rules.Knows) levels[item] = Math.Min(100, level + _rules.Practice);
        }
    }

    // a structure that stands opens its processes to those who raised it: whoever knows its wall, and the builders
    private void Raised(IReadOnlyList<VillageEvent> received, long tick)
    {
        foreach (var built in received.OfType<Living.Built>())
        {
            var processes = WorldData.From(_village.Data).Processes.Where(p => p.Needs == built.Structure).Select(p => p.Id).ToList();
            var makers = _folk.Living.Where(p => _folk.StageOf(p, tick) >= LifeStage.Adult
                && (p.Trade == _rules.Builder || Known(p.Id).Any(k => k.Item.EndsWith(":" + built.Substance, StringComparison.Ordinal)))).ToList();
            foreach (var process in processes)
            {
                foreach (var p in makers)
                {
                    Raise(p.Id, process, _rules.FoundLevel, "found", tick);
                    if (p.Trade is { } trade) JoinTrade(trade, process);
                }
            }
        }
    }

    // the dead leave, in id order; what nobody living knows any more is forgotten, the last of its bearers named
    private void ForgetTheDead(long tick)
    {
        foreach (var person in _levels.Keys.ToList())
        {
            if (_folk.Get(person) is { DiedTick: null }) continue;
            var known = _levels[person].Where(l => l.Value > _rules.Knows).Select(l => l.Key).ToList();
            _levels.Remove(person);
            foreach (var item in known)
                if (!_levels.Values.Any(levels => levels.TryGetValue(item, out var level) && level > _rules.Knows))
                    _village.Publish(new Forgotten(tick, item, person));
        }
    }

    // every gain is reckoned from the levels at the start of the day, then all are taken in, by person and item
    private void Teach(long tick, bool workingDay)
    {
        var gains = new SortedDictionary<(int Person, string Item), (double Gain, string How)>(Comparer<(int Person, string Item)>.Create(
            (a, b) => a.Person != b.Person ? a.Person.CompareTo(b.Person) : string.CompareOrdinal(a.Item, b.Item)));

        // a learner moves towards the teacher's level; of a day's sources for one item the one that teaches most counts
        void Offer(int learner, string item, double towards, double rate, string how)
        {
            var gain = rate * (towards - LevelOf(learner, item)) / 100;
            if (gain <= 0 || (gains.TryGetValue((learner, item), out var best) && best.Gain >= gain)) return;
            gains[(learner, item)] = (gain, how);
        }

        var living = _folk.Living;
        var teachers = _living.Structures.Contains(_rules.School)
            ? living.Where(t => t.Trade == _rules.Teacher && _folk.StageOf(t, tick) >= LifeStage.Adult).ToList()
            : [];
        var workers = workingDay
            ? living.Where(w => w.Trade is not null && _folk.StageOf(w, tick) >= LifeStage.Adult && !_living.Ill.Contains(w.Id)).ToList()
            : [];
        foreach (var p in living)
        {
            var stage = _folk.StageOf(p, tick);
            if (stage is LifeStage.Child or LifeStage.Pupil)
                foreach (var parent in new[] { p.Mother, p.Father })
                    if (parent is { } id && _folk.Get(id) is { DiedTick: null })
                        foreach (var (item, level) in Known(id)) Offer(p.Id, item, level, _rules.ParentRate, "parent");

            if (stage >= LifeStage.Adult && p.Trade is { } trade)
                foreach (var item in ItemsOf(trade))
                {
                    var master = living.Where(m => m.Id != p.Id && m.Trade == trade).Select(m => LevelOf(m.Id, item)).DefaultIfEmpty(0).Max();
                    Offer(p.Id, item, master, _rules.MasterRate, "master");
                }

            if (stage == LifeStage.Pupil)
                foreach (var teacher in teachers)
                    foreach (var (item, level) in Known(teacher.Id))
                        if (level >= _rules.TeacherMin) Offer(p.Id, item, level, _rules.SchoolRate, "school");

            foreach (var worker in workers)
            {
                if (worker.Id == p.Id || worker.Household != p.Household) continue;
                foreach (var item in ItemsOf(worker.Trade))
                    if (Knows(worker.Id, item)) Offer(p.Id, item, LevelOf(worker.Id, item), _rules.WatchRate, "watching");
            }
        }

        foreach (var ((person, item), (gain, how)) in gains) Raise(person, item, LevelOf(person, item) + gain, how, tick);
    }

    // ── levels ──

    private IEnumerable<(string Item, double Level)> Known(int person) =>
        _levels.TryGetValue(person, out var levels) ? levels.Where(l => l.Value > _rules.Knows).Select(l => (l.Key, l.Value)) : [];

    private double LevelOf(int person, string item) =>
        _levels.TryGetValue(person, out var levels) && levels.TryGetValue(item, out var level) ? level : 0;

    private SortedDictionary<string, double> LevelsOf(int person)
    {
        if (!_levels.TryGetValue(person, out var levels)) _levels[person] = levels = new(StringComparer.Ordinal);
        return levels;
    }

    private void Raise(int person, string item, double level, string how, long tick)
    {
        var levels = LevelsOf(person);
        var before = levels.GetValueOrDefault(item);
        level = Math.Min(100, level);
        if (level <= before) return;
        levels[item] = level;
        if (before <= _rules.Knows && level > _rules.Knows) _village.Publish(new Learned(tick, person, item, how));
    }

    // ── saving ──

    public JsonNode Save() => JsonSerializer.SerializeToNode(new SavedKnowing(
        [.. _levels.SelectMany(p => p.Value.Select(l => new SavedLevel(p.Key, l.Key, l.Value)))], [.. _joined]), SaveJson.Options)!;

    public void Load(ModuleContext village, JsonNode data, int version)
    {
        Read(village);
        var saved = data.Deserialize<SavedKnowing>(SaveJson.Options) ?? throw new InvalidDataException("The knowing part of the save is empty.");
        _joined.Clear();
        foreach (var joined in saved.Joined ?? []) JoinTrade(joined.Trade, joined.Item);
        _levels.Clear();
        foreach (var l in saved.Levels) LevelsOf(l.Person)[l.Item] = l.Level;
    }

    private sealed record SavedKnowing(IReadOnlyList<SavedLevel> Levels, IReadOnlyList<Joined>? Joined = null);

    private sealed record Joined(string Trade, string Item);

    private sealed record SavedLevel(int Person, string Item, double Level);
}
