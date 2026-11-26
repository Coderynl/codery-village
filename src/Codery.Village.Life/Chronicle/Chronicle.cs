using System.Text.Json;
using System.Text.Json.Nodes;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;
using Codery.Village.Laws;
using Codery.Village.Life.Feelings;
using Codery.Village.Life.Folk;
using Codery.Village.Life.Inventing;
using Codery.Village.Life.Knowing;
using Codery.Village.Life.Living;
using FeelingsModule = Codery.Village.Life.Feelings.Feelings;
using FolkModule = Codery.Village.Life.Folk.Folk;
using InventingModule = Codery.Village.Life.Inventing.Inventing;
using KnowingModule = Codery.Village.Life.Knowing.Knowing;
using LivingModule = Codery.Village.Life.Living.Living;

namespace Codery.Village.Life.Chronicle;

/// <summary>
/// The record of a run: what is worth telling, one line each, in nl and en. Births, pairings, deaths, coming of age,
/// discoveries, what was forgotten, what was built, droughts, long shortages, illness waves and walls are told as their events
/// arrive; once a life year, the wishes many share are told with their causes, and the year's numbers are kept. What was
/// tried, made and learned is counted, not told. <see cref="Markdown"/> writes it out.
/// </summary>
public sealed class Chronicle(ulong seed = 0) : IModule
{
    private readonly List<Entry> _entries = [];
    private readonly List<LifeYear> _years = [];
    private readonly SortedDictionary<int, string> _causes = [];
    private readonly SortedDictionary<int, List<Name>> _found = [];
    private readonly SortedDictionary<string, ToldWish> _told = new(StringComparer.Ordinal);
    private ModuleContext _village = null!;
    private ChronicleRules _rules = null!;
    private WorldData _world = null!;
    private long _ticksPerYear;
    private IReadOnlyDictionary<string, Name> _processes = new Dictionary<string, Name>();
    private ulong _seed = seed;
    private int _openYear = 1;
    private DateOnly _openFirst;
    private DateOnly _openLast;
    private Tally _tally = new(0, 0, 0, 0, 0, 0);
    private bool _wave;

    public string Name => "chronicle";

    public int Version => 1;

    /// <summary>Everything told, in the order it was told.</summary>
    public IReadOnlyList<Entry> Entries => _entries.AsReadOnly();

    internal ChronicleRules Rules => _rules;
    internal WorldData World => _world;
    internal ulong Seed => _seed;
    internal long Tick => _village.Clocks.Tick;
    internal long TicksPerYear => _ticksPerYear;

    /// <summary>The life years that are over, in order.</summary>
    internal IReadOnlyList<LifeYear> Years => _years.AsReadOnly();

    /// <summary>The life year still running, its first day and the last day it has reached.</summary>
    internal (int Year, DateOnly First, DateOnly Last) Open => (_openYear, _openFirst, _openLast);

    /// <summary>How someone died: <c>age</c>, <c>sickness</c>, <c>cold</c>, <c>hunger</c> or <c>accident</c>; null while living.</summary>
    internal string? CauseOfDeath(int person) => _causes.GetValueOrDefault(person);

    /// <summary>The names of what someone discovered, in order.</summary>
    internal IReadOnlyList<Name> FoundBy(int person) => _found.TryGetValue(person, out var found) ? found : [];

    /// <summary>The numbers of the running life year so far, with the living and their happiness now.</summary>
    internal YearNumbers NumbersNow(FolkModule folk, FeelingsModule feelings) => Numbers(folk, feelings);

    public void Start(ModuleContext village)
    {
        Read(village);
        _openFirst = _openLast = DateOnly.FromDateTime(village.Clocks.Calendar.Now.DateTime);
    }

    public void Step(ModuleContext village)
    {
        var tick = village.Clocks.Tick;
        if (village.Received.Count > 0) TakeIn(village.Received, tick);
        if (!Hourly.Now(village)) return;

        // the calendar shows the moment of the tick before: the engine moves it after every module stepped
        _openLast = DateOnly.FromDateTime(village.Clocks.Calendar.Now.DateTime);
        if (tick % _ticksPerYear != 0) return;
        Wishes(tick - 1);
        _years.Add(new LifeYear(_openYear, _openFirst, _openLast, Numbers(Folk, Feelings)));
        _openYear = (int)(tick / _ticksPerYear) + 1;
        _openFirst = _openLast = DateOf(tick, tick);
        _tally = new Tally(0, 0, 0, 0, 0, 0);
    }

    private FolkModule Folk => _village.Module<FolkModule>();
    private FeelingsModule Feelings => _village.Module<FeelingsModule>();
    private InventingModule? Inventing => _village.Find<InventingModule>();

    private int YearOf(long tick) => (int)(tick / _ticksPerYear) + 1;

    // the calendar date of a tick, from the calendar as it stands in this step
    private DateOnly DateOf(long tick, long now) =>
        DateOnly.FromDateTime((_village.Clocks.Calendar.Now + TimeSpan.FromMilliseconds(1000.0 / Clocks.TicksPerSecond * (tick - (now - 1)))).DateTime);

    // ── what happens ──

    private void TakeIn(IReadOnlyList<VillageEvent> events, long now)
    {
        var illness = false;
        foreach (var e in events)
        {
            switch (e)
            {
                case Tried:
                    _tally = _tally with { Tried = _tally.Tried + 1 };
                    break;
                case Made:
                    _tally = _tally with { Made = _tally.Made + 1 };
                    break;
                case Learned:
                    _tally = _tally with { Learned = _tally.Learned + 1 };
                    break;
                case Born b:
                    _tally = _tally with { Born = _tally.Born + 1 };
                    var sex = Folk.Get(b.Person)?.Sex.ToString().ToLowerInvariant() ?? "woman";
                    Tell(e.Tick, now, "born", [b.Person, b.Mother, b.Father], l =>
                        _rules.Say($"born.{sex}", l, ("name", NameOf(b.Person)), ("mother", NameOf(b.Mother)), ("father", NameOf(b.Father))));
                    break;
                case Paired pair:
                    Tell(e.Tick, now, "paired", [pair.A, pair.B], l => _rules.Say("paired", l, ("a", NameOf(pair.A)), ("b", NameOf(pair.B))));
                    break;
                case Died d:
                    _tally = _tally with { Died = _tally.Died + 1 };
                    _causes[d.Person] = d.Cause;
                    illness = true;
                    Tell(e.Tick, now, "died", [d.Person], l =>
                        _rules.Say("died", l, ("name", NameOf(d.Person)), ("cause", _rules.Say($"cause.{d.Cause}", l)), ("age", Age(d.Person, d.Tick, l))));
                    break;
                case CameOfAge c:
                    var trade = Folk.Get(c.Person)?.Trade;
                    Tell(e.Tick, now, "came-of-age", [c.Person], l => trade is null
                        ? _rules.Say("came-of-age.no-trade", l, ("name", NameOf(c.Person)))
                        : _rules.Say("came-of-age", l, ("name", NameOf(c.Person)), ("trade", Named(trade, l))));
                    break;
                case Discovered found:
                    TellDiscovery(found.D, now);
                    break;
                case Forgotten f:
                    Tell(e.Tick, now, "forgotten", [f.LastBearer], l => _rules.Say("forgotten", l, ("name", NameOf(f.LastBearer)), ("item", Item(f.Item, l))));
                    break;
                case Built built:
                    Tell(e.Tick, now, "built", [], l => _rules.Say("built", l,
                        ("structure", _rules.Structures.TryGetValue(built.Structure, out var s) ? Pick(s, l) : Named(built.Structure, l)),
                        ("substance", Substance(built.Substance, l))));
                    break;
                case DroughtBegan:
                    Tell(e.Tick, now, "drought-began", [], l => _rules.Say("drought-began", l));
                    break;
                case DroughtEnded:
                    Tell(e.Tick, now, "drought-ended", [], l => _rules.Say("drought-ended", l));
                    break;
                case Shortage s when s.Days == _rules.ShortageDays:
                    Tell(e.Tick, now, "shortage", [], l => _rules.Say("shortage", l, ("days", _rules.Number(s.Days, l)), ("what", _rules.Say($"what.{s.What}", l))));
                    break;
                case HitAWall wall:
                    Tell(e.Tick, now, "wall", [], l => _rules.Say("wall", l, ("use", UseName(wall.Use, l))));
                    break;
                case FellIll or Recovered:
                    illness = true;
                    break;
            }
        }
        if (illness) Illness(now);
    }

    private void TellDiscovery(Discovery d, long now)
    {
        _tally = _tally with { Discoveries = _tally.Discoveries + 1 };
        var substance = Inventing?.NameOf(d.Substance, "nl") is { } nl ? new Name(nl, Inventing!.NameOf(d.Substance, "en")) : new Name(d.Substance, d.Substance);
        if (!_found.TryGetValue(d.By, out var list)) _found[d.By] = list = [];
        list.Add(substance);

        var recipe = Inventing?.Recipes.GetValueOrDefault(d.Recipe) ?? _world.Start.Recipes.FirstOrDefault(r => r.Id == d.Recipe);
        var how = d.How == "accident" ? "accident" : "trying";
        Tell(d.Tick, now, "discovered", [d.By, .. d.Witnesses], l =>
        {
            var inputs = _rules.List([.. (recipe?.In ?? []).Select(i => Substance(i.Substance, l))], l);
            var text = _rules.Say($"discovered.{how}", l, ("name", NameOf(d.By)), ("substance", Pick(substance, l)), ("inputs", inputs),
                ("process", recipe is null ? "" : ProcessName(recipe.Process, l)), ("use", UseName(d.Use, l)));
            if (d.Witnesses.Count == 0) return text;
            var witnesses = _rules.List([.. d.Witnesses.Select(NameOf)], l);
            return $"{text} {_rules.Say(d.Witnesses.Count == 1 ? "witness.one" : "witness.many", l, ("names", witnesses))}";
        });
    }

    // a wave is told when it starts, and is over once fewer are ill than make one
    private void Illness(long now)
    {
        var ill = _village.Find<LivingModule>()?.Ill;
        if (ill is null) return;
        if (!_wave && ill.Count >= _rules.IllnessWave)
        {
            _wave = true;
            var count = ill.Count;
            var who = ill.Order().ToList();
            Tell(now - 1, now, "illness", who, l => count <= _rules.IllnessNames
                ? _rules.Say("illness.names", l, ("names", _rules.List([.. who.Select(NameOf)], l)))
                : _rules.Say("illness", l, ("count", _rules.Number(count, l))));
        }
        else if (_wave && ill.Count < _rules.IllnessWave) _wave = false;
    }

    // ── once a life year: the wishes many share, with their causes ──

    // a wish line is told when it is new, or changed against the last line told for its use; unchanged, now and then as
    // "still"; and once when nobody wants the use any more
    private void Wishes(long tick)
    {
        var year = YearOf(tick);
        var wanted = Folk.Living
            .SelectMany(p => Feelings.Wishes(p.Id).Select(w => (Person: p.Id, Wish: w)))
            .GroupBy(x => x.Wish.Use, StringComparer.Ordinal)
            .Select(g => (Use: g.Key, Wishes: g.OrderBy(x => x.Person).ToList()))
            .OrderByDescending(g => g.Wishes.Count).ThenByDescending(g => g.Wishes.Sum(x => x.Wish.Weight)).ThenBy(g => g.Use, StringComparer.Ordinal)
            .ToList();
        var heavy = wanted.Where(g => g.Wishes.Count >= _rules.HeavyWish).ToList();
        if (heavy.Count == 0) heavy = [.. wanted.Take(_rules.TopWishes)];

        foreach (var (use, wishes) in heavy)
        {
            var causes = wishes.GroupBy(x => x.Wish.Cause, StringComparer.Ordinal)
                .Select(g => (Cause: g.Key, Count: g.Count(), Because: Commonest(g.Select(x => x.Wish.Because))))
                .OrderByDescending(c => c.Count).ThenBy(c => IndexOf(c.Cause)).ToList();
            var told = new ToldWish(wishes.Count, causes[0].Cause, year);
            if (_told.TryGetValue(use, out var last))
            {
                var by = Math.Abs(told.Count - last.Count);
                var changed = (by > 0 && (by >= _rules.WishChangeBy || by >= last.Count * _rules.WishChangeShare)) || told.Cause != last.Cause;
                if (!changed)
                {
                    if (year - last.Year < _rules.WishStillEvery) continue;
                    _told[use] = last with { Year = year };
                    var people = wishes.Select(x => x.Person).ToList();
                    Tell(tick, tick + 1, "wish-still", people, l => people.Count == 1
                        ? _rules.Say("wish.still.one", l, ("name", NameOf(people[0])), ("use", UseName(use, l)))
                        : _rules.Say(CountKey("wish.still", people.Count), l, ("count", _rules.Number(people.Count, l)), ("use", UseName(use, l))));
                    continue;
                }
            }
            _told[use] = told;
            Tell(tick, tick + 1, "wish", [.. wishes.Select(x => x.Person)], l =>
            {
                string Reason((string Cause, int Count, string? Because) c) => _rules.Say($"because.{c.Cause}", l,
                    ("person", c.Because is { } who && int.TryParse(who, out var id) ? NameOf(id) : ""),
                    ("substance", c.Because is { } what ? Substance(what, l) : ""));
                var name = UseName(use, l);
                if (wishes.Count == 1)
                    return _rules.Say("wish.one", l, ("name", NameOf(wishes[0].Person)), ("use", name), ("reasons", _rules.List([.. causes.Select(Reason)], l)));
                var count = _rules.Number(wishes.Count, l);
                if (causes.Count == 1) return _rules.Say(wishes.Count == 2 ? "wish.both" : CountKey("wish.all", wishes.Count), l, ("count", count), ("use", name), ("reason", Reason(causes[0])));
                var parts = causes.Select(c => _rules.Say("wish.part", l, ("count", _rules.Number(c.Count, l)), ("reason", Reason(c)))).ToList();
                return _rules.Say(CountKey("wish.many", wishes.Count), l, ("count", count), ("use", name), ("reasons", _rules.List(parts, l)));
            });
        }

        foreach (var gone in _told.Keys.Where(use => !wanted.Any(g => g.Use == use)).ToList())
        {
            _told.Remove(gone);
            Tell(tick, tick + 1, "wish-gone", [], l => _rules.Say("wish.gone", l, ("use", UseName(gone, l))));
        }
    }

    // a sentence that opens with a count has a form for a count in digits, which never opens a sentence
    private string CountKey(string key, int count) =>
        count >= _rules.Numbers.Nl.Count && _rules.Sentences.ContainsKey($"{key}.digits") ? $"{key}.digits" : key;

    private static int IndexOf(string cause)
    {
        var at = Wish.Causes.ToList().IndexOf(cause);
        return at < 0 ? int.MaxValue : at;
    }

    private static string? Commonest(IEnumerable<string?> values) =>
        values.Where(v => v is not null).GroupBy(v => v!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).FirstOrDefault();

    private YearNumbers Numbers(FolkModule folk, FeelingsModule feelings)
    {
        var happiness = folk.Living.Select(p => (double)feelings.Happiness(p.Id)).Order().ToList();
        var known = _village.Find<KnowingModule>()?.KnownOnIsland().Count ?? 0;
        return new YearNumbers(folk.Living.Count, _tally.Born, _tally.Died, _tally.Tried, _tally.Made, _tally.Learned, _tally.Discoveries, known,
            (int)Math.Round(Quantile(happiness, 0.5)), (int)Math.Round(Quantile(happiness, 0.75) - Quantile(happiness, 0.25)));
    }

    // linear between the nearest ranks
    private static double Quantile(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        var at = (sorted.Count - 1) * q;
        var low = (int)Math.Floor(at);
        var high = Math.Min(low + 1, sorted.Count - 1);
        return sorted[low] + (sorted[high] - sorted[low]) * (at - low);
    }

    // ── telling ──

    private void Tell(long tick, long now, string kind, IReadOnlyList<int> people, Func<string, string> say) =>
        _entries.Add(new Entry(tick, YearOf(tick), DateOf(tick, now), kind, people, Capital(say("nl")), Capital(say("en"))));

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private string NameOf(int person) => Folk.Get(person)?.Name ?? "?";

    internal string Age(int person, long tick, string language)
    {
        var years = Folk.Get(person) is { } p ? (int)Math.Floor(Folk.AgeOf(p, tick)) : 0;
        return years < 1 ? _rules.Say("age.baby", language) : _rules.Say("age.years", language, ("years", years.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static string Pick(Name name, string language) => language == "en" ? name.En : name.Nl;

    // a trade, process, raw or start substance by its name in the world
    // processes by their own names: a process and a use can share an id (cut), and the world's names keep one per id
    private string ProcessName(string id, string language) => _processes.TryGetValue(id, out var name) ? Pick(name, language) : Named(id, language);

    internal string Named(string id, string language) => _world.NamesById.TryGetValue(id, out var name) ? Pick(name, language) : id;

    private string Substance(string id, string language) => Inventing?.NameOf(id, language) ?? Named(id, language);

    private string UseName(string id, string language) => _world.Uses.FirstOrDefault(u => u.Id == id) is { } use ? (language == "en" ? use.En : use.Nl) : id;

    // what Knowing knows: a recipe ("{process}:{out}"), a process or a use
    private string Item(string item, string language)
    {
        var colon = item.IndexOf(':');
        if (colon > 0) return _rules.Say("item.recipe", language, ("substance", Substance(item[(colon + 1)..], language)));
        if (_world.Processes.Any(p => p.Id == item)) return _rules.Say("item.process", language, ("process", ProcessName(item, language)));
        return _rules.Say("item.use", language, ("use", UseName(item, language)));
    }

    // ── reading and saving ──

    private void Read(ModuleContext village)
    {
        _village = village;
        _rules = village.Data.Section<ChronicleRules>("chronicle");
        _world = WorldData.From(village.Data);
        _ticksPerYear = village.Data.Section<LifeSettings>("life").TicksPerLifeYear;
        _processes = village.Data.Section<List<ProcessNames>>("processes").ToDictionary(x => x.Id, x => new Name(x.Nl, x.En), StringComparer.Ordinal);
    }

    public JsonNode Save() => JsonSerializer.SerializeToNode(new SavedChronicle(_seed, _entries, _years, _openYear, _openFirst, _openLast, _tally, _wave,
        _causes, _found.ToDictionary(f => f.Key, f => (IReadOnlyList<Name>)f.Value), _told), SaveJson.Options)!;

    public void Load(ModuleContext village, JsonNode data, int version)
    {
        Read(village);
        var saved = data.Deserialize<SavedChronicle>(SaveJson.Options) ?? throw new InvalidDataException("The chronicle part of the save is empty.");
        _seed = saved.Seed;
        _entries.Clear();
        _entries.AddRange(saved.Entries);
        _years.Clear();
        _years.AddRange(saved.Years);
        (_openYear, _openFirst, _openLast, _tally, _wave) = (saved.OpenYear, saved.OpenFirst, saved.OpenLast, saved.Tally, saved.Wave);
        _causes.Clear();
        foreach (var (person, cause) in saved.Causes) _causes[person] = cause;
        _found.Clear();
        foreach (var (person, found) in saved.Found) _found[person] = [.. found];
        _told.Clear();
        foreach (var (use, told) in saved.Told) _told[use] = told;
    }

    /// <summary>The counts of the running life year.</summary>
    private sealed record Tally(int Born, int Died, int Tried, int Made, int Learned, int Discoveries);

    private sealed record SavedChronicle(ulong Seed, IReadOnlyList<Entry> Entries, IReadOnlyList<LifeYear> Years, int OpenYear, DateOnly OpenFirst,
        DateOnly OpenLast, Tally Tally, bool Wave, IReadOnlyDictionary<int, string> Causes, IReadOnlyDictionary<int, IReadOnlyList<Name>> Found,
        IReadOnlyDictionary<string, ToldWish> Told);

    /// <summary>A process's names, from the section "processes".</summary>
    private sealed record ProcessNames(string Id, string Nl, string En);

    /// <summary>The last wish line told for a use: how many wanted it, the leading cause, and the life year.</summary>
    private sealed record ToldWish(int Count, string Cause, int Year);
}
