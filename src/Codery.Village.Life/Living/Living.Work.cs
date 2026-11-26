using Codery.Village.Core;
using Codery.Village.Core.Map;
using Codery.Village.Core.Modules;
using Codery.Village.Laws;
using Codery.Village.Life.Folk;

namespace Codery.Village.Life.Living;

// the land, and the work done on it: gathering, the trades' recipes, building, and making for households
public sealed partial class Living
{
    private const double Nothing = 1e-9;

    // every tile of the island, by row and column, draws each deposit that fits its ground and level, in data order;
    // the deposit of "own" lays the island's own substance
    private void LayDeposits(Rng random)
    {
        var map = _village.Map;
        var deposits = _world.Deposits.Append(_world.Own.Deposit).ToList();
        for (var row = 0; row < map.Rows; row++)
        for (var col = 0; col < map.Cols; col++)
        {
            var cell = new Cell(row, col);
            if (map.IslandOf(cell) != Island) continue;
            var ground = map.At(cell);
            var level = map.LevelOf(cell);
            foreach (var deposit in deposits)
            {
                if (deposit.Ground != ground || (deposit.Level is { } at && at != level)) continue;
                if (random.NextDouble() >= deposit.Chance) continue;
                var id = deposit.Substance == "own" ? _own.Id : deposit.Substance;
                if (!_tiles.TryGetValue(id, out var tiles)) _tiles[id] = tiles = [];
                if (!tiles.Contains(cell)) tiles.Add(cell);
            }
        }
    }

    // who works today: everyone of age with a trade, the old too, while well
    private List<Folk.Person> Workers(long tick) =>
        [.. _folk.Living.Where(p => p.Trade is not null && _folk.StageOf(p, tick) >= LifeStage.Adult && !_ill.Contains(p.Id))];

    private void Work(long tick)
    {
        var workers = Workers(tick);
        Gather(workers);
        var structure = StructureToBuild(tick);
        foreach (var p in workers)
        {
            if (structure is { } s && p.Trade == _rules.Roles.Builder && _knows(p.Id, s.Recipe.Id)) BuildPart(p.Id, s.Use, s.Recipe, tick);
            else WorkTrade(p, tick);
        }
        if (structure is { } built && Stock[built.Recipe.Out] >= _rules.StructureCost - Nothing)
        {
            Stock.Take(built.Recipe.Out, _rules.StructureCost);
            _structures.Add(built.Use.Structure!);
            _village.Publish(new Built(tick, built.Use.Structure!, built.Recipe.Out));
        }
        foreach (var p in workers) MakeForAHousehold(p.Id, tick);
    }

    // each gatherer splits its output over what its trade gathers here; workers on one substance share its tiles
    private void Gather(List<Folk.Person> workers)
    {
        var month = _village.Clocks.Calendar.Now.Month;
        var gathers = workers.ToDictionary(p => p.Id, p => _trades.TryGetValue(p.Trade!, out var t)
            ? t.Gathers.Where(s => !_rules.Endless.Contains(s) && _tiles.ContainsKey(s)).ToList()
            : []);
        var on = gathers.Values.SelectMany(s => s).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        foreach (var p in workers)
        {
            var substances = gathers[p.Id];
            if (substances.Count == 0) continue;
            var output = _rules.Output.GetValueOrDefault(p.Trade!) / substances.Count;
            foreach (var s in substances)
            {
                var land = Math.Min(1, _tiles[s].Count / (on[s] * _rules.TilesPerWorker));
                var season = 1.0;
                if (_rules.Seasons.TryGetValue(s, out var months))
                    season = months[month - 1] * (Weather.Drought ? _rules.Drought.Factor : 1);
                Stock.Add(s, output * land * season);
            }
        }
    }

    // each recipe of the trade the worker knows, up to the trade's output, from what is in stock
    private void WorkTrade(Folk.Person p, long tick)
    {
        if (!_trades.TryGetValue(p.Trade!, out var trade)) return;
        var output = _rules.Output.GetValueOrDefault(p.Trade!);
        foreach (var id in trade.Recipes)
        {
            if (!_recipeById.TryGetValue(id, out var recipe) || !_knows(p.Id, id) || !CanDo(recipe)) continue;
            var yield = Yield(recipe);
            var batches = output / yield;
            foreach (var input in recipe.In)
                if (!_rules.Endless.Contains(input.Substance)) batches = Math.Min(batches, Stock[input.Substance] / input.Parts);
            if (batches <= Nothing) continue;
            foreach (var input in recipe.In)
                if (!_rules.Endless.Contains(input.Substance)) Stock.Take(input.Substance, batches * input.Parts);
            Stock.Add(recipe.Out, batches * yield);
            _village.Publish(new Made(tick, p.Id, recipe.Id, recipe.Out, null, null));
        }
    }

    // ── structures ──

    // the first structure not yet standing whose use some living builder can serve: by the best recipe a builder knows
    private (Use Use, Recipe Recipe)? StructureToBuild(long tick)
    {
        var builders = _folk.Living.Where(p => p.Trade == _rules.Roles.Builder && _folk.StageOf(p, tick) >= LifeStage.Adult).ToList();
        if (builders.Count == 0) return null;
        foreach (var use in _world.Uses)
        {
            if (use.Structure is not { } structure || _structures.Contains(structure)) continue;
            var best = _recipes.Where(r => CanDo(r) && builders.Any(b => _knows(b.Id, r.Id)))
                .Select(r => (Recipe: r, Score: use.Score(_substances[r.Out].Profile)))
                .Where(x => x.Score >= use.Threshold && x.Score > 0)
                .OrderByDescending(x => x.Score).ThenBy(x => x.Recipe.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (best.Recipe is not null) return (use, best.Recipe);
        }
        return null;
    }

    // a builder's day on a structure: the builder's output of its substance, from stock or at hand
    private void BuildPart(int builder, Use use, Recipe recipe, long tick)
    {
        var batches = (int)Math.Ceiling(_rules.Output.GetValueOrDefault(_rules.Roles.Builder) / Yield(recipe));
        var made = false;
        for (var i = 0; i < batches; i++)
        {
            var pending = new List<VillageEvent>();
            if (!Attempt(() => Batch(recipe, builder, 0, pending))) break;
            foreach (var e in pending) _village.Publish(e);
            made = true;
        }
        if (made) _village.Publish(new Made(tick, builder, recipe.Id, recipe.Out, use.Id, null));
    }

    private void BuildSchool(long tick)
    {
        if (_structures.Contains(School)) return;
        var pupils = _folk.Living.Count(p => _folk.StageOf(p, tick) == LifeStage.Pupil);
        if (pupils < _rules.School.Pupils || !_folk.Living.Any(p => p.Trade == _rules.Roles.Teacher)) return;
        var use = _world.Uses.Single(u => u.Id == _rules.School.Of);
        var best = _tiles.Keys.Concat(Stock.Amounts.Keys).Distinct().Where(_substances.ContainsKey)
            .Select(id => (Id: id, Score: use.Score(_substances[id].Profile)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (best.Id is null) return;
        _structures.Add(School);
        _village.Publish(new Built(tick, School, best.Id));
    }

    // ── making for households ──

    // at most one thing a day: for the first household use the maker can serve, the best recipe they know, for the
    // household that waited longest, or else one whose possession scores clearly worse
    private void MakeForAHousehold(int maker, long tick)
    {
        foreach (var use in _householdUses)
        {
            var options = _recipes.Where(r => _knows(maker, r.Id) && CanDo(r))
                .Select(r => (Recipe: r, Score: use.Score(_substances[r.Out].Profile)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score).ThenBy(x => x.Recipe.Id, StringComparer.Ordinal);
            foreach (var (recipe, score) in options)
            {
                if (Waiting(use.Id, score) is not { } household) break;
                var pending = new List<VillageEvent>();
                if (!Attempt(() => Batch(recipe, maker, 0, pending) && Stock.Take(recipe.Out, 1) >= 1 - Nothing)) continue;
                foreach (var e in pending) _village.Publish(e);
                _village.Publish(new Made(tick, maker, recipe.Id, recipe.Out, use.Id, household));
                Possess(household, use.Id, recipe.Out);
                return;
            }
        }
    }

    // the household that waited longest for the use, or else the one that has had a worse thing longest
    private int? Waiting(string use, double score)
    {
        var lacking = _waiting.Where(h => h.Value.ContainsKey(use)).OrderBy(h => h.Value[use]).ThenBy(h => h.Key).Select(h => (int?)h.Key).FirstOrDefault();
        if (lacking is not null) return lacking;
        return _possessions
            .Where(h => h.Value.TryGetValue(use, out var had) && _substances.TryGetValue(had.Substance, out var s)
                && _world.Uses.Single(u => u.Id == use).Score(s.Profile) <= score - _rules.Upgrade)
            .OrderBy(h => h.Value[use].SinceTick).ThenBy(h => h.Key).Select(h => (int?)h.Key).FirstOrDefault();
    }

    // ── one batch ──

    // runs a making that may fail halfway; on failure the stock is as it was
    private bool Attempt(Func<bool> making)
    {
        var before = Stock.Copy();
        if (making()) return true;
        Stock.Restore(before);
        return false;
    }

    // one batch of the recipe into stock; a missing made input is made first while depth allows
    private bool Batch(Recipe recipe, int maker, int depth, List<VillageEvent> pending)
    {
        if (!CanDo(recipe)) return false;
        foreach (var input in recipe.In)
            if (!Obtain(input.Substance, input.Parts, maker, depth, pending)) return false;
        Stock.Add(recipe.Out, Yield(recipe));
        return true;
    }

    // from the endless, from stock, straight from a tile, or made by a recipe the maker knows
    private bool Obtain(string substance, double units, int maker, int depth, List<VillageEvent> pending)
    {
        if (_rules.Endless.Contains(substance)) return true;
        units -= Stock.Take(substance, units);
        if (units <= Nothing || _tiles.ContainsKey(substance)) return true;
        if (depth >= _rules.MaxDepth) return false;
        var recipe = _recipes.FirstOrDefault(r => r.Out == substance && _knows(maker, r.Id));
        if (recipe is null) return false;
        while (units > Nothing)
        {
            if (!Batch(recipe, maker, depth + 1, pending)) return false;
            pending.Add(new Made(_village.Clocks.Tick, maker, recipe.Id, substance, null, null));
            units -= Stock.Take(substance, units);
        }
        return true;
    }

    // the structure the recipe's process needs stands
    private bool CanDo(Recipe recipe) =>
        !_world.Nature.Processes.TryGetValue(recipe.Process, out var process) || process.Needs is not { } needs || _structures.Contains(needs);

    // a batch gives as many units as went in
    private static double Yield(Recipe recipe) => recipe.In.Sum(i => i.Parts);
}
