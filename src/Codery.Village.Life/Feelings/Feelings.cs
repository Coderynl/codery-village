using System.Text.Json;
using System.Text.Json.Nodes;
using Codery.Village.Core;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;
using Codery.Village.Laws;
using Codery.Village.Life.Folk;
using Codery.Village.Life.Inventing;
using Codery.Village.Life.Living;
using FolkModule = Codery.Village.Life.Folk.Folk;
using KnowingModule = Codery.Village.Life.Knowing.Knowing;
using LivingModule = Codery.Village.Life.Living.Living;

namespace Codery.Village.Life.Feelings;

/// <summary>
/// What people feel and want. Each person keeps a short memory, the joys of what came true (fading until it is normal),
/// the pain of what was lost (more than the joy it gave), and wishes that arise from causes: shortage, comparing oneself with
/// acquaintances, the promise of something found, habituation, and loss. Happiness follows a target made of these, slowly,
/// with no ceiling but 100. Events are taken in as they come; the target moves each village hour; wishes arise each village
/// day at 06:00.
/// </summary>
public sealed class Feelings : IModule
{
    private const double Faded = 0.25;

    private readonly SortedDictionary<int, Heart> _hearts = [];
    private readonly SortedDictionary<string, long> _lastShortage = new(StringComparer.Ordinal);
    private ModuleContext _village = null!;
    private FeelingsRules _rules = null!;
    private WorldData _world = null!;
    private IReadOnlyList<Use> _householdUses = [];
    private long _ticksPerYear;

    public string Name => "feelings";

    public int Version => 1;

    /// <summary>The rules this village feels by.</summary>
    public FeelingsRules Rules => _rules;

    /// <summary>How happy someone is, 0 to 100; 0 for someone without feelings here (not living).</summary>
    public int Happiness(int person) => _hearts.TryGetValue(person, out var heart) ? (int)Math.Round(heart.Happiness) : 0;

    /// <summary>What someone remembers, oldest first.</summary>
    public IReadOnlyList<Moment> Memory(int person) => _hearts.TryGetValue(person, out var heart) ? [.. heart.Memory] : [];

    /// <summary>What someone wants now, heaviest first.</summary>
    public IReadOnlyList<Wish> Wishes(int person) =>
        _hearts.TryGetValue(person, out var heart) ? [.. heart.Wishes.OrderByDescending(w => w.Weight).ThenBy(w => w.Use, StringComparer.Ordinal)] : [];

    /// <summary>Why someone feels as they do, as of the last village hour: each part of the target with its points.</summary>
    public IReadOnlyList<(string Reason, double Points)> Reasons(int person) =>
        _hearts.TryGetValue(person, out var heart) ? heart.Reasons : [];

    /// <summary>The weight of every wished use, summed over the living, heaviest first.</summary>
    public IReadOnlyList<(string Use, double Weight)> Pressing() =>
        [.. _hearts.Values.SelectMany(h => h.Wishes)
            .GroupBy(w => w.Use, StringComparer.Ordinal)
            .Select(g => (Use: g.Key, Weight: g.Sum(w => w.Weight)))
            .OrderByDescending(p => p.Weight).ThenBy(p => p.Use, StringComparer.Ordinal)];

    /// <summary>
    /// Makes someone want <paramref name="use"/> for <paramref name="cause"/>: a new wish, or a heavier one if they want it
    /// already. The way in for influence from outside the village (a trader's praise, from v6-2).
    /// </summary>
    public void Want(int person, string use, string cause, string? because)
    {
        if (!Wish.Causes.Contains(cause)) throw new ArgumentException($"Unknown cause '{cause}'.", nameof(cause));
        if (_hearts.TryGetValue(person, out var heart)) Add(person, heart, use, cause, because, _village.Clocks.Tick, _rules.Wishes.StartWeight);
    }

    public void Start(ModuleContext village)
    {
        Read(village);
        foreach (var p in Folk.Living) _hearts[p.Id] = new Heart { Happiness = _rules.Base + Temper(p) };
    }

    public void Step(ModuleContext village)
    {
        if (village.Received.Count > 0) TakeIn(village.Received, village.Clocks.Tick);
        if (!Hourly.Now(village)) return;

        var tick = village.Clocks.Tick;
        if (Hourly.DayStarts(village)) Daily(tick);
        foreach (var (id, heart) in _hearts) Feel(Folk.Get(id)!, heart, tick);
    }

    private FolkModule Folk => _village.Module<FolkModule>();
    private LivingModule Living => _village.Module<LivingModule>();
    private KnowingModule? Knowing => _village.Find<KnowingModule>();

    private double Years(long ticks) => (double)ticks / _ticksPerYear;

    private double Temper(Person p) => _rules.Temper * (p.Traits.Extraversion - p.Traits.Sensitivity) / 100.0;

    private static double Sensitive(Person p) => 0.5 + p.Traits.Sensitivity / 100.0;

    // ── what happens ──

    private void TakeIn(IReadOnlyList<VillageEvent> events, long tick)
    {
        foreach (var e in events)
        {
            switch (e)
            {
                case Born b:
                    if (Folk.Get(b.Person) is { } child) _hearts[b.Person] = new Heart { Happiness = _rules.Base + Temper(child) };
                    foreach (var parent in new[] { b.Mother, b.Father }) Remember(parent, e.Tick, "born", b.Person.ToString());
                    break;
                case Paired pair:
                    Remember(pair.A, e.Tick, "paired", pair.B.ToString());
                    Remember(pair.B, e.Tick, "paired", pair.A.ToString());
                    break;
                case Died d:
                    _hearts.Remove(d.Person);
                    if (Folk.Get(d.Person) is { } dead)
                        foreach (var kin in Kin(dead)) Remember(kin, e.Tick, kin == dead.Partner ? "partner-died" : "kin-died", d.Person.ToString());
                    break;
                case FellIll ill:
                    Remember(ill.Person, e.Tick, "ill", "");
                    break;
                case Shortage s:
                    _lastShortage[s.What] = e.Tick;
                    foreach (var (id, heart) in _hearts)
                        if (Folk.StageOf(Folk.Get(id)!, tick) != LifeStage.Child) Add(id, heart, s.What, "shortage", s.What, tick, _rules.Wishes.MaxWeight);
                    break;
                case Got got:
                    foreach (var p in Members(got.Household)) Fulfil(p, got.Use, e.Tick);
                    break;
                case Lost lost:
                    foreach (var p in Members(lost.Household)) Lose(p, lost.Use, e.Tick);
                    break;
                case Discovered found:
                    Promise(found.D.Substance, e.Tick);
                    break;
            }
        }
    }

    // a discovery suggests more: the open-minded come to want what it could serve at home, where nobody has that yet
    private void Promise(string substance, long tick)
    {
        if (Living.SubstanceOf(substance) is not { } found) return;
        var had = Households().SelectMany(h => Living.Of(h).Select(o => o.Use)).ToHashSet(StringComparer.Ordinal);
        var promised = _householdUses.Where(u => !had.Contains(u.Id) && u.Score(found.Profile) > 0)
            .OrderByDescending(u => u.Score(found.Profile)).ThenBy(u => u.Id, StringComparer.Ordinal).FirstOrDefault();
        if (promised is null) return;
        foreach (var (id, heart) in _hearts)
        {
            var p = Folk.Get(id)!;
            if (Folk.StageOf(p, tick) == LifeStage.Child || Wants(heart, promised.Id)) continue;
            if (_village.Random.NextDouble() < _rules.Wishes.PromiseDaily * p.Traits.Openness / 100.0)
                Add(id, heart, promised.Id, "promise", substance, tick, _rules.Wishes.StartWeight);
        }
    }

    private IEnumerable<Person> Members(int household) => Folk.Living.Where(p => p.Household == household && _hearts.ContainsKey(p.Id));

    private void Fulfil(Person p, string use, long tick)
    {
        var heart = _hearts[p.Id];
        var wish = heart.Wishes.FirstOrDefault(w => w.Use == use);
        double joy;
        if (wish is not null)
        {
            heart.Wishes.Remove(wish);
            var waited = Math.Clamp(Years(tick - wish.SinceTick), _rules.WaitFloor, _rules.WaitCap);
            joy = _rules.JoyScale * waited * wish.Weight / _rules.Wishes.MaxWeight;
            _village.Publish(new Fulfilled(tick, p.Id, use, tick - wish.SinceTick));
        }
        else if (heart.Fulfilments.Any(f => f.Use == use)) return; // a better one nobody asked for: the old joy and its age stay
        else joy = _rules.JoyScale * _rules.WaitFloor * _rules.UnwishedStrength;
        heart.Fulfilments.RemoveAll(f => f.Use == use);
        heart.Fulfilments.Add(new Fulfilment(use, tick, joy));
        Note(heart, new Moment(tick, "got", use, 0));
    }

    private void Lose(Person p, string use, long tick)
    {
        var heart = _hearts[p.Id];
        var had = heart.Fulfilments.FirstOrDefault(f => f.Use == use);
        if (had is null) return;
        heart.Fulfilments.Remove(had);
        // losing what had become normal hurts more than it ever pleased; losing something new only ends its joy
        if (Years(tick - had.Tick) >= _rules.ExpectedAfter) heart.Losses.Add(new Loss(use, tick, _rules.LossAversion * had.Joy));
        Note(heart, new Moment(tick, "lost", use, 0));
        Add(p.Id, heart, use, "loss", use, tick, _rules.Wishes.StartWeight);
    }

    private void Remember(int person, long tick, string kind, string subject)
    {
        if (_hearts.TryGetValue(person, out var heart)) Note(heart, new Moment(tick, kind, subject, _rules.Moments.GetValueOrDefault(kind)));
    }

    // a full memory lets go of what weighs least now
    private void Note(Heart heart, Moment moment)
    {
        heart.Memory.Add(moment);
        if (heart.Memory.Count <= _rules.MemorySize) return;
        var tick = moment.Tick;
        var lightest = heart.Memory.Take(heart.Memory.Count - 1)
            .OrderBy(m => Math.Abs(m.Weight) * Fade(tick - m.Tick, _rules.MemoryHalfLife)).ThenBy(m => m.Tick).First();
        heart.Memory.Remove(lightest);
    }

    // a new wish, or a heavier old one; a full heart gives up its lightest wish only for a heavier new one, and says so
    private void Add(int person, Heart heart, string use, string cause, string? because, long tick, double weight)
    {
        var wishes = _rules.Wishes;
        var had = heart.Wishes.FindIndex(w => w.Use == use);
        if (had >= 0)
        {
            heart.Wishes[had] = heart.Wishes[had] with { Weight = Math.Min(wishes.MaxWeight, Math.Max(weight, heart.Wishes[had].Weight + wishes.StartWeight / 2)) };
            return;
        }
        if (heart.Wishes.Count >= wishes.MaxWishes)
        {
            var lightest = heart.Wishes.OrderBy(w => w.Weight).ThenByDescending(w => w.SinceTick).First();
            if (lightest.Weight >= weight) return;
            heart.Wishes.Remove(lightest);
            _village.Publish(new GaveUp(tick, person, lightest.Use));
        }
        heart.Wishes.Add(new Wish(use, cause, tick, weight, because));
        _village.Publish(new Wished(tick, person, use, cause, because));
        Note(heart, new Moment(tick, "wished", use, 0));
    }

    // ── each day: wishes grow, shortages end, new wishes arise ──

    private void Daily(long tick)
    {
        var rules = _rules.Wishes;
        var day = Hourly.TicksPerHour * 24;
        var random = _village.Random;
        var holders = _householdUses.ToDictionary(u => u.Id, u => Households().Count(h => Living.Of(h).Any(o => o.Use == u.Id)), StringComparer.Ordinal);
        var known = Knowing?.KnownOnIsland() ?? [];

        foreach (var (id, heart) in _hearts)
        {
            var p = Folk.Get(id)!;
            for (var i = 0; i < heart.Wishes.Count; i++)
                heart.Wishes[i] = heart.Wishes[i] with { Weight = Math.Min(rules.MaxWeight, heart.Wishes[i].Weight + rules.GrowthPerYear * day / _ticksPerYear) };

            // a shortage is over after a day without it
            foreach (var over in heart.Wishes.Where(w => w.Cause == "shortage" && w.Because == w.Use && tick - _lastShortage.GetValueOrDefault(w.Use) > day).ToList())
            {
                heart.Wishes.Remove(over);
                _village.Publish(new Fulfilled(tick, id, over.Use, tick - over.SinceTick));
            }

            if (Folk.StageOf(p, tick) == LifeStage.Child) continue;
            var own = Living.Of(p.Household).ToDictionary(o => o.Use, StringComparer.Ordinal);

            // shortage: a household use the island knows, that the household lacks
            foreach (var use in _householdUses)
                if (known.Contains(use.Id) && !own.ContainsKey(use.Id) && !Wants(heart, use.Id) && random.NextDouble() < rules.LackDaily)
                    Add(id, heart, use.Id, "shortage", null, tick, rules.StartWeight);

            // comparison
            var jealousy = rules.ComparisonDaily * Math.Pow((p.Traits.Sensitivity + p.Traits.Status) / 100.0, rules.ComparisonPower);
            foreach (var (household, face) in Acquaintances(p))
                foreach (var theirs in Living.Of(household))
                {
                    if (!_householdUses.Any(u => u.Id == theirs.Use) || Wants(heart, theirs.Use)) continue;
                    var mine = own.GetValueOrDefault(theirs.Use);
                    if (mine is not null && Score(theirs.Use, theirs.Substance) - Score(theirs.Use, mine.Substance) < rules.Better) continue;
                    if (random.NextDouble() < jealousy) Add(id, heart, theirs.Use, "comparison", face.ToString(), tick, rules.StartWeight);
                }

            // habituation
            if (heart.Wishes.Count == 0 && heart.Fulfilments.Count > 0
                && heart.Fulfilments.All(f => Years(tick - f.Tick) >= _rules.ExpectedAfter)
                && random.NextDouble() < rules.HabituationDaily * p.Traits.Openness / 100.0)
            {
                var next = _householdUses.Where(u => !own.ContainsKey(u.Id))
                    .OrderBy(u => holders[u.Id]).ThenBy(u => u.Id, StringComparer.Ordinal).FirstOrDefault();
                if (next is not null) Add(id, heart, next.Id, "habituation", null, tick, rules.StartWeight);
            }
        }
    }

    private static bool Wants(Heart heart, string use) => heart.Wishes.Any(w => w.Use == use);

    private IEnumerable<int> Households() => Folk.Living.Select(p => p.Household).Distinct().Order();

    // the households someone knows, each with the person there they know best: neighbours by number, kin, workmates
    private IEnumerable<(int Household, int Face)> Acquaintances(Person p)
    {
        var faces = new SortedDictionary<int, int>();
        void Meet(Person q)
        {
            if (q.Household != p.Household && !faces.ContainsKey(q.Household)) faces[q.Household] = q.Id;
        }
        foreach (var q in Folk.Living.Where(q => Math.Abs(q.Household - p.Household) <= _rules.Wishes.Neighbours).OrderBy(q => q.Id)) Meet(q);
        foreach (var kin in Kin(p)) if (Folk.Get(kin) is { DiedTick: null } q) Meet(q);
        if (p.Trade is { } trade) foreach (var q in Folk.Living.Where(q => q.Trade == trade).OrderBy(q => q.Id)) Meet(q);
        return faces.Select(f => (f.Key, f.Value));
    }

    // partner, parents, children and siblings, by id
    private IEnumerable<int> Kin(Person p)
    {
        var kin = new SortedSet<int>();
        if (p.Partner is { } partner) kin.Add(partner);
        if (p.Mother is { } mother) kin.Add(mother);
        if (p.Father is { } father) kin.Add(father);
        for (var id = 0; Folk.Get(id) is { } q; id++)
        {
            if (q.Id == p.Id) continue;
            if (q.Mother == p.Id || q.Father == p.Id) kin.Add(q.Id);
            else if ((p.Mother is not null && q.Mother == p.Mother) || (p.Father is not null && q.Father == p.Father)) kin.Add(q.Id);
        }
        return kin;
    }

    private double Score(string use, string substance)
    {
        var u = _householdUses.FirstOrDefault(x => x.Id == use);
        return u is not null && Living.SubstanceOf(substance) is { } s ? u.Score(s.Profile) : 0;
    }

    // ── each hour: the target, and happiness one step towards it ──

    private void Feel(Person p, Heart heart, long tick)
    {
        var reasons = new List<(string, double)> { ("base", _rules.Base), ("temper", Temper(p)) };
        var needs = _rules.Needs;
        var day = Hourly.TicksPerHour * 24;
        bool Short(string what) => _lastShortage.TryGetValue(what, out var at) && tick - at <= day;
        if (Short("food")) reasons.Add(("hungry", needs.Hungry * Sensitive(p)));
        if (Short("fuel") && Living.Weather.Cold > 0) reasons.Add(("cold", needs.Cold * Sensitive(p)));
        if (Living.Ill.Contains(p.Id)) reasons.Add(("ill", needs.Ill));
        if (p.Partner is not null) reasons.Add(("partner", needs.Partner));
        if (p.Health < 100) reasons.Add(("health", needs.Health * (100 - p.Health) / 100.0));

        foreach (var f in heart.Fulfilments)
        {
            var age = Years(tick - f.Tick);
            if (age < _rules.ExpectedAfter) reasons.Add(($"joy:{f.Use}", f.Joy * Fade(tick - f.Tick, _rules.JoyHalfLife)));
        }
        heart.Losses.RemoveAll(l => l.Pain * Fade(tick - l.Tick, _rules.PainHalfLife) < Faded);
        foreach (var l in heart.Losses) reasons.Add(($"loss:{l.Use}", -l.Pain * Fade(tick - l.Tick, _rules.PainHalfLife)));
        foreach (var w in heart.Wishes) reasons.Add(($"wish:{w.Use}", -_rules.WishPain * w.Weight / _rules.Wishes.MaxWeight * Sensitive(p)));
        foreach (var m in heart.Memory.Where(m => m.Weight != 0))
        {
            var points = m.Weight * Fade(tick - m.Tick, _rules.MemoryHalfLife);
            if (Math.Abs(points) >= Faded) reasons.Add(($"{m.Kind}:{m.Subject}", points));
        }

        heart.Reasons = reasons;
        var target = Math.Clamp(reasons.Sum(r => r.Item2), 0, 100);
        heart.Happiness += Math.Clamp(target - heart.Happiness, -_rules.MaxStepPerHour, _rules.MaxStepPerHour);
    }

    private double Fade(long ticks, double halfLife) => Math.Pow(0.5, Years(ticks) / halfLife);

    // ── reading and saving ──

    private void Read(ModuleContext village)
    {
        _village = village;
        _rules = village.Data.Section<FeelingsRules>("feelings");
        _world = WorldData.From(village.Data);
        _householdUses = [.. _world.Uses.Where(u => u.Household)];
        _ticksPerYear = village.Data.Section<LifeSettings>("life").TicksPerLifeYear;
    }

    public JsonNode Save() => JsonSerializer.SerializeToNode(new SavedFeelings(
        [.. _hearts.Select(h => new SavedHeart(h.Key, h.Value.Happiness, h.Value.Memory, h.Value.Wishes, h.Value.Fulfilments, h.Value.Losses))],
        _lastShortage), SaveJson.Options)!;

    public void Load(ModuleContext village, JsonNode data, int version)
    {
        Read(village);
        var saved = data.Deserialize<SavedFeelings>(SaveJson.Options) ?? throw new InvalidDataException("The feelings part of the save is empty.");
        _hearts.Clear();
        foreach (var h in saved.Hearts)
            _hearts[h.Person] = new Heart { Happiness = h.Happiness, Memory = [.. h.Memory], Wishes = [.. h.Wishes], Fulfilments = [.. h.Fulfilments], Losses = [.. h.Losses] };
        _lastShortage.Clear();
        foreach (var (what, at) in saved.LastShortage) _lastShortage[what] = at;
    }

    private sealed class Heart
    {
        public double Happiness;
        public List<Moment> Memory = [];
        public List<Wish> Wishes = [];
        public List<Fulfilment> Fulfilments = [];
        public List<Loss> Losses = [];
        public IReadOnlyList<(string Reason, double Points)> Reasons = [];
    }

    private sealed record SavedFeelings(IReadOnlyList<SavedHeart> Hearts, IReadOnlyDictionary<string, long> LastShortage);

    private sealed record SavedHeart(int Person, double Happiness, IReadOnlyList<Moment> Memory, IReadOnlyList<Wish> Wishes,
        IReadOnlyList<Fulfilment> Fulfilments, IReadOnlyList<Loss> Losses);
}
