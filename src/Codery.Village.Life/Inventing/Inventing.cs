using System.Text.Json;
using System.Text.Json.Nodes;
using Codery.Village.Core;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;
using Codery.Village.Laws;
using Codery.Village.Life.Folk;
using Codery.Village.Life.Living;
using FeelingsModule = Codery.Village.Life.Feelings.Feelings;
using FolkModule = Codery.Village.Life.Folk.Folk;
using KnowingModule = Codery.Village.Life.Knowing.Knowing;
using LivingModule = Codery.Village.Life.Living.Living;

namespace Codery.Village.Life.Inventing;

/// <summary>
/// Where the new comes from. Each village day the open-minded try something with what is at hand: weighted towards their
/// trade and what the village wants, with a trace of pure chance. Everyday work sometimes goes astray. What comes out is
/// worked out by the laws, never chosen; it is a discovery only when someone notices that it serves a felt use better than
/// anything the island had, or will after one more step of the fire. The finder learns it, Living can make it, and it gets
/// a name. A need that presses for years with nothing in reach hits a wall.
/// </summary>
public sealed class Inventing : IModule
{
    private static readonly string[] Heat = ["fire", "kiln", "furnace"];

    private readonly List<Discovery> _discoveries = [];
    private readonly SortedDictionary<string, Substance> _found = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, Recipe> _recipes = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, Name> _names = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _tried = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _pressingSince = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _walled = new(StringComparer.Ordinal);
    private ModuleContext _village = null!;
    private InventingRules _rules = null!;
    private NameRules _naming = null!;
    private WorldData _world = null!;
    private Rng _namer = null!;
    private long _ticksPerYear;

    public string Name => "inventing";

    public int Version => 1;

    /// <summary>Every discovery, in the order made.</summary>
    public IReadOnlyList<Discovery> Discoveries => _discoveries.AsReadOnly();

    /// <summary>The substances found on the island beyond the raw ones and the start's, by id.</summary>
    public IReadOnlyDictionary<string, Substance> Found => _found;

    /// <summary>The recipes of what was found, by id.</summary>
    public IReadOnlyDictionary<string, Recipe> Recipes => _recipes;

    /// <summary>Any substance the island has met: raw, its own, the start's or found; null otherwise.</summary>
    public Substance? Substance(string id) =>
        _found.GetValueOrDefault(id) ?? _world.Substances.GetValueOrDefault(id) ?? (Living.Own.Id == id ? Living.Own : null);

    /// <summary>The name of a substance in <paramref name="language"/> (<c>nl</c> or <c>en</c>); the id when it has none.</summary>
    public string NameOf(string substance, string language)
    {
        var name = _names.GetValueOrDefault(substance) ?? _world.NamesById.GetValueOrDefault(substance)
            ?? (substance == Living.Own.Id ? _naming.Own : null);
        return name is null ? substance : language == "en" ? name.En : name.Nl;
    }

    public void Start(ModuleContext village)
    {
        Read(village);
        _namer = new Rng(village.Random.State ^ 0x6E616D6573UL);
    }

    public void Step(ModuleContext village)
    {
        if (village.Received.Count > 0)
        {
            Accidents(village.Received);
            Forget(village.Received);
        }
        if (!Hourly.DayStarts(village)) return;
        var tick = village.Clocks.Tick;
        foreach (var p in Folk.Living.ToArray())
            if (Folk.StageOf(p, tick) is LifeStage.Adult or LifeStage.Old && p.Traits.Openness >= _rules.TryOpenness) Try(p, tick);
        Walls(tick);
    }

    private FolkModule Folk => _village.Module<FolkModule>();
    private LivingModule Living => _village.Module<LivingModule>();
    private KnowingModule Knowing => _village.Module<KnowingModule>();
    private FeelingsModule? Feelings => _village.Find<FeelingsModule>();

    // ── what the village wants ──

    private IReadOnlyList<(string Use, double Weight)> Pressing() => Feelings?.Pressing() ?? [];

    // uses felt enough that a better answer is noticed: household uses, what is wished, and a hotter fire not yet had
    private IEnumerable<Use> Felt()
    {
        var wished = Pressing().Select(p => p.Use).ToHashSet(StringComparer.Ordinal);
        return _world.Uses.Where(u => u.Household || wished.Contains(u.Id) || (u.Structure is { } s && !Living.Structures.Contains(s)));
    }

    private HashSet<string> Helpful()
    {
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (use, _) in Pressing()) kinds.UnionWith(_rules.Helps.GetValueOrDefault(use) ?? []);
        foreach (var u in _world.Uses.Where(u => u.Structure is { } s && !Living.Structures.Contains(s))) kinds.UnionWith(_rules.Helps.GetValueOrDefault(u.Id) ?? []);
        return kinds;
    }

    private HashSet<string> TradeKinds(Person p)
    {
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        if (p.Trade is null || _world.Start.Trades.FirstOrDefault(t => t.Id == p.Trade) is not { } trade) return kinds;
        foreach (var g in trade.Gathers) if (Substance(g) is { } s) kinds.Add(s.Kind);
        foreach (var r in _world.Start.Recipes.Where(r => trade.Recipes.Contains(r.Id)))
        {
            foreach (var i in r.In) if (Substance(i.Substance) is { } s) kinds.Add(s.Kind);
            if (Substance(r.Out) is { } o) kinds.Add(o.Kind);
        }
        return kinds;
    }

    // what someone can lay hands on to try with: raw substances at hand, the start's and the found whose recipe is still known
    private List<Substance> AtHand()
    {
        var known = Knowing.KnownOnIsland();
        var start = _world.Start.Recipes.Where(r => known.Contains(r.Id)).Select(r => _world.Substances[r.Out]);
        var found = _recipes.Values.Where(r => known.Contains(r.Id)).Select(r => _found[r.Out]);
        return [.. Living.AtHand.Values.Concat(start).Concat(found).DistinctBy(s => s.Id).OrderBy(s => s.Id, StringComparer.Ordinal)];
    }

    private bool CanDo(Person p, Process process) =>
        Knowing.Knows(p.Id, process.Id) && (process.Needs is null || Living.Structures.Contains(process.Needs));

    // ── trying ──

    private void Try(Person p, long tick)
    {
        var random = _village.Random;
        var trade = TradeKinds(p);
        var helpful = Helpful();
        var near = Pressing().Where(x => (_rules.Helps.GetValueOrDefault(x.Use) ?? []).Any(trade.Contains)).Sum(x => x.Weight);
        var chance = _rules.TryBase * p.Traits.Openness / 100.0 * (1 + near / _rules.PressingScale);
        if (random.NextDouble() >= chance) return;

        var processes = _world.Processes.Where(x => CanDo(p, x)).ToList();
        var hand = AtHand();
        if (processes.Count == 0 || hand.Count == 0) return;
        var wild = random.NextDouble() < _rules.WildChance;
        double Weight(Substance s) => wild ? 1 : (trade.Contains(s.Kind) ? _rules.TradeWeight : 1) * (helpful.Contains(s.Kind) ? _rules.PressingWeight : 1);

        (Process Process, IReadOnlyList<Portion> Inputs) attempt = (processes[0], [new Portion(hand[0], 1)]);
        for (var redraw = 0; redraw < 5; redraw++)
        {
            var process = wild ? processes[random.Next(processes.Count)] : PickProcess(processes, helpful, random);
            var a = Pick(hand, Weight, random);
            IReadOnlyList<Portion> inputs = [new Portion(a, 1)];
            if (process.MaxInputs >= 2 && hand.Count > 1 && random.NextDouble() < 0.5)
            {
                var b = Pick([.. hand.Where(s => s.Id != a.Id)], Weight, random);
                var ratio = _rules.Ratios[random.Next(_rules.Ratios.Count)];
                inputs = [new Portion(a, ratio[0]), new Portion(b, ratio[1])];
            }
            attempt = (process, inputs);
            if (wild || !_tried.Contains(Key(process.Id, inputs)) || random.NextDouble() < _rules.RetryWeight) break;
        }

        var key = Key(attempt.Process.Id, attempt.Inputs);
        _tried.Add(key);
        var result = _world.Nature.Apply(attempt.Process.Id, attempt.Inputs);
        _village.Publish(new Tried(tick, p.Id, key, result?.Id));
        if (result is not null) Notice(p, attempt.Process, attempt.Inputs, result, "trying", tick);
    }

    // a process whose laws work on a kind that would help is the likelier try: people know that fire changes clay
    private Process PickProcess(IReadOnlyList<Process> processes, HashSet<string> helpful, Rng random)
    {
        double Weight(Process p) => _world.Nature.LawsOf(p.Id).Any(l => l.When.Any(c => c.Kind is { } k && helpful.Contains(k))) ? _rules.PressingWeight : 1;
        var at = random.NextDouble() * processes.Sum(Weight);
        foreach (var p in processes)
        {
            at -= Weight(p);
            if (at < 0) return p;
        }
        return processes[^1];
    }

    private static Substance Pick(IReadOnlyList<Substance> from, Func<Substance, double> weight, Rng random)
    {
        var total = from.Sum(weight);
        var at = random.NextDouble() * total;
        foreach (var s in from)
        {
            at -= weight(s);
            if (at < 0) return s;
        }
        return from[^1];
    }

    private static string Key(string process, IReadOnlyList<Portion> inputs) =>
        $"{process}|{string.Join("+", inputs.OrderBy(i => i.Substance.Id, StringComparer.Ordinal).Select(i => $"{i.Parts}:{i.Substance.Id}"))}";

    // when a found recipe dies with its last knower, so does the memory of having tried it
    private void Forget(IReadOnlyList<VillageEvent> events)
    {
        foreach (var gone in events.OfType<Knowing.Forgotten>())
            if (_recipes.GetValueOrDefault(gone.Item) is { } recipe && recipe.In.All(i => Substance(i.Substance) is not null))
                _tried.Remove(Key(recipe.Process, [.. recipe.In.Select(i => new Portion(Substance(i.Substance)!, i.Parts))]));
    }

    // ── accidents ──

    private void Accidents(IReadOnlyList<VillageEvent> events)
    {
        foreach (var made in events.OfType<Made>())
        {
            if (made.Maker < 0 || Folk.Get(made.Maker) is not { DiedTick: null } maker) continue;
            var recipe = _recipes.GetValueOrDefault(made.Recipe) ?? _world.Start.Recipes.FirstOrDefault(r => r.Id == made.Recipe);
            if (recipe is null) continue;
            var random = _village.Random;
            if (random.NextDouble() >= _rules.AccidentChance) continue;

            var inputs = recipe.In.Select(i => new Portion(Substance(i.Substance)!, i.Parts)).ToList();
            var process = recipe.Process;
            var hotter = Array.IndexOf(Heat, process) is var at && at >= 0 && at + 1 < Heat.Length ? Heat[at + 1] : null;
            if (hotter is not null && _world.Nature.Processes[hotter].Needs is { } needs && Living.Structures.Contains(needs))
                process = hotter; // left too long in a hotter fire
            else
            {
                var stock = Living.Stock.Amounts.Where(x => x.Value >= 1 && Substance(x.Key) is not null).Select(x => x.Key).ToList();
                if (stock.Count == 0) continue;
                var swap = random.Next(inputs.Count);
                inputs[swap] = new Portion(Substance(stock[random.Next(stock.Count)])!, inputs[swap].Parts); // the wrong jar
            }
            if (_world.Nature.Apply(process, inputs) is { } result)
                Notice(maker, _world.Nature.Processes[process], inputs, result, "accident", made.Tick);
        }
    }

    // ── noticing ──

    private void Notice(Person maker, Process process, IReadOnlyList<Portion> inputs, Substance result, string how, long tick)
    {
        // what the island can still make or gather: a substance whose recipe died with its last knower can be found again
        var known = KnownSubstances();
        bool Met(Substance s) => known.Any(k => k.Id == s.Id);
        if (Met(result)) return;
        var felt = Felt().ToList();
        var best = felt.ToDictionary(u => u.Id, u => known.Max(s => u.Score(s.Profile)), StringComparer.Ordinal);
        (Use Use, double Gain)? Better(Substance s) => felt
            .Select(u => (Use: u, Gain: u.Score(s.Profile) - best[u.Id]))
            .Where(x => x.Use.Score(s.Profile) > 0 && x.Gain >= _rules.NoticeMargin)
            .OrderByDescending(x => x.Gain).ThenBy(x => x.Use.Id, StringComparer.Ordinal)
            .Select(x => ((Use, double)?)x).FirstOrDefault();

        // the result itself, or what one more step in a fire the island has would make of it
        var steps = new List<(Process Process, IReadOnlyList<Portion> Inputs, Substance Result)> { (process, inputs, result) };
        var serves = Better(result);
        if (serves is null)
        {
            foreach (var heat in Heat.Select(h => _world.Nature.Processes[h]).Where(h => CanDo(maker, h)))
            {
                if (_world.Nature.Apply(heat.Id, [new Portion(result, 1)]) is not { } next || Met(next)) continue;
                if (Better(next) is { } promise)
                {
                    steps.Add((heat, [new Portion(result, 1)], next));
                    serves = promise;
                    break;
                }
            }
        }
        if (serves is not { } use) return;

        var present = Present(maker);
        var finder = Notices(maker, process, 1) ? maker : present.FirstOrDefault(w => Notices(w, process, _rules.WitnessShare));
        if (finder is null) return;
        var witnesses = present.Where(w => w.Id != finder.Id).Append(maker).Where(w => w.Id != finder.Id).Select(w => w.Id).Distinct().Order().ToList();

        foreach (var (stepProcess, stepInputs, stepResult) in steps)
        {
            var recipe = new Recipe($"{stepProcess.Id}:{stepResult.Id}", stepProcess.Id,
                [.. stepInputs.Select(i => new RecipeInput(i.Substance.Id, i.Parts))], stepResult.Id);
            _found[stepResult.Id] = stepResult;
            _recipes[recipe.Id] = recipe;
            if (!_names.ContainsKey(stepResult.Id)) _names[stepResult.Id] = Named(stepResult); // found again, it keeps its name
            Living.Learn(recipe, stepResult);
            Knowing.Learn(finder.Id, recipe.Id, _rules.FoundLevel, "found");
            var discovery = new Discovery(tick, finder.Id, stepResult.Id, recipe.Id, use.Use.Id, how, witnesses);
            _discoveries.Add(discovery);
            _village.Publish(new Discovered(tick, discovery));
        }
        Knowing.Learn(finder.Id, use.Use.Id, _rules.FoundLevel, "found");
    }

    private bool Notices(Person p, Process process, double share) =>
        _village.Random.NextDouble() < share * Math.Min(1, 0.5 + p.Traits.Openness / 200.0 + Knowing.Level(p.Id, process.Id) / 200.0);

    // who is there when someone works: their household and their workmates
    private List<Person> Present(Person maker) =>
        [.. Folk.Living.Where(q => q.Id != maker.Id && Folk.StageOf(q, _village.Clocks.Tick) != LifeStage.Child
            && (q.Household == maker.Household || (maker.Trade is not null && q.Trade == maker.Trade))).OrderBy(q => q.Id)];

    // what the island has now: raw substances at hand, and what someone living still knows how to make
    private List<Substance> KnownSubstances() => AtHand();

    private Name Named(Substance s)
    {
        var kind = _world.Kinds.GetValueOrDefault(s.Kind) ?? _naming.Own;
        var taken = _names.Values.Select(n => n.Nl).Concat(_world.NamesById.Values.Select(n => n.Nl)).ToHashSet(StringComparer.Ordinal);
        var free = _naming.Adjectives
            .Where(a => (s.Profile[a.Property] >= 50) == a.High)
            .OrderByDescending(a => Math.Abs(s.Profile[a.Property] - 50)).ThenBy(a => a.Property)
            .Select(a => new Name($"{a.Nl} {kind.Nl}", $"{a.En} {kind.En}"))
            .Where(n => !taken.Contains(n.Nl)).Take(3).ToList();
        if (free.Count > 0) return free[_namer.Next(free.Count)];
        for (var n = 2; ; n++)
        {
            var nl = _naming.Numbered.Nl.Replace("{name}", kind.Nl).Replace("{n}", n.ToString());
            if (!taken.Contains(nl)) return new Name(nl, _naming.Numbered.En.Replace("{name}", kind.En).Replace("{n}", n.ToString()));
        }
    }

    // ── the wall ──

    private void Walls(long tick)
    {
        var pressing = Pressing().Where(p => p.Weight >= _rules.WallWeight).ToList();
        foreach (var gone in _pressingSince.Keys.Where(u => pressing.All(p => p.Use != u)).ToList()) _pressingSince.Remove(gone);
        foreach (var (use, weight) in pressing)
        {
            if (_walled.Contains(use)) continue;
            if (!_pressingSince.TryGetValue(use, out var since)) _pressingSince[use] = since = tick;
            if (tick - since < _rules.WallYears * _ticksPerYear || Reachable(use)) continue;
            _walled.Add(use);
            _village.Publish(new HitAWall(tick, use, weight));
        }
    }

    // whether anything the island has, or can make in one step with what it can do, serves the use at all
    private bool Reachable(string useId)
    {
        var use = _world.Uses.First(u => u.Id == useId);
        var hand = AtHand();
        var known = Knowing.KnownOnIsland();
        var processes = _world.Processes.Where(x => known.Contains(x.Id) && (x.Needs is null || Living.Structures.Contains(x.Needs))).ToList();
        if (hand.Any(s => use.Score(s.Profile) > 0)) return true;
        foreach (var process in processes)
            foreach (var a in hand)
            {
                if (_world.Nature.Apply(process.Id, [new Portion(a, 1)]) is { } one && use.Score(one.Profile) > 0) return true;
                if (process.MaxInputs < 2) continue;
                foreach (var b in hand.Where(b => string.CompareOrdinal(b.Id, a.Id) > 0))
                    foreach (var r in _rules.Ratios.Concat(_rules.Ratios.Select(x => new[] { x[1], x[0] })))
                        if (_world.Nature.Apply(process.Id, [new Portion(a, r[0]), new Portion(b, r[1])]) is { } two && use.Score(two.Profile) > 0)
                            return true;
            }
        return false;
    }

    // ── reading and saving ──

    private void Read(ModuleContext village)
    {
        _village = village;
        _rules = village.Data.Section<InventingRules>("inventing");
        _naming = village.Data.Section<NameRules>("names");
        _world = WorldData.From(village.Data);
        _ticksPerYear = village.Data.Section<LifeSettings>("life").TicksPerLifeYear;
    }

    public JsonNode Save() => JsonSerializer.SerializeToNode(new SavedInventing(
        _discoveries,
        [.. _found.Values.Select(s => new SavedSubstance(s.Id, s.Kind, [.. s.Profile.Values]))],
        [.. _recipes.Values],
        _names,
        [.. _tried],
        _pressingSince,
        [.. _walled],
        _namer.State), SaveJson.Options)!;

    public void Load(ModuleContext village, JsonNode data, int version)
    {
        Read(village);
        var saved = data.Deserialize<SavedInventing>(SaveJson.Options) ?? throw new InvalidDataException("The inventing part of the save is empty.");
        _discoveries.Clear();
        _discoveries.AddRange(saved.Discoveries);
        _found.Clear();
        foreach (var s in saved.Found) _found[s.Id] = new Substance(s.Id, s.Kind, new Profile([.. s.Profile]), Raw: false);
        _recipes.Clear();
        foreach (var r in saved.Recipes) _recipes[r.Id] = r;
        _names.Clear();
        foreach (var (id, name) in saved.Names) _names[id] = name;
        _tried.Clear();
        _tried.UnionWith(saved.Tried);
        _pressingSince.Clear();
        foreach (var (use, since) in saved.PressingSince) _pressingSince[use] = since;
        _walled.Clear();
        _walled.UnionWith(saved.Walled);
        _namer = new Rng(saved.Namer);
    }

    private sealed record SavedSubstance(string Id, string Kind, IReadOnlyList<int> Profile);

    private sealed record SavedInventing(IReadOnlyList<Discovery> Discoveries, IReadOnlyList<SavedSubstance> Found, IReadOnlyList<Recipe> Recipes,
        IReadOnlyDictionary<string, Name> Names, IReadOnlyList<string> Tried, IReadOnlyDictionary<string, long> PressingSince,
        IReadOnlyList<string> Walled, ulong Namer);
}
