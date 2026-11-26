using System.Text.Json;
using System.Text.Json.Nodes;
using Codery.Village.Core.Map;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Laws;

namespace Codery.Village.Life.Living;

/// <summary>
/// How West lives: the stock it holds in common, the trades that fill it, what households own, the structures that stand,
/// and the weather, hunger, cold and sickness that act on its people. Once a day at 06:00: the weather, on a working day the
/// work (gathering, the trades' recipes, building, making for households), eating, burning fuel, sickness, healing, spoiling
/// and wear. A person works only what the knower says they know: <c>knower(person, recipe or process)</c>.
/// </summary>
public sealed partial class Living : IModule
{
    /// <summary>The island Living lives on in v6-1: West.</summary>
    public const int Island = 0;

    private const string Food = "food";
    private const string Fuel = "fuel";
    private const string School = "school";

    private readonly Func<int, string, bool>? _givenKnower;
    private Func<int, string, bool> _knows = (_, _) => false;
    private ModuleContext _village = null!;
    private LivingRules _rules = null!;
    private WorldData _world = null!;
    private Folk.Folk _folk = null!;
    private Dictionary<string, Trade> _trades = [];
    private List<Recipe> _recipes = [];
    private readonly List<(Recipe Recipe, Substance Output)> _learned = [];
    private readonly SortedDictionary<string, double> _site = new(StringComparer.Ordinal);
    private Dictionary<string, Recipe> _recipeById = [];
    private Dictionary<string, Substance> _substances = [];
    private List<Use> _householdUses = [];
    private Use _food = null!;
    private Use _fuel = null!;

    // the state that is saved
    private Substance _own = null!;
    private readonly SortedDictionary<string, List<Cell>> _tiles = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _structures = new(StringComparer.Ordinal);
    private readonly SortedDictionary<int, SortedDictionary<string, Possession>> _possessions = [];
    private readonly SortedDictionary<int, SortedDictionary<string, long>> _waiting = [];
    private readonly SortedSet<int> _ill = [];
    private readonly SortedSet<int> _households = [];
    private int _foodShortDays;
    private int _fuelShortDays;
    private int _droughtLeft;

    /// <param name="knower">Whether a person knows a recipe or process; null: their trade's start recipes and every start process.</param>
    public Living(Func<int, string, bool>? knower = null) => _givenKnower = knower;

    public string Name => "living";

    public int Version => 1;

    /// <summary>What the island holds in common.</summary>
    public Stock Stock { get; } = new();

    /// <summary>The structures that stand: <c>house</c> from the start, then <c>kiln</c>, <c>furnace</c>, <c>school</c>.</summary>
    public IReadOnlySet<string> Structures => _structures;

    /// <summary>Who is ill now.</summary>
    public IReadOnlySet<int> Ill => _ill;

    /// <summary>The weather of the day.</summary>
    public Weather Weather { get; private set; } = new(false, 0);

    /// <summary>The island's own substance, found nowhere else.</summary>
    public Substance Own => _own;

    /// <summary>Every substance that can be gathered on the island: the raw ones on at least one tile, and its own.</summary>
    public IReadOnlyDictionary<string, Substance> AtHand =>
        _tiles.Keys.ToDictionary(id => id, id => _substances[id], StringComparer.Ordinal);

    /// <summary>The tiles that hold a substance; empty for one not at hand.</summary>
    public IReadOnlyList<Cell> TilesOf(string substance) => _tiles.TryGetValue(substance, out var tiles) ? tiles : [];

    /// <summary>
    /// Takes up a recipe found on the island and the substance it makes, so that whoever knows it can make it: for a
    /// household, for a structure, or at <see cref="Make"/>. Kept in the save.
    /// </summary>
    public void Learn(Recipe recipe, Substance output)
    {
        if (_recipeById.ContainsKey(recipe.Id)) return;
        _learned.Add((recipe, output));
        Take(recipe, output);
    }

    private void Take(Recipe recipe, Substance output)
    {
        _recipes.Add(recipe);
        _recipeById[recipe.Id] = recipe;
        _substances.TryAdd(output.Id, output);
    }

    /// <summary>For how many days in a row <paramref name="what"/> (<c>food</c> or <c>fuel</c>) has run short; 0 when it has not.</summary>
    public int DaysShort(string what) => what == Food ? _foodShortDays : what == Fuel ? _fuelShortDays : 0;

    /// <summary>A substance Living knows (raw, the island's own, or made), or null.</summary>
    public Substance? SubstanceOf(string id) => _substances.GetValueOrDefault(id);

    /// <summary>What a household has, one possession per use, in use order.</summary>
    public IReadOnlyList<Possession> Of(int household) =>
        _possessions.TryGetValue(household, out var owned) ? [.. owned.Values] : [];

    /// <summary>Gives a household something of <paramref name="substance"/> for <paramref name="use"/>, in place of what it had.</summary>
    public void Give(int household, string use, string substance)
    {
        if (!_substances.ContainsKey(substance)) throw new ArgumentException($"No substance '{substance}' is known here.", nameof(substance));
        Possess(household, use, substance);
    }

    /// <summary>Takes a household's possession for <paramref name="use"/>, if it has one.</summary>
    public void Take(int household, string use)
    {
        if (!_possessions.TryGetValue(household, out var owned) || !owned.Remove(use)) return;
        var tick = _village.Clocks.Tick;
        Want(household)[use] = tick;
        _village.Publish(new Lost(tick, household, use));
    }

    /// <summary>
    /// One batch of <paramref name="recipe"/> by <paramref name="maker"/>, now: when its inputs are in stock or at hand and
    /// the structure its process needs stands. The output goes into stock. False, and nothing taken, otherwise.
    /// </summary>
    public bool Make(string recipe, int maker)
    {
        if (!_recipeById.TryGetValue(recipe, out var r)) return false;
        // at the deepest step nothing missing is made first: only what is in stock or at hand
        if (!Attempt(() => Batch(r, maker, _rules.MaxDepth, []))) return false;
        _village.Publish(new Made(_village.Clocks.Tick, maker, r.Id, r.Out, null, null));
        return true;
    }

    public void Start(ModuleContext village)
    {
        Read(village);
        _own = OwnSubstance.Draw(village.Random, _world.Own, Island, _world.Raw);
        _substances[_own.Id] = _own;
        LayDeposits(village.Random);
        foreach (var (id, units) in _rules.Start) Stock.Add(id, units);
        _structures.UnionWith(_rules.Built);
        Weather = new Weather(false, ColdOf(village));
        SeeHouseholds(village.Clocks.Tick);
    }

    public void Step(ModuleContext village)
    {
        if (!Hourly.DayStarts(village)) return;

        var tick = village.Clocks.Tick;
        ChangeWeather(village);
        SeeHouseholds(tick);
        if (!village.Clocks.Calendar.Moment.Weekend) Work(tick);
        var hungry = Eat(tick);
        var cold = Warm(tick);
        Sicken(tick, hungry, cold);
        Heal(hungry, cold);
        Spoil(tick);
        Wear();
        BuildSchool(tick);
    }

    private void Read(ModuleContext village)
    {
        _village = village;
        _rules = village.Data.Section<LivingRules>("living");
        _world = WorldData.From(village.Data);
        _folk = village.Module<Folk.Folk>();
        _trades = _world.Start.Trades.ToDictionary(t => t.Id, StringComparer.Ordinal);
        _recipes = [.. _world.Start.Recipes];
        _recipeById = _recipes.ToDictionary(r => r.Id, StringComparer.Ordinal);
        _substances = new Dictionary<string, Substance>(_world.Substances, StringComparer.Ordinal);
        _householdUses = [.. _world.Uses.Where(u => u.Household)];
        _food = _world.Uses.Single(u => u.Id == Food);
        _fuel = _world.Uses.Single(u => u.Id == Fuel);
        _knows = _givenKnower ?? KnowsAtStart;
    }

    // a person knows their own trade's start recipes and every start process
    private bool KnowsAtStart(int person, string item) =>
        _world.Start.Processes.Contains(item)
        || (_folk.Get(person)?.Trade is { } trade && _trades.TryGetValue(trade, out var t) && t.Recipes.Contains(item));

    // ── households ──

    // new households start wanting every household use; the possessions of households that are gone go with them
    private void SeeHouseholds(long tick)
    {
        var now = _folk.Living.Select(p => p.Household).ToHashSet();
        foreach (var gone in _households.Where(h => !now.Contains(h)).ToList())
        {
            _households.Remove(gone);
            _waiting.Remove(gone);
            _possessions.Remove(gone);
        }
        foreach (var household in now.Order())
        {
            if (!_households.Add(household)) continue;
            var wants = Want(household);
            foreach (var use in _householdUses) wants[use.Id] = tick;
        }
    }

    private SortedDictionary<string, long> Want(int household)
    {
        if (!_waiting.TryGetValue(household, out var wants)) _waiting[household] = wants = new(StringComparer.Ordinal);
        return wants;
    }

    private void Possess(int household, string use, string substance)
    {
        var tick = _village.Clocks.Tick;
        if (!_possessions.TryGetValue(household, out var owned)) _possessions[household] = owned = new(StringComparer.Ordinal);
        owned[use] = new Possession(use, substance, tick);
        Want(household).Remove(use);
        _village.Publish(new Got(tick, household, use, substance));
    }

    private int Durability(string substance) =>
        _substances.TryGetValue(substance, out var s) ? s.Profile[Property.Durability] : 100;

    // ── saving ──

    public JsonNode Save() => JsonSerializer.SerializeToNode(new SavedLiving(
        [.. _own.Profile.Values],
        _tiles.ToDictionary(t => t.Key, t => t.Value),
        Stock.Copy(),
        [.. _structures],
        [.. _possessions.SelectMany(h => h.Value.Values.Select(p => new SavedPossession(h.Key, p.Use, p.Substance, p.SinceTick)))],
        [.. _waiting.SelectMany(h => h.Value.Select(w => new SavedWait(h.Key, w.Key, w.Value)))],
        [.. _ill],
        [.. _households],
        _foodShortDays,
        _fuelShortDays,
        Weather.Drought,
        _droughtLeft,
        Weather.Cold,
        [.. _learned.Select(l => new SavedLearned(l.Recipe.Id, l.Recipe.Process, [.. l.Recipe.In], l.Output.Id, l.Output.Kind, [.. l.Output.Profile.Values]))],
        new Dictionary<string, double>(_site)),
        SaveJson.Options)!;

    public void Load(ModuleContext village, JsonNode data, int version)
    {
        Read(village);
        var saved = data.Deserialize<SavedLiving>(SaveJson.Options) ?? throw new InvalidDataException("The living part of the save is empty.");
        _own = new Substance($"own-{Island}", $"own-{Island}", new Profile([.. saved.Own]), Raw: true);
        _substances[_own.Id] = _own;
        _tiles.Clear();
        foreach (var (id, cells) in saved.Tiles) _tiles[id] = [.. cells];
        Stock.Restore(saved.Stock);
        _structures.Clear();
        _structures.UnionWith(saved.Structures);
        _possessions.Clear();
        foreach (var p in saved.Possessions)
        {
            if (!_possessions.TryGetValue(p.Household, out var owned)) _possessions[p.Household] = owned = new(StringComparer.Ordinal);
            owned[p.Use] = new Possession(p.Use, p.Substance, p.SinceTick);
        }
        _waiting.Clear();
        foreach (var w in saved.Waiting) Want(w.Household)[w.Use] = w.Since;
        _ill.Clear();
        _ill.UnionWith(saved.Ill);
        _households.Clear();
        _households.UnionWith(saved.Households);
        _foodShortDays = saved.FoodShortDays;
        _fuelShortDays = saved.FuelShortDays;
        _droughtLeft = saved.DroughtLeft;
        Weather = new Weather(saved.Drought, saved.Cold);
        _site.Clear();
        foreach (var (structure, amount) in saved.Site ?? new Dictionary<string, double>()) _site[structure] = amount;
        _learned.Clear();
        foreach (var l in saved.Learned ?? [])
        {
            var learned = (new Recipe(l.Recipe, l.Process, l.In, l.Out), new Substance(l.Out, l.Kind, new Profile([.. l.Profile]), Raw: false));
            _learned.Add(learned);
            Take(learned.Item1, learned.Item2);
        }
    }

    private sealed record SavedLiving(IReadOnlyList<int> Own, Dictionary<string, List<Cell>> Tiles, Dictionary<string, double> Stock,
        IReadOnlyList<string> Structures, IReadOnlyList<SavedPossession> Possessions, IReadOnlyList<SavedWait> Waiting, IReadOnlyList<int> Ill,
        IReadOnlyList<int> Households, int FoodShortDays, int FuelShortDays, bool Drought, int DroughtLeft, int Cold,
        IReadOnlyList<SavedLearned>? Learned = null, IReadOnlyDictionary<string, double>? Site = null);

    private sealed record SavedLearned(string Recipe, string Process, IReadOnlyList<RecipeInput> In, string Out, string Kind, IReadOnlyList<int> Profile);

    private sealed record SavedPossession(int Household, string Use, string Substance, long SinceTick);

    private sealed record SavedWait(int Household, string Use, long Since);
}
