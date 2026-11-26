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
        _village.Publish(new Made(_village.Clocks.Tick, maker, r.Out, null, null));
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
        Weather.Cold), SaveJson.Options)!;

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
    }

    private sealed record SavedLiving(IReadOnlyList<int> Own, Dictionary<string, List<Cell>> Tiles, Dictionary<string, double> Stock,
        IReadOnlyList<string> Structures, IReadOnlyList<SavedPossession> Possessions, IReadOnlyList<SavedWait> Waiting, IReadOnlyList<int> Ill,
        IReadOnlyList<int> Households, int FoodShortDays, int FuelShortDays, bool Drought, int DroughtLeft, int Cold);

    private sealed record SavedPossession(int Household, string Use, string Substance, long SinceTick);

    private sealed record SavedWait(int Household, string Use, long Since);
}
