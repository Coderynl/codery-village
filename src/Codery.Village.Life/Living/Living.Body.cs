using Codery.Village.Core.Modules;
using Codery.Village.Laws;
using Codery.Village.Life.Folk;

namespace Codery.Village.Life.Living;

// what acts on the village and its people: the weather, eating, fuel, sickness, healing, spoiling and wear
public sealed partial class Living
{
    private const string Hunger = "hunger";
    private const string Cold = "cold";
    private const string Sickness = "sickness";

    private int ColdOf(ModuleContext village) => _rules.Cold[village.Clocks.Calendar.Now.Month - 1];

    // a drought lasts its days, the first included; one may begin on a day of the dry months
    private void ChangeWeather(ModuleContext village)
    {
        var tick = village.Clocks.Tick;
        var drought = Weather.Drought;
        var dry = _rules.Drought;
        if (drought)
        {
            if (_droughtLeft == 0)
            {
                drought = false;
                village.Publish(new DroughtEnded(tick));
            }
            else _droughtLeft--;
        }
        else if (dry.Months.Contains(village.Clocks.Calendar.Now.Month) && village.Random.NextDouble() < dry.Chance)
        {
            drought = true;
            _droughtLeft = dry.MinDays + village.Random.Next(dry.MaxDays - dry.MinDays + 1) - 1;
            village.Publish(new DroughtBegan(tick));
        }
        Weather = new Weather(drought, ColdOf(village));
    }

    // everyone eats from stock, what spoils soonest first; on a short day everyone goes short alike
    private bool Eat(long tick)
    {
        var need = _folk.Living.Count * _rules.DailyNutrition;
        var share = Consume(need, _food, s => s.Profile[Property.Nutrition], out _);
        if (share <= Nothing)
        {
            _foodShortDays = 0;
            return false;
        }
        _village.Publish(new Shortage(tick, Food, ++_foodShortDays));
        foreach (var p in _folk.Living.ToArray()) _folk.Harm(p.Id, Round(_rules.Harm.Hunger * share), Hunger);
        return true;
    }

    // each household burns fuel by the cold; ash remains; without enough, the cold harms the old and children most
    private bool Warm(long tick)
    {
        var households = _folk.Living.Select(p => p.Household).Distinct().Count();
        var cold = Weather.Cold / 100.0;
        var need = households * _rules.FuelPerHousehold * cold;
        if (need <= Nothing)
        {
            _fuelShortDays = 0;
            return false;
        }
        var share = Consume(need, _fuel, _ => 1, out var burnt);
        Stock.Add(_rules.Ash, burnt * _rules.AshShare);
        if (share <= Nothing)
        {
            _fuelShortDays = 0;
            return false;
        }
        _village.Publish(new Shortage(tick, Fuel, ++_fuelShortDays));
        foreach (var p in _folk.Living.ToArray())
        {
            var stage = _folk.StageOf(p, tick);
            var frail = stage == LifeStage.Old ? _rules.Harm.Old : stage < LifeStage.Adult ? _rules.Harm.Child : 1;
            _folk.Harm(p.Id, Round(_rules.Harm.Cold * cold * share * frail), Cold);
        }
        return true;
    }

    // takes what serves the use from stock, least durable first, until need is met in value; returns the share left short
    private double Consume(double need, Use use, Func<Substance, int> value, out double units)
    {
        units = 0;
        var left = need;
        var serving = Stock.Amounts.Keys
            .Where(id => _substances.TryGetValue(id, out var s) && use.Score(s.Profile) > 0 && value(s) > 0)
            .OrderBy(Durability).ThenBy(id => id, StringComparer.Ordinal).ToList();
        foreach (var id in serving)
        {
            if (left <= Nothing) break;
            var worth = value(_substances[id]);
            var taken = Stock.Take(id, left / worth);
            units += taken;
            left -= taken * worth;
        }
        return need <= 0 ? 0 : Math.Max(0, left) / need;
    }

    // the ill recover or lose health; the well may fall ill, from their household most of all
    private void Sicken(long tick, bool hungry, bool cold)
    {
        var rules = _rules.Sickness;
        var random = _village.Random;
        var living = _folk.Living.ToDictionary(p => p.Id);
        _ill.RemoveWhere(id => !living.ContainsKey(id));
        var illBefore = _ill.Select(id => living[id]).ToList();

        foreach (var p in illBefore)
        {
            if (random.NextDouble() < rules.Recover)
            {
                _ill.Remove(p.Id);
                _village.Publish(new Recovered(tick, p.Id));
                continue;
            }
            var cared = _folk.Living.Any(c => c.Id != p.Id && c.Trade == _rules.Roles.Carer && !_ill.Contains(c.Id));
            _folk.Harm(p.Id, Round(rules.Harm * (cared ? 0.5 : 1)), Sickness);
            if (p.DiedTick is not null) _ill.Remove(p.Id);
        }

        var spontaneous = rules.Chance * (hungry ? rules.Hungry : 1) * (cold ? rules.Cold : 1);
        foreach (var p in _folk.Living.ToArray())
        {
            if (_ill.Contains(p.Id) || illBefore.Contains(p)) continue;
            var housemates = illBefore.Count(q => q.Household == p.Household);
            var others = illBefore.Count - housemates;
            var well = (1 - spontaneous) * Math.Pow(1 - rules.Household, housemates) * Math.Pow(1 - rules.Beyond, others);
            if (random.NextDouble() >= 1 - well) continue;
            _ill.Add(p.Id);
            _village.Publish(new FellIll(tick, p.Id));
        }
    }

    // whoever is fed, warm and well heals a little
    private void Heal(bool hungry, bool cold)
    {
        if (hungry || cold) return;
        var heal = Round(_rules.Harm.Heal);
        foreach (var p in _folk.Living.ToArray())
            if (!_ill.Contains(p.Id) && p.Health < 100) _folk.Harm(p.Id, -heal, Sickness);
    }

    // stock rots by its durability; whole units lost are told
    private void Spoil(long tick)
    {
        foreach (var (id, units) in Stock.Amounts.ToArray())
        {
            var lost = Stock.Take(id, units * (100 - Durability(id)) / 100.0 * _rules.SpoilRate);
            var whole = (int)Math.Round(lost, MidpointRounding.AwayFromZero);
            if (whole > 0) _village.Publish(new Spoiled(tick, id, whole));
        }
    }

    // a possession breaks or rots by its durability
    private void Wear()
    {
        var random = _village.Random;
        foreach (var (household, owned) in _possessions.ToArray())
        foreach (var possession in owned.Values.ToArray())
            if (random.NextDouble() < (100 - Durability(possession.Substance)) / 100.0 * _rules.BreakRate)
                Take(household, possession.Use);
    }

    private static int Round(double health) => (int)Math.Round(health, MidpointRounding.AwayFromZero);
}
