using System.Text.Json;
using System.Text.Json.Nodes;
using Codery.Village.Core;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;
using Codery.Village.Laws;
using Codery.Village.Modules.People;

namespace Codery.Village.Life.Folk;

/// <summary>
/// The people of West: who they are, their pairs and households, their children, their trades and their deaths.
/// Twelve to fifteen people start in pairs with children. Once a day at 06:00 the old may die of age, those who turn
/// 18 choose a trade, single adults may pair, and pairs may have a child. Other modules harm people through
/// <see cref="Harm"/>; whoever reaches health 0 dies of what harmed them last.
/// </summary>
public sealed class Folk : IModule
{
    /// <summary>The cause of a death by age.</summary>
    public const string Age = "age";

    // everyone who ever lived here, by id: _everyone[id].Id == id
    private readonly List<Person> _everyone = [];
    // the living, in id order
    private readonly List<Person> _living = [];
    private FolkPeople _people = null!;
    private LifeRules _rules = null!;
    private LifeClock _life = null!;
    private ModuleContext _village = null!;
    private IReadOnlyList<string> _trades = [];
    private long _ticksPerYear;
    private double _daysPerYear;
    private int _nextHousehold;

    public string Name => "folk";

    public int Version => 1;

    /// <summary>Everyone alive, in id order.</summary>
    public IReadOnlyList<Person> Living => _living.AsReadOnly();

    /// <summary>Anyone who lived here, also the dead; null for an id nobody had.</summary>
    public Person? Get(int id) => id >= 0 && id < _everyone.Count ? _everyone[id] : null;

    /// <summary>The age in life years at <paramref name="tick"/>.</summary>
    public double AgeOf(Person p, long tick) => _life.YearsAt(tick - p.BornTick);

    public LifeStage StageOf(Person p, long tick)
    {
        var age = AgeOf(p, tick);
        if (age < _rules.Stages.Pupil) return LifeStage.Child;
        if (age < _rules.Stages.Adult) return LifeStage.Pupil;
        return age < _rules.Stages.Old ? LifeStage.Adult : LifeStage.Old;
    }

    /// <summary>
    /// Takes <paramref name="health"/> from someone alive (a negative amount gives it back, up to 100). At 0 they die of
    /// <paramref name="cause"/>: <c>sickness</c>, <c>cold</c>, <c>hunger</c> or <c>accident</c>. The dead are not harmed.
    /// </summary>
    public void Harm(int person, int health, string cause)
    {
        if (Get(person) is not { DiedTick: null } p) return;
        p.Health = Math.Clamp(p.Health - health, 0, 100);
        if (p.Health == 0) Die(p, cause);
    }

    public void Start(ModuleContext village)
    {
        Read(village);
        var random = village.Random;
        var start = _people.Start;
        var count = start.Min + random.Next(start.Max - start.Min + 1);
        while (_living.Count < count)
        {
            var band = Pick(random, start.PairAges, b => b.Weight);
            if (count - _living.Count == 1)
            {
                Add(random.Next(2) == 0 ? Sex.Man : Sex.Woman, YearsIn(random, band), null, null, _nextHousehold++, null);
                continue;
            }

            var household = _nextHousehold++;
            var first = band.From + random.Next(band.To - band.From + 1);
            var second = Math.Clamp(first + random.Next(2 * start.PartnerGap + 1) - start.PartnerGap, band.From, band.To);
            // a pair of one sex, at the share drawn to their own sex, has no children
            if (random.NextDouble() < _rules.Pairing.OwnSex)
            {
                var sex = random.Next(2) == 0 ? Sex.Man : Sex.Woman;
                var one = Add(sex, first + random.NextDouble(), null, null, household, sex);
                var other = Add(sex, second + random.NextDouble(), null, null, household, sex);
                one.Partner = other.Id;
                other.Partner = one.Id;
                continue;
            }
            var man = Add(Sex.Man, first + random.NextDouble(), null, null, household, Sex.Woman);
            var woman = Add(Sex.Woman, second + random.NextDouble(), null, null, household, Sex.Man);
            man.Partner = woman.Id;
            woman.Partner = man.Id;

            // children under 18, each born while the mother was of an age to have them
            var children = Math.Min(Pick(random, start.Children, c => c.Weight).Count, count - _living.Count);
            var mother = AgeOf(woman, village.Clocks.Tick);
            var youngest = Math.Max(0, mother - (_rules.Births.MotherTo + 1));
            var oldest = Math.Min(_rules.Stages.Adult, mother - _rules.Births.MotherFrom);
            for (var i = 0; i < children && youngest < oldest; i++)
                Add(random.Next(2) == 0 ? Sex.Man : Sex.Woman, youngest + random.NextDouble() * (oldest - youngest), woman.Id, man.Id, household, null);
        }

        // a village begins with the trades it cannot do without, each taken by an adult drawn at random, the rest by choice
        var adults = _living.Where(p => StageOf(p, village.Clocks.Tick) >= LifeStage.Adult).ToList();
        foreach (var trade in (_rules.Trades.Essential ?? []).Where(_trades.Contains))
        {
            var free = adults.Where(p => p.Trade is null && AgeOf(p, village.Clocks.Tick) < _rules.Stages.Old).ToList();
            if (free.Count > 0) free[random.Next(free.Count)].Trade = trade;
        }
        foreach (var p in adults.Where(p => p.Trade is null)) p.Trade = ChooseTrade(random, p);
    }

    public void Step(ModuleContext village)
    {
        if (!Hourly.DayStarts(village)) return;

        var random = village.Random;
        var tick = village.Clocks.Tick;
        foreach (var p in _living.ToArray()) DieOfAge(random, p, tick);
        foreach (var p in _living.ToArray()) ComeOfAge(village, p, tick);
        foreach (var p in _living.ToArray()) TryToPair(village, p, tick);
        foreach (var p in _living.ToArray()) TryForAChild(village, p, tick);
        AnswerANeed(village, tick);
    }

    // when food or fuel has run short for days, someone takes up the trade that gives it
    private void AnswerANeed(ModuleContext village, long tick)
    {
        if (_rules.Trades.Need is not { } need || village.Find<Living.Living>() is not { } living) return;
        foreach (var (what, trade) in need.Trades.Where(n => _trades.Contains(n.Value)).OrderBy(n => n.Key, StringComparer.Ordinal))
        {
            if (living.DaysShort(what) < need.Days || village.Random.NextDouble() >= need.Chance) continue;
            var who = _living.Where(p => p.Trade is not null && p.Trade != trade && StageOf(p, tick) == LifeStage.Adult && AgeOf(p, tick) <= need.UpTo)
                .OrderBy(p => p.Id).ToList();
            if (who.Count == 0) continue;
            var p = who[village.Random.Next(who.Count)];
            var from = p.Trade!;
            p.Trade = trade;
            village.Publish(new ChangedTrade(tick, p.Id, from, trade));
        }
    }

    // ── a life ──

    private void DieOfAge(Rng random, Person p, long tick)
    {
        var age = AgeOf(p, tick);
        var chance = 0.0;
        var from = int.MinValue;
        foreach (var row in _rules.Dying)
            if (row.From <= age && row.From > from) (from, chance) = (row.From, row.Chance);
        if (chance > 0 && random.NextDouble() < Daily(chance)) Die(p, Age);
    }

    private void Die(Person p, string cause)
    {
        p.DiedTick = _village.Clocks.Tick;
        _living.Remove(p);
        if (p.Partner is { } partner && Get(partner) is { DiedTick: null } widowed) widowed.Partner = null;
        _village.Publish(new Died(p.DiedTick.Value, p.Id, cause));
    }

    private void ComeOfAge(ModuleContext village, Person p, long tick)
    {
        if (p.Trade is not null || StageOf(p, tick) < LifeStage.Adult) return;
        p.Trade = ChooseTrade(village.Random, p);
        village.Publish(new CameOfAge(tick, p.Id));
    }

    // a single adult pairs with a single adult they are drawn to and who is drawn to them, near in age and not close kin
    private void TryToPair(ModuleContext village, Person p, long tick)
    {
        if (p.Partner is not null || StageOf(p, tick) < LifeStage.Adult) return;
        var age = AgeOf(p, tick);
        var singles = _living.Where(q => q.Sex == p.DrawnTo && q.DrawnTo == p.Sex && q.Partner is null && StageOf(q, tick) >= LifeStage.Adult
            && Math.Abs(AgeOf(q, tick) - age) <= _rules.Pairing.AgeGap && !Kin(p, q)).ToList();
        if (singles.Count == 0) return;

        var pairing = _rules.Pairing;
        // capped below 1, so the most outgoing still differ from the less outgoing
        var chance = Math.Min(pairing.Max, pairing.Chance * (0.5 + p.Traits.Extraversion / 100.0) * Math.Min(1.0, (double)singles.Count / pairing.Crowd));
        if (village.Random.NextDouble() >= Daily(chance)) return;

        var q = singles[village.Random.Next(singles.Count)];
        p.Partner = q.Id;
        q.Partner = p.Id;
        // the pair makes a new household; the children under 18 of either come along
        var household = _nextHousehold++;
        var old = new[] { p.Household, q.Household };
        foreach (var child in _living.Where(c => old.Contains(c.Household) && StageOf(c, tick) < LifeStage.Adult
            && (c.Mother == p.Id || c.Father == p.Id || c.Mother == q.Id || c.Father == q.Id)))
            child.Household = household;
        p.Household = household;
        q.Household = household;
        village.Publish(new Paired(tick, p.Id, q.Id));
    }

    private static bool Kin(Person a, Person b) =>
        a.Mother == b.Id || a.Father == b.Id || b.Mother == a.Id || b.Father == a.Id
        || (a.Mother is not null && a.Mother == b.Mother) || (a.Father is not null && a.Father == b.Father);

    private void TryForAChild(ModuleContext village, Person mother, long tick)
    {
        // a child needs a woman and a man in the pair
        if (mother.Sex != Sex.Woman || mother.Partner is not { } partner || _everyone[partner].Sex != Sex.Man) return;
        var births = _rules.Births;
        var age = AgeOf(mother, tick);
        if (age < births.MotherFrom || age >= births.MotherTo + 1) return;
        var last = _everyone.Where(c => c.Mother == mother.Id).Select(c => (long?)c.BornTick).Max();
        if (last is { } born && tick - born < births.GapYears * _ticksPerYear) return;
        if (village.Random.NextDouble() >= Daily(births.Chance)) return;

        var father = _everyone[partner];
        var random = village.Random;
        var sex = random.Next(2) == 0 ? Sex.Man : Sex.Woman;
        var name = FreeName(sex, random.Next(int.MaxValue));
        var child = new Person(_everyone.Count, name, sex, tick, Inherit(random, mother.Traits, father.Traits), DrawInterests(random),
            DrawnTo(random, sex), mother.Id, father.Id, mother.Household);
        _everyone.Add(child);
        _living.Add(child);
        village.Publish(new Born(tick, child.Id, mother.Id, father.Id));
    }

    // each trait the parents' mean, give or take the spread, within 0 to 100
    private Personality Inherit(Rng random, Personality mother, Personality father)
    {
        var spread = _rules.Births.Spread;
        var m = mother.Values;
        var f = father.Values;
        var values = new int[m.Count];
        for (var i = 0; i < m.Count; i++)
        {
            var mean = (m[i] + f[i]) / 2.0;
            var low = (int)Math.Ceiling(mean - spread);
            var high = (int)Math.Floor(mean + spread);
            values[i] = Math.Clamp(low + random.Next(high - low + 1), 0, 100);
        }
        return Personality.From(values);
    }

    // weighted: a parent's trade, a trade matching an interest, a trade nobody living has; some only for the agreeable
    private string ChooseTrade(Rng random, Person p)
    {
        var rules = _rules.Trades;
        var parents = new[] { p.Mother, p.Father }.Select(id => id is { } i ? Get(i)?.Trade : null).ToList();
        var weights = new double[_trades.Count];
        for (var i = 0; i < _trades.Count; i++)
        {
            var trade = _trades[i];
            if (rules.Agreeable.Contains(trade) && p.Traits.Agreeableness < rules.AgreeableFrom) continue;
            var weight = 1.0;
            if (parents.Contains(trade)) weight *= rules.Parent;
            if (rules.Interests.TryGetValue(trade, out var interests) && interests.Any(p.Interests.Contains)) weight *= rules.Interest;
            if (!_living.Any(q => q != p && q.Trade == trade)) weight *= rules.Missing;
            if (rules.Need is { } need && _village?.Find<Living.Living>() is { } living
                && need.Trades.Any(n => n.Value == trade && living.DaysShort(n.Key) > 0)) weight *= need.Weight;
            weights[i] = weight;
        }

        var roll = random.NextDouble() * weights.Sum();
        for (var i = 0; i < weights.Length; i++)
        {
            roll -= weights[i];
            if (roll < 0) return _trades[i];
        }
        return _trades[Array.FindLastIndex(weights, w => w > 0)];
    }

    // the chance for one day of a chance per life year
    private double Daily(double yearly) => yearly >= 1 ? 1 : 1 - Math.Pow(1 - yearly, 1 / _daysPerYear);

    // ── making people ──

    // drawnTo null: drawn as for anyone born here
    private Person Add(Sex sex, double age, int? mother, int? father, int household, Sex? drawnTo)
    {
        var random = _village.Random;
        var name = FreeName(sex, random.Next(int.MaxValue));
        var p = new Person(_everyone.Count, name, sex, _village.Clocks.Tick - _life.TickOfAge(age), DrawTraits(random), DrawInterests(random),
            drawnTo ?? DrawnTo(random, sex), mother, father, household);
        _everyone.Add(p);
        _living.Add(p);
        return p;
    }

    // their own sex at the share in the data, else the other
    private Sex DrawnTo(Rng random, Sex sex) =>
        random.NextDouble() < _rules.Pairing.OwnSex ? sex : sex == Sex.Man ? Sex.Woman : Sex.Man;

    private static double YearsIn(Rng random, AgeBand band) => band.From + random.Next(band.To - band.From + 1) + random.NextDouble();

    // the drawn name, or the next in the list that nobody living bears; a draw, not a search, so the stream stays the same
    private string FreeName(Sex sex, int drawn)
    {
        var names = sex == Sex.Man ? _people.Names.Man : _people.Names.Woman;
        for (var step = 0; step < names.Count; step++)
        {
            var name = names[(drawn + step) % names.Count];
            if (!_living.Any(p => p.Name == name)) return name;
        }
        return names[drawn % names.Count];
    }

    // per trait a normal draw, the sum of twelve uniform draws less six, held between 0 and 100
    private Personality DrawTraits(Rng random)
    {
        var spreads = _people.Personality.All;
        var values = new int[spreads.Count];
        for (var i = 0; i < spreads.Count; i++)
        {
            var z = -6.0;
            for (var k = 0; k < 12; k++) z += random.NextDouble();
            values[i] = (int)Math.Clamp(Math.Round(spreads[i].Mean + spreads[i].Spread * z, MidpointRounding.AwayFromZero), 0, 100);
        }
        return Personality.From(values);
    }

    // one to three different interests
    private List<string> DrawInterests(Rng random)
    {
        var all = _people.Interests;
        var count = 1 + random.Next(Math.Min(3, all.Count));
        var chosen = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var at = random.Next(all.Count);
            while (chosen.Contains(all[at].Id)) at = (at + 1) % all.Count;
            chosen.Add(all[at].Id);
        }
        return chosen;
    }

    private static T Pick<T>(Rng random, IReadOnlyList<T> items, Func<T, int> weight)
    {
        var roll = random.Next(items.Sum(weight));
        foreach (var item in items)
        {
            roll -= weight(item);
            if (roll < 0) return item;
        }
        return items[^1];
    }

    private void Read(ModuleContext village)
    {
        _village = village;
        _people = village.Data.Section<FolkPeople>("people");
        _rules = village.Data.Section<LifeRules>("life");
        _life = village.Clocks.Life;
        _ticksPerYear = village.Data.Section<LifeSettings>("life").TicksPerLifeYear;
        _daysPerYear = (double)_ticksPerYear / (Hourly.TicksPerHour * 24);
        _trades = [.. WorldData.From(village.Data).Start.Trades.Select(t => t.Id)];
        if (_trades.Count == 0 || _trades.All(_rules.Trades.Agreeable.Contains))
            throw new InvalidDataException("The world has no trade that anyone can choose.");
    }

    // ── saving ──

    public JsonNode Save() =>
        JsonSerializer.SerializeToNode(new SavedFolk(_nextHousehold, [.. _everyone.Select(SavedPerson.Of)]), SaveJson.Options)!;

    public void Load(ModuleContext village, JsonNode data, int version)
    {
        Read(village);
        var saved = data.Deserialize<SavedFolk>(SaveJson.Options) ?? throw new InvalidDataException("The folk part of the save is empty.");
        _nextHousehold = saved.NextHousehold;
        _everyone.Clear();
        _everyone.AddRange(saved.People.Select(p => p.ToPerson()));
        _living.Clear();
        _living.AddRange(_everyone.Where(p => p.DiedTick is null));
    }

    private sealed record SavedFolk(int NextHousehold, IReadOnlyList<SavedPerson> People);

    private sealed record SavedPerson(int Id, string Name, Sex Sex, long BornTick, Personality Traits, IReadOnlyList<string> Interests,
        Sex DrawnTo, int Household, int Health, int? Mother = null, int? Father = null, int? Partner = null, string? Trade = null, long? DiedTick = null)
    {
        public static SavedPerson Of(Person p) =>
            new(p.Id, p.Name, p.Sex, p.BornTick, p.Traits, p.Interests, p.DrawnTo, p.Household, p.Health, p.Mother, p.Father, p.Partner, p.Trade, p.DiedTick);

        public Person ToPerson() => new(Id, Name, Sex, BornTick, Traits, Interests, DrawnTo, Mother, Father, Household)
        {
            Partner = Partner,
            Trade = Trade,
            Health = Health,
            DiedTick = DiedTick,
        };
    }
}
